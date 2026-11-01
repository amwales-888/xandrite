using System.IO;
using System.Reflection;
using System.Windows;
using Xandrite.Library;
using Xandrite.Library.Logging;
using Xandrite.Library.Sqlite;
using Xandrite.Library.SevenZip;
using Xandrite.Library.Thumbnails;
using Xandrite.Presentation;
using Xandrite.Rendering.Direct3D11;

namespace Xandrite.App;

/// <summary>
/// Start-up: one copy of the app at a time; the library in %APPDATA%\Xandrite.NET (brought across from the Electron
/// app the first time); the library window; then the thumbnail queue.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;

    /// <summary>Started with --developer: Preferences shows the Developer menu (extra logging).</summary>
    public static bool DeveloperMode { get; } =
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, "--developer", StringComparison.OrdinalIgnoreCase));
    private LibraryDatabase? _db;
    private LibraryViewModel? _library;
    private ViewerViewModel? _viewer;
    private UpdateViewModel? _updates;
    private ILog _log = NullLog.Instance;
    private ILog _statistics = NullLog.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "Xandrite.NET.Library", out bool first);
        if (!first)
        {
            // A second copy would fight over the database file.
            MessageBox.Show("Xandrite is already running.", "Xandrite", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var location = LibraryLocation.Default();
        try
        {
            _log = new FileLog(location.LogFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log = NullLog.Instance;   // no log is better than no app
        }
        try
        {
            // Small (a line per scan or thumbnail run), so 2 MB holds years of history.
            _statistics = new FileLog(location.StatisticsFile, maxBytes: 2 * 1024 * 1024, keep: 2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _statistics = NullLog.Instance;
        }
        WatchForCrashes();
        var updates = new VelopackUpdates();
        _log.Info("app.start",
            ("developer", DeveloperMode),
            ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)),
            ("installed", updates.IsInstalled),
            ("os", Environment.OSVersion.VersionString),
            ("dotnet", Environment.Version.ToString()),
            ("data", location.DataFolder),
            ("sqlite", SafeSqliteVersion()),
            ("sevenzip", SevenZipArchiveReader.IsAvailable ? "7z.dll" : "not available"));

        string? electron = ElectronImport.FindElectronDataFolder();
        try
        {
            if (ElectronImport.CopyDatabaseIfNew(location, electron)) _log.Info("data.imported", ("from", electron), ("database", location.DatabaseFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _log.Error("data.import_failed", ("from", electron), ("message", ex.Message));
            MessageBox.Show("The library of the Electron app could not be copied, so Xandrite starts with an empty library.\n\n" + ex.Message,
                "Xandrite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        try
        {
            _db = new LibraryDatabase(location.DatabaseFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or DllNotFoundException)
        {
            _log.Error("library.open_failed", ("database", location.DatabaseFile), ("message", ex.Message));
            _log.Flush();
            MessageBox.Show("The library could not be opened:\n\n" + ex.Message, "Xandrite", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A few workers draw at once, each with its own Direct3D renderer.
        int workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var queue = new ThumbnailQueue(_db, location, CreateRenderer, workers: workers) { Log = _log, Statistics = _statistics };

        var shell = new WpfShell();
        var dispatcher = new WpfDispatcher(Dispatcher);
        _library = new LibraryViewModel(_db, location, queue, shell, dispatcher, log: _log, statistics: _statistics) { DeveloperMode = DeveloperMode };

        // The viewer: what it changes (thumbnail, view, rating) shows in the library grid straight away.
        _viewer = new ViewerViewModel(_db, location, queue, CreateRenderer, shell, dispatcher, _log);
        _viewer.ThumbnailReplaced += _library.ApplyThumbnailReplaced;
        _viewer.ViewReverted += _library.ApplyViewReverted;
        _viewer.RatingChanged += _library.ApplyRating;
        shell.Viewer = _viewer;

        _updates = new UpdateViewModel(updates, _db, shell, dispatcher, _log);
        _library.Updates = _updates;

        var window = new LibraryWindow(_library);
        shell.Owner = window;
        MainWindow = window;
        window.Show();
        _library.Initialize();

        _ = FinishImportThenDrawAsync(location, electron);
        _ = _updates.StartAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Copies the Electron app's thumbnail images (if an import is under way), then starts drawing.</summary>
    private async Task FinishImportThenDrawAsync(LibraryLocation location, string? electron)
    {
        if (_db is null || _library is null) return;
        if (electron is not null && ElectronImport.ThumbnailsPending(_db))
        {
            var library = _library;
            var progress = new Progress<(int Done, int Total)>(p =>
                library.ImportText = $"Bringing thumbnails across from the Electron app: {p.Done:N0} of {p.Total:N0}");
            try
            {
                int copied = await DevLog.Run("import.thumbnails", () => ElectronImport.CopyThumbnailsAsync(_db, location, electron, progress));
                _log.Info("data.thumbnails_imported", ("from", electron), ("copied", copied));
                library.ImportText = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("data.thumbnails_import_failed", ("from", electron), ("message", ex.Message));
                library.ImportText = "Some thumbnails could not be copied from the Electron app: " + ex.Message;
            }
            library.RefreshAll();   // show the copied images
        }
        _library.StartThumbnails();
    }

    /// <summary>Makes a renderer for a thumbnail worker or the viewer, and records which graphics adapter it uses.</summary>
    private D3D11Renderer CreateRenderer()
    {
        var renderer = D3D11Renderer.Create();
        _log.Info("renderer.start", ("adapter", renderer.AdapterDescription), ("software", renderer.IsSoftware), ("msaa", renderer.SampleCount));
        return renderer;
    }

    private static string SafeSqliteVersion()
    {
        try { return SqliteConnection.Version; }
        catch (DllNotFoundException) { return "not found"; }
    }

    /// <summary>Anything that would crash the app (or that was silently lost) is written to the log first.</summary>
    private void WatchForCrashes()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log.Error("app.exception", ("where", "ui"), ("message", e.Exception.ToString()));
            _log.Flush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log.Error("app.exception", ("where", "background"), ("message", e.ExceptionObject.ToString()));
            _log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            _log.Error("app.rejection", ("message", e.Exception.ToString()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.Info("app.quit");
        _updates?.Close();
        _viewer?.Close();
        // Let the thumbnail workers finish the model in hand (a few seconds at most), then close the database.
        if (_library is not null)
        {
            var library = _library;
            Task.Run(() => library.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        }
        _db?.Dispose();
        (_statistics as IDisposable)?.Dispose();
        (_log as IDisposable)?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
