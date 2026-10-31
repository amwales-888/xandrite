using System.Text;
using Xandrite.Geometry;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Library.SevenZip;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>
/// Thumbnails of models inside archives: each archive is read once for all its models (a solid .7z used to be
/// decompressed from the start once per model), while the models are drawn as they come out.
/// </summary>
public class ArchiveThumbnailTests
{
    private static string Stl(string name) =>
        $"solid {name}\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid {name}\n";

    private sealed class Renderer(int drawMs = 0) : IModelRenderer
    {
        public static int Drawing, MostAtOnce;

        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();

        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request)
        {
            int now = Interlocked.Increment(ref Drawing);
            InterlockedMax(ref MostAtOnce, now);
            try
            {
                if (drawMs > 0) Thread.Sleep(drawMs);
                return new RenderedImage(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
            }
            finally
            {
                Interlocked.Decrement(ref Drawing);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }

        public void Dispose() { }

        private sealed class FakeMesh : IGpuMesh
        {
            public int TriangleCount => 1;
            public void Dispose() { }
        }
    }

    /// <summary>A reader that counts how archives are read.</summary>
    private sealed class CountingReader(IArchiveReader inner) : IArchiveReader
    {
        public int OneEntry, ManyEntries;

        public bool CanRead(string archivePath) => inner.CanRead(archivePath);

        public Task<int> EachModelEntryAsync(string archivePath, Func<ArchiveEntry, Task> onEntry, Action<string, string> onProblem, ArchiveLimits limits, CancellationToken cancellation) =>
            inner.EachModelEntryAsync(archivePath, onEntry, onProblem, limits, cancellation);

        public Task<byte[]> ReadEntryAsync(string archivePath, string entryName, long maxBytes, CancellationToken cancellation)
        {
            Interlocked.Increment(ref OneEntry);
            return inner.ReadEntryAsync(archivePath, entryName, maxBytes, cancellation);
        }

        public Task ReadEntriesAsync(string archivePath, IReadOnlyCollection<string> entryNames, long maxBytes, Func<EntryRead, Task> onEntry, CancellationToken cancellation)
        {
            Interlocked.Increment(ref ManyEntries);
            return inner.ReadEntriesAsync(archivePath, entryNames, maxBytes, onEntry, cancellation);
        }
    }

    private static async Task RunToEnd(ThumbnailQueue queue)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Idle += () => idle.TrySetResult();
        queue.Start();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static Dictionary<string, string> Models(string prefix, int count) =>
        Enumerable.Range(0, count).ToDictionary(i => $"parts/{prefix}{i:00}.stl", i => Stl(prefix + i));

    [Fact]
    public async Task EachArchiveIsReadOnceForAllItsModelsAndLooseFilesOneByOne()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("a.zip", Models("a", 6));
        lib.WriteZip("b.zip", Models("b", 4));
        lib.WriteZip("single.zip", Models("s", 1));
        lib.Write("loose.stl", Stl("loose"));
        await lib.Scan();

        var zip = new CountingReader(new ZipArchiveReader());
        using var log = new FileLog(Path.Combine(lib.Dir, "logs", "x.log"));
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(), new ModelSource([zip]), workers: 3) { Log = log };
        await RunToEnd(queue);

        Assert.Equal(12, lib.Db.GetThumbnailCounts(Look.RenderVersion).Ok);
        Assert.Equal(2, zip.ManyEntries);   // a.zip and b.zip, once each
        Assert.Equal(1, zip.OneEntry);      // the only model in single.zip is read on its own
        log.Flush();
        string text = File.ReadAllText(log.Path!);
        Assert.Matches(@"thumbs\.archive archive=[^\n]*a\.zip""? models=6 read=6 ms=\d+", text);
        Assert.Matches(@"thumbs\.archive archive=[^\n]*b\.zip""? models=4 read=4 ms=\d+", text);
        Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(text, @" INFO  thumb\.ok ").Count);   // archive models logged like loose ones
    }

    [Fact]
    public async Task IdleWorkersHelpDrawTheModelsOfABigArchive()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("big.zip", Models("m", 16));
        await lib.Scan();
        Renderer.MostAtOnce = 0;
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(drawMs: 40), workers: 4);
        await RunToEnd(queue);

        Assert.Equal(16, lib.Db.GetThumbnailCounts(Look.RenderVersion).Ok);
        Assert.InRange(Renderer.MostAtOnce, 2, 4);   // not one at a time, though the archive is read by one thread
    }

    [Fact]
    public async Task ModelsGoneFromTheArchiveAreLeftAloneAndTheRestAreDrawn()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("a.zip", Models("a", 4));
        await lib.Scan();
        lib.WriteZip("a.zip", Models("a", 4).Take(2).ToDictionary());   // two models taken out after the scan

        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(), workers: 2);
        await RunToEnd(queue);

        var counts = lib.Db.GetThumbnailCounts(Look.RenderVersion);
        Assert.Equal(2, counts.Ok);
        Assert.Equal(0, counts.Failed);
        Assert.Equal(2, counts.Pending);   // still pending: the next scan marks them missing
        Assert.Equal(2, queue.GetStatus().Skipped);
    }

    [Fact]
    public async Task AnArchiveThatHasGoneOrBrokenIsAnsweredForEveryModel()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("gone.zip", Models("g", 3));
        lib.WriteZip("broken.zip", Models("b", 3));
        await lib.Scan();
        lib.Delete("gone.zip");
        File.WriteAllText(lib.At("broken.zip"), "this is no longer a zip file");

        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(), workers: 2);
        await RunToEnd(queue);

        var counts = lib.Db.GetThumbnailCounts(Look.RenderVersion);
        Assert.Equal(0, counts.Ok);
        Assert.Equal(3, counts.Failed);        // the broken archive: each of its models says why
        Assert.Equal(3, queue.GetStatus().Skipped);   // the deleted one: left for the next scan
        Assert.All(lib.Present().Where(r => r.ArchiveName == "broken.zip"),
            r => Assert.StartsWith("Could not read the file", r.ThumbnailError));
    }

    [Fact]
    public async Task ASolid7zIsDecompressedOnceForAllItsModels()
    {
        if (!SevenZipArchiveReader.IsAvailable) return;
        using var lib = new TempLibrary();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "solid.7z"), lib.At("solid.7z"));
        await lib.Scan();

        var sevenZip = new CountingReader(new SevenZipArchiveReader());
        await using var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(), new ModelSource([sevenZip]), workers: 2);
        await RunToEnd(queue);

        var counts = lib.Db.GetThumbnailCounts(Look.RenderVersion);
        Assert.Equal(1, sevenZip.ManyEntries);
        Assert.Equal(0, sevenZip.OneEntry);
        // The fixture's "models" are placeholders that do not load, but each was read out of the archive and handed on.
        Assert.Equal(3, counts.Failed);
        Assert.All(lib.Present(), r => Assert.False(r.ThumbnailError?.StartsWith("Could not read") ?? false, r.ThumbnailError));
    }

    [Fact]
    public async Task SevenZipReadsSeveralEntriesInOnePassAndReportsTheMissingOnes()
    {
        if (!SevenZipArchiveReader.IsAvailable) return;
        string archive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "solid.7z");
        var reads = new List<EntryRead>();
        await new SevenZipArchiveReader().ReadEntriesAsync(archive, ["models/sub/arm.3mf", "models/leg.stl", "models/gone.stl"], 1 << 20,
            read => { reads.Add(read); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(3, reads.Count);
        Assert.True(reads.Single(r => r.Name == "models/gone.stl").Gone);
        Assert.Equal("solid leg\n", Encoding.UTF8.GetString(reads.Single(r => r.Name == "models/leg.stl").Bytes!));
        Assert.Equal("pk arm", Encoding.UTF8.GetString(reads.Single(r => r.Name == "models/sub/arm.3mf").Bytes!));

        await Assert.ThrowsAsync<ModelGoneException>(() =>
            new SevenZipArchiveReader().ReadEntriesAsync(archive + ".missing", ["models/leg.stl"], 1 << 20, _ => Task.CompletedTask, CancellationToken.None));
    }
}
