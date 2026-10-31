using System.Text;
using Xandrite.Imaging;
using Xandrite.Library;
using Xandrite.Rendering;
using static Xandrite.Presentation.Tests.LibraryViewModelTests;

namespace Xandrite.Presentation.Tests;

public class ViewerViewModelTests
{
    private static ViewerViewModel Viewer(Fixture f) =>
        new(f.Db, f.Location, f.Queue, () => new FakeRenderer(), f.Shell, new ImmediateDispatcher());

    private static async Task<RenderedImage> NextFrame(ViewerViewModel viewer, Action trigger)
    {
        var frame = new TaskCompletionSource<RenderedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RenderedImage image) => frame.TrySetResult(image);
        viewer.FrameReady += Handler;
        try
        {
            trigger();
            return await frame.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            viewer.FrameReady -= Handler;
        }
    }

    private static async Task<(Fixture F, ViewerViewModel Viewer, LocationRow Row)> OpenFromLibrary()
    {
        var f = new Fixture(files: 2);
        await f.AddModelsFolder();
        var viewer = Viewer(f);
        var tile = f.Vm.Tiles[0];
        f.Shell.Opened = null;
        await f.Vm.OpenAsync(tile);
        viewer.SetViewport(400, 300);
        await NextFrame(viewer, () => _ = viewer.LoadAsync(f.Shell.Opened!));
        return (f, viewer, tile.Row);
    }

    [Fact]
    public async Task AModelFromTheLibraryOpensWithItsDetailsAndCanBeRatedAndUpdated()
    {
        var (f, viewer, row) = await OpenFromLibrary();
        await using var cleanup = f;
        Assert.True(viewer.HasModel);
        Assert.Equal(row.Name, viewer.FileName);
        Assert.Equal("STL", viewer.FormatText);
        Assert.Equal("1", viewer.TrianglesText);
        Assert.Equal(row.Sha256, viewer.Sha256Text);
        Assert.True(viewer.CanUpdate);
        Assert.True(viewer.CanRate);
        Assert.False(viewer.HasCustomView);
        Assert.Equal("Fake", viewer.AdapterText);
        Assert.Contains("Render        version", viewer.ViewSettingsText);
    }

    [Fact]
    public async Task FramesFollowTheViewportAndTheMouse()
    {
        var (f, viewer, _) = await OpenFromLibrary();
        await using var cleanup = f;
        // A frame already being drawn may arrive first, at the old size; the new size follows straight after.
        var frame = await NextFrame(viewer, () => viewer.SetViewport(640, 360));
        for (int i = 0; i < 5 && frame.Width != 640; i++) frame = await NextFrame(viewer, viewer.RequestFrame);
        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);

        var before = viewer.Camera.Position;
        await NextFrame(viewer, () => viewer.Rotate(50, 0));
        Assert.NotEqual(before, viewer.Camera.Position);
        double distance = viewer.Camera.Distance;
        await NextFrame(viewer, () => viewer.Zoom(120));
        Assert.True(viewer.Camera.Distance < distance);
    }

    [Fact]
    public async Task ManyRequestsWhileDrawingAreFoldedTogether()
    {
        var (f, viewer, _) = await OpenFromLibrary();
        await using var cleanup = f;
        int frames = 0;
        viewer.FrameReady += _ => { Interlocked.Increment(ref frames); };
        for (int i = 0; i < 50; i++) viewer.Rotate(1, 0);
        await Task.Delay(500);
        Assert.InRange(frames, 1, 10);   // not fifty
    }

    [Fact]
    public async Task UpdateReplacesTheLibraryThumbnailAndRemembersTheView()
    {
        var (f, viewer, row) = await OpenFromLibrary();
        await using var cleanup = f;
        string? replaced = null;
        viewer.ThumbnailReplaced += sha => replaced = sha;
        viewer.Rotate(100, -20);
        await viewer.UpdateThumbnailAsync();

        Assert.Equal(row.Sha256, replaced);
        Assert.True(viewer.HasCustomView);
        Assert.Equal("Thumbnail updated in the library.", viewer.UpdateNote);
        var saved = f.Db.GetView(row.Sha256)!;
        Assert.Equal(viewer.Camera.Position.X, (float)saved.Position[0], 3);
        string json = PngText.Read(File.ReadAllBytes(f.Location.ThumbnailPath(row.Sha256)), Look.StateKeyword)!;
        Assert.Contains("\"auto\": false", json);

        // The library grid shows it straight away.
        f.Vm.ApplyThumbnailReplaced(row.Sha256);
        Assert.True(f.Vm.Tiles.First(t => t.Row.Sha256 == row.Sha256).HasCustomView);

        // Opening the model again starts from the saved view.
        var again = Viewer(f);
        again.SetViewport(400, 300);
        await NextFrame(again, () => _ = again.LoadAsync(f.Shell.Opened!));
        Assert.True(again.HasCustomView);
        Assert.Equal(viewer.Camera.Position.X, again.Camera.Position.X, 3);
    }

    [Fact]
    public async Task RevertForgetsTheViewAndQueuesTheThumbnail()
    {
        var (f, viewer, row) = await OpenFromLibrary();
        await using var cleanup = f;
        viewer.Rotate(100, 0);
        await viewer.UpdateThumbnailAsync();
        Assert.Equal("", viewer.UpdateNote is { } note && note.StartsWith("Could not") ? note : "");
        Assert.True(viewer.HasCustomView);
        string? reverted = null;
        viewer.ViewReverted += sha => reverted = sha;
        viewer.Revert();
        Assert.Equal(row.Sha256, reverted);
        Assert.False(viewer.HasCustomView);
        Assert.Null(f.Db.GetView(row.Sha256));
    }

    [Fact]
    public async Task RatingFromTheViewerIsSavedAndReported()
    {
        var (f, viewer, row) = await OpenFromLibrary();
        await using var cleanup = f;
        (string Sha, int? Stars)? reported = null;
        viewer.RatingChanged += (sha, stars) => reported = (sha, stars);
        viewer.Rating = viewer.RatingChoices.Single(r => r.Stars == 4);
        Assert.Equal(4, f.Db.GetRating(row.Sha256));
        Assert.Equal((row.Sha256, (int?)4), reported);
        Assert.Equal("Saved.", viewer.RatingNote);
    }

    [Fact]
    public async Task AFileFromOutsideTheLibraryCanBeViewedButNotRatedOrUpdated()
    {
        await using var f = new Fixture();
        string path = Path.Combine(f.Dir, "outside.stl");
        File.WriteAllText(path, Stl.Replace("solid t", "solid outside"));
        var viewer = Viewer(f);
        await NextFrame(viewer, () => _ = viewer.OpenPathAsync(path));   // as if dropped on the viewer
        Assert.True(viewer.HasModel);
        Assert.False(viewer.CanUpdate);
        Assert.False(viewer.CanRate);
        Assert.Contains("not in the library", viewer.UpdateNote);
    }

    [Fact]
    public async Task ABadFileIsReportedAndExportWritesAPngOfTheChosenSizeNamedAfterTheModel()
    {
        var (f, viewer, row) = await OpenFromLibrary();
        await using var cleanup = f;
        await viewer.LoadAsync(new ViewerDocument("bad.stl", "stl", Encoding.ASCII.GetBytes("nope"), FromLibrary: false));
        Assert.StartsWith("Could not read bad.stl", viewer.StatusText);
        Assert.Equal(row.Name, viewer.FileName);   // the model on show stays

        f.Shell.SaveTo = Path.Combine(f.Dir, "export.png");
        viewer.ExportSize = 256;
        await viewer.SavePngAsync();
        Assert.Equal(Path.GetFileNameWithoutExtension(row.Name) + ".png", f.Shell.SuggestedName);   // not the SHA-256
        Assert.StartsWith("Exported ", viewer.StatusText);
        byte[] png = File.ReadAllBytes(f.Shell.SaveTo);
        Assert.Equal(256, png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]);   // IHDR width
    }
}
