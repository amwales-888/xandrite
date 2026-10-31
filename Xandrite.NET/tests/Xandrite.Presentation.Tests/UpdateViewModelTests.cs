using Xandrite.Library;
using Xandrite.Library.Logging;
using Xandrite.Presentation;
using static Xandrite.Presentation.Tests.LibraryViewModelTests;

namespace Xandrite.Presentation.Tests;

public sealed class UpdateViewModelTests : IDisposable
{
    private sealed class FakeUpdates : IAppUpdates
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "0.3.0";
        public string? Available;
        public string? PendingVersion { get; set; }
        public Exception? CheckFails;
        public int Checks, Downloads, Restarts;

        public Task<string?> CheckAsync(CancellationToken cancellation)
        {
            Checks++;
            return CheckFails is { } e ? Task.FromException<string?>(e) : Task.FromResult(Available);
        }

        public Task DownloadAsync(Action<int> progress, CancellationToken cancellation)
        {
            Downloads++;
            progress(50);
            progress(100);
            return Task.CompletedTask;
        }

        public void RestartToUpdate() => Restarts++;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xandrite-updates-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly FakeShell _shell = new();
    private readonly FakeUpdates _updates = new();
    private readonly FileLog _log;

    public UpdateViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _log = new FileLog(Path.Combine(_dir, "x.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private UpdateViewModel Make() => new(_updates, _db, _shell, new ImmediateDispatcher(), _log);

    private string LogText()
    {
        _log.Flush();
        return File.ReadAllText(_log.Path!);
    }

    [Fact]
    public async Task ANewVersionIsDownloadedThenOfferedAsARestart()
    {
        _updates.Available = "0.4.0";
        var vm = Make();
        Assert.False(vm.RestartCommand.CanExecute(null));

        await vm.StartAsync(TimeSpan.Zero);

        Assert.Equal(1, _updates.Downloads);
        Assert.True(vm.IsReady);
        Assert.Equal("Xandrite 0.4.0 is ready.", vm.Text);
        Assert.True(vm.RestartCommand.CanExecute(null));
        Assert.False(vm.CheckNowCommand.CanExecute(null));   // nothing more to find until it restarts
        Assert.Empty(_shell.Messages);                         // automatic checks do not interrupt

        vm.RestartCommand.Execute(null);
        Assert.Equal(1, _updates.Restarts);
        string log = LogText();
        Assert.Contains("update.available version=0.4.0", log);
        Assert.Contains("update.downloaded version=0.4.0", log);
        Assert.Contains("update.restart from=0.3.0 to=0.4.0", log);
    }

    [Fact]
    public async Task NothingNewSaysSoOnlyWhenAsked()
    {
        var vm = Make();
        await vm.StartAsync(TimeSpan.Zero);
        Assert.Equal(1, _updates.Checks);
        Assert.Empty(_shell.Messages);
        Assert.Equal("", vm.Text);

        await vm.CheckAsync(manual: true);
        Assert.Equal("Xandrite 0.3.0 is the latest version.", Assert.Single(_shell.Messages));
        Assert.False(vm.IsReady);
    }

    [Fact]
    public async Task ProblemsAreLoggedAndShownOnlyWhenAsked()
    {
        _updates.CheckFails = new HttpRequestException("No such host is known.");
        var vm = Make();
        await vm.StartAsync(TimeSpan.Zero);
        Assert.Empty(_shell.Messages);
        Assert.Equal("", vm.Text);
        Assert.True(vm.CheckNowCommand.CanExecute(null));   // can try again

        await vm.CheckAsync(manual: true);
        Assert.Contains("No such host is known.", Assert.Single(_shell.Messages));
        Assert.Contains("WARN  update.failed", LogText());
    }

    [Fact]
    public async Task TheAutomaticCheckCanBeTurnedOff()
    {
        _updates.Available = "0.4.0";
        var vm = Make();
        vm.CheckAutomatically = false;
        Assert.Equal("0", _db.GetSetting(UpdateViewModel.AutoCheckSetting));
        await Make().StartAsync(TimeSpan.Zero);
        Assert.Equal(0, _updates.Checks);

        await vm.CheckAsync(manual: true);   // asking still works
        Assert.True(vm.IsReady);
    }

    [Fact]
    public async Task ACopyThatWasNotInstalledNeverChecks()
    {
        _updates.IsInstalled = false;
        var vm = Make();
        await vm.StartAsync(TimeSpan.Zero);
        Assert.Equal(0, _updates.Checks);
        await vm.CheckAsync(manual: true);
        Assert.Equal(0, _updates.Checks);
        Assert.Contains("cannot update itself", Assert.Single(_shell.Messages));
    }

    [Fact]
    public void AnUpdateDownloadedLastTimeIsOfferedStraightAway()
    {
        _updates.PendingVersion = "0.4.0";
        var vm = Make();
        Assert.True(vm.IsReady);
        Assert.True(vm.RestartCommand.CanExecute(null));
        vm.AboutCommand.Execute(null);
        Assert.Empty(_shell.Messages);
        Assert.Equal("0.3.0", _shell.About!.Version);
        Assert.Contains("0.4.0 has been downloaded", _shell.About.Updates);
        Assert.Equal("https://github.com/amwales-888/xandrite", _shell.About.Link);
    }

    [Fact]
    public async Task ClosingStopsAWaitingCheck()
    {
        _updates.Available = "0.4.0";
        var vm = Make();
        var start = vm.StartAsync(TimeSpan.FromMinutes(5));
        vm.Close();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _updates.Checks);
    }
}
