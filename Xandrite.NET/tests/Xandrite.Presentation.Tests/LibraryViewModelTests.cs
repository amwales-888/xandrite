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
        public string? FileToOpen;

        public string? PickFolder(string title) => FolderToPick;
        public RemoveChoice ConfirmRemoveFolder(string folder, long files) => RemoveAnswer;
        public void ShowMessage(string title, string message) => Messages.Add(message);
        public void CopyText(string text) => Clipboard = text;
        public void Reveal(string path, bool selectFile) => Revealed = (path, selectFile);
        public string? PickSaveFile(string title, string suggestedName, string filter) => SaveTo;
        public string? PickOpenFile(string title, string filter) => FileToOpen;
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
