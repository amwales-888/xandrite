using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
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

                var work = new ConcurrentQueue<ThumbnailJobRow>(jobs);
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

    private async Task WorkerAsync(int slot, ConcurrentQueue<ThumbnailJobRow> work, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            // A slower speed leaves the higher-numbered workers idle until it is raised again (or the work runs out).
            var limits = Limits;
            if (slot >= limits.Workers)
            {
                if (work.IsEmpty) return;
                await Task.Delay(250, cancellation);
                continue;
            }
            if (!work.TryDequeue(out var job)) return;
            BackgroundWork.Current = limits;   // seen by the archive reader too

            Task resumed;
            lock (_gate)
            {
                if (_blocked is not null) return;
                resumed = _resumed.Task;
            }
            await resumed.WaitAsync(cancellation);
            lock (_gate)
            {
                if (_blocked is not null) return;
                _current = Path.GetFileName(job.Path);
            }
            Emit();
            await ProcessAsync(slot, job, cancellation);
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
        catch (ModelGoneException)
        {
            Stale(job, "the file is no longer where the scan found it");
            return;
        }
        catch (NotSupportedException e)
        {
            Stale(job, e.Message);   // an archive that cannot be read here (7z.dll missing): keep any old image
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail(job, "Could not read the file: " + e.Message);
            return;
        }

        if (BackgroundWork.Run(() => Convert.ToHexStringLower(SHA256.HashData(bytes))) != job.Sha256)
        {
            Stale(job, "the file changed since it was scanned");
            return;
        }
        long readMs = Ms();

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
