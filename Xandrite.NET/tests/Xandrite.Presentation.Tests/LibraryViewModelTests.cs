using Xandrite.Geometry;
using Xandrite.Library;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Presentation.Tests;

public class LibraryViewModelTests
{
    internal const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    internal sealed class FakeRenderer : IModelRenderer
    {
        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();
        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request) => new(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        public void Dispose() { }
        private sealed class FakeMesh : IGpuMesh { public int TriangleCount => 1; public void Dispose() { } }
    }

    internal sealed class FakeShell : ILibraryShell
    {
        public string? FolderToPick;
        public RemoveChoice RemoveAnswer = RemoveChoice.Keep;
        public readonly List<string> Messages = [];
        public string? Clipboard;
        public (string Path, bool SelectFile)? Revealed;
        public ViewerDocument? Opened;
        public string? SaveTo;
        public string? SuggestedName;
        public AboutInfo? About;

        public string? PickFolder(string title) => FolderToPick;
        public RemoveChoice ConfirmRemoveFolder(string folder, long files) => RemoveAnswer;
        public void ShowMessage(string title, string message) => Messages.Add(message);
        public void CopyText(string text) => Clipboard = text;
        public void Reveal(string path, bool selectFile) => Revealed = (path, selectFile);
        public string? PickSaveFile(string title, string suggestedName, string filter)
        {
            SuggestedName = suggestedName;
            return SaveTo;
        }

        public void ShowAbout(AboutInfo about) => About = about;
        public void OpenModel(ViewerDocument document) => Opened = document;
        public string? OpenedFile;
        public void OpenFile(string path) => OpenedFile = path;
    }

    /// <summary>A library on disk with a few models, and a view model over it.</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        public Fixture(int files = 0)
        {
            Dir = Path.Combine(Path.GetTempPath(), "xandrite-vm-" + Guid.NewGuid().ToString("N"));
            Models = Path.Combine(Dir, "models");
            Directory.CreateDirectory(Path.Combine(Models, "sub"));
            for (int i = 0; i < files; i++)
                File.WriteAllText(Path.Combine(Models, i % 2 == 0 ? "" : "sub", $"m{i:00}.stl"), Stl.Replace("solid t", "solid " + i));
            Location = new LibraryLocation(Path.Combine(Dir, "data"));
            Db = new LibraryDatabase(Location.DatabaseFile);
            Queue = new ThumbnailQueue(Db, Location, () => new FakeRenderer(), workers: 1);
            Vm = new LibraryViewModel(Db, Location, Queue, Shell, new ImmediateDispatcher()) { SearchDelay = TimeSpan.Zero };
            Vm.Initialize();
            Vm.SetViewportWidth(4 * (Vm.TileSize + LibraryViewModel.TileSpacing) - LibraryViewModel.TileSpacing);   // four columns
        }

        public string Dir { get; }
        public string Models { get; }
        public LibraryLocation Location { get; }
        public LibraryDatabase Db { get; }
        public ThumbnailQueue Queue { get; }
        public FakeShell Shell { get; } = new();
        public LibraryViewModel Vm { get; }

        public async Task AddModelsFolder()
        {
            Shell.FolderToPick = Models;
            await Vm.AddFolderAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync();
            Db.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task WithNoFoldersTheWelcomeScreenShows()
    {
        await using var f = new Fixture();
        Assert.False(f.Vm.HasRoots);
        Assert.Equal(0, f.Vm.Total);
        Assert.Single(f.Vm.Folders);   // just "All files"
    }

    [Fact]
    public async Task AddingAFolderScansItAndFillsTheTreeAndGrid()
    {
        await using var f = new Fixture(files: 10);
        await f.AddModelsFolder();
        Assert.True(f.Vm.HasRoots);
        Assert.Equal(10, f.Vm.Total);
        Assert.Equal(3, f.Vm.Rows.Count);                    // 10 tiles in rows of 4
        Assert.Equal(new[] { 4, 4, 2 }, f.Vm.Rows.Select(r => r.Tiles.Count));
        Assert.StartsWith("Scan finished: 10 files, 10 new", f.Vm.ScanText);
        var root = f.Vm.Folders[1];
        Assert.Equal(10, root.Count);
        Assert.True(root.IsExpanded);
        Assert.Equal("sub", Assert.Single(root.Children).Name);
    }

    [Fact]
    public async Task ChoosingAFolderAndSubfoldersNarrowsTheList()
    {
        await using var f = new Fixture(files: 10);
        await f.AddModelsFolder();
        f.Vm.Folders[1].IsSelected = true;
        Assert.Equal(10, f.Vm.Total);
        f.Vm.IncludeSubfolders = false;
        Assert.Equal(5, f.Vm.Total);
        f.Vm.Folders[1].Children[0].IsSelected = true;
        Assert.Equal(5, f.Vm.Total);
        f.Vm.Folders[0].IsSelected = true;     // All files
        Assert.Null(f.Vm.SelectedFolder);
    }

    [Fact]
    public async Task ClickCtrlClickShiftClickAndKeys()
    {
        await using var f = new Fixture(files: 10);
        await f.AddModelsFolder();
        var t = f.Vm.Tiles;

        f.Vm.Click(t[1], ctrl: false, shift: false);
        Assert.Equal(1, f.Vm.SelectedCount);
        f.Vm.Click(t[1], ctrl: false, shift: false);     // clicking the only selected tile deselects it
        Assert.Equal(0, f.Vm.SelectedCount);

        f.Vm.Click(t[2], false, false);
        f.Vm.Click(t[5], false, shift: true);            // 2..5
        Assert.Equal(new[] { 2, 3, 4, 5 }, f.Vm.SelectedTiles.Select(x => x.Index));
        f.Vm.Click(t[8], ctrl: true, shift: false);      // add one
        Assert.Equal(5, f.Vm.SelectedCount);
        f.Vm.Click(t[3], ctrl: true, shift: false);      // remove one
        Assert.Equal(4, f.Vm.SelectedCount);

        f.Vm.SelectAll();
        Assert.Equal(10, f.Vm.SelectedCount);
        f.Vm.ClearSelection();
        Assert.Equal(0, f.Vm.SelectedCount);

        f.Vm.Click(t[1], false, false);
        f.Vm.Move(0, 1);                                 // down one row of four
        Assert.Equal(5, f.Vm.Current!.Index);
        f.Vm.Move(1, 0);
        Assert.Equal(6, f.Vm.Current!.Index);
        f.Vm.Move(0, 5);                                 // clamps to the last tile
        Assert.Equal(9, f.Vm.Current!.Index);
        Assert.Equal(1, f.Vm.SelectedCount);
    }

    [Fact]
    public async Task RightClickOutsideTheSelectionSelectsJustThatTile()
    {
        await using var f = new Fixture(files: 6);
        await f.AddModelsFolder();
        var t = f.Vm.Tiles;
        f.Vm.Click(t[0], false, false);
        f.Vm.Click(t[1], true, false);
        Assert.Equal(2, f.Vm.SelectionForMenu(t[0]).Count);   // inside: the whole selection
        Assert.Equal(new[] { t[4] }, f.Vm.SelectionForMenu(t[4]));
    }

    [Fact]
    public async Task ChangingTheListClearsTheSelection()
    {
        await using var f = new Fixture(files: 6);
        await f.AddModelsFolder();
        f.Vm.SelectAll();
        f.Vm.SearchText = "m0";
        Assert.Equal(0, f.Vm.SelectedCount);
        Assert.Equal(6, f.Vm.Total);      // every name contains "m0"
        f.Vm.SearchText = "m03";
        Assert.Equal("m03.stl", Assert.Single(f.Vm.Tiles).Name);
    }

    [Fact]
    public async Task RatingsUpdateTheTilesAndTheFilter()
    {
        await using var f = new Fixture(files: 6);
        await f.AddModelsFolder();
        f.Vm.Click(f.Vm.Tiles[0], false, false);
        f.Vm.Click(f.Vm.Tiles[1], true, false);
        f.Vm.Rate(4);
        Assert.Equal("★★★★☆", f.Vm.Tiles[0].RatingText);
        f.Vm.RatingFilter = FilterOptions.Ratings.Single(r => r.Value == RatingFilter.AtLeast3);
        Assert.Equal(2, f.Vm.Total);
        Assert.True(f.Vm.FiltersActive);
        f.Vm.ClearFilters();
        Assert.Equal(6, f.Vm.Total);
        f.Vm.Rate([f.Vm.Tiles.First(t => t.RatingText != "")], null);
        Assert.Equal(1, f.Db.List(new ListQuery { Rating = RatingFilter.Rated }).Total);
    }

    [Fact]
    public async Task ActiveFiltersShowAsChipsThatEachTurnOneFilterOff()
    {
        await using var f = new Fixture(files: 6);
        await f.AddModelsFolder();
        Assert.Empty(f.Vm.ActiveFilters);
        Assert.False(f.Vm.ShowFilterChips);
        Assert.Equal(0, f.Vm.ActiveFilterCount);

        f.Vm.Click(f.Vm.Tiles[0], false, false);
        f.Vm.Rate(4);
        f.Vm.ClearSelection();
        f.Vm.RatingFilter = FilterOptions.Ratings.Single(r => r.Value == RatingFilter.AtLeast3);
        f.Vm.AgeByAdded = true;
        f.Vm.AgeFilter = FilterOptions.Ages.Single(a => a.Label == "Last 30 days");
        f.Vm.ShowMissing = true;

        Assert.Equal(new[] { "★★★ and up", "Added in the last 30 days", "Including missing files" }, f.Vm.ActiveFilters.Select(c => c.Label));
        Assert.Equal(3, f.Vm.ActiveFilterCount);
        Assert.True(f.Vm.ShowFilterChips);
        Assert.Equal("1 file match", f.Vm.MatchText);
        Assert.StartsWith("Remove the rating filter", f.Vm.ActiveFilters[0].RemoveLabel);

        f.Vm.SizeFilter = FilterOptions.Sizes.Single(z => z.Label == "10 – 50 MB");
        Assert.Contains("Size 10 – 50 MB", f.Vm.ActiveFilters.Select(c => c.Label));   // units keep their case
        f.Vm.SizeFilter = FilterOptions.Sizes.Single(z => z.Label == "Under 100 KB");
        Assert.Contains("Size under 100 KB", f.Vm.ActiveFilters.Select(c => c.Label));
        f.Vm.SizeFilter = FilterOptions.Sizes[0];

        f.Vm.ActiveFilters[0].Remove.Execute(null);                 // the rating chip's ×
        Assert.Equal(RatingFilter.Any, f.Vm.RatingFilter.Value);
        Assert.Equal(new[] { "Added in the last 30 days", "Including missing files" }, f.Vm.ActiveFilters.Select(c => c.Label));
        Assert.Equal("6 files match", f.Vm.MatchText);

        f.Vm.ClearAllFiltersCommand.Execute(null);
        Assert.Empty(f.Vm.ActiveFilters);
        Assert.False(f.Vm.ShowMissing);
        Assert.Equal("0", f.Db.GetSetting("ui.missing"));             // remembered as off
        Assert.True(f.Vm.AgeByAdded);                                 // which date to use is a choice, not a filter
    }

    [Fact]
    public async Task TheViewsFilterKeepsCustomOrDefaultViews()
    {
        await using var f = new Fixture(files: 4);
        await f.AddModelsFolder();
        string custom = f.Vm.Tiles[0].Row.Sha256;
        f.Db.SetView(custom, new SavedView([1, 2, 3], [0, 0, 0], 35, true));

        f.Vm.ViewFilter = FilterOptions.Views.Single(v => v.Value == ViewFilter.Custom);
        Assert.Equal(custom, Assert.Single(f.Vm.Tiles).Row.Sha256);
        Assert.True(f.Vm.FiltersActive);
        Assert.Equal("Custom views", Assert.Single(f.Vm.ActiveFilters).Label);

        f.Vm.ViewFilter = FilterOptions.Views.Single(v => v.Value == ViewFilter.Default);
        Assert.Equal(3, f.Vm.Total);
        Assert.False(f.Vm.Tiles.Any(t => t.Row.Sha256 == custom));

        // Saving a view in the viewer takes the model out of "Default views" straight away.
        string next = f.Vm.Tiles[0].Row.Sha256;
        f.Db.SetView(next, new SavedView([1, 2, 3], [0, 0, 0], 35, true));
        f.Vm.ApplyThumbnailReplaced(next);
        Assert.Equal(2, f.Vm.Total);

        f.Vm.ActiveFilters[0].Remove.Execute(null);
        Assert.Equal(ViewFilter.Any, f.Vm.ViewFilter.Value);
        Assert.Equal(4, f.Vm.Total);
    }

    [Fact]
    public async Task TheCopiesFilterAndTheStatusBarLinkShowModelsFoundTwice()
    {
        var f = new Fixture(files: 4);
        await using var cleanup = f;
        File.Copy(Path.Combine(f.Models, "m00.stl"), Path.Combine(f.Models, "sub", "copy of m00.stl"));
        await f.AddModelsFolder();
        Assert.Equal("1 with copies", f.Vm.CopiesText);
        Assert.False(f.Vm.StatsText.Contains("copies"));   // shown as its own link

        f.Vm.ShowCopiesCommand.Execute(null);               // the status bar link
        Assert.True(f.Vm.CopiesFilter.CopiesOnly);
        Assert.Equal(new[] { "copy of m00.stl", "m00.stl" }, f.Vm.Tiles.Select(t => t.Name).Order());
        Assert.Equal("With copies", Assert.Single(f.Vm.ActiveFilters).Label);
        Assert.Equal(1, f.Vm.ActiveFilterCount);

        f.Vm.ClearAllFiltersCommand.Execute(null);
        Assert.False(f.Vm.CopiesFilter.CopiesOnly);
        Assert.Equal(5, f.Vm.Total);
    }

    [Fact]
    public async Task DeveloperLoggingOptionsAreSavedButOnlyUsedInDeveloperMode()
    {
        var f = new Fixture();
        await using var cleanup = f;
        string logFile = Path.Combine(f.Dir, "logs", "x.log");
        using var log = new Xandrite.Library.Logging.FileLog(logFile);
        try
        {
            var dev = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher(), log: log) { DeveloperMode = true };
            dev.Initialize();
            Assert.False(dev.LogArchives);
            dev.LogArchives = true;
            dev.LogThreadIds = true;
            Assert.True(Xandrite.Library.Logging.DevLog.Archives);
            Assert.True(log.ThreadIds);
            Assert.Equal("1", f.Db.GetSetting(LibraryViewModel.DevArchivesSetting));

            // Started normally: the saved options are not put in force (and the menu is hidden).
            Xandrite.Library.Logging.DevLog.Archives = false;
            log.ThreadIds = false;
            var normal = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher(), log: log);
            normal.Initialize();
            Assert.False(normal.DeveloperMode);
            Assert.False(Xandrite.Library.Logging.DevLog.Archives);
            Assert.False(log.ThreadIds);

            // Started with --developer again: they are.
            var again = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher(), log: log) { DeveloperMode = true };
            again.Initialize();
            Assert.True(again.LogArchives);
            Assert.True(again.LogThreadIds);
            Assert.False(again.LogFiles);
            log.Flush();
            Assert.Contains("developer.options archives=true archive_io=false files=false threads=false thread_ids=true", File.ReadAllText(logFile));
        }
        finally
        {
            Xandrite.Library.Logging.DevLog.Archives = false;
            Xandrite.Library.Logging.DevLog.Log = Xandrite.Library.Logging.NullLog.Instance;
        }
    }

    [Fact]
    public async Task ScansAndRescansAreRecordedInTheStatisticsFile_WithTheReadSize()
    {
        var f = new Fixture(files: 2);
        await using var cleanup = f;
        string other = Path.Combine(f.Dir, "more");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.stl"), Stl.Replace("solid t", "solid x"));
        using var stats = new Xandrite.Library.Logging.FileLog(f.Location.StatisticsFile);
        var vm = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher(), statistics: stats);
        vm.Initialize();
        vm.ShowStatistics();
        Assert.Null(f.Shell.OpenedFile);   // nothing recorded yet: a message instead
        Assert.Contains(f.Shell.Messages, m => m.StartsWith("Nothing has been recorded yet", StringComparison.Ordinal));
        try
        {
            vm.ArchiveRead32768 = true;
            Assert.Equal(32768, vm.ArchiveReadKb);
            f.Shell.FolderToPick = f.Models;
            await vm.AddFolderAsync();
            f.Shell.FolderToPick = other;
            await vm.AddFolderAsync();
            await vm.RescanAsync(null);
        }
        finally
        {
            Xandrite.Library.Scanning.ArchiveFiles.ReadBufferBytes = Xandrite.Library.Scanning.ArchiveFiles.DefaultReadBufferBytes;
        }

        vm.ShowStatistics();
        Assert.Equal(f.Location.StatisticsFile, f.Shell.OpenedFile);
        var lines = File.ReadAllLines(f.Location.StatisticsFile);
        Assert.Equal(4, lines.Count(l => l.Contains(" scan root=")));   // two when added, two in the rescan
        string rescan = Assert.Single(lines, l => l.Contains(" rescan "));
        Assert.Contains(" folders=2 ", rescan);
        Assert.Contains(" files=3 ", rescan);
        Assert.Contains(" archive_read_kb=32768 ", rescan);
        Assert.Matches(@" took=[\d.]+s ", rescan);
        foreach (var scan in lines.Where(l => l.Contains(" scan root="))) Assert.Contains(" archive_read_kb=32768 ", scan);
    }

    [Fact]
    public async Task TheArchiveReadSizeIsSavedAndUsedEvenWithoutDeveloperMode()
    {
        var f = new Fixture();
        await using var cleanup = f;
        try
        {
            Assert.Equal(1024, f.Vm.ArchiveReadKb);   // 1 MB unless chosen otherwise
            Assert.True(f.Vm.ArchiveRead1024);
            f.Vm.ArchiveRead256 = true;
            Assert.Equal(256 * 1024, Xandrite.Library.Scanning.ArchiveFiles.ReadBufferBytes);
            Assert.False(f.Vm.ArchiveRead1024);
            Assert.Equal("256", f.Db.GetSetting(LibraryViewModel.ArchiveReadSetting));

            Xandrite.Library.Scanning.ArchiveFiles.ReadBufferBytes = 64 * 1024;
            var normal = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher());
            normal.Initialize();                       // started normally: the saved size still applies
            Assert.Equal(256 * 1024, Xandrite.Library.Scanning.ArchiveFiles.ReadBufferBytes);
        }
        finally
        {
            Xandrite.Library.Scanning.ArchiveFiles.ReadBufferBytes = Xandrite.Library.Scanning.ArchiveFiles.DefaultReadBufferBytes;
        }
    }

    [Fact]
    public void DateChipsReadAsAPhrase()
    {
        var modified = FilterOptions.AgeBases.Single(b => b.Value == AgeBasis.Modified);
        Assert.Equal("Modified in the last 7 days", LibraryViewModel.DateChipLabel(modified, FilterOptions.Ages.Single(a => a.Label == "Last 7 days")));
        Assert.Equal("Modified more than a year ago", LibraryViewModel.DateChipLabel(modified, FilterOptions.Ages.Single(a => a.Label == "Older than a year")));
    }

    [Fact]
    public async Task WithASelectionTheStripOffersItsActionsInsteadOfTheChips()
    {
        await using var f = new Fixture(files: 4);
        await f.AddModelsFolder();
        f.Vm.RatingFilter = FilterOptions.Ratings.Single(r => r.Value == RatingFilter.Unrated);
        Assert.True(f.Vm.ShowFilterChips);
        Assert.False(f.Vm.ShowSelectionBar);
        Assert.False(f.Vm.CopySelectedPathsCommand.CanExecute(null));

        f.Vm.Click(f.Vm.Tiles[0], false, false);
        f.Vm.Click(f.Vm.Tiles[1], true, false);
        Assert.True(f.Vm.ShowSelectionBar);
        Assert.False(f.Vm.ShowFilterChips);
        Assert.Equal("2 selected", f.Vm.SelectionText);
        Assert.Equal("Select all 4", f.Vm.SelectAllText);

        f.Vm.CopySelectedPathsCommand.Execute(null);
        Assert.Equal(2, f.Shell.Clipboard!.Split(Environment.NewLine).Length);
        f.Vm.RateSelectedCommand.Execute("5");
        Assert.Equal(2, f.Db.List(new ListQuery { Rating = RatingFilter.Five }).Total);
        f.Vm.Click(f.Vm.Tiles[0], false, false);
        f.Vm.RateSelectedCommand.Execute("");                         // unrated again
        Assert.Equal(1, f.Db.List(new ListQuery { Rating = RatingFilter.Five }).Total);

        f.Vm.ClearSelection();
        Assert.False(f.Vm.ShowSelectionBar);
        Assert.True(f.Vm.ShowFilterChips);
    }

    [Fact]
    public async Task TheOrderButtonSaysWhichWayTheListRuns()
    {
        await using var f = new Fixture(files: 2);
        await f.AddModelsFolder();
        Assert.True(f.Vm.IsAscending);
        Assert.Equal("A to Z. Click to reverse.", f.Vm.DirectionToolTip);
        f.Vm.Sort = FilterOptions.Sorts.Single(s => s.Value == SortOrder.Added);
        Assert.False(f.Vm.IsAscending);
        Assert.Equal("Newest first. Click to reverse.", f.Vm.DirectionToolTip);
        f.Vm.Descending = true;
        Assert.True(f.Vm.IsAscending);
        Assert.Equal("Oldest first. Click to reverse.", f.Vm.DirectionToolTip);
    }

    [Fact]
    public async Task SortingByDateShowsDatesAndTheDirectionCanBeReversed()
    {
        await using var f = new Fixture(files: 3);
        File.SetLastWriteTimeUtc(Path.Combine(f.Models, "m00.stl"), new DateTime(2020, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        await f.AddModelsFolder();
        f.Vm.Sort = FilterOptions.Sorts.Single(s => s.Value == SortOrder.Modified);
        Assert.NotNull(f.Vm.Tiles[0].Caption);
        Assert.Equal("m00.stl", f.Vm.Tiles[^1].Name);      // newest first: the 2020 file last
        f.Vm.Descending = true;
        Assert.Equal("m00.stl", f.Vm.Tiles[0].Name);
        f.Vm.Sort = FilterOptions.Sorts[0];
        Assert.Null(f.Vm.Tiles[0].Caption);
    }

    [Fact]
    public async Task SettingsAreRememberedBetweenRuns()
    {
        await using var f = new Fixture();
        f.Vm.Sort = FilterOptions.Sorts.Single(s => s.Value == SortOrder.Size);
        f.Vm.TileSize = 250;
        f.Vm.ShowMissing = true;
        var again = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher());
        again.Initialize();
        Assert.Equal(SortOrder.Size, again.Sort.Value);
        Assert.Equal(250, again.TileSize);
        Assert.True(again.ShowMissing);
    }

    [Fact]
    public async Task TheTileSizeAndWindowWidthDecideTheColumns()
    {
        await using var f = new Fixture(files: 10);
        await f.AddModelsFolder();
        f.Vm.SetViewportWidth(10 * (f.Vm.TileSize + LibraryViewModel.TileSpacing));
        Assert.Equal(10, f.Vm.Columns);
        Assert.Single(f.Vm.Rows);
        f.Vm.TileSize = LibraryViewModel.MaxTileSize;
        Assert.True(f.Vm.Columns < 10);
        f.Vm.TileSize = 10_000;   // clamped
        Assert.Equal(LibraryViewModel.MaxTileSize, f.Vm.TileSize);
    }

    [Fact]
    public async Task ThumbnailsArrivingUpdateTheirTiles()
    {
        await using var f = new Fixture(files: 3);
        await f.AddModelsFolder();   // the scan starts the queue
        var tile = f.Vm.Tiles[0];
        for (int i = 0; i < 400 && tile.ImageVersion == 0; i++) await Task.Delay(50);   // up to 20 s on a busy machine
        Assert.Equal(1, tile.ImageVersion);
        Assert.Equal(ThumbnailState.Ok, tile.Row.ThumbnailState);
        Assert.True(File.Exists(tile.ImagePath));

        f.Vm.Redraw([tile], revert: false);
        for (int i = 0; i < 400 && tile.ImageVersion == 1; i++) await Task.Delay(50);
        Assert.Equal(2, tile.ImageVersion);
    }

    [Fact]
    public async Task OpenRevealCopyAndFindCopies()
    {
        await using var f = new Fixture(files: 2);
        File.Copy(Path.Combine(f.Models, "m00.stl"), Path.Combine(f.Models, "sub", "copy of m00.stl"));
        await f.AddModelsFolder();
        var first = f.Vm.Tiles.First(t => t.Name == "m00.stl");

        await f.Vm.OpenAsync(first);
        Assert.Equal("m00.stl", f.Shell.Opened!.Name);
        Assert.True(f.Shell.Opened.FromLibrary);

        f.Vm.Reveal([first]);
        Assert.Equal((first.Row.Path, true), f.Shell.Revealed);

        f.Vm.CopyHashes([first]);
        Assert.Equal(first.Row.Sha256, f.Shell.Clipboard);

        f.Vm.FindCopies(first);
        Assert.Equal(2, f.Vm.Total);
        Assert.Equal(first.Row.Sha256, f.Vm.SearchText);

        File.Delete(first.Row.Path);
        await f.Vm.OpenAsync(first);
        Assert.Contains("no longer where the library found it", f.Shell.Messages.Last());
    }

    [Fact]
    public async Task RemovingAFolderAsksAndCanPurge()
    {
        await using var f = new Fixture(files: 2);
        await f.AddModelsFolder();
        f.Shell.RemoveAnswer = RemoveChoice.Cancel;
        f.Vm.RemoveFolder(f.Models);
        Assert.True(f.Vm.HasRoots);
        f.Shell.RemoveAnswer = RemoveChoice.Purge;
        f.Vm.RemoveFolder(f.Models);
        Assert.False(f.Vm.HasRoots);
        Assert.Equal(0, f.Vm.Total);
        Assert.Equal(0, f.Db.Locked(c => c.Scalar("SELECT COUNT(*) FROM models")));
    }

    [Fact]
    public void TheScanSummaryReadsWell()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var text = LibraryViewModel.Describe(
                [new Library.Scanning.ScanSummary { Root = "a", Found = 1200, Added = 3, Missing = 1, Errors = 1, Problems = [new("bad.zip", "not a zip")] }], cancelled: false);
            Assert.Equal("Scan finished: 1,200 files, 3 new, 1 missing, 1 problem (first: bad.zip: not a zip)", text);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task TheThumbnailSpeedIsSavedAndUsedTheNextTime()
    {
        var f = new Fixture();
        await using var cleanup = f;
        Assert.True(f.Vm.SpeedFull);
        Assert.Equal(ThumbnailSpeed.Full, f.Queue.Speed);

        var changed = new List<string?>();
        f.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        f.Vm.SpeedBackground = true;
        Assert.Equal(ThumbnailSpeed.Background, f.Queue.Speed);
        Assert.True(f.Vm.SpeedBackground);
        Assert.False(f.Vm.SpeedFull);
        Assert.Contains(nameof(LibraryViewModel.SpeedFull), changed);   // the old tick goes
        Assert.Equal("background", f.Db.GetSetting(LibraryViewModel.ThumbnailSpeedSetting));

        f.Vm.SpeedBackground = false;   // clicking the ticked item again keeps it
        Assert.Equal(ThumbnailSpeed.Background, f.Queue.Speed);

        // Next start: the queue begins at the saved speed.
        await using var queue = new ThumbnailQueue(f.Db, f.Location, () => new FakeRenderer(), workers: 1);
        await using var again = new LibraryViewModel(f.Db, f.Location, queue, f.Shell, new ImmediateDispatcher());
        again.Initialize();
        Assert.Equal(ThumbnailSpeed.Background, queue.Speed);
        Assert.True(again.SpeedBackground);
    }

    [Fact]
    public async Task ShowLogFileOpensTheLogWithWhatHasHappenedSoFar()
    {
        await using var f = new Fixture(files: 2);
        string file = Path.Combine(f.Dir, "logs", "xandrite.log");
        using var log = new Library.Logging.FileLog(file, flushMs: 60_000);
        var vm = new LibraryViewModel(f.Db, f.Location, f.Queue, f.Shell, new ImmediateDispatcher(), log: log) { SearchDelay = TimeSpan.Zero };
        vm.Initialize();
        f.Shell.FolderToPick = f.Models;
        await vm.AddFolderAsync();
        vm.SelectAll();
        vm.Rate(3);

        vm.ShowLog();
        Assert.Equal(file, f.Shell.OpenedFile);
        string text = File.ReadAllText(file);   // written before it was opened, though the timer had not fired
        Assert.Contains(" folder.added root=", text);
        Assert.Contains(" scan.end ", text);
        Assert.Contains(" ratings.set models=2 stars=3", text);
    }
}
