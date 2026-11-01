using System.Text.RegularExpressions;
using Xandrite.Geometry;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Library.SevenZip;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>The measurements written to the log, so a slow scan or thumbnail run shows where its time went.</summary>
public class MetricsTests
{
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    private static string Field(string line, string name) =>
        Regex.Match(line, $@"\b{name}=(""[^""]*""|\S+)") is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException($"no {name}= in {line}");

    private static long Number(string line, string name) => (long)double.Parse(Field(line, name), System.Globalization.CultureInfo.InvariantCulture);

    private static string Line(string text, string ev) =>
        text.Split('\n').LastOrDefault(l => l.Contains(" " + ev + " ")) ?? throw new InvalidOperationException("no " + ev + " line in the log:\n" + text);

    private static string ReadLog(FileLog log)
    {
        log.Flush();
        return File.ReadAllText(log.Path!);
    }

    /// <summary>A stream that takes a while over each read, like a slow network share.</summary>
    private sealed class SlowStream(byte[] data, int delayMs) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(delayMs);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            var bytes = new byte[buffer.Length];
            int n = Read(bytes, 0, bytes.Length);
            bytes.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
    }

    [Fact]
    public void AMeteredStreamCountsBytesAndTimeAndReportsStalls()
    {
        var stalls = new List<(string Path, long Ms, int Bytes)>();
        var parent = new IoMeter();
        var meter = new IoMeter { StallAfter = TimeSpan.FromMilliseconds(30), OnStall = (p, ms, b) => stalls.Add((p, ms, b)), Parent = parent };
        IoMeter.Current = meter;
        try
        {
            using var stream = IoMeter.Wrap(new SlowStream(new byte[10_000], 60), "share/kit.7z");
            var buffer = new byte[4096];
            while (stream.Read(buffer) > 0) { }
        }
        finally
        {
            IoMeter.Current = null;
        }

        Assert.Equal(10_000, meter.Read.Bytes);
        Assert.Equal(4, meter.Read.Calls);                 // 4096 + 4096 + 1808 + the empty read at the end
        Assert.True(meter.Read.Ms >= 200, $"{meter.Read.Ms} ms");
        Assert.Equal(10_000, parent.Read.Bytes);           // the scan's running total sees it too
        Assert.Equal(4, stalls.Count);
        Assert.All(stalls, s => Assert.Equal("share/kit.7z", s.Path));
        Assert.Same(Stream.Null, IoMeter.Wrap(Stream.Null, "x"));   // nobody measuring: nothing wrapped
    }

    [Fact]
    public async Task AScanWritesWhereItsTimeWentAndARescanShowsWhatItSkipped()
    {
        using var lib = new TempLibrary();
        lib.Write("a.stl", Stl);
        lib.Write("sub/b.stl", Stl.Replace("solid t", "solid b"));
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["p/c.stl"] = Stl.Replace("solid t", "solid c"), ["p/d.stl"] = Stl.Replace("solid t", "solid d") });
        bool sevenZip = SevenZipArchiveReader.IsAvailable;
        if (sevenZip) File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "solid.7z"), lib.At("solid.7z"));
        using var log = new FileLog(Path.Combine(lib.Dir, "logs", "x.log"));

        await new LibraryScanner(lib.Db) { Log = log }.ScanAsync(lib.Root);
        string text = ReadLog(log);
        string metrics = Line(text, "scan.metrics");
        Assert.Equal(2, Number(metrics, "folders"));                 // the root and sub
        Assert.Equal(2, Number(metrics, "files_read"));
        Assert.True(Number(metrics, "files_mb") >= 0);
        Assert.Equal(sevenZip ? 2 : 1, Number(metrics, "archives_read"));
        Assert.Equal(0, Number(metrics, "archives_unchanged"));
        Assert.True(Number(metrics, "archive_read_calls") > 0);       // the archives' bytes went through the meter
        Assert.True(Number(metrics, "db_calls") > 0);
        foreach (var name in new[] { "wall_ms", "list_ms", "stat_ms", "files_ms", "archives_ms", "archive_read_ms", "models_ms", "db_ms", "wait_ms", "busy_ms", "parallel", "read_mb_s", "stalls" })
            Field(metrics, name);
        string zip = text.Split('\n').Single(l => l.Contains(" archive path=") && l.Contains("kit.zip"));
        Assert.Equal(2, Number(zip, "models"));
        Assert.True(Number(zip, "read_mb") >= 0 && Number(zip, "size_mb") >= 0);
        foreach (var name in new[] { "unchanged", "unpacked_mb", "read_ms", "read_mb_s", "models_ms", "other_ms", "open_ms", "list_ms" }) Field(zip, name);
        Assert.Matches(@" hash path=[^\n]*a\.stl""? sha256=\w+ size=\d+ ms=\d+ read_ms=\d+", text);

        // A rescan with nothing changed opens no archive and reads no file.
        await new LibraryScanner(lib.Db) { Log = log }.ScanAsync(lib.Root);
        string again = Line(ReadLog(log), "scan.metrics");
        Assert.Equal(0, Number(again, "files_read"));
        Assert.Equal(0, Number(again, "archives_read"));
        Assert.Equal(sevenZip ? 2 : 1, Number(again, "archives_unchanged"));
        Assert.Equal(0, Number(again, "archive_read_calls"));
        Assert.True(Number(again, "stats") >= 2);                    // the loose files were looked up again
    }

    /// <summary>A zip reader that takes its time over each model, so a scan lasts long enough to report progress.</summary>
    private sealed class SlowZipReader : IArchiveReader
    {
        private readonly ZipArchiveReader _inner = new();
        public bool CanRead(string archivePath) => _inner.CanRead(archivePath);
        public Task<int> EachModelEntryAsync(string archivePath, Func<ArchiveEntry, Task> onEntry, Action<string, string> onProblem, ArchiveLimits limits, CancellationToken cancellation) =>
            _inner.EachModelEntryAsync(archivePath, async e => { await Task.Delay(60, cancellation); await onEntry(e); }, onProblem, limits, cancellation);
        public Task<byte[]> ReadEntryAsync(string archivePath, string entryName, long maxBytes, CancellationToken cancellation) =>
            _inner.ReadEntryAsync(archivePath, entryName, maxBytes, cancellation);
    }

    [Fact]
    public async Task ALongScanReportsItsProgressAsItGoes()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("many.zip", Enumerable.Range(0, 12).ToDictionary(i => $"m{i}.stl", i => Stl.Replace("solid t", "solid " + i)));
        using var log = new FileLog(Path.Combine(lib.Dir, "logs", "x.log"));
        await new LibraryScanner(lib.Db, new ModelSource([new SlowZipReader()])) { Log = log, ProgressEvery = TimeSpan.FromMilliseconds(150) }.ScanAsync(lib.Root);

        string text = ReadLog(log);
        var lines = text.Split('\n').Where(l => l.Contains(" scan.progress ")).ToList();
        Assert.True(lines.Count >= 2, text);
        foreach (var name in new[] { "elapsed_s", "found", "hashed", "unchanged", "archives", "folders", "in_flight", "read_mb", "read_mb_s", "models_s", "db_ms", "stalls" })
            Field(lines[0], name);
        Assert.True(Number(lines[^1], "found") >= Number(lines[0], "found"));
    }

    private sealed class Renderer : IModelRenderer
    {
        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();
        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request)
        {
            Thread.Sleep(40);
            return new RenderedImage(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        }
        public void Dispose() { }
        private sealed class FakeMesh : IGpuMesh { public int TriangleCount => 1; public void Dispose() { } }
    }

    [Fact]
    public async Task AThumbnailRunWritesWhereItsTimeWent()
    {
        using var lib = new TempLibrary();
        for (int i = 0; i < 6; i++) lib.Write($"m{i}.stl", Stl.Replace("solid t", "solid " + i));
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["a.stl"] = Stl.Replace("solid t", "solid za"), ["b.stl"] = Stl.Replace("solid t", "solid zb") });
        await lib.Scan();
        using var log = new FileLog(Path.Combine(lib.Dir, "logs", "x.log"));
        await using (var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new Renderer(), workers: 1)
        {
            Log = log,
            ProgressEvery = TimeSpan.FromMilliseconds(100),
        })
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Idle += () => idle.TrySetResult();
            queue.Start();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        string text = ReadLog(log);
        string end = Line(text, "thumbs.end");
        Assert.Equal(8, Number(end, "made"));
        Assert.True(Number(end, "read_mb") >= 0);
        Assert.True(Number(end, "render_ms") >= 8 * 40 - 20, end);   // the renderer's time is counted
        foreach (var name in new[] { "read_ms", "read_mb_s", "open_ms", "list_ms", "hash_ms", "load_ms", "encode_ms", "write_ms", "stalls" }) Field(end, name);
        Assert.Matches(@" thumb\.ok [^\n]* load_ms=\d+ render_ms=\d+ encode_ms=\d+", text);
        string progress = Line(text, "thumbs.progress");
        foreach (var name in new[] { "made", "of", "per_min", "read_mb_s", "workers", "speed", "render_ms" }) Field(progress, name);
        Assert.Matches(@" thumbs\.archive [^\n]* extract_ms=\d+", text);
    }
}
