using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Xandrite.Framing;
using Xandrite.Geometry;
using Xandrite.Library;
using Xandrite.Library.Logging;
using Xandrite.Library.Thumbnails;
using Xandrite.Loading;
using Xandrite.Rendering;
using Xandrite.Thumbnails;

namespace Xandrite.Presentation;

public sealed record RatingChoice(string Label, int? Stars);

/// <summary>
/// The viewer: turn a model to the view that shows it best, then press Update to use that view as its thumbnail in
/// the library. A port of the Electron app's renderer/viewer.js.
///
/// Frames are drawn on a background thread. While one is being drawn, further requests (the mouse keeps moving) are
/// folded into one, drawn as soon as the current frame is shown, so the window never falls behind the mouse.
/// </summary>
public sealed class ViewerViewModel : ObservableObject, IDisposable
{
    private readonly LibraryDatabase _db;
    private readonly LibraryLocation _location;
    private readonly ThumbnailQueue _queue;
    private readonly Func<IModelRenderer> _createRenderer;
    private readonly ILibraryShell _shell;
    private readonly IUiDispatcher _ui;
    private readonly ILog _log;

    private readonly object _renderLock = new();
    private IModelRenderer? _renderer;
    private IGpuMesh? _gpuMesh;
    private Mesh? _gpuMeshSource;

    private Mesh? _mesh;
    private ModelFrame _frame;
    private string? _sha;
    private ThumbnailFileInfo? _info;
    private int _loadToken;
    private int _viewportWidth = 1, _viewportHeight = 1;
    private bool _rendering, _pending;
    private byte[]? _buffer;
    private bool _loading;

    private string _title = "Viewer";
    private string _status = "Open a model from the library (double-click a thumbnail), or open a file.";
    private bool _zUp = true;
    private bool _canUpdate, _canRate, _hasCustomView;
    private RatingChoice _rating;
    private string _ratingNote = "", _updateNote = "";
    private int _exportSize = Look.ThumbnailSize;
    private string _adapter = "";

    public ViewerViewModel(LibraryDatabase db, LibraryLocation location, ThumbnailQueue queue, Func<IModelRenderer> createRenderer, ILibraryShell shell, IUiDispatcher ui, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _db = db;
        _location = location;
        _queue = queue;
        _createRenderer = createRenderer;
        _shell = shell;
        _ui = ui;
        _rating = RatingChoices[0];

        ResetViewCommand = new RelayCommand(ResetView, () => HasModel);
        UpdateThumbnailCommand = new RelayCommand(() => _ = UpdateThumbnailAsync(), () => HasModel && CanUpdate);
        RevertCommand = new RelayCommand(Revert, () => HasModel && HasCustomView);
        SavePngCommand = new RelayCommand(() => _ = SavePngAsync(), () => HasModel);
    }

    /// <summary>A new frame, on the UI thread. Its pixels are RGBA and may be changed by the handler (they are not reused until the next frame).</summary>
    public event Action<RenderedImage>? FrameReady;

    /// <summary>The library thumbnail of this model was replaced by the current view.</summary>
    public event Action<string>? ThumbnailReplaced;

    /// <summary>The saved view was dropped; the thumbnail is being drawn again automatically.</summary>
    public event Action<string>? ViewReverted;

    public event Action<string, int?>? RatingChanged;

    public OrbitCamera Camera { get; } = new();

    public RelayCommand ResetViewCommand { get; }
    public RelayCommand UpdateThumbnailCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand SavePngCommand { get; }

    /* ---------- what is shown ---------- */

    public bool HasModel => _mesh is not null;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string StatusText
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string FileName { get; private set; } = "";
    public string FormatText { get; private set; } = "";
    public string SizeText { get; private set; } = "";
    public string TrianglesText { get; private set; } = "";
    public string DimensionsText { get; private set; } = "";
    public string Sha256Text { get; private set; } = "";

    public string AdapterText
    {
        get => _adapter;
        private set => Set(ref _adapter, value);
    }

    /// <summary>Turns the file's Z axis upwards (STL and 3MF files are normally Z-up). Changing it resets the view.</summary>
    public bool ZUp
    {
        get => _zUp;
        set
        {
            if (!Set(ref _zUp, value) || _loading || _mesh is null) return;
            _frame = ViewMath.OrientAndCentre(_mesh.Bounds, _zUp);
            ResetView();
        }
    }

    /* ---------- the library: update, revert, rating ---------- */

    /// <summary>The model is in the library, so its thumbnail can be replaced.</summary>
    public bool CanUpdate
    {
        get => _canUpdate;
        private set { if (Set(ref _canUpdate, value)) UpdateThumbnailCommand.Refresh(); }
    }

    /// <summary>Only a model opened from the library can be rated.</summary>
    public bool CanRate
    {
        get => _canRate;
        private set => Set(ref _canRate, value);
    }

    public bool HasCustomView
    {
        get => _hasCustomView;
        private set { if (Set(ref _hasCustomView, value)) RevertCommand.Refresh(); }
    }

    public IReadOnlyList<RatingChoice> RatingChoices { get; } =
    [
        new("Unrated", null), new("0 stars", 0), new("★", 1), new("★★", 2), new("★★★", 3), new("★★★★", 4), new("★★★★★", 5),
    ];

    public RatingChoice Rating
    {
        get => _rating;
        set
        {
            if (value is null || !Set(ref _rating, value) || _loading || _sha is null || !CanRate) return;
            _db.SetRating(_sha, value.Stars);
            _log.Info("rating.set", ("sha256", _sha), ("stars", value.Stars));
            RatingNote = "Saved.";
            RatingChanged?.Invoke(_sha, value.Stars);
        }
    }

    public string RatingNote
    {
        get => _ratingNote;
        private set => Set(ref _ratingNote, value);
    }

    public string UpdateNote
    {
        get => _updateNote;
        private set => Set(ref _updateNote, value);
    }

    /* ---------- advanced ---------- */

    public IReadOnlyList<int> ExportSizes { get; } = [256, 512, 1024, 2048];

    public int ExportSize
    {
        get => _exportSize;
        set
        {
            if (Set(ref _exportSize, value)) OnPropertyChanged(nameof(ViewSettingsText));
        }
    }

    /// <summary>The view settings a thumbnail made now would carry (the "View settings" panel of the JS viewer).</summary>
    public string ViewSettingsText
    {
        get
        {
            if (_mesh is null) return "";
            var view = Camera.ThumbnailView(Aspect, _zUp);
            var p = view.Position;
            var t = view.Target;
            double d = Vector3.Distance(p, t);
            if (!(d > 0)) d = 1;
            double azimuth = Math.Atan2(p.X - t.X, p.Z - t.Z) * 180 / Math.PI;
            double elevation = Math.Asin(Math.Clamp((p.Y - t.Y) / d, -1, 1)) * 180 / Math.PI;
            static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.000}, {v.Y:0.000}, {v.Z:0.000}");
            return string.Join(Environment.NewLine,
                $"Position      {V(p)}",
                $"Target        {V(t)}",
                $"Distance      {d.ToString("0.000", CultureInfo.InvariantCulture)}",
                $"Azimuth       {azimuth.ToString("0.0", CultureInfo.InvariantCulture)}°",
                $"Elevation     {elevation.ToString("0.0", CultureInfo.InvariantCulture)}°",
                $"Field of view {view.FovDeg.ToString("0.0", CultureInfo.InvariantCulture)}°",
                $"Z-up          {(_zUp ? "yes" : "no")}",
                $"Size          {_exportSize} × {_exportSize} px",
                $"Background    #{Look.BackgroundHex:x6}",
                $"Render        version {Look.RenderVersion}");
        }
    }

    /* ---------- loading ---------- */

    /// <summary>A file dropped on the viewer. It can still update its library thumbnail if the library knows it.</summary>
    public async Task OpenPathAsync(string path)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = Path.GetFileName(path) + ": " + e.Message;
            return;
        }
        await LoadAsync(new ViewerDocument(Path.GetFileName(path), Path.GetExtension(path).TrimStart('.'), bytes, FromLibrary: false));
    }

    public async Task LoadAsync(ViewerDocument document)
    {
        int token = ++_loadToken;
        StatusText = "Reading " + document.Name + "…";
        Mesh mesh;
        string sha;
        try
        {
            (mesh, sha) = await Task.Run(() => (ModelLoader.Load(document.Bytes, document.Format), Convert.ToHexStringLower(SHA256.HashData(document.Bytes))));
        }
        catch (ModelFormatException e)
        {
            _log.Warn("viewer.unreadable", ("name", document.Name), ("message", e.Message));
            if (token == _loadToken) StatusText = "Could not read " + document.Name + ": " + e.Message;
            return;
        }
        if (token != _loadToken) return;   // another file was opened meanwhile

        _loading = true;
        try
        {
            _mesh = mesh;
            _sha = sha;
            string format = document.Format.TrimStart('.').ToUpperInvariant();
            _info = new ThumbnailFileInfo(document.Name, format, document.Bytes.Length, sha);
            Title = document.Name + " – Xandrite viewer";
            FileName = document.Name;
            FormatText = format;
            SizeText = Format.Bytes(document.Bytes.Length);
            TrianglesText = mesh.TriangleCount.ToString("N0", CultureInfo.CurrentCulture);
            var size = mesh.Bounds.Size;
            DimensionsText = string.Create(CultureInfo.CurrentCulture, $"{size.X:0.##} × {size.Y:0.##} × {size.Z:0.##} (file units, usually mm)");
            Sha256Text = sha;

            bool inLibrary = _db.HasModel(sha);
            CanUpdate = inLibrary;
            CanRate = document.FromLibrary && inLibrary;
            int? stars = inLibrary ? _db.GetRating(sha) : null;
            Rating = RatingChoices.First(r => r.Stars == stars);
            RatingNote = "";
            UpdateNote = inLibrary ? "" : "This file is not in the library, so it has no thumbnail to update.";

            var saved = inLibrary ? _db.GetView(sha) : null;
            HasCustomView = saved is not null;
            _zUp = saved?.ZUp ?? true;
            _frame = ViewMath.OrientAndCentre(mesh.Bounds, _zUp);
            if (saved is not null)
                Camera.SetView(new StoredView(ToVector(saved.Position), ToVector(saved.Target), (float)saved.FovDeg, saved.ZUp), _frame.Radius);
            else
                Camera.Reset(_frame.Radius, Aspect);
        }
        finally
        {
            _loading = false;
        }

        StatusText = "Loaded " + document.Name + ".";
        OnPropertyChanged(string.Empty);   // every panel reads again
        ResetViewCommand.Refresh();
        SavePngCommand.Refresh();
        UpdateThumbnailCommand.Refresh();
        RevertCommand.Refresh();
        RequestFrame();
    }

    private static Vector3 ToVector(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);

    /* ---------- the camera ---------- */

    private float Aspect => _viewportWidth / (float)Math.Max(1, _viewportHeight);

    /// <summary>The size of the drawing area in pixels (device pixels, not WPF units).</summary>
    public void SetViewport(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _viewportWidth && height == _viewportHeight) return;
        _viewportWidth = width;
        _viewportHeight = height;
        RequestFrame();
    }

    public void Rotate(double dx, double dy)
    {
        if (_mesh is null) return;
        Camera.Rotate((float)dx, (float)dy, _viewportHeight);
        CameraMoved();
    }

    public void Pan(double dx, double dy)
    {
        if (_mesh is null) return;
        Camera.Pan((float)dx, (float)dy, _viewportHeight);
        CameraMoved();
    }

    public void Zoom(double wheelDelta)
    {
        if (_mesh is null) return;
        Camera.Zoom((float)wheelDelta);
        CameraMoved();
    }

    public void ResetView()
    {
        if (_mesh is null) return;
        Camera.Reset(_frame.Radius, Aspect);
        CameraMoved();
    }

    private void CameraMoved()
    {
        UpdateNote = "";   // a changed view makes "updated" no longer true
        OnPropertyChanged(nameof(ViewSettingsText));
        RequestFrame();
    }

    /* ---------- drawing ---------- */

    /// <summary>Asks for a new frame. Requests made while one is being drawn are folded into one.</summary>
    public void RequestFrame()
    {
        if (_mesh is null) return;
        if (_rendering)
        {
            _pending = true;
            return;
        }
        _rendering = true;
        var mesh = _mesh;
        var request = new RenderRequest(_frame.Transform, Camera.Setup(Aspect), _viewportWidth, _viewportHeight);
        var buffer = _buffer;
        Task.Run(() => DrawFrame(mesh, request, buffer)).ContinueWith(task => _ui.Post(() =>
        {
            _rendering = false;
            if (task.IsCompletedSuccessfully)
            {
                _buffer = task.Result.Rgba;
                if (ReferenceEquals(mesh, _mesh)) FrameReady?.Invoke(task.Result);
            }
            else if (task.Exception?.InnerException is { } error)
            {
                _log.Error("viewer.render_failed", ("message", error.Message));
                StatusText = "The model could not be drawn: " + error.Message;
                _pending = false;
            }
            if (_pending)
            {
                _pending = false;
                RequestFrame();
            }
        }), TaskScheduler.Default);
    }

    private RenderedImage DrawFrame(Mesh mesh, RenderRequest request, byte[]? buffer)
    {
        lock (_renderLock)
        {
            try
            {
                var renderer = EnsureRenderer();
                if (_gpuMesh is null || !ReferenceEquals(_gpuMeshSource, mesh))
                {
                    _gpuMesh?.Dispose();
                    _gpuMesh = null;
                    _gpuMesh = renderer.Upload(mesh);
                    _gpuMeshSource = mesh;
                }
                return renderer.Render(_gpuMesh, request, buffer);
            }
            catch (RendererException)
            {
                DropRenderer();   // start a fresh one next time
                throw;
            }
        }
    }

    private IModelRenderer EnsureRenderer()
    {
        if (_renderer is null)
        {
            _renderer = _createRenderer();
            string adapter = _renderer.AdapterDescription;
            _ui.Post(() => AdapterText = adapter);
        }
        return _renderer;
    }

    private void DropRenderer()
    {
        _gpuMesh?.Dispose();
        _gpuMesh = null;
        _gpuMeshSource = null;
        _renderer?.Dispose();
        _renderer = null;
    }

    /// <summary>A square PNG of the current view, with the view settings inside, at the given size.</summary>
    private Task<(byte[] Png, StoredView View)> DrawThumbnailAsync(int size)
    {
        var mesh = _mesh ?? throw new InvalidOperationException("No model is open.");
        var view = Camera.ThumbnailView(Aspect, _zUp);
        var info = _info;
        return Task.Run(() =>
        {
            lock (_renderLock)
            {
                try
                {
                    var (png, _, _, _) = new ThumbnailGenerator(EnsureRenderer()).Draw(mesh, view, size, info);
                    return (png, view);
                }
                catch (RendererException)
                {
                    DropRenderer();
                    throw;
                }
            }
        });
    }

    /* ---------- update, revert, save ---------- */

    /// <summary>Replaces the model's library thumbnail with the current view, and remembers the view.</summary>
    public async Task UpdateThumbnailAsync()
    {
        if (_mesh is null || _sha is not { } sha || !CanUpdate) return;
        UpdateNote = "Updating…";
        try
        {
            var (png, view) = await DrawThumbnailAsync(Look.ThumbnailSize);
            // Off the UI thread, so a Revert waiting for the same lock on the UI thread cannot hold this up.
            await Task.Run(async () =>
            {
                using (await ThumbnailFiles.LockAsync(sha))   // the queue may be drawing this model too
                {
                    await ThumbnailFiles.WriteAsync(_location.ThumbnailPath(sha), png);
                    _db.SetView(sha, new SavedView([view.Position.X, view.Position.Y, view.Position.Z], [view.Target.X, view.Target.Y, view.Target.Z], view.FovDeg, view.ZUp));
                    _db.RecordThumbnail(sha, ThumbnailState.Ok, Look.RenderVersion, Look.ThumbnailSize);
                    _queue.Skip(sha);   // in case it was waiting in the queue
                }
            });
            HasCustomView = true;
            _log.Info("view.saved", ("sha256", sha), ("fov_deg", view.FovDeg), ("z_up", view.ZUp));
            UpdateNote = "Thumbnail updated in the library.";
            ThumbnailReplaced?.Invoke(sha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RendererException)
        {
            _log.Error("view.save_failed", ("sha256", sha), ("message", e.Message));
            UpdateNote = "Could not update: " + e.Message;
        }
    }

    /// <summary>Forgets the saved view; the queue draws the automatic view again.</summary>
    public void Revert()
    {
        if (_sha is not { } sha || !HasCustomView) return;
        using (ThumbnailFiles.Lock(sha))   // so a drawing of the saved view cannot land after this
        {
            _db.DeleteView(sha);
            _db.DeleteThumbnailRow(sha);
            _queue.Unskip(sha);
        }
        _queue.Start();
        HasCustomView = false;
        _log.Info("view.reverted", ("sha256", sha));
        ResetView();
        UpdateNote = "Back to the automatic view. The thumbnail is being drawn again.";
        ViewReverted?.Invoke(sha);
    }

    /// <summary>Exports the current view as a PNG of the size chosen under Advanced, named after the model by default.</summary>
    public async Task SavePngAsync()
    {
        if (_mesh is null) return;
        string suggested = Path.GetFileNameWithoutExtension(FileName) + ".png";   // named after the model, not its SHA-256
        string? path = _shell.PickSaveFile("Export the view as a PNG", suggested, "PNG image (*.png)|*.png");
        if (path is null) return;
        try
        {
            var (png, _) = await DrawThumbnailAsync(_exportSize);
            await File.WriteAllBytesAsync(path, png);
            StatusText = "Exported " + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RendererException)
        {
            StatusText = "Could not export the image: " + e.Message;
        }
    }

    /// <summary>The window closed: lets go of the model and the graphics device.</summary>
    public void Close()
    {
        _loadToken++;
        _mesh = null;
        _sha = null;
        _buffer = null;
        lock (_renderLock) DropRenderer();
        OnPropertyChanged(nameof(HasModel));
    }

    public void Dispose() => Close();
}
