using System.Globalization;
using System.Text;
using System.Text.Json;
using Xandrite.Library.Sqlite;

namespace Xandrite.Library;

/// <summary>
/// The library index, a port of the Electron app's src/db.js with the same schema, so a library.sqlite written by
/// the Electron app opens here unchanged (and the other way round).
///
/// Two tables carry the idea behind the whole app:
///   models     one row per distinct file content, identified by its SHA-256
///   locations  every path where some content has been seen
/// Two copies of a model share one models row and have two locations rows. When a file is moved, its content is
/// found again by hash at the new path, so the thumbnail that belongs to the SHA is kept.
///
/// Thread-safe: every call takes the same lock.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS roots (
          path            TEXT PRIMARY KEY,
          added_at        INTEGER NOT NULL,
          last_scanned_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS models (
          sha256        TEXT PRIMARY KEY,
          size          INTEGER NOT NULL,
          format        TEXT NOT NULL,
          first_seen_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS locations (
          path           TEXT PRIMARY KEY,
          root           TEXT NOT NULL,
          sha256         TEXT NOT NULL REFERENCES models(sha256),
          size           INTEGER NOT NULL,
          mtime_ms       REAL NOT NULL,
          last_seen_scan INTEGER NOT NULL,
          added_scan     INTEGER NOT NULL,
          missing        INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_locations_sha  ON locations(sha256);
        CREATE INDEX IF NOT EXISTS idx_locations_root ON locations(root);

        CREATE TABLE IF NOT EXISTS containers (
          path           TEXT PRIMARY KEY,
          root           TEXT NOT NULL,
          size           INTEGER NOT NULL,
          mtime_ms       REAL NOT NULL,
          last_seen_scan INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS thumbnails (
          sha256         TEXT PRIMARY KEY REFERENCES models(sha256),
          status         TEXT NOT NULL,
          render_version INTEGER NOT NULL,
          size_px        INTEGER,
          error          TEXT,
          created_at     INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ratings (
          sha256     TEXT PRIMARY KEY REFERENCES models(sha256),
          stars      INTEGER NOT NULL CHECK (stars BETWEEN 0 AND 5),
          updated_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS views (
          sha256     TEXT PRIMARY KEY REFERENCES models(sha256),
          position   TEXT NOT NULL,
          target     TEXT NOT NULL,
          fov_deg    REAL NOT NULL,
          z_up       INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        """;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    /// <summary>Opens (or creates) a library. ":memory:" makes a throwaway one.</summary>
    /// <param name="separator">The path separator used in stored paths. Only tests change it.</param>
    public LibraryDatabase(string file, char separator = '\0')
    {
        Separator = separator == '\0' ? System.IO.Path.DirectorySeparatorChar : separator;
        if (file != ":memory:") Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        _db = new SqliteConnection(file);
        try
        {
            // WAL with synchronous=NORMAL keeps the many small writes of a scan fast and still safe across crashes.
            _db.Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
            _db.Exec(Schema);
            Migrate();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
        File = file;
    }

    public string File { get; }

    public char Separator { get; }

    /// <summary>The clock, in milliseconds since 1970. Tests replace it.</summary>
    public Func<long> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Columns added after the first release of the Electron app. Older databases get them here; nothing stored changes.
    ///   container / entry  set for a model inside an archive: the archive's path and the entry's name in it
    ///   crc32              the entry's checksum, used to tell that an entry is unchanged
    /// </summary>
    private void Migrate()
    {
        var have = _db.Query("PRAGMA table_info(locations)", r => r.GetString(1)).ToHashSet();
        foreach (var (name, type) in new[] { ("container", "TEXT"), ("entry", "TEXT"), ("crc32", "INTEGER") })
        {
            if (!have.Contains(name)) _db.Exec($"ALTER TABLE locations ADD COLUMN {name} {type}");
        }
        _db.Exec("CREATE INDEX IF NOT EXISTS idx_locations_container ON locations(container)");
    }

    /// <summary>Runs work holding the database lock, inside a transaction that is rolled back if it throws.</summary>
    public T Transaction<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate)
        {
            _db.Exec("BEGIN");
            try
            {
                T result = work(_db);
                _db.Exec("COMMIT");
                return result;
            }
            catch
            {
                try { _db.Exec("ROLLBACK"); } catch (SqliteException) { /* nothing to undo */ }
                throw;
            }
        }
    }

    /// <summary>Runs work holding the database lock (no transaction). For the scanner's many small statements.</summary>
    public T Locked<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate) return work(_db);
    }

    public void Locked(Action<SqliteConnection> work)
    {
        lock (_gate) work(_db);
    }

    /* ---------- settings ---------- */

    public string? GetSetting(string key, string? fallback = null) =>
        Locked(db => db.QueryFirst("SELECT value FROM settings WHERE key = ?", r => r.GetString(0), key)) ?? fallback;

    public void SetSetting(string key, string value) => Locked(db =>
        db.Execute("INSERT INTO settings(key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value));

    /* ---------- roots ---------- */

    public void AddRoot(string root) => Locked(db =>
        db.Execute("INSERT OR IGNORE INTO roots(path, added_at) VALUES (?, ?)", root, Now()));

    public bool HasRoot(string root) => Locked(db => db.Scalar("SELECT 1 FROM roots WHERE path = ?", root) == 1);

    public IReadOnlyList<RootInfo> GetRoots() => Locked(db => db.Query("""
        SELECT r.path,
               r.last_scanned_at,
               (SELECT COUNT(*) FROM locations l WHERE l.root = r.path AND l.missing = 0)
        FROM roots r
        ORDER BY r.path COLLATE NOCASE
        """, r => new RootInfo(r.GetString(0), r.GetInt64OrNull(1), r.GetInt64(2))));

    /// <summary>
    /// Takes a folder out of the library. Its file records go; the files on disk are never touched.
    /// By default thumbnails, views and ratings are kept, so adding the folder again brings them back. With
    /// <paramref name="purge"/>, models found nowhere else are deleted along with them. Returns the hashes whose
    /// thumbnail images can be deleted.
    /// </summary>
    public RemoveRootResult RemoveRoot(string root, bool purge = false) => Transaction(db =>
    {
        var shas = db.Query("SELECT DISTINCT sha256 FROM locations WHERE root = ?", r => r.GetString(0), root);
        int removed = db.Execute("DELETE FROM locations WHERE root = ?", root);
        db.Execute("DELETE FROM containers WHERE root = ?", root);
        db.Execute("DELETE FROM roots WHERE path = ?", root);
        var purged = new List<string>();
        if (purge)
        {
            foreach (var sha in shas)
            {
                if (db.Scalar("SELECT 1 FROM locations WHERE sha256 = ? LIMIT 1", sha) == 1) continue;
                db.Execute("DELETE FROM views WHERE sha256 = ?", sha);
                db.Execute("DELETE FROM ratings WHERE sha256 = ?", sha);
                db.Execute("DELETE FROM thumbnails WHERE sha256 = ?", sha);
                db.Execute("DELETE FROM models WHERE sha256 = ?", sha);
                purged.Add(sha);
            }
        }
        return new RemoveRootResult(removed, purged);
    });

    /// <summary>Where to read a known path from. Null if the library does not know the path.</summary>
    public ModelLocation? GetLocation(string path) => Locked(db =>
        db.QueryFirst("SELECT path, container, entry FROM locations WHERE path = ?",
            r => new ModelLocation(r.GetString(0), r.GetStringOrNull(1), r.GetStringOrNull(2)), path));

    public bool HasModel(string sha256) => Locked(db => db.Scalar("SELECT 1 FROM models WHERE sha256 = ?", sha256) == 1);

    /* ---------- the file list ---------- */

    private string SortSql(SortOrder sort, bool? ascending)
    {
        var (sql, defaultAscending) = sort switch
        {
            // The file's own name: the path with everything up to the last separator removed.
            SortOrder.Name => ($"replace(l.path, rtrim(l.path, replace(l.path, '{Separator}', '')), '') COLLATE NOCASE", true),
            SortOrder.Path => ("l.path COLLATE NOCASE", true),
            SortOrder.Modified => ("l.mtime_ms", false),
            SortOrder.Added => ("m.first_seen_at", false),
            SortOrder.Size => ("l.size", false),
            SortOrder.Rating => ("COALESCE(r.stars, -1)", false),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        // The path is the tie-breaker, so equal values keep a stable order and paging never repeats or skips a file.
        return $"{sql} {((ascending ?? defaultAscending) ? "ASC" : "DESC")}, l.path COLLATE NOCASE ASC";
    }

    private static string? RatingSql(RatingFilter rating) => rating switch
    {
        RatingFilter.Unrated => "r.stars IS NULL",
        RatingFilter.Rated => "r.stars IS NOT NULL",
        RatingFilter.Zero => "r.stars = 0",
        RatingFilter.AtLeast1 => "r.stars >= 1",
        RatingFilter.AtLeast2 => "r.stars >= 2",
        RatingFilter.AtLeast3 => "r.stars >= 3",
        RatingFilter.AtLeast4 => "r.stars >= 4",
        RatingFilter.Five => "r.stars = 5",
        _ => null,
    };

    /// <summary>One page of known file locations, filtered and ordered.</summary>
    public ListPage List(ListQuery query)
    {
        var where = new List<string>();
        var args = new List<object?>();

        if (!string.IsNullOrEmpty(query.Folder))
        {
            // Files inside the folder. Non-recursive keeps only those with no further separator after its prefix.
            string prefix = query.Folder.EndsWith(Separator) ? query.Folder : query.Folder + Separator;
            where.Add("substr(l.path, 1, ?) = ?");
            args.Add(prefix.Length);
            args.Add(prefix);
            if (!query.Recursive)
            {
                where.Add("instr(substr(l.path, ?), ?) = 0");
                args.Add(prefix.Length + 1);
                args.Add(Separator.ToString());
            }
        }

        string term = query.Search.Trim();
        if (term.Length > 0)
        {
            var like = new StringBuilder("%");
            foreach (char c in term) like.Append(c is '\\' or '%' or '_' ? "\\" + c : c.ToString());
            like.Append('%');
            where.Add("(l.path LIKE ? ESCAPE '\\' OR l.sha256 LIKE ? ESCAPE '\\')");
            args.Add(like.ToString());
            args.Add(like.ToString());
        }
        if (!query.IncludeMissing) where.Add("l.missing = 0");

        if (query.SizeMin is { } min) { where.Add("l.size >= ?"); args.Add(min); }
        if (query.SizeMax is { } max) { where.Add("l.size < ?"); args.Add(max); }
        string dateColumn = query.AgeBasis == AgeBasis.Added ? "m.first_seen_at" : "l.mtime_ms";
        if (query.Since is { } since && double.IsFinite(since)) { where.Add($"{dateColumn} >= ?"); args.Add(since); }
        if (query.Before is { } before && double.IsFinite(before)) { where.Add($"{dateColumn} < ?"); args.Add(before); }
        if (RatingSql(query.Rating) is { } rating) where.Add(rating);
        if (query.View == ViewFilter.Custom) where.Add("EXISTS (SELECT 1 FROM views cv WHERE cv.sha256 = l.sha256)");
        if (query.View == ViewFilter.Default) where.Add("NOT EXISTS (SELECT 1 FROM views cv WHERE cv.sha256 = l.sha256)");

        string clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var filterArgs = args.ToArray();
        var pageArgs = args.Append(query.Limit).Append(query.Offset).ToArray();

        return Locked(db =>
        {
            long total = db.Scalar($"""
                SELECT COUNT(*) FROM locations l
                JOIN models m ON m.sha256 = l.sha256 LEFT JOIN ratings r ON r.sha256 = l.sha256 {clause}
                """, filterArgs);
            var rows = db.Query($"""
                SELECT l.path, l.root, l.sha256, l.size, l.missing, l.mtime_ms, l.container, l.entry, m.format, m.first_seen_at,
                       t.status, t.error, t.created_at, (v.sha256 IS NOT NULL), r.stars
                FROM locations l
                JOIN models m ON m.sha256 = l.sha256
                LEFT JOIN thumbnails t ON t.sha256 = l.sha256
                LEFT JOIN views v ON v.sha256 = l.sha256
                LEFT JOIN ratings r ON r.sha256 = l.sha256
                {clause}
                ORDER BY {SortSql(query.Sort, query.Ascending)}
                LIMIT ? OFFSET ?
                """, ToRow, pageArgs);
            return new ListPage(total, rows);
        });
    }

    private LocationRow ToRow(SqliteRow r)
    {
        string path = r.GetString(0), root = r.GetString(1);
        string relative = Relative(root, path);
        int cut = relative.LastIndexOf(Separator);
        string? container = r.GetStringOrNull(6);
        string? status = r.GetStringOrNull(10);
        return new LocationRow
        {
            Path = path,
            Root = root,
            RelativePath = relative,
            Name = BaseName(path),
            Folder = cut < 0 ? "" : relative[..cut],
            Sha256 = r.GetString(2),
            Size = r.GetInt64(3),
            Missing = r.GetBoolean(4),
            ModifiedMs = r.GetDouble(5),
            Container = string.IsNullOrEmpty(container) ? null : container,
            Entry = r.GetStringOrNull(7) is { Length: > 0 } entry ? entry : null,
            ArchiveName = string.IsNullOrEmpty(container) ? null : BaseName(container),
            Format = r.GetString(8),
            AddedAt = r.GetInt64(9),
            ThumbnailState = status switch { "ok" => ThumbnailState.Ok, "failed" => ThumbnailState.Failed, _ => ThumbnailState.Pending },
            ThumbnailError = r.GetStringOrNull(11) is { Length: > 0 } error ? error : null,
            ThumbnailCreatedAt = r.GetInt64OrNull(12) is { } created && created != 0 ? created : null,
            CustomView = r.GetBoolean(13),
            Rating = r.GetInt32OrNull(14),
        };
    }

    /* ---------- folder tree ---------- */

    private sealed class TreeBuilder(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public int Direct;
        public int Count;
        public Dictionary<string, TreeBuilder> Children { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// One node per root, with a nested node for every folder that holds at least one present file.
    /// Folders are sorted the way people expect: case-insensitive, with numbers in number order ("part 2" before "part 10").
    /// </summary>
    public IReadOnlyList<FolderNode> GetFolderTree()
    {
        var roots = GetRoots().Select(r => r.Path).ToList();
        var files = Locked(db => db.Query("SELECT root, path FROM locations WHERE missing = 0", r => (Root: r.GetString(0), Path: r.GetString(1))));
        var byRoot = roots.ToDictionary(r => r, r => new TreeBuilder(r, r), StringComparer.Ordinal);

        foreach (var (root, file) in files)
        {
            if (!byRoot.TryGetValue(root, out var node)) continue;
            node.Count++;
            string relative = Relative(root, file);
            int cut = relative.LastIndexOf(Separator);
            string current = root;
            if (cut > 0)
            {
                foreach (var part in relative[..cut].Split(Separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Join(current, part);
                    if (!node.Children.TryGetValue(part, out var child))
                    {
                        child = new TreeBuilder(part, current);
                        node.Children[part] = child;
                    }
                    node = child;
                    node.Count++;
                }
            }
            node.Direct++;
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions natural = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering;
        FolderNode Finish(TreeBuilder n) => new(n.Name, n.Path, n.Direct, n.Count,
            n.Children.Values.OrderBy(c => c.Name, Comparer<string>.Create((a, b) => compare.Compare(a, b, natural))).Select(Finish).ToList());
        return roots.Select(r => Finish(byRoot[r])).ToList();
    }

    /* ---------- ratings ---------- */

    /// <summary>Sets the rating (0 to 5), or clears it with null so the model is unrated again (which is not 0 stars).</summary>
    public void SetRating(string sha256, int? stars) => Locked(db => SetRating(db, sha256, stars));

    /// <summary>Rates many models in one transaction. Only models the library knows are changed; returns those.</summary>
    public IReadOnlyList<string> SetRatings(IEnumerable<string> shas, int? stars) => Transaction(db =>
    {
        var changed = new List<string>();
        foreach (var sha in shas.Distinct())
        {
            if (db.Scalar("SELECT 1 FROM models WHERE sha256 = ?", sha) != 1) continue;
            SetRating(db, sha, stars);
            changed.Add(sha);
        }
        return changed;
    });

    private void SetRating(SqliteConnection db, string sha256, int? stars)
    {
        if (stars is null)
        {
            db.Execute("DELETE FROM ratings WHERE sha256 = ?", sha256);
            return;
        }
        if (stars is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(stars), "A rating is a whole number from 0 to 5.");
        db.Execute("""
            INSERT INTO ratings(sha256, stars, updated_at) VALUES (?, ?, ?)
            ON CONFLICT(sha256) DO UPDATE SET stars = excluded.stars, updated_at = excluded.updated_at
            """, sha256, stars.Value, Now());
    }

    public int? GetRating(string sha256) => Locked(db => db.QueryFirst("SELECT stars FROM ratings WHERE sha256 = ?", r => (int?)r.GetInt32(0), sha256));

    /* ---------- views saved in the viewer ---------- */

    public void SetView(string sha256, SavedView view)
    {
        static bool Vec3(double[] v) => v.Length == 3 && v.All(double.IsFinite);
        if (!Vec3(view.Position) || !Vec3(view.Target) || !double.IsFinite(view.FovDeg) || view.FovDeg <= 0 || view.FovDeg >= 170)
            throw new ArgumentException("That is not a valid view.", nameof(view));
        Locked(db => db.Execute("""
            INSERT INTO views(sha256, position, target, fov_deg, z_up, updated_at) VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(sha256) DO UPDATE SET position = excluded.position, target = excluded.target,
              fov_deg = excluded.fov_deg, z_up = excluded.z_up, updated_at = excluded.updated_at
            """, sha256, JsonSerializer.Serialize(view.Position), JsonSerializer.Serialize(view.Target), view.FovDeg, view.ZUp, Now()));
    }

    public SavedView? GetView(string sha256) => Locked(db => db.QueryFirst(
        "SELECT position, target, fov_deg, z_up FROM views WHERE sha256 = ?",
        r => new SavedView(JsonSerializer.Deserialize<double[]>(r.GetString(0))!, JsonSerializer.Deserialize<double[]>(r.GetString(1))!, r.GetDouble(2), r.GetBoolean(3)),
        sha256));

    public bool DeleteView(string sha256) => Locked(db => db.Execute("DELETE FROM views WHERE sha256 = ?", sha256) > 0);

    /* ---------- thumbnails ---------- */

    // A model needs a thumbnail when it has at least one present location, and either has no thumbnail row yet or
    // was rendered with an older render version.
    private const string NeedsThumbnail = """
        EXISTS (SELECT 1 FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0)
        AND (t.sha256 IS NULL OR (t.status = 'ok' AND t.render_version < ?))
        """;

    /// <summary>Models waiting for a thumbnail, each with one present path to read it from. Ordered by path, so folders finish together.</summary>
    public IReadOnlyList<ThumbnailJobRow> PendingThumbnailJobs(int renderVersion) => Locked(db => db.Query($"""
        SELECT m.sha256, m.format, m.size,
               (SELECT l.path FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0 ORDER BY l.path LIMIT 1) AS path,
               (SELECT l.container FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0 ORDER BY l.path LIMIT 1),
               (SELECT l.entry FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0 ORDER BY l.path LIMIT 1)
        FROM models m
        LEFT JOIN thumbnails t ON t.sha256 = m.sha256
        WHERE {NeedsThumbnail}
        ORDER BY path COLLATE NOCASE
        """, r => new ThumbnailJobRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
            r.GetStringOrNull(4) is { Length: > 0 } c ? c : null, r.GetStringOrNull(5) is { Length: > 0 } e ? e : null), renderVersion));

    public void RecordThumbnail(string sha256, ThumbnailState status, int renderVersion, int? sizePx = null, string? error = null)
    {
        if (status == ThumbnailState.Pending) throw new ArgumentException("Record 'ok' or 'failed'; delete the row to make a model pending.", nameof(status));
        Locked(db => db.Execute("""
            INSERT INTO thumbnails(sha256, status, render_version, size_px, error, created_at) VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(sha256) DO UPDATE SET status = excluded.status, render_version = excluded.render_version,
              size_px = excluded.size_px, error = excluded.error, created_at = excluded.created_at
            """, sha256, status == ThumbnailState.Ok ? "ok" : "failed", renderVersion, sizePx, error, Now()));
    }

    public IReadOnlyList<string> OkThumbnailShas() =>
        Locked(db => db.Query("SELECT sha256 FROM thumbnails WHERE status = 'ok'", r => r.GetString(0)));

    public void DeleteThumbnailRow(string sha256) => Locked(db => db.Execute("DELETE FROM thumbnails WHERE sha256 = ?", sha256));

    /// <summary>Forgets failed thumbnails so they are tried again. Returns how many were reset.</summary>
    public int ResetFailedThumbnails() => Locked(db => db.Execute("DELETE FROM thumbnails WHERE status = 'failed'"));

    public ThumbnailCounts GetThumbnailCounts(int renderVersion) => Locked(db => new ThumbnailCounts(
        db.Scalar("""
            SELECT COUNT(*) FROM thumbnails t JOIN models m ON m.sha256 = t.sha256
            WHERE t.status = 'ok' AND t.render_version >= ?
              AND EXISTS (SELECT 1 FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0)
            """, renderVersion),
        db.Scalar("""
            SELECT COUNT(*) FROM thumbnails t JOIN models m ON m.sha256 = t.sha256
            WHERE t.status = 'failed'
              AND EXISTS (SELECT 1 FROM locations l WHERE l.sha256 = m.sha256 AND l.missing = 0)
            """),
        db.Scalar($"SELECT COUNT(*) FROM models m LEFT JOIN thumbnails t ON t.sha256 = m.sha256 WHERE {NeedsThumbnail}", renderVersion)));

    public LibraryStats GetStats() => Locked(db => new LibraryStats(
        db.Scalar("SELECT COUNT(*) FROM locations WHERE missing = 0"),
        db.Scalar("SELECT COUNT(DISTINCT sha256) FROM locations WHERE missing = 0"),
        db.Scalar("SELECT COUNT(*) FROM locations WHERE missing = 1"),
        db.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM locations WHERE missing = 0 GROUP BY sha256 HAVING COUNT(*) > 1)")));

    /* ---------- paths, using the library's separator ---------- */

    internal string Relative(string root, string path)
    {
        string prefix = root.EndsWith(Separator) ? root : root + Separator;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    internal string BaseName(string path)
    {
        string trimmed = path.TrimEnd(Separator);
        int cut = trimmed.LastIndexOf(Separator);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    internal string Join(string folder, string name) => folder.EndsWith(Separator) ? folder + name : folder + Separator + name;

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
