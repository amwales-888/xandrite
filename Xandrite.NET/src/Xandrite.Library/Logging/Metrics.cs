using System.Diagnostics;

namespace Xandrite.Library.Logging;

/// <summary>
/// Time, bytes and number of calls for one kind of work (listing folders, reading from disk, hashing...), added to from
/// any thread. Scans and thumbnail runs keep a few of these and write them to the log, so a slow run shows where the
/// time went.
/// </summary>
public sealed class Meter
{
    private long _ticks, _bytes, _calls;

    public long Calls => Interlocked.Read(ref _calls);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Ms => (long)(Interlocked.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency);

    public void Add(long elapsedTicks, long bytes = 0, long calls = 1)
    {
        Interlocked.Add(ref _ticks, elapsedTicks);
        if (bytes != 0) Interlocked.Add(ref _bytes, bytes);
        if (calls != 0) Interlocked.Add(ref _calls, calls);
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

    /// <summary>Adds everything another meter recorded (one archive's reads into the scan's total, say).</summary>
    public void Add(Meter other) => Add(Interlocked.Read(ref other._ticks), other.Bytes, other.Calls);

    /// <summary>Times a piece of work: <c>using (meter.Time()) { ... }</c>.</summary>
    public Timing Time() => new(this);

    public T Time<T>(Func<T> work)
    {
        long started = Stopwatch.GetTimestamp();
        try { return work(); }
        finally { Add(Stopwatch.GetTimestamp() - started); }
    }

    public void Time(Action work)
    {
        long started = Stopwatch.GetTimestamp();
        try { work(); }
        finally { Add(Stopwatch.GetTimestamp() - started); }
    }

    /// <summary>Megabytes per second over the time measured, or 0.</summary>
    public double MBps => Ms > 0 ? Bytes / 1048576.0 / (Ms / 1000.0) : 0;

    /// <summary>The same, rounded for the log, or null (written as "-") when the time was too short to measure.</summary>
    public double? MBpsForLog => Ms > 0 ? Math.Round(MBps, 1) : null;

    public readonly struct Timing(Meter meter) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => meter.Add(Stopwatch.GetTimestamp() - _started);
    }
}

/// <summary>
/// File input measured for the work going on in this async flow (one archive, one file), so readers far below the
/// scanner (7-Zip's input, the zip reader, hashing) can report what they read and how long the disk or network took.
/// Null when nobody is measuring.
/// </summary>
public sealed class IoMeter
{
    private static readonly AsyncLocal<IoMeter?> Here = new();

    public static IoMeter? Current
    {
        get => Here.Value;
        set => Here.Value = value;
    }

    /// <summary>Reads from files: bytes, time spent waiting for them, and how many calls.</summary>
    public Meter Read { get; } = new();

    /// <summary>Opening files (each part of a split archive is a file).</summary>
    public Meter Open { get; } = new();

    /// <summary>Listing a folder to find the parts of a split archive.</summary>
    public Meter List { get; } = new();

    /// <summary>Single reads slower than this are reported as stalls (a busy disk or a struggling network).</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Also counts every read here as it happens (a scan's running total, while archives are still being read).</summary>
    public IoMeter? Parent { get; init; }

    /// <summary>Where stalls are reported (the path, how long, how many bytes were asked for).</summary>
    public Action<string, long, int>? OnStall { get; init; }

    /// <summary>Wraps a stream so its reads are measured, if anything is measuring here.</summary>
    public static Stream Wrap(Stream stream, string path) => Current is { } meter ? new MeteredStream(stream, meter, path) : stream;

    /// <summary>Opens a file for reading, measured if anything is measuring here.</summary>
    public static FileStream OpenFile(string path, int bufferSize, FileOptions options = FileOptions.None)
    {
        long started = Stopwatch.GetTimestamp();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options);
        Current?.Open.Add(Stopwatch.GetTimestamp() - started);
        return stream;
    }
}

/// <summary>A read-only stream that records each read in an <see cref="IoMeter"/>.</summary>
internal sealed class MeteredStream(Stream inner, IoMeter meter, string path) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long started = Stopwatch.GetTimestamp();
        int n = inner.Read(buffer);
        Record(started, n, buffer.Length);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        int n = await inner.ReadAsync(buffer, cancellationToken);
        Record(started, n, buffer.Length);
        return n;
    }

    private void Record(long started, int read, int asked)
    {
        long elapsed = Stopwatch.GetTimestamp() - started;
        meter.Read.Add(elapsed, read);
        meter.Parent?.Read.Add(elapsed, read);
        if (elapsed > meter.StallAfter.TotalSeconds * Stopwatch.Frequency)
            meter.OnStall?.Invoke(path, (long)(elapsed * 1000.0 / Stopwatch.Frequency), asked);
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
