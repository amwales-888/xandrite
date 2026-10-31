using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Xandrite.Library;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Library.Thumbnails;

namespace Xandrite.Presentation;

/// <summary>
/// The library window: folder tree, filter bar, thumbnail grid, selection, status bar. Ported from the Electron app's
/// renderer/app.js and selection.js, and the parts of main.js the window called.
///
/// Everything here runs on the UI thread; background work (scans, the thumbnail queue) reports back through
/// <see cref="IUiDispatcher"/>.
/// </summary>
public sealed class LibraryViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The grid holds at most this many files at once (the rest are counted, not shown).</summary>
    public const int MaxShown = 250_000;

    public const double MinTileSize = 96, MaxTileSize = 384, TileSpacing = 8;

    private readonly LibraryDatabase _db;
    private readonly LibraryLocation _location;
    private readonly ThumbnailQueue _queue;
    private readonly ModelSource _source;
    private readonly ILibraryShell _shell;
    private readonly IUiDispatcher _ui;
    private readonly ILog _log;

    private List<TileViewModel> _tiles = [];
    private Dictionary<string, List<TileViewModel>> _bySha = [];
    private IReadOnlyList<TileRowViewModel> _rows = [];
    private int _anchor = -1;
    private int _columns = 1;
    private double _viewportWidth = 800;
    private bool _loading;

    private string _searchText = "";
    private CancellationTokenSource? _searchDelay;
    private string? _selectedFolder;
    private bool _includeSubfolders = true;
    private bool _showMissing;
    private SortOption _sort = FilterOptions.Sorts[0];
    private bool _descending;
    private SizeFilterOption _sizeFilter = FilterOptions.Sizes[0];
    private AgeFilterOption _ageFilter = FilterOptions.Ages[0];
    private AgeBasisOption _ageBasis = FilterOptions.AgeBases[0];
    private RatingFilterOption _ratingFilter = FilterOptions.Ratings[0];
    private double _tileSize = 180;

    private long _total;
    private int _selectedCount;
    private bool _hasRoots;
    private string _statsText = "";
    private string _scanText = "";
    private bool _isScanning;
    private CancellationTokenSource? _scanCancel;
    private string _thumbText = "";
    private bool _thumbsPaused;
    private bool _thumbsRunning;
    private string _importText = "";
    private string _activityText = "";
    private readonly Stopwatch _lastThumbStatus = Stopwatch.StartNew();
    private bool _thumbStatusQueued;

    public LibraryViewModel(LibraryDatabase db, LibraryLocation location, ThumbnailQueue queue, ILibraryShell shell, IUiDispatcher ui, ModelSource? source = null, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _db = db;
        _location = location;
        _queue = queue;
        _shell = shell;
        _ui = ui;
        _source = source ?? ModelSource.Default;

        AddFolderCommand = new RelayCommand(() => _ = AddFolderAsync());
        RescanAllCommand = new RelayCommand(() => _ = RescanAsync(null), () => !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCancel?.Cancel(), () => IsScanning);
        PauseThumbnailsCommand = new RelayCommand(_queue.Pause);
        ResumeThumbnailsCommand = new RelayCommand(_queue.Resume);
        RetryFailedCommand = new RelayCommand(RetryFailed);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        ToggleDirectionCommand = new RelayCommand(() => Descending = !Descending);
        OpenDataFolderCommand = new RelayCommand(() => _shell.Reveal(_location.DataFolder, selectFile: false));
        ShowLogCommand = new RelayCommand(ShowLog);

        _queue.JobDone += done => _ui.Post(() => OnThumbnailDone(done));
        _queue.Progress += _ => QueueThumbnailStatus();
        _queue.Idle += QueueThumbnailStatus;
    }

    /* ---------- start-up ---------- */

    /// <summary>The search box waits this long after the last key before searching.</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Loads the saved settings, the tree and the list. Call once, on the UI thread.</summary>
    public void Initialize()
    {
        _loading = true;
        try
        {
            _sort = FilterOptions.Sorts.FirstOrDefault(s => s.Value.ToString() == _db.GetSetting("ui.sort")) ?? FilterOptions.Sorts[0];
            _descending = _db.GetSetting("ui.descending") == "1";
            _includeSubfolders = _db.GetSetting("ui.subfolders", "1") == "1";
            _showMissing = _db.GetSetting("ui.missing") == "1";
            _queue.Speed = WorkLimits.Parse(_db.GetSetting(ThumbnailSpeedSetting));
            if (double.TryParse(_db.GetSetting("ui.tileSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
                _tileSize = Math.Clamp(size, MinTileSize, MaxTileSize);
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(string.Empty);   // every binding reads again
        RefreshAll();
        UpdateThumbnailStatus();
    }

    /// <summary>Starts drawing thumbnails (after any import of the Electron library has finished).</summary>
    public void StartThumbnails() => _queue.Start();

    /* ---------- commands ---------- */

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RescanAllCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand PauseThumbnailsCommand { get; }
    public RelayCommand ResumeThumbnailsCommand { get; }
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ToggleDirectionCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ShowLogCommand { get; }

    /// <summary>New versions of the app (set by the app; the status bar and the Preferences menu show it).</summary>
    public UpdateViewModel? Updates { get; set; }

    /// <summary>Preferences → Show log file: writes what is waiting, then opens the file.</summary>
    public void ShowLog()
    {
        _log.Flush();
        if (_log.Path is { } path && File.Exists(path)) _shell.OpenFile(path);
        else _shell.ShowMessage("Log file", "There is no log file yet.");
    }

    /* ---------- the filter bar ---------- */

    public IReadOnlyList<SortOption> SortOptions => FilterOptions.Sorts;
    public IReadOnlyList<SizeFilterOption> SizeOptions => FilterOptions.Sizes;
    public IReadOnlyList<AgeFilterOption> AgeOptions => FilterOptions.Ages;
    public IReadOnlyList<AgeBasisOption> AgeBasisOptions => FilterOptions.AgeBases;
    public IReadOnlyList<RatingFilterOption> RatingOptions => FilterOptions.Ratings;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            _searchDelay?.Cancel();
            if (SearchDelay <= TimeSpan.Zero)
            {
                Reload();
                return;
            }
            var delay = _searchDelay = new CancellationTokenSource();
            _ = Task.Delay(SearchDelay, delay.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) _ui.Post(() => { if (!delay.IsCancellationRequested) Reload(); });
            }, TaskScheduler.Default);
        }
    }

    public SortOption Sort
    {
        get => _sort;
        set { if (value is not null && Set(ref _sort, value)) { Save("ui.sort", value.Value.ToString()); Reload(); } }
    }

    /// <summary>Reverses the order's natural direction (newest first, largest first, A to Z...).</summary>
    public bool Descending
    {
        get => _descending;
        set
        {
            if (!Set(ref _descending, value)) return;
            OnPropertyChanged(nameof(DirectionGlyph));
            Save("ui.descending", value ? "1" : "0");
            Reload();
        }
    }

    public string DirectionGlyph => _descending ? "⇅ reversed" : "⇅";

    public SizeFilterOption SizeFilter
    {
        get => _sizeFilter;
        set { if (value is not null && Set(ref _sizeFilter, value)) FiltersChanged(); }
    }

    public AgeFilterOption AgeFilter
    {
        get => _ageFilter;
        set { if (value is not null && Set(ref _ageFilter, value)) FiltersChanged(); }
    }

    public AgeBasisOption AgeBasis
    {
        get => _ageBasis;
        set { if (value is not null && Set(ref _ageBasis, value)) FiltersChanged(); }
    }

    public RatingFilterOption RatingFilter
    {
        get => _ratingFilter;
        set { if (value is not null && Set(ref _ratingFilter, value)) FiltersChanged(); }
    }

    public bool FiltersActive => _sizeFilter.Min is not null || _sizeFilter.Max is not null
        || _ageFilter.NewerThan is not null || _ageFilter.OlderThan is not null || _ratingFilter.Value != Library.RatingFilter.Any;

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(FiltersActive));
        Reload();
    }

    public void ClearFilters()
    {
        _sizeFilter = FilterOptions.Sizes[0];
        _ageFilter = FilterOptions.Ages[0];
        _ratingFilter = FilterOptions.Ratings[0];
        OnPropertyChanged(nameof(SizeFilter));
        OnPropertyChanged(nameof(AgeFilter));
        OnPropertyChanged(nameof(RatingFilter));
        FiltersChanged();
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { if (Set(ref _includeSubfolders, value)) { Save("ui.subfolders", value ? "1" : "0"); Reload(); } }
    }

    public bool ShowMissing
    {
        get => _showMissing;
        set { if (Set(ref _showMissing, value)) { Save("ui.missing", value ? "1" : "0"); Reload(); } }
    }

    public double TileSize
    {
        get => _tileSize;
        set
        {
            value = Math.Clamp(value, MinTileSize, MaxTileSize);
            if (!Set(ref _tileSize, value)) return;
            OnPropertyChanged(nameof(ImageSize));
            Save("ui.tileSize", value.ToString(CultureInfo.InvariantCulture));
            Regroup();
        }
    }

    /// <summary>The square the image is drawn in, inside a tile.</summary>
    public double ImageSize => _tileSize - 16;   // the tile less its padding (6 + 6) and border (2 + 2)

    /// <summary>The view reports the width it has for tiles; the number of columns follows from it.</summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 1) return;
        _viewportWidth = width;
        Regroup();
    }

    private void Save(string key, string value)
    {
        if (!_loading) _db.SetSetting(key, value);
    }

    /* ---------- the folder tree ---------- */

    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    public bool HasRoots
    {
        get => _hasRoots;
        private set => Set(ref _hasRoots, value);
    }

    /// <summary>The folder whose files are shown, or null for all files.</summary>
    public string? SelectedFolder
    {
        get => _selectedFolder;
        private set { if (Set(ref _selectedFolder, value)) Reload(); }
    }

    private void LoadTree()
    {
        var expanded = Folders.SelectMany(f => f.SelfAndDescendants()).Where(f => f.IsExpanded && f.Path is not null).Select(f => f.Path!).ToHashSet();
        var stats = _db.GetStats();
        var tree = _db.GetFolderTree();
        HasRoots = tree.Count > 0;

        Folders.Clear();
        var all = new FolderNodeViewModel("All files", null, (int)stats.Files, isRoot: false, OnFolderSelected);
        Folders.Add(all);
        FolderNodeViewModel Map(FolderNode node, bool isRoot)
        {
            var vm = new FolderNodeViewModel(node.Name, node.Path, node.Count, isRoot, OnFolderSelected)
            {
                IsExpanded = isRoot && expanded.Count == 0 || expanded.Contains(node.Path),
            };
            foreach (var child in node.Children) vm.Children.Add(Map(child, false));
            return vm;
        }
        foreach (var root in tree) Folders.Add(Map(root, true));

        // Select the same folder again without reloading (the caller reloads).
        var selected = Folders.SelectMany(f => f.SelfAndDescendants()).FirstOrDefault(f => f.Path == _selectedFolder) ?? all;
        if (selected.Path != _selectedFolder) _selectedFolder = selected.Path;
        _loading = true;
        try { selected.IsSelected = true; }
        finally { _loading = false; }
        OnPropertyChanged(nameof(SelectedFolder));
    }

    private void OnFolderSelected(FolderNodeViewModel node)
    {
        if (_loading) return;
        // One folder at a time (the tree view does this too, but the view model should not depend on it).
        _loading = true;
        try
        {
            foreach (var other in Folders.SelectMany(f => f.SelfAndDescendants()))
                if (!ReferenceEquals(other, node)) other.IsSelected = false;
        }
        finally
        {
            _loading = false;
        }
        SelectedFolder = node.Path;
    }

    /* ---------- the grid ---------- */

    /// <summary>The tiles in rows; the view virtualises this list.</summary>
    public IReadOnlyList<TileRowViewModel> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public IReadOnlyList<TileViewModel> Tiles => _tiles;

    public int Columns => _columns;

    /// <summary>True when nothing matches the folder, search and filters.</summary>
    public bool IsListEmpty => _tiles.Count == 0;

    public long Total
    {
        get => _total;
        private set => Set(ref _total, value);
    }

    public string ListText => _tiles.Count < _total
        ? $"Showing the first {_tiles.Count:N0} of {_total:N0} files"
        : $"{_total:N0} file{(_total == 1 ? "" : "s")}";

    /// <summary>Raised when a tile should be scrolled into view (arrow keys).</summary>
    public event Action<TileViewModel>? ScrollRequested;

    internal ListQuery BuildQuery(DateTimeOffset now) => new()
    {
        Search = _searchText,
        Limit = MaxShown,
        Folder = _selectedFolder,
        Recursive = _includeSubfolders,
        IncludeMissing = _showMissing,
        Sort = _sort.Value,
        Ascending = _descending ? !NaturallyAscending(_sort.Value) : null,
        SizeMin = _sizeFilter.Min,
        SizeMax = _sizeFilter.Max,
        Since = _ageFilter.NewerThan is { } newer ? (now - newer).ToUnixTimeMilliseconds() : null,
        Before = _ageFilter.OlderThan is { } older ? (now - older).ToUnixTimeMilliseconds() : null,
        AgeBasis = _ageBasis.Value,
        Rating = _ratingFilter.Value,
    };

    private static bool NaturallyAscending(SortOrder sort) => sort is SortOrder.Name or SortOrder.Path;

    /// <summary>Reads the list again from the database. The selection is cleared, as in the Electron app.</summary>
    public void Reload()
    {
        if (_loading) return;
        var page = _db.List(BuildQuery(DateTimeOffset.Now));
        bool dated = _sort.Value is SortOrder.Modified or SortOrder.Added;
        _tiles = page.Rows.Select((row, i) => new TileViewModel(row, i, _location.ThumbnailPath(row.Sha256))
        {
            Caption = dated ? Format.Date(_sort.Value == SortOrder.Added ? row.AddedAt : row.ModifiedMs) : null,
        }).ToList();
        _bySha = _tiles.GroupBy(t => t.Row.Sha256).ToDictionary(g => g.Key, g => g.ToList());
        _anchor = -1;
        Total = page.Total;
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(ListText));
        OnPropertyChanged(nameof(IsListEmpty));
        Regroup();
        SelectionChanged();
    }

    private void Regroup()
    {
        int columns = Math.Max(1, (int)((_viewportWidth + TileSpacing) / (_tileSize + TileSpacing)));
        bool changed = columns != _columns;
        _columns = columns;
        if (changed) OnPropertyChanged(nameof(Columns));
        var rows = new List<TileRowViewModel>((_tiles.Count + columns - 1) / columns);
        for (int i = 0; i < _tiles.Count; i += columns)
            rows.Add(new TileRowViewModel(_tiles.GetRange(i, Math.Min(columns, _tiles.Count - i))));
        Rows = rows;
    }

    /* ---------- selection ---------- */

    public int SelectedCount
    {
        get => _selectedCount;
        private set => Set(ref _selectedCount, value);
    }

    public string SelectionText => _selectedCount == 0 ? "" : $"{_selectedCount:N0} selected";

    public IReadOnlyList<TileViewModel> SelectedTiles => _tiles.Where(t => t.IsSelected).ToList();

    private void SelectionChanged()
    {
        SelectedCount = _tiles.Count(t => t.IsSelected);
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>
    /// A click: selects the tile (or deselects it if it was the only one selected). Ctrl adds or removes one tile;
    /// Shift selects everything from the last clicked tile to this one.
    /// </summary>
    public void Click(TileViewModel tile, bool ctrl, bool shift)
    {
        if (shift && _anchor >= 0 && _anchor < _tiles.Count)
        {
            if (!ctrl) foreach (var t in _tiles) t.IsSelected = false;
            int from = Math.Min(_anchor, tile.Index), to = Math.Max(_anchor, tile.Index);
            for (int i = from; i <= to; i++) _tiles[i].IsSelected = true;
        }
        else if (ctrl)
        {
            tile.IsSelected = !tile.IsSelected;
            _anchor = tile.Index;
        }
        else
        {
            bool onlyThis = tile.IsSelected && _tiles.Count(t => t.IsSelected) == 1;
            foreach (var t in _tiles) t.IsSelected = false;
            tile.IsSelected = !onlyThis;
            _anchor = tile.Index;
        }
        SelectionChanged();
    }

    /// <summary>The round tick in a tile's corner: adds or removes that tile, like Ctrl-click.</summary>
    public void ToggleTick(TileViewModel tile) => Click(tile, ctrl: true, shift: false);

    /// <summary>Right-click: inside the selection acts on the selection; outside it selects just that tile first.</summary>
    public IReadOnlyList<TileViewModel> SelectionForMenu(TileViewModel tile)
    {
        if (!tile.IsSelected)
        {
            foreach (var t in _tiles) t.IsSelected = false;
            tile.IsSelected = true;
            _anchor = tile.Index;
            SelectionChanged();
        }
        return SelectedTiles;
    }

    public void SelectAll()
    {
        foreach (var t in _tiles) t.IsSelected = true;
        SelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var t in _tiles) t.IsSelected = false;
        SelectionChanged();
    }

    /// <summary>Arrow keys: moves the selection by columns and rows.</summary>
    public void Move(int dx, int dy)
    {
        if (_tiles.Count == 0) return;
        int from = _anchor >= 0 ? _anchor : -1;
        int to = from < 0 ? 0 : Math.Clamp(from + dx + dy * _columns, 0, _tiles.Count - 1);
        foreach (var t in _tiles) t.IsSelected = false;
        _tiles[to].IsSelected = true;
        _anchor = to;
        SelectionChanged();
        ScrollRequested?.Invoke(_tiles[to]);
    }

    /// <summary>The tile Enter opens: the last one clicked or moved to.</summary>
    public TileViewModel? Current => _anchor >= 0 && _anchor < _tiles.Count ? _tiles[_anchor] : null;

    /* ---------- ratings ---------- */

    /// <summary>Rates the selected models (0 to 5 stars, or null for unrated).</summary>
    public void Rate(int? stars) => Rate(SelectedTiles, stars);

    public void Rate(IReadOnlyList<TileViewModel> tiles, int? stars)
    {
        var changed = _db.SetRatings(tiles.Select(t => t.Row.Sha256), stars);
        _log.Info("ratings.set", ("models", changed.Count), ("stars", stars));
        foreach (var sha in changed) UpdateRows(sha, row => row with { Rating = stars });
    }

    private void UpdateRows(string sha, Func<LocationRow, LocationRow> change)
    {
        if (!_bySha.TryGetValue(sha, out var tiles)) return;
        foreach (var tile in tiles) tile.Row = change(tile.Row);
    }

    /* ---------- changes made in the viewer ---------- */

    /// <summary>The viewer saved a new thumbnail (and view) for a model.</summary>
    public void ApplyThumbnailReplaced(string sha)
    {
        UpdateRows(sha, row => row with { ThumbnailState = ThumbnailState.Ok, ThumbnailError = null, CustomView = true });
        if (_bySha.TryGetValue(sha, out var tiles)) foreach (var tile in tiles) tile.ImageVersion++;
        UpdateThumbnailStatus();
    }

    /// <summary>The viewer went back to the automatic view: the thumbnail is being drawn again.</summary>
    public void ApplyViewReverted(string sha)
    {
        UpdateRows(sha, row => row with { ThumbnailState = ThumbnailState.Pending, CustomView = false });
        UpdateThumbnailStatus();
    }

    public void ApplyRating(string sha, int? stars) => UpdateRows(sha, row => row with { Rating = stars });

    /* ---------- actions on the selection ---------- */

    /// <summary>Opens a model (double-click or Enter).</summary>
    public async Task OpenAsync(TileViewModel tile)
    {
        var location = _db.GetLocation(tile.Row.Path);
        if (location is null)
        {
            _shell.ShowMessage("Open", "That file is no longer in the library.");
            return;
        }
        try
        {
            byte[] bytes = await _source.ReadAsync(location);
            _shell.OpenModel(new ViewerDocument(tile.Name, tile.Row.Format, bytes, FromLibrary: true));
        }
        catch (ModelGoneException)
        {
            _log.Warn("open.gone", ("path", tile.Row.Path));
            _shell.ShowMessage("Open", "That file is no longer where the library found it. Rescan to update the library.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _log.Warn("open.failed", ("path", tile.Row.Path), ("message", e.Message));
            _shell.ShowMessage("Open", "Could not read the file: " + e.Message);
        }
    }

    /// <summary>The folder that holds the selection, if every selected model is in the same one (an archive counts as its folder).</summary>
    public static string? CommonFolder(IReadOnlyList<TileViewModel> tiles)
    {
        var folders = tiles.Select(t => Path.GetDirectoryName(t.Row.Container ?? t.Row.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return folders.Count == 1 ? folders[0] : null;
    }

    public void Reveal(IReadOnlyList<TileViewModel> tiles)
    {
        if (tiles.Count == 1)
        {
            _shell.Reveal(tiles[0].Row.Container ?? tiles[0].Row.Path, selectFile: true);
            return;
        }
        if (CommonFolder(tiles) is { } folder) _shell.Reveal(folder, selectFile: false);
    }

    public void RevealFolder(string folder) => _shell.Reveal(folder, selectFile: false);

    public void CopyPaths(IReadOnlyList<TileViewModel> tiles) =>
        _shell.CopyText(string.Join(Environment.NewLine, tiles.Select(t => t.Row.Path)));

    public void CopyHashes(IReadOnlyList<TileViewModel> tiles) =>
        _shell.CopyText(string.Join(Environment.NewLine, tiles.Select(t => t.Row.Sha256).Distinct()));

    /// <summary>Lists every copy of a model, wherever it is, by searching for its hash.</summary>
    public void FindCopies(TileViewModel tile)
    {
        string sha = tile.Row.Sha256;
        var all = Folders.FirstOrDefault();
        if (all is not null && !all.IsSelected)
        {
            _loading = true;
            try { all.IsSelected = true; }
            finally { _loading = false; }
            _selectedFolder = null;
            OnPropertyChanged(nameof(SelectedFolder));
        }
        _searchDelay?.Cancel();
        _searchText = sha;
        OnPropertyChanged(nameof(SearchText));
        Reload();
    }

    /// <summary>Draws the thumbnails again (keeping hand-made views), or with <paramref name="revert"/> forgets those views first.</summary>
    public void Redraw(IReadOnlyList<TileViewModel> tiles, bool revert)
    {
        foreach (var sha in tiles.Select(t => t.Row.Sha256).Distinct())
        {
            if (revert) _db.DeleteView(sha);
            _db.DeleteThumbnailRow(sha);
            _queue.Unskip(sha);
            UpdateRows(sha, row => row with { ThumbnailState = ThumbnailState.Pending, ThumbnailError = null, CustomView = !revert && row.CustomView });
        }
        _log.Info(revert ? "views.revert" : "thumbs.regenerate", ("models", tiles.Select(t => t.Row.Sha256).Distinct().Count()));
        _queue.Start();
        UpdateThumbnailStatus();
    }

    public async Task DownloadAsync(IReadOnlyList<TileViewModel> tiles)
    {
        string? folder = _shell.PickFolder(tiles.Count == 1 ? "Choose a folder to copy the file to" : $"Choose a folder to copy {tiles.Count:N0} files to");
        if (folder is null) return;
        var progress = new Progress<DownloadProgress>(p => ActivityText = p.Current is null ? "" : $"Copying {p.Done + 1:N0} of {p.Total:N0}: {Path.GetFileName(p.Current)}");
        _log.Info("download.start", ("files", tiles.Count), ("destination", folder));
        var started = Stopwatch.StartNew();
        var result = await Downloads.CopyAsync(tiles.Select(t => t.Row).ToList(), folder, _source, progress);
        ActivityText = "";
        foreach (var failure in result.Failed) _log.Warn("download.fail", ("path", failure.Path), ("message", failure.Message));
        _log.InfoOrWarn(result.Failed.Count > 0 || result.Changed > 0 || result.Cancelled, "download.end", ("destination", folder),
            ("duration_ms", started.ElapsedMilliseconds), ("copied", result.Copied), ("failed", result.Failed.Count), ("changed", result.Changed), ("cancelled", result.Cancelled));
        var lines = new List<string> { $"{result.Copied:N0} of {result.Total:N0} copied to {folder}." };
        if (result.Changed > 0) lines.Add($"{result.Changed:N0} had changed since the last scan (copied anyway).");
        if (result.Failed.Count > 0)
        {
            lines.Add($"{result.Failed.Count:N0} could not be copied:");
            lines.AddRange(result.Failed.Take(10).Select(f => $"  {Path.GetFileName(f.Path)}: {f.Message}"));
        }
        _shell.ShowMessage("Download", string.Join(Environment.NewLine, lines));
    }

    /* ---------- folders and scanning ---------- */

    public IReadOnlyList<RootInfo> Roots => _db.GetRoots();

    public const string ThumbnailSpeedSetting = "thumbs.speed";

    /// <summary>How hard the thumbnail queue works (Preferences → Thumbnail speed). Takes effect from the next model.</summary>
    public ThumbnailSpeed ThumbnailSpeed
    {
        get => _queue.Speed;
        set
        {
            if (_queue.Speed == value) return;
            _queue.Speed = value;
            _db.SetSetting(ThumbnailSpeedSetting, value.ToString().ToLowerInvariant());
            _log.Info("setting.changed", ("name", "thumbnail_speed"), ("value", value.ToString().ToLowerInvariant()));
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedFull));
            OnPropertyChanged(nameof(SpeedBalanced));
            OnPropertyChanged(nameof(SpeedBackground));
            UpdateThumbnailStatus();
        }
    }

    // One per menu item, so the menu shows a tick by the current speed (unticking one does nothing).
    public bool SpeedFull { get => ThumbnailSpeed == ThumbnailSpeed.Full; set => ChooseSpeed(ThumbnailSpeed.Full, value); }
    public bool SpeedBalanced { get => ThumbnailSpeed == ThumbnailSpeed.Balanced; set => ChooseSpeed(ThumbnailSpeed.Balanced, value); }
    public bool SpeedBackground { get => ThumbnailSpeed == ThumbnailSpeed.Background; set => ChooseSpeed(ThumbnailSpeed.Background, value); }

    private void ChooseSpeed(ThumbnailSpeed speed, bool chosen)
    {
        if (chosen) ThumbnailSpeed = speed;
        else OnPropertyChanged(speed switch
        {
            ThumbnailSpeed.Balanced => nameof(SpeedBalanced),
            ThumbnailSpeed.Background => nameof(SpeedBackground),
            _ => nameof(SpeedFull),
        });   // keep the tick
    }

    public bool SearchInsideArchives
    {
        get => _db.GetSetting("scanZips", "1") == "1";
        set
        {
            _db.SetSetting("scanZips", value ? "1" : "0");
            _log.Info("setting.changed", ("name", "search_inside_archives"), ("value", value));
            OnPropertyChanged();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            RescanAllCommand.Refresh();
            CancelScanCommand.Refresh();
        }
    }

    public string ScanText
    {
        get => _scanText;
        private set => Set(ref _scanText, value);
    }

    /// <summary>Asks for a folder, adds it, and scans it straight away.</summary>
    public async Task AddFolderAsync()
    {
        string? folder = _shell.PickFolder("Choose a folder of 3D models");
        if (folder is null) return;
        await AddFoldersAsync([folder]);
    }

    /// <summary>Adds folders (from the dialog, or dropped on the window) and scans the new ones.</summary>
    public async Task AddFoldersAsync(IEnumerable<string> folders)
    {
        var added = new List<string>();
        foreach (var folder in folders.Select(Path.GetFullPath))
        {
            if (!Directory.Exists(folder) || _db.HasRoot(folder)) continue;
            _db.AddRoot(folder);
            _log.Info("folder.added", ("root", folder));
            added.Add(folder);
        }
        if (added.Count == 0) return;
        RefreshAll();
        await ScanAsync(added);
    }

    public void RemoveFolder(string root)
    {
        if (IsScanning)
        {
            _shell.ShowMessage("Remove folder", "Wait for the running scan to finish (or cancel it) first.");
            return;
        }
        long files = _db.GetRoots().FirstOrDefault(r => r.Path == root)?.Files ?? 0;
        var choice = _shell.ConfirmRemoveFolder(root, files);
        if (choice == RemoveChoice.Cancel) return;
        var result = _db.RemoveRoot(root, purge: choice == RemoveChoice.Purge);
        _log.Info("folder.removed", ("root", root), ("purge", choice == RemoveChoice.Purge), ("files", result.RemovedLocations), ("models_purged", result.PurgedShas.Count));
        foreach (var sha in result.PurgedShas)
        {
            try { File.Delete(_location.ThumbnailPath(sha)); } catch (IOException) { }
        }
        if (_selectedFolder is not null && (_selectedFolder == root || _selectedFolder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            _selectedFolder = null;
        RefreshAll();
    }

    /// <summary>Rescans one folder of the library, or every folder (null).</summary>
    public Task RescanAsync(string? root)
    {
        if (root is null) return ScanAsync(_db.GetRoots().Select(r => r.Path).ToList());
        // A folder inside a library folder: scan the library folder it belongs to.
        var owner = _db.GetRoots().Select(r => r.Path)
            .FirstOrDefault(r => root == r || root.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        return owner is null ? Task.CompletedTask : ScanAsync([owner]);
    }

    private async Task ScanAsync(IReadOnlyList<string> roots)
    {
        if (IsScanning || roots.Count == 0) return;
        IsScanning = true;
        var cancel = _scanCancel = new CancellationTokenSource();
        var scanner = new LibraryScanner(_db, _source) { Log = _log };
        var scanClock = Stopwatch.StartNew();
        if (roots.Count > 1) _log.Info("rescan.start", ("folders", roots.Count));
        var summaries = new List<ScanSummary>();
        var throttle = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < roots.Count && !cancel.IsCancellationRequested; i++)
            {
                string prefix = roots.Count > 1 ? $"Scanning {i + 1} of {roots.Count}: " : "Scanning: ";
                ScanText = prefix + roots[i];
                try
                {
                    summaries.Add(await scanner.ScanAsync(roots[i], SearchInsideArchives, p =>
                    {
                        if (throttle.ElapsedMilliseconds < 150) return;
                        throttle.Restart();
                        _ui.Post(() => ScanText = $"{prefix}{p.Found:N0} found, {p.Hashed:N0} read, {p.Reused:N0} unchanged");
                    }, cancel.Token));
                }
                catch (DirectoryNotFoundException e)
                {
                    _log.Error("scan.failed", ("root", roots[i]), ("message", e.Message));
                    summaries.Add(new ScanSummary { Root = roots[i], Errors = 1, Problems = [new ScanProblem(roots[i], e.Message)] });
                }
            }
        }
        finally
        {
            IsScanning = false;
            _scanCancel = null;
        }

        if (cancel.IsCancellationRequested) _log.Warn("scan.cancel_requested");
        if (roots.Count > 1)
            _log.InfoOrWarn(cancel.IsCancellationRequested || summaries.Any(s => s.Errors > 0), "rescan.end", ("duration_ms", scanClock.ElapsedMilliseconds),
                ("folders", roots.Count), ("cancelled", cancel.IsCancellationRequested));
        ScanText = Describe(summaries, cancel.IsCancellationRequested);
        RefreshAll();
        _queue.Start();   // thumbnails for anything new
    }

    internal static string Describe(IReadOnlyList<ScanSummary> summaries, bool cancelled)
    {
        if (summaries.Count == 0) return cancelled ? "Scan cancelled." : "";
        long added = summaries.Sum(s => s.Added), moved = summaries.Sum(s => s.Moved), missing = summaries.Sum(s => s.Missing);
        int found = summaries.Sum(s => s.Found), errors = summaries.Sum(s => s.Errors);
        var parts = new List<string> { $"{(cancelled ? "Scan cancelled" : "Scan finished")}: {found:N0} files" };
        if (added > 0) parts.Add($"{added:N0} new");
        if (moved > 0) parts.Add($"{moved:N0} moved");
        if (missing > 0) parts.Add($"{missing:N0} missing");
        if (errors > 0) parts.Add($"{errors:N0} problem{(errors == 1 ? "" : "s")}" +
                                  (summaries.SelectMany(s => s.Problems).FirstOrDefault() is { } first ? $" (first: {Path.GetFileName(first.Path)}: {first.Message})" : ""));
        if (summaries.Any(s => s.MissingSkipped)) parts.Add("nothing marked missing because a folder could not be read");
        return string.Join(", ", parts);
    }

    /// <summary>Reads the tree, the list and the counts again (after a scan, or adding or removing a folder).</summary>
    public void RefreshAll()
    {
        LoadTree();
        var stats = _db.GetStats();
        StatsText = $"{stats.Files:N0} files · {stats.UniqueModels:N0} models" +
                    (stats.DuplicateGroups > 0 ? $" · {stats.DuplicateGroups:N0} with copies" : "") +
                    (stats.Missing > 0 ? $" · {stats.Missing:N0} missing" : "");
        OnPropertyChanged(nameof(Roots));
        Reload();
    }

    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    /// <summary>Short-lived work in progress (copying files, importing the Electron library).</summary>
    public string ActivityText
    {
        get => _activityText;
        set => Set(ref _activityText, value);
    }

    public string ImportText
    {
        get => _importText;
        set => Set(ref _importText, value);
    }

    /* ---------- thumbnails ---------- */

    public string ThumbnailText
    {
        get => _thumbText;
        private set => Set(ref _thumbText, value);
    }

    public bool ThumbnailsPaused
    {
        get => _thumbsPaused;
        private set => Set(ref _thumbsPaused, value);
    }

    public bool ThumbnailsRunning
    {
        get => _thumbsRunning;
        private set => Set(ref _thumbsRunning, value);
    }

    /// <summary>Pause is offered while drawing; Resume while paused or stopped by a renderer problem.</summary>
    public bool CanPauseThumbnails => _thumbsRunning && !_thumbsPaused;

    public bool CanResumeThumbnails => _thumbsPaused;

    private long _failedCount;

    public bool HasFailedThumbnails => _failedCount > 0;

    public void RetryFailed()
    {
        _db.ResetFailedThumbnails();
        foreach (var tile in _tiles.Where(t => t.Row.ThumbnailState == ThumbnailState.Failed).ToList())
            tile.Row = tile.Row with { ThumbnailState = ThumbnailState.Pending, ThumbnailError = null };
        _queue.Start();
        UpdateThumbnailStatus();
    }

    private void OnThumbnailDone(ThumbnailJobDone done)
    {
        if (!_bySha.TryGetValue(done.Sha256, out var tiles)) return;
        foreach (var tile in tiles)
        {
            tile.Row = tile.Row with { ThumbnailState = done.State, ThumbnailError = done.Error, ThumbnailCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (done.State == ThumbnailState.Ok) tile.ImageVersion++;
        }
    }

    /// <summary>Progress arrives many times a second from the workers; the status bar is updated at most every 200 ms.</summary>
    private void QueueThumbnailStatus()
    {
        lock (_lastThumbStatus)
        {
            if (_thumbStatusQueued) return;
            _thumbStatusQueued = true;
        }
        int wait = Math.Max(0, 200 - (int)_lastThumbStatus.ElapsedMilliseconds);
        _ = Task.Delay(wait).ContinueWith(_ => _ui.Post(() =>
        {
            lock (_lastThumbStatus)
            {
                _thumbStatusQueued = false;
                _lastThumbStatus.Restart();
            }
            UpdateThumbnailStatus();
        }), TaskScheduler.Default);
    }

    private void UpdateThumbnailStatus()
    {
        var s = _queue.GetStatus();
        ThumbnailsRunning = s.Running;
        ThumbnailsPaused = s.Paused || s.Blocked is not null;
        OnPropertyChanged(nameof(CanPauseThumbnails));
        OnPropertyChanged(nameof(CanResumeThumbnails));
        _failedCount = s.Counts.Failed;
        OnPropertyChanged(nameof(HasFailedThumbnails));
        long all = s.Counts.Ok + s.Counts.Pending + s.Counts.Failed;
        string failed = s.Counts.Failed > 0 ? $" · {s.Counts.Failed:N0} failed" : "";
        ThumbnailText = s switch
        {
            { Blocked: { } blocked } => $"Thumbnails stopped: {blocked} Resume to try again.",
            { Paused: true } => $"Thumbnails paused · {s.Counts.Ok:N0} of {all:N0} drawn{failed}",
            { Running: true } => $"Drawing thumbnails{SpeedNote} · {s.Counts.Ok:N0} of {all:N0}{failed}" + (s.Current is { } c ? $" · {c}" : ""),
            _ when s.Counts.Pending > 0 => $"Thumbnails: {s.Counts.Ok:N0} of {all:N0} drawn{failed}",
            _ when all > 0 => $"Thumbnails: all {s.Counts.Ok:N0} drawn{failed}",
            _ => "",
        };
    }

    private string SpeedNote => _queue.Speed switch
    {
        ThumbnailSpeed.Balanced => " (balanced)",
        ThumbnailSpeed.Background => " (in the background)",
        _ => "",
    };

    public async ValueTask DisposeAsync()
    {
        _scanCancel?.Cancel();
        _searchDelay?.Cancel();
        await _queue.DisposeAsync();
    }
}
