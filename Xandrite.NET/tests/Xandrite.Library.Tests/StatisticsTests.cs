using System.Text.RegularExpressions;
using Xandrite.Geometry;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>The statistics file: a line per scan and per thumbnail run, for comparing runs over time.</summary>
public class StatisticsTests
{
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    private static List<string> Lines(FileLog log)
    {
        log.Flush();
        return File.Exists(log.Path) ? File.ReadAllLines(log.Path).ToList() : [];
    }

    private static string Field(string line, string key) =>
        Regex.Match(line, $@" {Regex.Escape(key)}=(""[^""]*""|\S+)") is { Success: true } m ? m.Groups[1].Value.Trim('"') : throw new InvalidOperationException($"no {key} in: {line}");

    [Fact]
    public void DurationsAndTimesAreEasyToRead()
    {
        Assert.Equal("0.4s", Stat.Took(400));
        Assert.Equal("45.2s", Stat.Took(45_200));
        Assert.Equal("3m07s", Stat.Took(187_000));
        Assert.Equal("2h05m09s", Stat.Took((2 * 3600 + 5 * 60 + 9) * 1000L));
        Assert.Equal("3d04h12m", Stat.Took(((3 * 24 + 4) * 3600 + 12 * 60) * 1000L));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", Stat.Time(DateTimeOffset.Now));
        Assert.Equal(120.0, Stat.Rate(2, 1000, per: 60));
        Assert.Null(Stat.Rate(5, 0));
        Assert.Equal("network", Stat.WhereIs(@"\\nas\models"));
        Assert.Equal("local", Stat.WhereIs(Path.GetTempPath()));
    }

    [Fact]
    public async Task EachScanAddsALineWithItsTimesWhatItFoundAndTheReadSize()
    {
        using var lib = new TempLibrary();
        using var stats = new FileLog(Path.Combine(lib.Dir, "logs", "statistics.log"));
        lib.Write("a.stl", Stl.Replace("solid t", "solid a"));
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["b.stl"] = Stl.Replace("solid t", "solid b") });
        var scanner = new LibraryScanner(lib.Db) { Statistics = stats };
        try
        {
            ArchiveFiles.ReadBufferBytes = 16 << 20;
            await scanner.ScanAsync(lib.Root);
            await scanner.ScanAsync(lib.Root);
        }
        finally
        {
            ArchiveFiles.ReadBufferBytes = ArchiveFiles.DefaultReadBufferBytes;
        }

        var lines = Lines(stats);
        Assert.Equal(2, lines.Count);   // nothing else goes in this file
        Assert.All(lines, l => Assert.Contains(" INFO  scan ", l));
        string first = lines[0], second = lines[1];
        Assert.Equal("finished", Field(first, "result"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", Field(first, "started"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", Field(first, "ended"));
        Assert.Matches(@"^[\d.]+s$", Field(first, "took"));
        Assert.Equal("2", Field(first, "files"));
        Assert.Equal("2", Field(first, "new"));
        Assert.Equal("1", Field(first, "archives"));
        Assert.Equal("16384", Field(first, "archive_read_kb"));   // the read size in force
        Assert.Equal("never", Field(first, "previous_scan"));
        Assert.Equal("local", Field(first, "folder_on"));

        Assert.Equal("2", Field(second, "unchanged"));
        Assert.Equal("0", Field(second, "new"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT", Field(second, "previous_scan"));   // the first scan
        Assert.Matches(@"^[\d.]+s$", Field(second, "since_previous"));
    }

    [Fact]
    public async Task ACancelledScanSaysSoAndIsAWarning()
    {
        using var lib = new TempLibrary();
        using var stats = new FileLog(Path.Combine(lib.Dir, "logs", "statistics.log"));
        lib.Write("a.stl", Stl);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await new LibraryScanner(lib.Db) { Statistics = stats }.ScanAsync(lib.Root, cancellation: cancel.Token);
        string line = Assert.Single(Lines(stats));
        Assert.Contains(" WARN  scan ", line);
        Assert.Equal("cancelled", Field(line, "result"));
    }

    private sealed class FakeRenderer : IModelRenderer
    {
        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();
        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request) => new(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        public void Dispose() { }
        private sealed class FakeMesh : IGpuMesh { public int TriangleCount => 1; public void Dispose() { } }
    }

    [Fact]
    public async Task EachThumbnailRunAddsALineWithItsTimesAndCounts_AndARunWithNothingToDoAddsNone()
    {
        using var lib = new TempLibrary();
        using var stats = new FileLog(Path.Combine(lib.Dir, "logs", "statistics.log"));
        for (int i = 0; i < 3; i++) lib.Write($"m{i}.stl", Stl.Replace("solid t", "solid " + i));
        lib.Write("bad.stl", "cannot parse this");
        await lib.Scan();

        await using (var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new FakeRenderer(), workers: 1) { Statistics = stats })
        {
            for (int run = 0; run < 2; run++)   // the second run finds nothing to draw
            {
                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queue.Idle += () => idle.TrySetResult();
                queue.Start();
                await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        string line = Assert.Single(Lines(stats));
        Assert.Contains(" WARN  thumbnails ", line);   // one failed
        Assert.Equal("finished", Field(line, "result"));
        Assert.Equal("3", Field(line, "made"));
        Assert.Equal("1", Field(line, "failed"));
        Assert.Equal("4", Field(line, "waiting_at_start"));
        Assert.Equal("0", Field(line, "waiting_at_end"));
        Assert.Matches(@"^[\d.]+s$", Field(line, "took"));
        Assert.Equal("0.0s", Field(line, "paused"));
        Assert.Equal("1024", Field(line, "archive_read_kb"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", Field(line, "started"));
    }

    [Fact]
    public async Task TimePausedIsCountedSeparatelyFromTimeWorking()
    {
        using var lib = new TempLibrary();
        using var stats = new FileLog(Path.Combine(lib.Dir, "logs", "statistics.log"));
        for (int i = 0; i < 3; i++) lib.Write($"m{i}.stl", Stl.Replace("solid t", "solid " + i));
        await lib.Scan();

        await using (var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => new FakeRenderer(), workers: 1) { Statistics = stats })
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Idle += () => idle.TrySetResult();
            queue.Start();
            queue.Pause();
            await Task.Delay(1200);
            queue.Resume();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        string line = Assert.Single(Lines(stats));
        double paused = double.Parse(Field(line, "paused").TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture);
        double took = double.Parse(Field(line, "duration_s"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(paused >= 1.1, line);
        Assert.True(took >= paused, line);
    }
}
