using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Xandrite.Library.Logging;

/// <summary>
/// Developer logging: every archive and file opened and closed, and every piece of background work (and the thread
/// pool's own threads) starting and ending, each with its thread id. Off unless switched on (Preferences → Developer,
/// shown when the app is started with --developer). Written to the normal log.
///
/// It is static because the opening and closing happen deep inside the readers, far from anything that holds a log;
/// when an option is off, each call costs one field read.
/// </summary>
public static class DevLog
{
    private static ILog _log = NullLog.Instance;
    private static volatile bool _archives, _files, _threads, _archiveIo;
    private static PoolThreadListener? _pool;
    private static readonly object Gate = new();

    public static ILog Log
    {
        get => _log;
        set => _log = value ?? NullLog.Instance;
    }

    /// <summary>archive.open / archive.close: zip, 7z and rar archives (each split or multi-volume set as one).</summary>
    public static bool Archives
    {
        get => _archives;
        set => _archives = value;
    }

    /// <summary>
    /// archive.read / archive.seek for every call on an archive's files (each part and volume); archive.item /
    /// archive.write / archive.item.end for each item 7-Zip goes through; archive.entry for each zip entry read.
    /// </summary>
    public static bool ArchiveIo
    {
        get => _archiveIo;
        set => _archiveIo = value;
    }

    /// <summary>file.open / file.close: every file the library reads or writes (the log file itself excepted).</summary>
    public static bool Files
    {
        get => _files;
        set => _files = value;
    }

    /// <summary>
    /// work.start / work.end for each piece of background work, and pool.thread.start / pool.thread.stop as the .NET
    /// thread pool adds and retires the threads that run it.
    /// </summary>
    public static bool Threads
    {
        get => _threads;
        set
        {
            _threads = value;
            lock (Gate)
            {
                if (value && _pool is null) _pool = new PoolThreadListener();
                else if (!value && _pool is not null) { _pool.Dispose(); _pool = null; }
            }
        }
    }

    private static int Tid => Environment.CurrentManagedThreadId;

    private static long Ms(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /* ---------- archives ---------- */

    /// <summary>Logs an archive being opened; disposing the result logs it being closed, with how long it was open.</summary>
    public static IDisposable Archive(string path, string kind, int parts = 1)
    {
        if (!_archives) return Nothing.Instance;
        _log.Info("archive.open", ("path", path), ("kind", kind), ("parts", parts), ("tid", Tid));
        return new Closer(started => _log.Info("archive.close", ("path", path), ("kind", kind), ("ms", Ms(started)), ("tid", Tid)));
    }

    /// <summary>An archive file (or one part or volume of one), with every read and seek on it logged when ArchiveIo is on.</summary>
    public static Stream ArchiveIoStream(Stream stream, string path) => _archiveIo ? new ArchiveIoLogStream(stream, path) : stream;

    /// <summary>A zip entry's content, logged as one archive.entry line (bytes, reads, time) when it is closed.</summary>
    public static Stream ArchiveEntryStream(Stream stream, string archivePath, string entry) =>
        _archiveIo ? new ArchiveEntryLogStream(stream, archivePath, entry) : stream;

    /// <summary>7-Zip starting an item: wanted (its content comes to us) or not (passed over, though in a solid
    /// block still decompressed).</summary>
    public static void ArchiveItem(string archivePath, uint index, string? name, bool wanted) =>
        _log.Info("archive.item", ("path", archivePath), ("index", index), ("entry", name), ("wanted", wanted), ("tid", Tid));

    /// <summary>7-Zip handing us a piece of an item's decompressed content.</summary>
    public static void ArchiveWrite(string archivePath, uint index, uint bytes, long total) =>
        _log.Info("archive.write", ("path", archivePath), ("index", index), ("bytes", bytes), ("total", total), ("tid", Tid));

    /// <summary>7-Zip finishing an item: its result code, the bytes it wrote and how long it took.</summary>
    public static void ArchiveItemEnd(string archivePath, uint index, string? name, int result, long bytes, long ms) =>
        _log.Info("archive.item.end", ("path", archivePath), ("index", index), ("entry", name), ("result", result), ("bytes", bytes),
            ("ms", ms), ("tid", Tid));

    /// <summary>Logs each read (where, how much asked for, how much came, how long) and each seek.</summary>
    private sealed class ArchiveIoLogStream(Stream inner, string path) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long at = inner.Position, started = Stopwatch.GetTimestamp();
            int n = inner.Read(buffer);
            Logged(at, buffer.Length, n, started);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long at = inner.Position, started = Stopwatch.GetTimestamp();
            int n = await inner.ReadAsync(buffer, cancellationToken);
            Logged(at, buffer.Length, n, started);
            return n;
        }

        private void Logged(long at, int asked, int got, long started) =>
            _log.Info("archive.read", ("path", path), ("at", at), ("asked", asked), ("got", got),
                ("us", (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds), ("tid", Tid));

        public override long Seek(long offset, SeekOrigin origin)
        {
            long from = inner.Position;
            long to = inner.Seek(offset, origin);
            _log.Info("archive.seek", ("path", path), ("from", from), ("to", to), ("origin", origin.ToString().ToLowerInvariant()),
                ("offset", offset), ("tid", Tid));
            return to;
        }

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

    /// <summary>Counts what is read from one zip entry and logs it when closed.</summary>
    private sealed class ArchiveEntryLogStream(Stream inner, string archivePath, string entry) : Stream
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _bytes, _reads;
        private int _closed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _bytes += n;
            _reads++;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(buffer, cancellationToken);
            _bytes += n;
            _reads++;
            return n;
        }

        private void Closed()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _log.Info("archive.entry", ("path", archivePath), ("entry", entry), ("bytes", _bytes), ("reads", _reads),
                ("ms", Ms(_started)), ("tid", Tid));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); Closed(); }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Closed();
            await base.DisposeAsync();
        }
    }

    /* ---------- files ---------- */

    /// <summary>Opens a file for reading; with the option on, its opening and closing are logged.</summary>
    public static FileStream OpenRead(string path, int bufferSize, FileOptions options, string purpose) =>
        Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options, purpose);

    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options, string purpose)
    {
        if (!_files) return new FileStream(path, mode, access, share, bufferSize, options);
        long started = Stopwatch.GetTimestamp();
        FileStream stream;
        try
        {
            stream = new LoggedFileStream(path, mode, access, share, bufferSize, options, purpose);
        }
        catch (Exception e)
        {
            _log.Info("file.open_failed", ("path", path), ("purpose", purpose), ("message", e.Message), ("tid", Tid));
            throw;
        }
        _log.Info("file.open", ("path", path), ("access", access.ToString().ToLowerInvariant()), ("purpose", purpose),
            ("open_ms", Ms(started)), ("tid", Tid));
        return stream;
    }

    public static async Task<byte[]> ReadAllBytesAsync(string path, string purpose, CancellationToken cancellation = default)
    {
        if (!_files) return await File.ReadAllBytesAsync(path, cancellation);
        await using var stream = OpenRead(path, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan, purpose);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        return bytes;
    }

    public static byte[] ReadAllBytes(string path, string purpose)
    {
        if (!_files) return File.ReadAllBytes(path);
        using var stream = OpenRead(path, 1 << 16, FileOptions.SequentialScan, purpose);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, string purpose)
    {
        if (!_files)
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return;
        }
        await using var stream = Open(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous, purpose);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>A file whose closing is logged, with the bytes read or written while it was open.</summary>
    private sealed class LoggedFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options, string purpose)
        : FileStream(path, mode, access, share, bufferSize, options)
    {
        private readonly long _opened = Stopwatch.GetTimestamp();
        private long _read, _written;
        private int _closed;

        // A FileStream subclass gets every read and write through these array overloads (the span and memory ones
        // call them), so counting here counts everything; overriding both would send them round in a loop.
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            _read += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await base.ReadAsync(buffer, offset, count, cancellationToken);
            _read += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            _written += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            _written += count;
        }

        private void Closed()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _log.Info("file.close", ("path", Name), ("purpose", purpose), ("read", _read), ("written", _written),
                ("open_ms", Ms(_opened)), ("tid", Tid));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Closed();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Closed();
        }
    }

    /* ---------- threads ---------- */

    /// <summary>
    /// Logs a piece of background work starting, on the thread it starts on; disposing the result logs it ending, on
    /// the thread it ends on (async work can move between pool threads at each await, so the two can differ).
    /// </summary>
    public static IDisposable Work(string name)
    {
        if (!_threads) return Nothing.Instance;
        int startTid = Tid;
        _log.Info("work.start", ("name", name), ("tid", startTid), ("pool", Thread.CurrentThread.IsThreadPoolThread));
        return new Closer(started => _log.Info("work.end", ("name", name), ("ms", Ms(started)), ("tid", Tid), ("start_tid", startTid)));
    }

    /// <summary>Runs work on the thread pool as a named piece of work.</summary>
    public static Task Run(string name, Func<Task> work) => Task.Run(async () =>
    {
        using (Work(name)) await work();
    });

    public static Task<T> Run<T>(string name, Func<Task<T>> work) => Task.Run(async () =>
    {
        using (Work(name)) return await work();
    });

    public static Task<T> Run<T>(string name, Func<T> work) => Task.Run(() =>
    {
        using (Work(name)) return work();
    });

    public static Task<T> Run<T>(string name, Func<T> work, CancellationToken cancellation) => Task.Run(() =>
    {
        using (Work(name)) return work();
    }, cancellation);

    public static Task Run(string name, Action work) => Task.Run(() =>
    {
        using (Work(name)) work();
    });

    /// <summary>
    /// The .NET runtime's own events for the thread pool adding and retiring threads (the threads every piece of work
    /// above runs on), with the operating system's id for each.
    /// </summary>
    private sealed class PoolThreadListener : EventListener
    {
        private const EventKeywords Threading = (EventKeywords)0x10000;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Informational, Threading);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            string? evt = e.EventName switch
            {
                "ThreadPoolWorkerThreadStart" => "pool.thread.start",
                "ThreadPoolWorkerThreadStop" => "pool.thread.stop",
                "ThreadPoolWorkerThreadRetirementStart" => "pool.thread.retire",
                "ThreadPoolIOWorkerThreadStart" => "pool.io_thread.start",
                "ThreadPoolIOWorkerThreadStop" => "pool.io_thread.stop",
                _ => null,
            };
            if (evt is null || !_threads) return;
            _log.Info(evt, ("os_tid", e.OSThreadId), ("active", Field(e, "ActiveWorkerThreadCount")), ("retired", Field(e, "RetiredWorkerThreadCount")));
        }

        private static string? Field(EventWrittenEventArgs e, string name)
        {
            int i = e.PayloadNames?.IndexOf(name) ?? -1;
            return i >= 0 && e.Payload is { } payload ? Convert.ToString(payload[i], CultureInfo.InvariantCulture) : null;
        }
    }

    private sealed class Closer(Action<long> close) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) close(_started);
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
