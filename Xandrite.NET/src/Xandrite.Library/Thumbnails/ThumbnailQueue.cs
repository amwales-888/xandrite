using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Xandrite.Framing;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Loading;
using Xandrite.Rendering;
using Xandrite.Thumbnails;

namespace Xandrite.Library.Thumbnails;

/// <summary>A snapshot of the queue, for the status bar.</summary>
public sealed record ThumbnailQueueStatus(
    bool Running, bool Paused, string? Blocked, int Total, int Done, int Failed, int Skipped, string? Current, ThumbnailCounts Counts);

/// <summary>One model finished: drawn, or failed with a reason.</summary>
public sealed record ThumbnailJobDone(string Sha256, ThumbnailState State, string? Error);

/// <summary>
/// Draws a thumbnail for every model that needs one and saves it as &lt;thumbnails&gt;\&lt;sha256&gt;.png.
/// A port of the Electron app's src/thumbnail-queue.js, with several workers drawing at once, each with its own renderer.
///
///  - A model whose file has gone or changed since the scan is skipped for this run (a rescan sorts it out).
///  - A model that cannot be read or drawn is recorded as failed, with the reason; "Retry failed" tries again.
///  - If the renderer itself fails (no graphics device, driver reset), the queue pauses with <see cref="ThumbnailQueueStatus.Blocked"/>
///    set, instead of failing every model. Resume starts fresh renderers.
///  - A thumbnail row whose image has been deleted is forgotten, so the image is drawn again.
///
/// Events are raised on worker threads.
/// </summary>
public sealed class ThumbnailQueue : IAsyncDisposable
{
    private readonly LibraryDatabase _db;
    private readonly LibraryLocation _location;
    private readonly Func<IModelRenderer> _createRenderer;
    private readonly ModelSource _source;
    private readonly IModelRenderer?[] _renderers;
    private readonly ConcurrentDictionary<string, byte> _skip = new();
    private readonly object _gate = new();

    private Task? _run;
    private CancellationTokenSource _cancel = new();
    private TaskCompletionSource _resumed = Resumed();
    private bool _paused;
    private bool _active;      // a run is going (cleared before Idle is raised)
    private bool _again;       // Start was called during a run: look for work once more before stopping
    private string? _blocked;
    private int _total, _done, _failed, _skipped;
    private string? _current;
    private ThumbnailSpeed _speed;
    private readonly ConcurrentDictionary<ArchiveStream, byte> _streams = new();
    private int _archivesOpen;   // archives taken from the work but not finished: idle workers stay to help (under _gate)

    public ThumbnailQueue(LibraryDatabase db, LibraryLocation location, Func<IModelRenderer> createRenderer, ModelSource? source = null, int workers = 2)
    {
        _db = db;
        _location = location;
        _createRenderer = createRenderer;
        _source = source ?? ModelSource.Default;
        _renderers = new IModelRenderer?[Math.Clamp(workers, 1, 16)];
    }

    /// <summary>
    /// How hard to work. Takes effect from the next model: workers beyond the new number finish the model in hand
    /// and wait.
    /// </summary>
    public ThumbnailSpeed Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            lock (_gate)
            {
                if (_speed == value) return;
                _speed = value;
            }
            var limits = Limits;
            Log.Info("thumbs.speed", ("speed", value.ToString().ToLowerInvariant()), ("workers", limits.Workers),
                ("priority", limits.Priority.ToString().ToLowerInvariant()), ("archive_threads", limits.ArchiveThreads?.ToString() ?? "all"));
        }
    }

    /// <summary>What <see cref="Speed"/> means on this machine.</summary>
    public WorkLimits Limits => WorkLimits.For(Speed, _renderers.Length, Environment.ProcessorCount);

    /// <summary>Models larger than this are not drawn (recorded as failed with a reason).</summary>
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Where the queue records each thumbnail (with its timings), failures, skips and totals.</summary>
    public ILog Log { get; init; } = NullLog.Instance;

    public event Action<ThumbnailQueueStatus>? Progress;
    public event Action<ThumbnailJobDone>? JobDone;
    public event Action? Idle;

    public string ImagePath(string sha256) => _location.ThumbnailPath(sha256);

    public bool IsRunning
    {
        get { lock (_gate) return _active; }
    }

    private static TaskCompletionSource Resumed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>Starts working through the models that need a thumbnail. Does nothing if it is already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_active)
            {
                _again = true;   // new work may have arrived (a scan just finished): the run looks again before it ends
                return;
            }
            _active = true;
            _again = false;
            _paused = false;
            _resumed.TrySetResult();   // never start behind a gate left over from an earlier run
            if (_cancel.IsCancellationRequested) _cancel = new CancellationTokenSource();
            _blocked = null;
            _total = _done = _failed = _skipped = 0;
            _current = null;
            _run = Task.Run(RunAsync);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused || !_active) return;
            _paused = true;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Log.Info("thumbs.paused");
        Emit();
    }

    public void Resume()
    {
        bool start;
        lock (_gate)
        {
            _blocked = null;
            start = !_active;
            if (_paused)
            {
                _paused = false;
                _resumed.TrySetResult();
                Log.Info("thumbs.resumed");
            }
        }
        if (start) Start();
        Emit();
    }

    /// <summary>Leaves a model alone for the rest of this run (the viewer has just saved its image).</summary>
    public void Skip(string sha256) => _skip[sha256] = 0;

    /// <summary>Lets a skipped model be drawn again (it was regenerated or reverted).</summary>
    public void Unskip(string sha256) => _skip.TryRemove(sha256, out _);

    public ThumbnailQueueStatus GetStatus()
    {
        var counts = _db.GetThumbnailCounts(Look.RenderVersion);
        lock (_gate)
            return new ThumbnailQueueStatus(_active, _paused, _blocked, _total, _done, _failed, _skipped, _current, counts);
    }

    private void Emit() => Progress?.Invoke(GetStatus());

    private async Task RunAsync()
    {
        var cancellation = _cancel.Token;
        long started = Stopwatch.GetTimestamp();
        try
        {
            Directory.CreateDirectory(_location.ThumbnailFolder);
            ForgetMissingImages();
            Log.Info("thumbs.start", ("pending", _db.GetThumbnailCounts(Look.RenderVersion).Pending), ("workers", Limits.Workers), ("speed", Speed.ToString().ToLowerInvariant()));

            while (!cancellation.IsCancellationRequested)
            {
                lock (_gate) _again = false;
                var jobs = _db.PendingThumbnailJobs(Look.RenderVersion).Where(j => !_skip.ContainsKey(j.Sha256)).ToList();
                if (jobs.Count == 0)
                {
                    lock (_gate) if (!_again) break;
                    continue;
                }
                lock (_gate) _total += jobs.Count;
                Emit();

                var work = new ConcurrentQueue<WorkItem>(Plan(jobs));
                await Task.WhenAll(Enumerable.Range(0, _renderers.Length).Select(slot => Task.Run(() => WorkerAsync(slot, work, cancellation))));

                // The renderer broke: wait for Resume (which clears the message), then look at what is still pending.
                Task wait;
                lock (_gate)
                {
                    if (_blocked is null) continue;
                    if (!_paused)
                    {
                        _paused = true;
                        _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    wait = _resumed.Task;
                }
                Emit();
                await wait.WaitAsync(cancellation);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: pending models stay pending for next time.
        }
        catch (Exception e)
        {
            lock (_gate) _blocked = "The thumbnail queue stopped: " + e.Message;
            Log.Error("thumbs.crashed", ("message", e.ToString()));
        }
        finally
        {
            bool restart;
            lock (_gate)
            {
                Log.InfoOrWarn(_failed > 0 || cancellation.IsCancellationRequested, "thumbs.end",
                    ("duration_ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), ("made", _done), ("failed", _failed),
                    ("skipped", _skipped), ("cancelled", cancellation.IsCancellationRequested));
                _current = null;
                _active = false;
                _paused = false;
                restart = _again && !cancellation.IsCancellationRequested;
                _again = false;
            }
            if (restart)
            {
                Start();
            }
            else
            {
                Emit();
                Idle?.Invoke();
            }
        }
    }

    private void ForgetMissingImages()
    {
        foreach (var sha in _db.OkThumbnailShas())
        {
            if (!File.Exists(ImagePath(sha))) _db.DeleteThumbnailRow(sha);
        }
    }

    /* ---------- the work: loose models one by one, archives in one pass ---------- */

    private abstract record WorkItem;

    /// <summary>A model read on its own: a loose file, or the only model wanted from its archive.</summary>
    private sealed record OneModel(ThumbnailJobRow Job) : WorkItem;

    /// <summary>Several models from one archive, read in a single pass (a solid .7z is decompressed once, not once each).</summary>
    private sealed record FromArchive(string Archive, IReadOnlyList<ThumbnailJobRow> Jobs) : WorkItem;

    /// <summary>A model read from an archive and waiting to be drawn, with how long reading it took.</summary>
    private sealed record ReadModel(ThumbnailJobRow Job, EntryRead Read, long ReadMs);

    /// <summary>
    /// The models coming out of one archive. The worker that took the archive draws them as they arrive; idle workers
    /// help. Only a couple wait at a time, so memory stays small and the archive is read no faster than they are drawn.
    /// </summary>
    private sealed class ArchiveStream
    {
        public Channel<ReadModel> Models { get; } = Channel.CreateBounded<ReadModel>(new BoundedChannelOptions(2) { SingleWriter = true });
        public Exception? Error { get; set; }
        public ConcurrentDictionary<string, byte> Delivered { get; } = new();
    }

    /// <summary>Puts the models of each archive together (in the place of its first model), the rest one by one.</summary>
    private IEnumerable<WorkItem> Plan(IReadOnlyList<ThumbnailJobRow> jobs)
    {
        var byArchive = jobs.Where(j => j.Container is not null && j.Entry is not null && j.Size <= MaxBytes)
            .GroupBy(j => j.Container!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (job.Container is { } archive && byArchive.TryGetValue(archive, out var group) && group.Contains(job))
            {
                if (planned.Add(archive)) yield return new FromArchive(archive, group);
            }
            else
            {
                yield return new OneModel(job);
            }
        }
    }

    private async Task WorkerAsync(int slot, ConcurrentQueue<WorkItem> work, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            // A slower speed leaves the higher-numbered workers idle until it is raised again (or the work runs out).
            var limits = Limits;
            if (slot >= limits.Workers)
            {
                lock (_gate) if (work.IsEmpty && _archivesOpen == 0) return;
                await Task.Delay(250, cancellation);
                continue;
            }
            BackgroundWork.Current = limits;   // seen by the archive reader too
            if (!await WaitIfPausedAsync(cancellation)) return;

            // Help draw what archives have already read, before starting anything new.
            if (TryTakeRead(out var read))
            {
                await DrawReadAsync(slot, read);
                continue;
            }
            WorkItem? item;
            bool archivesOpen;
            lock (_gate)
            {
                // Taken and counted together, so no worker can see "no work, no archives" while one is being opened.
                if (work.TryDequeue(out item) && item is FromArchive) _archivesOpen++;
                archivesOpen = _archivesOpen > 0;
            }
            if (item is not null)
            {
                if (item is OneModel one)
                {
                    Showing(one.Job);
                    await ProcessAsync(slot, one.Job, cancellation);
                }
                else
                {
                    await ReadArchiveAsync(slot, (FromArchive)item, cancellation);
                }
                continue;
            }
            if (!archivesOpen) return;
            await WaitForReadAsync(cancellation);   // nothing new: wait for an archive being read to produce a model
        }
    }

    /// <returns>False if the queue is blocked (the renderer broke): stop.</returns>
    private async Task<bool> WaitIfPausedAsync(CancellationToken cancellation)
    {
        Task resumed;
        lock (_gate)
        {
            if (_blocked is not null) return false;
            resumed = _resumed.Task;
        }
        await resumed.WaitAsync(cancellation);
        lock (_gate) return _blocked is null;
    }

    private void Showing(ThumbnailJobRow job)
    {
        lock (_gate) _current = Path.GetFileName(job.Path);
        Emit();
    }

    private bool TryTakeRead(out ReadModel read)
    {
        foreach (var stream in _streams.Keys)
        {
            if (stream.Models.Reader.TryRead(out read!)) return true;
        }
        read = null!;
        return false;
    }

    private async Task WaitForReadAsync(CancellationToken cancellation)
    {
        var waits = _streams.Keys.Where(s => !s.Models.Reader.Completion.IsCompleted)
            .Select(s => s.Models.Reader.WaitToReadAsync(cancellation).AsTask())
            .Append(Task.Delay(200, cancellation))
            .ToList();
        await Task.WhenAny(waits);
        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Reads the archive's models in one pass on another thread while drawing them here (with help from idle workers).
    /// If the archive cannot be read at all, the models it had not delivered get the same answer as when read one by one.
    /// </summary>
    private async Task ReadArchiveAsync(int slot, FromArchive group, CancellationToken cancellation)
    {
        try
        {
            await ReadArchiveCoreAsync(slot, group, cancellation);
        }
        finally
        {
            lock (_gate) _archivesOpen--;
        }
    }

    private async Task ReadArchiveCoreAsync(int slot, FromArchive group, CancellationToken cancellation)
    {
        var reader = _source.ReaderFor(group.Archive);
        if (reader is null)
        {
            foreach (var job in group.Jobs) Stale(job, "Reading models from " + Path.GetExtension(group.Archive) + " archives is not available yet.");
            return;
        }

        long started = Stopwatch.GetTimestamp();
        var byEntry = new Dictionary<string, ThumbnailJobRow>(StringComparer.Ordinal);
        foreach (var job in group.Jobs) byEntry.TryAdd(job.Entry!, job);
        var stream = new ArchiveStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var limits = BackgroundWork.Current;
        var producer = Task.Run(async () =>
        {
            BackgroundWork.Current = limits;
            long last = Stopwatch.GetTimestamp();
            try
            {
                await reader.ReadEntriesAsync(group.Archive, byEntry.Keys, MaxBytes, async read =>
                {
                    if (!byEntry.TryGetValue(read.Name, out var job)) return;
                    long now = Stopwatch.GetTimestamp();
                    long readMs = (long)Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                    last = now;
                    stream.Delivered[read.Name] = 0;
                    await stream.Models.Writer.WriteAsync(new ReadModel(job, read, readMs), stop.Token);
                }, stop.Token);
            }
            catch (Exception e)
            {
                stream.Error = e;
            }
            finally
            {
                stream.Models.Writer.TryComplete();
            }
        }, CancellationToken.None);

        _streams[stream] = 0;
        try
        {
            while (await stream.Models.Reader.WaitToReadAsync(cancellation))
            {
                if (!await WaitIfPausedAsync(cancellation)) return;   // the renderer broke: what is left stays pending
                if (stream.Models.Reader.TryRead(out var read)) await DrawReadAsync(slot, read);
            }
        }
        finally
        {
            stop.Cancel();
            _streams.TryRemove(stream, out _);
            await producer;
        }

        var notRead = group.Jobs.Where(j => !stream.Delivered.ContainsKey(j.Entry!)).ToList();
        if (stream.Error is { } error and not OperationCanceledException)
        {
            foreach (var job in notRead) ReadFailed(job, error, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else if (!cancellation.IsCancellationRequested)
        {
            foreach (var job in notRead) Stale(job, "the file is no longer where the scan found it");
        }
        Log.Info("thumbs.archive", ("archive", group.Archive), ("models", group.Jobs.Count), ("read", stream.Delivered.Count),
            ("ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    private async Task DrawReadAsync(int slot, ReadModel model)
    {
        var (job, read, readMs) = model;
        Showing(job);
        long started = Stopwatch.GetTimestamp() - (long)(readMs * Stopwatch.Frequency / 1000.0);   // so ms= includes reading, as for loose files
        if (read.Gone) Stale(job, "the file is no longer where the scan found it");
        else if (read.Bytes is null) FailJob(job, "Could not read the file: " + read.Problem, readMs);
        else await DrawAsync(slot, job, read.Bytes, started, readMs);
    }

    /// <summary>What a model that could not be read is recorded as: gone, not readable here, or failed.</summary>
    private void ReadFailed(ThumbnailJobRow job, Exception e, long ms)
    {
        switch (e)
        {
            case ModelGoneException:
                Stale(job, "the file is no longer where the scan found it");
                break;
            case NotSupportedException:
                Stale(job, e.Message);   // an archive that cannot be read here (7z.dll missing): keep any old image
                break;
            case IOException or UnauthorizedAccessException or InvalidDataException:
                FailJob(job, "Could not read the file: " + e.Message, ms);
                break;
            default:
                ExceptionDispatchInfo.Throw(e);
                break;
        }
    }

    private async Task ProcessAsync(int slot, ThumbnailJobRow job, CancellationToken cancellation)
    {
        long started = Stopwatch.GetTimestamp();
        long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        void Fail(ThumbnailJobRow failed, string message) => FailJob(failed, message, Ms());

        if (job.Size > MaxBytes)
        {
            Fail(job, $"The file is too large to thumbnail (limit {MaxBytes / 1048576} MB).");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await _source.ReadAsync(new ModelLocation(job.Path, job.Container, job.Entry), MaxBytes, cancellation);
        }
        catch (Exception e) when (e is ModelGoneException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReadFailed(job, e, Ms());
            return;
        }
        await DrawAsync(slot, job, bytes, started, Ms());
    }

    /// <summary>Checks the bytes are still the model the scan found, draws it and saves the image.</summary>
    private async Task DrawAsync(int slot, ThumbnailJobRow job, byte[] bytes, long started, long readMs)
    {
        long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        void Fail(ThumbnailJobRow failed, string message) => FailJob(failed, message, Ms());

        if (BackgroundWork.Run(() => Convert.ToHexStringLower(SHA256.HashData(bytes))) != job.Sha256)
        {
            Stale(job, "the file changed since it was scanned");
            return;
        }

        ThumbnailResult result;
        SavedView? usedView;
        try
        {
            var renderer = _renderers[slot] ??= _createRenderer();
            usedView = _db.GetView(job.Sha256);
            var view = usedView is { } saved
                ? new StoredView(ToVector(saved.Position), ToVector(saved.Target), (float)saved.FovDeg, saved.ZUp)
                : null;
            result = BackgroundWork.Run(() => new ThumbnailGenerator(renderer).Generate(new ThumbnailJob(bytes, job.Format, Path.GetFileName(job.Path), job.Sha256, view)));
        }
        catch (RendererException e)
        {
            // Not the model's fault: drop this renderer and wait for the person to resume.
            _renderers[slot]?.Dispose();
            _renderers[slot] = null;
            lock (_gate) _blocked = e.Message;
            Log.Error("thumbs.blocked", ("message", e.Message), ("device_lost", e.DeviceLost));
            Emit();
            return;
        }
        catch (ModelFormatException e)
        {
            Fail(job, e.Message);
            return;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OutOfMemoryException or OverflowException)
        {
            Fail(job, e.Message);
            return;
        }

        using (await ThumbnailFiles.LockAsync(job.Sha256))
        {
            // The viewer may have saved (or dropped) a view while this was drawing: then its image is the right one,
            // and a revert has already put the model back in the queue.
            if (!SameView(usedView, _db.GetView(job.Sha256)))
            {
                Log.Info("thumb.skip", ("path", job.Path), ("sha256", job.Sha256), ("reason", "the view was changed while drawing"));
                lock (_gate) _skipped++;
                Emit();
                return;
            }
            try
            {
                await ThumbnailFiles.WriteAsync(ImagePath(job.Sha256), result.Png);   // never a half-written image under the real name
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(job, "Could not save the thumbnail: " + e.Message);
                return;
            }
            _db.RecordThumbnail(job.Sha256, ThumbnailState.Ok, Look.RenderVersion, Look.ThumbnailSize);
        }
        Log.Info("thumb.ok", ("path", job.Path), ("sha256", job.Sha256), ("ms", Ms()), ("read_ms", readMs), ("size", job.Size), ("triangles", result.Triangles));
        lock (_gate) _done++;
        JobDone?.Invoke(new ThumbnailJobDone(job.Sha256, ThumbnailState.Ok, null));
        Emit();
    }

    private static bool SameView(SavedView? a, SavedView? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Position.AsSpan().SequenceEqual(b.Position) && a.Target.AsSpan().SequenceEqual(b.Target) && a.FovDeg == b.FovDeg && a.ZUp == b.ZUp;

    private static Vector3 ToVector(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);

    private void FailJob(ThumbnailJobRow job, string message, long ms)
    {
        Log.Warn("thumb.fail", ("path", job.Path), ("sha256", job.Sha256), ("ms", ms), ("message", message));
        _db.RecordThumbnail(job.Sha256, ThumbnailState.Failed, Look.RenderVersion, error: message);
        lock (_gate) _failed++;
        JobDone?.Invoke(new ThumbnailJobDone(job.Sha256, ThumbnailState.Failed, message));
        Emit();
    }

    private void Stale(ThumbnailJobRow job, string reason)
    {
        Log.Warn("thumb.skip", ("path", job.Path), ("sha256", job.Sha256), ("reason", reason));
        _skip[job.Sha256] = 0;
        lock (_gate) _skipped++;
    }

    /// <summary>Stops after the models being drawn now. Pending models stay pending.</summary>
    public async Task StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            _cancel.Cancel();
            _resumed.TrySetResult();
            _paused = false;
            run = _run;
        }
        if (run is not null)
        {
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        for (int i = 0; i < _renderers.Length; i++)
        {
            _renderers[i]?.Dispose();
            _renderers[i] = null;
        }
    }
}
