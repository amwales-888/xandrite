using System.Diagnostics;
using Xandrite.Library.Logging;
using Xandrite.Library.Sqlite;

namespace Xandrite.Library.Scanning;

public sealed record ScanProgress(int Found, int Hashed, int Reused, int Errors, string? Current);

public sealed record ScanProblem(string Path, string Message);

public sealed record ScanSummary
{
    public required string Root { get; init; }
    public bool Cancelled { get; init; }
    public int Found { get; init; }
    public int Hashed { get; init; }
    /// <summary>Files recognised as unchanged and not read again.</summary>
    public int Reused { get; init; }
    public long Added { get; init; }
    public long Moved { get; init; }
    public int Missing { get; init; }
    /// <summary>True when files were not marked missing because a folder could not be read.</summary>
    public bool MissingSkipped { get; init; }
    public long DuplicateGroups { get; init; }
    public int Archives { get; init; }
    public int NestedArchives { get; init; }
    public int Errors { get; init; }
    /// <summary>The first 50 problems.</summary>
    public IReadOnlyList<ScanProblem> Problems { get; init; } = [];
    public long DurationMs { get; init; }
}

/// <summary>
/// Scans one root folder and brings the database up to date. A port of the Electron app's src/indexer.js.
///
///  - A file whose path, size and modified time match what is stored is NOT read again. That is what makes a rescan
///    of thousands of files take seconds.
///  - Any other file is hashed and stored under its SHA-256. Models inside archives are hashed from their
///    decompressed bytes, so a model has the same SHA-256 loose or zipped.
///  - Files stored before but no longer found are marked missing, not deleted. If their content turns up elsewhere,
///    that is a "moved" file, and the SHA (and so the thumbnail) stays attached.
///  - If the scan is cancelled, or a folder cannot be read, nothing is marked missing.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase database, ModelSource? source = null)
{
    private readonly ModelSource _source = source ?? ModelSource.Default;

    /// <summary>How many files are hashed at the same time.</summary>
    public int Concurrency { get; init; } = 4;

    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>Where scans are recorded: start and end, every file hashed, every archive read, every problem.</summary>
    public ILog Log { get; init; } = NullLog.Instance;

    public Task<ScanSummary> ScanAsync(string root, bool includeArchives = true, Action<ScanProgress>? onProgress = null, CancellationToken cancellation = default) =>
        DevLog.Run("scan", () => ScanCoreAsync(root, includeArchives, onProgress, cancellation));

    private sealed class Counts
    {
        public int Found, Hashed, Reused, Errors, Archives, Nested, DirectoryErrors;
        public readonly List<ScanProblem> Problems = [];
    }

    /// <summary>How often a running scan writes scan.progress to the log.</summary>
    public TimeSpan ProgressEvery { get; init; } = TimeSpan.FromSeconds(30);

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static double? Rate(Meter meter) => meter.MBpsForLog;

    /// <summary>
    /// One line that says where the scan's time went. Times of things done by several workers at once are added up,
    /// so they can exceed the wall clock; "parallel" is the workers' total time divided by the wall clock.
    /// </summary>
    private void LogMetrics(string root, long wallMs, ScanMetrics m)
    {
        var walk = m.Walk;
        Log.Info("scan.metrics", ("root", root), ("wall_ms", wallMs),
            ("folders", walk.List.Calls), ("list_ms", walk.List.Ms), ("listed", Interlocked.Read(ref walk.Entries)),
            ("stats", walk.Stat.Calls), ("stat_ms", walk.Stat.Ms),
            ("files_read", m.LooseHash.Calls), ("files_mb", Mb(m.LooseHash.Bytes)), ("files_ms", m.LooseHash.Ms), ("files_read_ms", m.LooseRead.Ms),
            ("archives_read", m.Archives.Calls), ("archives_unchanged", Interlocked.Read(ref m.ArchivesUnchanged)), ("archives_mb", Mb(m.Archives.Bytes)),
            ("archives_ms", m.Archives.Ms), ("archive_read_mb", Mb(m.ArchiveRead.Bytes)), ("archive_read_ms", m.ArchiveRead.Ms),
            ("archive_read_calls", m.ArchiveRead.Calls), ("archive_opens", m.ArchiveOpen.Calls), ("archive_open_ms", m.ArchiveOpen.Ms),
            ("archive_lists", m.ArchiveList.Calls), ("archive_list_ms", m.ArchiveList.Ms),
            ("unpacked_mb", Mb(Interlocked.Read(ref m.Unpacked))), ("models_ms", m.Entries.Ms),
            ("read_mb_s", m.LooseRead.Ms + m.ArchiveRead.Ms > 0
                ? Math.Round((m.LooseRead.Bytes + m.ArchiveRead.Bytes) / 1048576.0 / ((m.LooseRead.Ms + m.ArchiveRead.Ms) / 1000.0), 1) : null),
            ("db_calls", m.Db.Calls), ("db_ms", m.Db.Ms), ("wait_ms", m.Wait.Ms), ("busy_ms", m.Busy.Ms),
            ("parallel", Math.Round(m.Busy.Ms / Math.Max(1.0, wallMs), 2)), ("stalls", Interlocked.Read(ref m.Stalls)));
    }

    /// <summary>The database, with the time of every call (including waiting for the lock) added to a meter.</summary>
    private sealed class TimedDatabase(LibraryDatabase db, Meter meter)
    {
        public T Locked<T>(Func<SqliteConnection, T> work) => meter.Time(() => db.Locked(work));
        public void Locked(Action<SqliteConnection> work) => meter.Time(() => db.Locked(work));
        public long Now() => db.Now();
    }

    /// <summary>Where a scan's time goes. Written to the log every 30 seconds (scan.progress) and at the end (scan.metrics).</summary>
    private sealed class ScanMetrics
    {
        public required FolderWalker.Meters Walk { get; init; }
        public Meter Db { get; } = new();          // all database work, including waiting for the lock
        public Meter Wait { get; } = new();        // the walk waiting for a free worker (all of them busy)
        public Meter Busy { get; } = new();        // workers' time, added up (busy / wall clock = how parallel it was)
        public Meter LooseRead { get; } = new();   // reading loose models from disk or the network
        public Meter LooseHash { get; } = new();   // hashing loose models, reading included
        public Meter ArchiveRead { get; } = new(); // reading archives from disk or the network (inside 7-Zip or the zip reader)
        public Meter ArchiveOpen { get; } = new(); // opening archive files (each part of a split archive)
        public Meter ArchiveList { get; } = new(); // listing a folder to find a split archive's parts
        public Meter Archives { get; } = new();    // whole archives read: time, packed bytes
        public Meter Entries { get; } = new();     // handling each model taken out of an archive: hashing it and storing it
        public IoMeter Live { get; } = new();      // every read as it happens, for scan.progress
        public long Unpacked, ArchivesUnchanged, Stalls, InFlight;
    }

    private async Task<ScanSummary> ScanCoreAsync(string root, bool includeArchives, Action<ScanProgress>? onProgress, CancellationToken cancellation)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Not a folder: " + root);

        var clock = Stopwatch.StartNew();
        var metrics = new ScanMetrics
        {
            Walk = new FolderWalker.Meters
            {
                OnSlowList = (folder, ms, entries) => Log.Warn("scan.slow", ("what", "list"), ("path", folder), ("ms", ms), ("entries", entries)),
            },
        };
        var db = new TimedDatabase(database, metrics.Db);
        void Stall(string path, long ms, int bytes)
        {
            Interlocked.Increment(ref metrics.Stalls);
            Log.Warn("scan.slow", ("what", "read"), ("path", path), ("ms", ms), ("bytes", bytes));
        }
        // The scan's id is its start time, as in the JS app, but always later than any earlier scan, so two scans in the
        // same millisecond cannot be confused (which would spoil the "new" and "missing" counts).
        long scanId = Math.Max(db.Now(), db.Locked(c => c.Scalar("SELECT COALESCE(MAX(last_seen_scan), 0) FROM locations")) + 1);
        var counts = new Counts();
        Log.Info("scan.start", ("root", root), ("archives", includeArchives), ("concurrency", Concurrency),
            ("archive_read_kb", ArchiveFiles.ReadBufferBytes / 1024));

        void Report(string? current)
        {
            if (onProgress is null) return;
            ScanProgress progress;
            lock (counts) progress = new ScanProgress(counts.Found, counts.Hashed, counts.Reused, counts.Errors, current);
            onProgress(progress);
        }
        void Problem(string path, string message, bool directory = false)
        {
            Log.Warn("problem", ("path", path), ("message", message));
            lock (counts)
            {
                counts.Errors++;
                if (directory) counts.DirectoryErrors++;
                if (counts.Problems.Count < 50) counts.Problems.Add(new ScanProblem(path, message));
            }
        }
        void Count(Action<Counts> change)
        {
            lock (counts) change(counts);
        }

        const string MarkSeen = "UPDATE locations SET last_seen_scan = ?, missing = 0 WHERE path = ?";
        const string InsertModel = "INSERT OR IGNORE INTO models(sha256, size, format, first_seen_at) VALUES (?, ?, ?, ?)";
        const string UpsertLocation = """
            INSERT INTO locations(path, root, sha256, size, mtime_ms, last_seen_scan, added_scan, missing, container, entry, crc32)
            VALUES (?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?)
            ON CONFLICT(path) DO UPDATE SET
              sha256 = excluded.sha256, size = excluded.size, mtime_ms = excluded.mtime_ms,
              last_seen_scan = excluded.last_seen_scan, missing = 0,
              container = excluded.container, entry = excluded.entry, crc32 = excluded.crc32
            """;
        const string MarkContainerSeen = "UPDATE locations SET last_seen_scan = ? WHERE container = ? AND missing = 0";
        const string SaveContainer = """
            INSERT INTO containers(path, root, size, mtime_ms, last_seen_scan) VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(path) DO UPDATE SET root = excluded.root, size = excluded.size, mtime_ms = excluded.mtime_ms, last_seen_scan = excluded.last_seen_scan
            """;
        const string TouchContainer = "UPDATE containers SET last_seen_scan = ? WHERE path = ?";

        void Store(string path, string sha, long size, string format, double mtimeMs, string? container, string? entry, long? crc) =>
            db.Locked(c =>
            {
                c.Execute(InsertModel, sha, size, format, db.Now());
                c.Execute(UpsertLocation, path, root, sha, size, mtimeMs, scanId, scanId, container, entry, crc);
            });

        async Task HashAndStore(WalkItem item)
        {
            using var work = DevLog.Work("scan.hash");
            long jobStarted = Stopwatch.GetTimestamp();
            var io = new IoMeter { OnStall = Stall, Parent = metrics.Live };
            IoMeter.Current = io;
            try
            {
                long started = Stopwatch.GetTimestamp();
                string sha = await Hashing.HashFileAsync(item.Path, cancellation);
                metrics.LooseHash.Add(Stopwatch.GetTimestamp() - started, item.Size);
                Store(item.Path, sha, item.Size, Path.GetExtension(item.Path)[1..].ToLowerInvariant(), item.MtimeMs, null, null, null);
                Count(c => c.Hashed++);
                Log.Info("hash", ("path", item.Path), ("sha256", sha), ("size", item.Size), ("ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                    ("read_ms", io.Read.Ms));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file that could not be read (locked, permission denied) keeps its old record instead of going missing.
                db.Locked(c => c.Execute(MarkSeen, scanId, item.Path));
                Problem(item.Path, e.Message);
            }
            finally
            {
                IoMeter.Current = null;
                metrics.LooseRead.Add(io.Read);
                metrics.Busy.Add(Stopwatch.GetTimestamp() - jobStarted);
            }
            Report(item.Path);
        }

        // Every model entry is hashed from its decompressed bytes. Entries whose size and checksum are unchanged are not
        // read again. (The JS app also compared entry dates; .NET reads zip dates slightly differently, and the CRC-32
        // already tells whether the content changed.)
        async Task ScanArchive(WalkItem item, IArchiveReader reader)
        {
            using var work = DevLog.Work("scan.archive");
            bool clean = true;
            long archiveStarted = Stopwatch.GetTimestamp();
            int models = 0, reused = 0;   // this archive's own counts (several archives are read at once, so not the shared totals)
            long unpacked = 0;
            var io = new IoMeter { OnStall = Stall, Parent = metrics.Live };
            var handling = new Meter();   // time spent on this archive's models once 7-Zip or the zip reader handed them over
            IoMeter.Current = io;          // seen by the reader, which opens and reads the archive
            try
            {
                int nested = await reader.EachModelEntryAsync(item.Path,
                    async entry =>
                    {
                        using var _ = handling.Time();
                        Count(c => c.Found++);
                        Interlocked.Increment(ref models);
                        string virtualPath = ArchiveEntryNames.VirtualPath(item.Path, entry.EntryName);
                        var known = db.Locked(c => c.QueryFirst("SELECT size, crc32 FROM locations WHERE path = ?",
                            r => (Size: r.GetInt64(0), Crc: r.GetInt64OrNull(1)), virtualPath));
                        if (known.Crc is { } crc && known.Size == entry.Size && crc == entry.Crc32)
                        {
                            db.Locked(c => c.Execute(MarkSeen, scanId, virtualPath));
                            Count(c => c.Reused++);
                            Interlocked.Increment(ref reused);
                            Report(virtualPath);
                            return;
                        }
                        try
                        {
                            long started = Stopwatch.GetTimestamp();
                            string sha;
                            await using (var stream = entry.Open()) sha = await Hashing.HashStreamAsync(stream, cancellation);
                            Interlocked.Add(ref unpacked, entry.Size);
                            Store(virtualPath, sha, entry.Size, entry.Format, entry.MtimeMs, item.Path, entry.EntryName, entry.Crc32);
                            Count(c => c.Hashed++);
                            Log.Info("hash", ("path", virtualPath), ("sha256", sha), ("size", entry.Size),
                                ("ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), ("archive", item.Path));
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException)
                        {
                            clean = false;
                            db.Locked(c => c.Execute(MarkSeen, scanId, virtualPath));   // keep any old record instead of marking it missing
                            Problem(virtualPath, e.Message);
                        }
                        Report(virtualPath);
                    },
                    (entryName, message) => Problem(ArchiveEntryNames.VirtualPath(item.Path, entryName), message),
                    Limits, cancellation);
                Count(c => { c.Nested += nested; c.Archives++; });
                long ms = (long)Stopwatch.GetElapsedTime(archiveStarted).TotalMilliseconds;
                // read_ms: waiting for the disk or network. models_ms: hashing and storing the models.
                // other_ms: the rest, mostly decompressing (7-Zip) and opening.
                Log.Info("archive", ("path", item.Path), ("models", models), ("unchanged", reused), ("nested", nested), ("parts", item.Parts),
                    ("size_mb", Mb(item.Size)), ("read_mb", Mb(io.Read.Bytes)), ("unpacked_mb", Mb(Interlocked.Read(ref unpacked))), ("ms", ms),
                    ("read_ms", io.Read.Ms), ("read_mb_s", Rate(io.Read)), ("models_ms", handling.Ms),
                    ("other_ms", Math.Max(0, ms - io.Read.Ms - handling.Ms)), ("open_ms", io.Open.Ms), ("list_ms", io.List.Ms));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                clean = false;
                db.Locked(c => c.Execute(MarkContainerSeen, scanId, item.Path));   // an unreadable archive keeps its old records
                Problem(item.Path, "could not read this archive: " + e.Message);
            }
            finally
            {
                IoMeter.Current = null;
                long elapsed = Stopwatch.GetTimestamp() - archiveStarted;
                metrics.Archives.Add(elapsed, item.Size);
                metrics.ArchiveRead.Add(io.Read);
                metrics.ArchiveOpen.Add(io.Open);
                metrics.ArchiveList.Add(io.List);
                metrics.Entries.Add(handling);
                Interlocked.Add(ref metrics.Unpacked, Interlocked.Read(ref unpacked));
                metrics.Busy.Add(elapsed);
            }
            // A clean read is remembered, so an unchanged archive is not opened again; otherwise it is looked at next scan.
            db.Locked(c => c.Execute(clean ? SaveContainer : TouchContainer, clean ? [item.Path, root, item.Size, item.MtimeMs, scanId] : [scanId, item.Path]));
            Report(item.Path);
        }

        var inflight = new List<Task>();
        async Task Launch(Task job)
        {
            inflight.Add(job);
            Interlocked.Exchange(ref metrics.InFlight, inflight.Count);
            if (inflight.Count >= Concurrency)
            {
                long waited = Stopwatch.GetTimestamp();
                var done = await Task.WhenAny(inflight);
                metrics.Wait.Add(Stopwatch.GetTimestamp() - waited);
                inflight.Remove(done);
                Interlocked.Exchange(ref metrics.InFlight, inflight.Count);
                await done;
            }
        }

        // Every 30 seconds: how far it has got and how fast it is going now.
        using var stopProgress = new CancellationTokenSource();
        var progress = DevLog.Run("scan.progress", async () =>
        {
            using var timer = new PeriodicTimer(ProgressEvery);
            long lastRead = 0, lastModels = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(stopProgress.Token))
                {
                    long read = metrics.Live.Read.Bytes;
                    long done;
                    int found, hashed, reused, archives;
                    lock (counts) (found, hashed, reused, archives) = (counts.Found, counts.Hashed, counts.Reused, counts.Archives);
                    done = hashed + reused;
                    double seconds = ProgressEvery.TotalSeconds;
                    Log.Info("scan.progress", ("root", root), ("elapsed_s", (long)clock.Elapsed.TotalSeconds), ("found", found), ("hashed", hashed),
                        ("unchanged", reused), ("archives", archives), ("folders", metrics.Walk.List.Calls), ("in_flight", Interlocked.Read(ref metrics.InFlight)),
                        ("read_mb", Mb(read)), ("read_mb_s", Math.Round((read - lastRead) / 1048576.0 / seconds, 1)),
                        ("models_s", Math.Round((done - lastModels) / seconds, 1)), ("db_ms", metrics.Db.Ms), ("stalls", Interlocked.Read(ref metrics.Stalls)));
                    (lastRead, lastModels) = (read, done);
                }
            }
            catch (OperationCanceledException) { }
        });

        foreach (var item in FolderWalker.Walk(root, includeArchives, cancellation, metrics.Walk))
        {
            if (item.Error is not null)
            {
                Problem(item.Path, item.Error, item.IsDirectory);
                Report(item.Path);
                continue;
            }

            if (item.IsArchive)
            {
                if (item.Incomplete is not null)
                {
                    db.Locked(c => c.Execute(MarkContainerSeen, scanId, item.Path));   // keep what is known about an incomplete set
                    Problem(item.Path, "could not read this archive: " + item.Incomplete);
                    Report(item.Path);
                    continue;
                }
                var reader = _source.ReaderFor(item.Path);
                if (reader is null)
                {
                    // No reader for this kind of archive: keep its models as they are.
                    db.Locked(c => c.Execute(MarkContainerSeen, scanId, item.Path));
                    Problem(item.Path, "could not read this archive: there is no reader for " + Path.GetExtension(item.Path) + " archives");
                    Report(item.Path);
                    continue;
                }
                var stored = db.Locked(c => c.QueryFirst("SELECT size, mtime_ms FROM containers WHERE path = ?",
                    r => (Size: r.GetInt64(0), Mtime: r.GetDouble(1), Known: true), item.Path));
                if (stored.Known && stored.Size == item.Size && stored.Mtime == item.MtimeMs)
                {
                    int n = db.Locked(c =>
                    {
                        int changed = c.Execute(MarkContainerSeen, scanId, item.Path);
                        c.Execute(TouchContainer, scanId, item.Path);
                        return changed;
                    });
                    Count(c => { c.Found += n; c.Reused += n; c.Archives++; });
                    Interlocked.Increment(ref metrics.ArchivesUnchanged);
                    Report(item.Path);
                    continue;
                }
                await Launch(ScanArchive(item, reader));
                continue;
            }

            Count(c => c.Found++);
            var known = db.Locked(c => c.QueryFirst("SELECT size, mtime_ms FROM locations WHERE path = ?",
                r => (Size: r.GetInt64(0), Mtime: r.GetDouble(1), Known: true), item.Path));
            if (known.Known && known.Size == item.Size && known.Mtime == item.MtimeMs)
            {
                db.Locked(c => c.Execute(MarkSeen, scanId, item.Path));
                Count(c => c.Reused++);
                Report(item.Path);
                continue;
            }
            await Launch(HashAndStore(item));
        }
        await Task.WhenAll(inflight);
        stopProgress.Cancel();
        await progress;

        bool cancelled = cancellation.IsCancellationRequested;
        int missing = 0;
        return db.Locked(c =>
        {
            // Only mark files missing after a complete, clean scan; a partial scan would make files look deleted.
            if (!cancelled && counts.DirectoryErrors == 0)
            {
                missing = c.Execute("UPDATE locations SET missing = 1 WHERE root = ? AND last_seen_scan <> ? AND missing = 0", root, scanId);
                c.Execute("DELETE FROM containers WHERE root = ? AND last_seen_scan <> ?", root, scanId);   // archives that are gone
            }
            if (!cancelled) c.Execute("UPDATE roots SET last_scanned_at = ? WHERE path = ?", db.Now(), root);

            long added = c.Scalar("SELECT COUNT(*) FROM locations WHERE root = ? AND added_scan = ?", root, scanId);
            // A "moved" file is new at this scan, and the same content has a location that is now missing.
            long moved = c.Scalar("""
                SELECT COUNT(DISTINCT n.sha256)
                FROM locations n JOIN locations o ON o.sha256 = n.sha256 AND o.path <> n.path
                WHERE n.root = ? AND n.added_scan = ? AND o.missing = 1
                """, root, scanId);
            long duplicates = c.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM locations WHERE root = ? AND missing = 0 GROUP BY sha256 HAVING COUNT(*) > 1)", root);

            lock (counts)
            {
                Log.InfoOrWarn(cancelled || counts.Errors > 0, "scan.end", ("root", root), ("cancelled", cancelled), ("duration_ms", clock.ElapsedMilliseconds), ("found", counts.Found),
                    ("hashed", counts.Hashed), ("unchanged", counts.Reused), ("new", added), ("moved", moved), ("missing", missing),
                    ("duplicate_groups", duplicates), ("archives", counts.Archives), ("nested_archives", counts.Nested), ("errors", counts.Errors));
                LogMetrics(root, clock.ElapsedMilliseconds, metrics);
                return new ScanSummary
                {
                    Root = root,
                    Cancelled = cancelled,
                    Found = counts.Found,
                    Hashed = counts.Hashed,
                    Reused = counts.Reused,
                    Added = added,
                    Moved = moved,
                    Missing = missing,
                    MissingSkipped = !cancelled && counts.DirectoryErrors > 0,
                    DuplicateGroups = duplicates,
                    Archives = counts.Archives,
                    NestedArchives = counts.Nested,
                    Errors = counts.Errors,
                    Problems = counts.Problems.ToList(),
                    DurationMs = clock.ElapsedMilliseconds,
                };
            }
        });
    }
}
