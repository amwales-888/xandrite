using System.Text;
using Xandrite.Geometry;
using Xandrite.Library.Scanning;
using Xandrite.Library.SevenZip;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>The thumbnail speed setting: how many models at once, at what priority, and how many 7-Zip threads.</summary>
public class ThumbnailSpeedTests
{
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    /// <summary>Takes a while to draw, and remembers how many were drawing at once and at what thread priority.</summary>
    private sealed class SlowRenderer(Recorder recorder) : IModelRenderer
    {
        public string AdapterDescription => "Slow";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();

        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request)
        {
            recorder.Enter(Thread.CurrentThread.Priority);
            try
            {
                Thread.Sleep(recorder.DrawMs);
                return new RenderedImage(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
            }
            finally
            {
                recorder.Leave();
            }
        }

        public void Dispose() { }

        private sealed class FakeMesh : IGpuMesh
        {
            public int TriangleCount => 1;
            public void Dispose() { }
        }
    }

    private sealed class Recorder
    {
        private int _now;
        public int DrawMs = 40;
        public int MostAtOnce;
        public readonly List<ThreadPriority> Priorities = [];

        public void Enter(ThreadPriority priority)
        {
            int now = Interlocked.Increment(ref _now);
            lock (this)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                Priorities.Add(priority);
            }
        }

        public void Leave() => Interlocked.Decrement(ref _now);

        public void Reset()
        {
            lock (this)
            {
                MostAtOnce = 0;
                Priorities.Clear();
            }
        }
    }

    private static async Task<TempLibrary> Library(int models)
    {
        var lib = new TempLibrary();
        for (int i = 0; i < models; i++) lib.Write($"m{i}.stl", Stl.Replace("solid t", "solid t" + i));
        await lib.Scan();
        return lib;
    }

    private static async Task RunToEnd(ThumbnailQueue queue)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Idle += () => idle.TrySetResult();
        queue.Start();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void EachSpeedMeansFewerWorkersLowerPriorityAndFewerArchiveThreads()
    {
        Assert.Equal(new WorkLimits(4, ThreadPriority.Normal, null), WorkLimits.For(ThumbnailSpeed.Full, 4, 24));
        Assert.Equal(new WorkLimits(2, ThreadPriority.BelowNormal, 6), WorkLimits.For(ThumbnailSpeed.Balanced, 4, 24));
        Assert.Equal(new WorkLimits(1, ThreadPriority.Lowest, 1), WorkLimits.For(ThumbnailSpeed.Background, 4, 24));
        Assert.Equal(new WorkLimits(1, ThreadPriority.BelowNormal, 1), WorkLimits.For(ThumbnailSpeed.Balanced, 1, 2));   // never none

        Assert.Equal(ThumbnailSpeed.Background, WorkLimits.Parse("background"));
        Assert.Equal(ThumbnailSpeed.Full, WorkLimits.Parse(null));
        Assert.Equal(ThumbnailSpeed.Full, WorkLimits.Parse("ludicrous"));
        Assert.Equal(ThumbnailSpeed.Full, WorkLimits.Parse("7"));   // a number that is not one of the speeds
    }

    [Fact]
    public async Task AtBackgroundSpeedOneModelIsDrawnAtATimeAtTheLowestPriority()
    {
        using var lib = await Library(6);
        var recorder = new Recorder();
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new SlowRenderer(recorder), workers: 4)
        {
            Speed = ThumbnailSpeed.Background,
        };
        await RunToEnd(queue);

        Assert.Equal(6, lib.Db.GetThumbnailCounts(Look.RenderVersion).Ok);
        Assert.Equal(1, recorder.MostAtOnce);
        Assert.All(recorder.Priorities, p => Assert.Equal(ThreadPriority.Lowest, p));
        Assert.Null(BackgroundWork.Current);   // the limits stay inside the queue's own work
    }

    [Fact]
    public async Task AtFullSpeedEveryWorkerDrawsAtNormalPriority()
    {
        using var lib = await Library(8);
        var recorder = new Recorder();
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new SlowRenderer(recorder), workers: 4);
        await RunToEnd(queue);

        Assert.Equal(8, lib.Db.GetThumbnailCounts(Look.RenderVersion).Ok);
        Assert.InRange(recorder.MostAtOnce, 2, 4);
        Assert.All(recorder.Priorities, p => Assert.Equal(ThreadPriority.Normal, p));
    }

    [Fact]
    public async Task ChangingTheSpeedWhileDrawingTakesEffectFromTheNextModel()
    {
        using var lib = await Library(30);
        var recorder = new Recorder { DrawMs = 30 };
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new SlowRenderer(recorder), workers: 4)
        {
            Speed = ThumbnailSpeed.Background,
        };
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Idle += () => idle.TrySetResult();
        queue.Start();

        await Task.Delay(200);
        Assert.Equal(1, recorder.MostAtOnce);
        recorder.Reset();
        queue.Speed = ThumbnailSpeed.Full;   // the waiting workers notice within a quarter of a second

        await idle.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(30, lib.Db.GetThumbnailCounts(Look.RenderVersion).Ok);
        Assert.InRange(recorder.MostAtOnce, 2, 4);
        Assert.Contains(ThreadPriority.Normal, recorder.Priorities);
    }

    [Fact]
    public async Task SevenZipCanBeToldToDecompressWithOneThread()
    {
        if (!SevenZipArchiveReader.IsAvailable) return;
        using var lib = new TempLibrary();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "solid.7z"), lib.At("solid.7z"));
        await lib.Scan();

        nint archive = SevenZipNative.CreateArchive(0x07);
        try
        {
            Assert.True(SevenZipNative.SetThreads(archive, 1));
        }
        finally
        {
            SevenZipNative.Release(archive);
        }

        // Reading through the queue's limits gives the same bytes.
        BackgroundWork.Current = new WorkLimits(1, ThreadPriority.Lowest, 1);
        try
        {
            var leg = lib.Present().Single(r => r.Name == "leg.stl");
            byte[] bytes = await ModelSource.Default.ReadAsync(lib.Db.GetLocation(leg.Path)!);
            Assert.Equal("solid leg\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            BackgroundWork.Current = null;
        }
    }
}
