using Xandrite.Library;
using Xandrite.Library.Logging;

namespace Xandrite.Presentation;

/// <summary>Installing new versions of the app (Velopack, from GitHub releases). Kept behind an interface so the view model can be tested.</summary>
public interface IAppUpdates
{
    /// <summary>False when the app runs from a build folder rather than an installation: there is nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>The version that is running, such as "0.4.0".</summary>
    string CurrentVersion { get; }

    /// <summary>The newer version on offer, or null when this one is the latest.</summary>
    Task<string?> CheckAsync(CancellationToken cancellation);

    /// <summary>Downloads the version found by <see cref="CheckAsync"/>; progress is 0–100 and may arrive on any thread.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellation);

    /// <summary>A version downloaded earlier (and not yet installed), or null.</summary>
    string? PendingVersion { get; }

    /// <summary>Closes the app normally, installs the downloaded version, and starts it again.</summary>
    void RestartToUpdate();
}

/// <summary>
/// Checks for a new version a little after start-up (unless turned off), downloads it in the background, then offers
/// "Restart to update" in the status bar. A downloaded update that is not restarted for is installed the next time the
/// app starts.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    public const string AutoCheckSetting = "updates.auto";

    private readonly IAppUpdates _updates;
    private readonly LibraryDatabase _db;
    private readonly ILibraryShell _shell;
    private readonly IUiDispatcher _ui;
    private readonly ILog _log;
    private readonly CancellationTokenSource _closing = new();
    private string _text = "";
    private string? _readyVersion;
    private bool _busy;

    public UpdateViewModel(IAppUpdates updates, LibraryDatabase db, ILibraryShell shell, IUiDispatcher ui, ILog? log = null)
    {
        _updates = updates;
        _db = db;
        _shell = shell;
        _ui = ui;
        _log = log ?? NullLog.Instance;
        CheckNowCommand = new RelayCommand(() => _ = CheckAsync(manual: true), () => !_busy && _readyVersion is null);
        RestartCommand = new RelayCommand(Restart, () => _readyVersion is not null);
        AboutCommand = new RelayCommand(About);

        if (_updates.PendingVersion is { } pending) Ready(pending);
    }

    public RelayCommand CheckNowCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand AboutCommand { get; }

    public string CurrentVersion => _updates.CurrentVersion;

    /// <summary>What the status bar says about updates; empty most of the time.</summary>
    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    public bool IsReady => _readyVersion is not null;

    public bool CheckAutomatically
    {
        get => _db.GetSetting(AutoCheckSetting, "1") == "1";
        set
        {
            _db.SetSetting(AutoCheckSetting, value ? "1" : "0");
            _log.Info("setting.changed", ("name", "check_for_updates"), ("value", value));
            OnPropertyChanged();
        }
    }

    /// <summary>Called once the library window is up: checks after a pause, so start-up is not slowed down.</summary>
    public async Task StartAsync(TimeSpan delay)
    {
        if (!_updates.IsInstalled || !CheckAutomatically || _readyVersion is not null) return;
        try
        {
            await Task.Delay(delay, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await CheckAsync(manual: false);
    }

    /// <summary>Looks for a newer version and downloads it. Problems are only shown when the person asked.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy || _readyVersion is not null) return;
        if (!_updates.IsInstalled)
        {
            if (manual)
                _shell.ShowMessage("Check for updates",
                    "This copy of Xandrite was not installed with Setup (it was probably built from source), so it cannot update itself.");
            return;
        }

        SetBusy(true);
        string? version = null;
        try
        {
            if (manual) Text = "Checking for updates…";
            _log.Info("update.check", ("current", _updates.CurrentVersion), ("manual", manual));
            version = await _updates.CheckAsync(_closing.Token);
            if (version is null)
            {
                _log.Info("update.none");
                Text = "";
                if (manual) _shell.ShowMessage("Check for updates", $"Xandrite {_updates.CurrentVersion} is the latest version.");
                return;
            }

            _log.Info("update.available", ("version", version));
            Text = $"Downloading Xandrite {version}…";
            await _updates.DownloadAsync(p => _ui.Post(() => Text = $"Downloading Xandrite {version}: {p}%"), _closing.Token);
            _log.Info("update.downloaded", ("version", version));
            Ready(version);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // closing
        }
        catch (Exception e)
        {
            // Offline, GitHub unavailable, a broken download: try again next time.
            _log.Warn("update.failed", ("version", version), ("message", e.Message));
            Text = "";
            if (manual) _shell.ShowMessage("Check for updates", "Could not check for updates:\n\n" + e.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Ready(string version)
    {
        _readyVersion = version;
        Text = $"Xandrite {version} is ready.";
        OnPropertyChanged(nameof(IsReady));
        RestartCommand.Refresh();
        CheckNowCommand.Refresh();
    }

    private void Restart()
    {
        if (_readyVersion is null) return;
        _log.Info("update.restart", ("from", _updates.CurrentVersion), ("to", _readyVersion));
        _log.Flush();
        _updates.RestartToUpdate();
    }

    private void About()
    {
        string updates = !_updates.IsInstalled
            ? "This copy was not installed with Setup, so it does not update itself."
            : _readyVersion is { } ready
                ? $"Version {ready} has been downloaded and will be installed when Xandrite restarts."
                : CheckAutomatically ? "Checks for updates automatically." : "Automatic update checks are turned off.";
        _shell.ShowAbout(new AboutInfo(_updates.CurrentVersion,
            "Browse 3D models (STL, 3MF), including those inside archives, by thumbnail.", updates, "https://github.com/amwales-888/xandrite"));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckNowCommand.Refresh();
    }

    /// <summary>Stops a check or download in progress (the app is closing).</summary>
    public void Close() => _closing.Cancel();
}
