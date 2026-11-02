using Xandrite.Geometry;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>The queue keeps the library's thumbnail counts itself, so asking for its status never waits for the library.</summary>
public class QueueCountsTests
{
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    private sealed class FakeRenderer : IModelRenderer
    {
        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();
        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request) => new(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        public void Dispose() { }
        private sealed class FakeMesh : IGpuMesh { public int TriangleCount => 1; public void Dispose() { } }
    }

    private static async Task RunUntilIdle(ThumbnailQueue queue)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnIdle() => idle.TrySetResult();
        queue.Idle += OnIdle;
        queue.Start();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
        queue.Idle -= OnIdle;
    }

    [Fact]
    public async Task TheCountsFollowEachThumbnailAndMatchTheLibraryAtTheEnd()
    {
        using var lib = new TempLibrary();
        for (int i = 0; i < 5; i++) lib.Write($"m{i}.stl", Stl.Replace("solid t", "solid " + i));
        lib.Write("bad.stl", "cannot parse this");
        await lib.Scan();

        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new FakeRenderer(), workers: 2);
        Assert.Equal(new ThumbnailCounts(0, 0, 6), queue.GetStatus().Counts);
        var seen = new List<ThumbnailCounts>();
        queue.Progress += s => { lock (seen) seen.Add(s.Counts); };
        await RunUntilIdle(queue);

        Assert.Equal(new ThumbnailCounts(5, 1, 0), queue.GetStatus().Counts);
        Assert.Equal(lib.Db.GetThumbnailCounts(Look.RenderVersion), queue.GetStatus().Counts);   // the same as counting
        lock (seen) Assert.Contains(seen, c => c.Ok is > 0 and < 5);   // and went up along the way
    }

    [Fact]
    public async Task ChangesMadeOutsideTheQueueShowAfterARecount()
    {
        using var lib = new TempLibrary();
        lib.Write("a.stl", Stl);
        await lib.Scan();
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new FakeRenderer(), workers: 1);
        await RunUntilIdle(queue);
        Assert.Equal(new ThumbnailCounts(1, 0, 0), queue.GetStatus().Counts);

        lib.Write("b.stl", Stl.Replace("solid t", "solid b"));
        await lib.Scan();   // a new model, not drawn: the queue has not been told yet
        Assert.Equal(new ThumbnailCounts(1, 0, 0), queue.GetStatus().Counts);

        var counted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Progress += s => { if (s.Counts.Pending == 1) counted.TrySetResult(); };
        queue.RecountLater();
        await counted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new ThumbnailCounts(1, 0, 1), queue.GetStatus().Counts);
    }
}
