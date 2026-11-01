namespace Xandrite.Library.Tests;

/// <summary>The list, tree, ratings, views and folder removal. Ported from the Electron app's test/explorer.test.js.</summary>
public class DatabaseTests
{
    private static readonly char S = Path.DirectorySeparatorChar;

    /// <summary>root/a.stl, root/sub/b.stl, root/sub/deep/c.3mf, root/sub2/d.stl (missing), root/sub/100%_x.stl</summary>
    private static (LibraryDatabase Db, string Root) Build()
    {
        var db = new LibraryDatabase(":memory:");
        string root = S + "lib";
        db.AddRoot(root);
        (string Rel, int Missing)[] files = [("a.stl", 0), ($"sub{S}b.stl", 0), ($"sub{S}deep{S}c.3mf", 0), ($"sub2{S}d.stl", 1), ($"sub{S}100%_x.stl", 0)];
        for (int i = 0; i < files.Length; i++)
        {
            string sha = new((char)('0' + i), 64);
            var (rel, missing) = files[i];
            db.Locked(c =>
            {
                c.Execute("INSERT INTO models(sha256,size,format,first_seen_at) VALUES (?,?,?,?)", sha, 10, rel.EndsWith("3mf") ? "3mf" : "stl", 0);
                c.Execute("INSERT INTO locations(path,root,sha256,size,mtime_ms,last_seen_scan,added_scan,missing) VALUES (?,?,?,?,?,?,?,?)",
                    root + S + rel, root, sha, 10, 0, 1, 1, missing);
            });
        }
        return (db, root);
    }

    private static List<string> Names(ListPage page) => page.Rows.Select(r => r.RelativePath).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void FolderFilterDirectFilesOnlyOrIncludingSubfolders()
    {
        var (db, root) = Build();
        var direct = db.List(new ListQuery { Folder = root + S + "sub", Recursive = false });
        Assert.Equal(new[] { $"sub{S}100%_x.stl", $"sub{S}b.stl" }, Names(direct));
        Assert.Equal(3, db.List(new ListQuery { Folder = root + S + "sub" }).Total);
        Assert.Equal(1, db.List(new ListQuery { Folder = root, Recursive = false }).Total);
    }

    [Fact]
    public void FolderFilterDoesNotMatchASiblingThatSharesANamePrefix()
    {
        var (db, root) = Build();
        Assert.True(db.List(new ListQuery { Folder = root + S + "sub" }).Rows.All(r => !r.Path.Contains("sub2")));
    }

    [Fact]
    public void MissingFilesAreLeftOutWhenAsked()
    {
        var (db, root) = Build();
        Assert.Equal(0, db.List(new ListQuery { Folder = root + S + "sub2", IncludeMissing = false }).Total);
        Assert.Equal(1, db.List(new ListQuery { Folder = root + S + "sub2" }).Total);
    }

    [Fact]
    public void SearchMatchesPartOfAPathOrHashAndTreatsWildcardsLiterally()
    {
        var (db, _) = Build();
        Assert.Equal(1, db.List(new ListQuery { Search = "100%" }).Total);
        Assert.Equal(0, db.List(new ListQuery { Search = "1_0" }).Total);    // "_" is not a wildcard
        Assert.Equal(1, db.List(new ListQuery { Search = new string('2', 10) }).Total);
    }

    [Fact]
    public void FolderTreeCountsPresentFilesAndNestsFolders()
    {
        var (db, root) = Build();
        var tree = Assert.Single(db.GetFolderTree());
        Assert.Equal(root, tree.Path);
        Assert.Equal(4, tree.Count);
        Assert.Equal(1, tree.Direct);
        Assert.Equal(new[] { "sub" }, tree.Children.Select(c => c.Name));   // sub2 holds only a missing file
        var sub = tree.Children[0];
        Assert.Equal(3, sub.Count);
        Assert.Equal(2, sub.Direct);
        Assert.Equal("deep", sub.Children[0].Name);
        Assert.Equal(root + S + "sub" + S + "deep", sub.Children[0].Path);
    }

    [Fact]
    public void FolderTreeSortsNumbersInNumberOrder()
    {
        var db = new LibraryDatabase(":memory:");
        string root = S + "lib";
        db.AddRoot(root);
        int i = 0;
        foreach (var folder in new[] { "Part 10", "part 2", "Part 1" })
        {
            string sha = new((char)('a' + i++), 64);
            db.Locked(c =>
            {
                c.Execute("INSERT INTO models(sha256,size,format,first_seen_at) VALUES (?,?,?,?)", sha, 1, "stl", 0);
                c.Execute("INSERT INTO locations(path,root,sha256,size,mtime_ms,last_seen_scan,added_scan) VALUES (?,?,?,?,?,?,?)",
                    root + S + folder + S + "x.stl", root, sha, 1, 0, 1, 1);
            });
        }
        Assert.Equal(new[] { "Part 1", "part 2", "Part 10" }, db.GetFolderTree()[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void AViewIsStoredPerModelReplacedValidatedAndReportedByTheList()
    {
        var (db, _) = Build();
        string sha = new('1', 64);
        db.SetView(sha, new SavedView([1, 2, 3], [0, 0, 0], 35, true));
        db.SetView(sha, new SavedView([4, 5, 6], [0, 1, 0], 40, false));
        var view = db.GetView(sha)!;
        Assert.Equal(new double[] { 4, 5, 6 }, view.Position);
        Assert.Equal(40, view.FovDeg);
        Assert.False(view.ZUp);
        Assert.Throws<ArgumentException>(() => db.SetView(sha, new SavedView([double.NaN, 0, 0], [0, 0, 0], 35, true)));
        Assert.Throws<ArgumentException>(() => db.SetView(sha, new SavedView([1, 2, 3], [0, 0, 0], 180, true)));
        Assert.True(db.List(new ListQuery()).Rows.Single(r => r.Sha256 == sha).CustomView);
        Assert.True(db.DeleteView(sha));
        Assert.Null(db.GetView(sha));
    }

    [Fact]
    public void RemovingAFolderDropsItsRecordsKeepsThumbnailsByDefaultAndCanPurgeThem()
    {
        var (db, root) = Build();
        string sha = new('0', 64);
        db.RecordThumbnail(sha, ThumbnailState.Ok, 2, 512);
        db.SetRating(sha, 3);

        var kept = db.RemoveRoot(root);
        Assert.Equal(5, kept.RemovedLocations);
        Assert.Empty(kept.PurgedShas);
        Assert.False(db.HasRoot(root));
        Assert.Equal(new[] { sha }, db.OkThumbnailShas());
        Assert.Equal(3, db.GetRating(sha));

        var (db2, root2) = Build();
        db2.RecordThumbnail(sha, ThumbnailState.Ok, 2, 512);
        db2.SetRating(sha, 3);
        var purged = db2.RemoveRoot(root2, purge: true);
        Assert.Equal(5, purged.PurgedShas.Count);
        Assert.Empty(db2.OkThumbnailShas());
        Assert.Null(db2.GetRating(sha));
        Assert.False(db2.HasModel(sha));
    }

    [Fact]
    public void OrderByNameDateSizeEitherDirectionWithAStableTieBreak()
    {
        var (db, _) = Build();
        db.Locked(c =>
        {
            c.Execute("UPDATE locations SET size = 30, mtime_ms = 300 WHERE path LIKE '%a.stl'");
            c.Execute("UPDATE locations SET size = 20, mtime_ms = 100 WHERE path LIKE '%b.stl'");
            c.Execute("UPDATE models SET first_seen_at = 5 WHERE sha256 = ?", new string('2', 64));
        });
        List<string> Order(SortOrder sort, bool? ascending = null) =>
            db.List(new ListQuery { Sort = sort, Ascending = ascending }).Rows.Select(r => r.Name).ToList();

        Assert.Equal(new[] { "100%_x.stl", "a.stl", "b.stl", "c.3mf", "d.stl" }, Order(SortOrder.Name));
        Assert.Equal(new[] { "d.stl", "c.3mf", "b.stl", "a.stl", "100%_x.stl" }, Order(SortOrder.Name, false));
        Assert.Equal("a.stl", Order(SortOrder.Modified)[0]);            // newest first by default
        Assert.Equal("a.stl", Order(SortOrder.Size)[0]);                // largest first
        Assert.Equal("c.3mf", Order(SortOrder.Added)[0]);
        // Ties are broken by path (case-insensitively), so equal sizes keep a stable order.
        var bySize = db.List(new ListQuery { Sort = SortOrder.Size }).Rows;
        Assert.Equal(new[] { "a.stl", "b.stl" }, bySize.Take(2).Select(r => r.Name));
        var tied = bySize.Skip(2).Select(r => r.Path).ToList();
        Assert.Equal(tied.Order(StringComparer.OrdinalIgnoreCase), tied);
    }

    [Fact]
    public void NameOrdersByFileNameAcrossFoldersPathOrdersFolderByFolder()
    {
        var (db, _) = Build();
        var byPath = db.List(new ListQuery { Sort = SortOrder.Path }).Rows.Select(r => r.Path).ToList();
        Assert.Equal(byPath.Order(StringComparer.OrdinalIgnoreCase), byPath);   // SQLite's NOCASE: ordinal, ASCII case folded
        var byName = db.List(new ListQuery { Sort = SortOrder.Name }).Rows.Select(r => r.Name).ToList();
        Assert.Equal(new[] { "100%_x.stl", "a.stl", "b.stl", "c.3mf", "d.stl" }, byName);   // across folders
    }

    [Fact]
    public void ZeroStarsIsDifferentFromUnratedCanBeClearedAndIsValidated()
    {
        var (db, _) = Build();
        string sha = new('0', 64);
        Assert.Null(db.GetRating(sha));
        db.SetRating(sha, 0);
        Assert.Equal(0, db.GetRating(sha));
        db.SetRating(sha, null);
        Assert.Null(db.GetRating(sha));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.SetRating(sha, 6));
        Assert.Equal(new[] { sha }, db.SetRatings([sha, sha, new string('f', 64)], 5));   // unknown models are ignored
    }

    [Fact]
    public void OrderByRatingPutsUnratedBelowZeroStars()
    {
        var (db, _) = Build();
        db.SetRating(new string('0', 64), 0);
        db.SetRating(new string('1', 64), 5);
        var names = db.List(new ListQuery { Sort = SortOrder.Rating }).Rows.Select(r => r.Name).ToList();
        Assert.Equal("b.stl", names[0]);    // 5 stars
        Assert.Equal("a.stl", names[1]);    // 0 stars, still above the unrated files
        var ascending = db.List(new ListQuery { Sort = SortOrder.Rating, Ascending = true }).Rows.Select(r => r.Name).ToList();
        Assert.Equal("a.stl", ascending[^2]);
        Assert.Equal("b.stl", ascending[^1]);
    }

    [Fact]
    public void FiltersBySizeAgeAndRatingAndTheyCombine()
    {
        var (db, _) = Build();
        db.Locked(c =>
        {
            c.Execute("UPDATE locations SET size = 100, mtime_ms = 1000 WHERE path LIKE '%a.stl'");
            c.Execute("UPDATE locations SET size = 200, mtime_ms = 2000 WHERE path LIKE '%b.stl'");
        });
        Assert.Equal(1, db.List(new ListQuery { SizeMin = 100, SizeMax = 200 }).Total);          // [min, max)
        Assert.Equal(1, db.List(new ListQuery { Since = 2000 }).Total);
        Assert.Equal(1, db.List(new ListQuery { Since = 500, Before = 2000 }).Total);
        Assert.Equal(0, db.List(new ListQuery { Since = 500, AgeBasis = AgeBasis.Added }).Total);  // all were added at 0

        db.SetRating(new string('0', 64), 4);
        db.SetRating(new string('1', 64), 2);
        Assert.Equal(2, db.List(new ListQuery { Rating = RatingFilter.Rated }).Total);
        Assert.Equal(3, db.List(new ListQuery { Rating = RatingFilter.Unrated }).Total);
        Assert.Equal(1, db.List(new ListQuery { Rating = RatingFilter.AtLeast3 }).Total);
        Assert.Equal(1, db.List(new ListQuery { Rating = RatingFilter.AtLeast2, SizeMin = 150 }).Total);

        db.SetView(new string('0', 64), new SavedView([1, 2, 3], [0, 0, 0], 35, true));
        var custom = db.List(new ListQuery { View = ViewFilter.Custom });
        Assert.Equal(1, custom.Total);
        Assert.True(Assert.Single(custom.Rows).CustomView);
        Assert.Equal(4, db.List(new ListQuery { View = ViewFilter.Default }).Total);
        Assert.Equal(0, db.List(new ListQuery { View = ViewFilter.Custom, Rating = RatingFilter.Unrated }).Total);   // combines
    }

    [Fact]
    public void ThumbnailBookkeeping()
    {
        var (db, _) = Build();
        Assert.Equal(new ThumbnailCounts(0, 0, 4), db.GetThumbnailCounts(2));   // the missing file's model needs none
        db.RecordThumbnail(new string('0', 64), ThumbnailState.Ok, 2, 512);
        db.RecordThumbnail(new string('1', 64), ThumbnailState.Failed, 2, error: "bad");
        db.RecordThumbnail(new string('2', 64), ThumbnailState.Ok, 1, 512);       // an older renderer: redraw
        Assert.Equal(new ThumbnailCounts(1, 1, 2), db.GetThumbnailCounts(2));
        Assert.Equal(2, db.PendingThumbnailJobs(2).Count);
        Assert.Equal(1, db.ResetFailedThumbnails());
        Assert.Equal(3, db.PendingThumbnailJobs(2).Count);
        var failedRow = db.List(new ListQuery()).Rows.First(r => r.Sha256 == new string('0', 64));
        Assert.Equal(ThumbnailState.Ok, failedRow.ThumbnailState);
    }

    [Fact]
    public void SettingsAreStored()
    {
        var db = new LibraryDatabase(":memory:");
        Assert.Equal("1", db.GetSetting("scanZips", "1"));
        db.SetSetting("scanZips", "0");
        Assert.Equal("0", db.GetSetting("scanZips", "1"));
    }

    [Fact]
    public void ADatabaseFromBeforeArchiveSupportIsUpgradedInPlace()
    {
        string file = Path.Combine(Path.GetTempPath(), "xandrite-old-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var old = new Sqlite.SqliteConnection(file))
            {
                old.Exec("""
                    CREATE TABLE locations (path TEXT PRIMARY KEY, root TEXT NOT NULL, sha256 TEXT NOT NULL, size INTEGER NOT NULL,
                      mtime_ms REAL NOT NULL, last_seen_scan INTEGER NOT NULL, added_scan INTEGER NOT NULL, missing INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO locations VALUES ('/x/a.stl', '/x', 'aa', 1, 0, 1, 1, 0);
                    """);
            }
            using var db = new LibraryDatabase(file);
            var columns = db.Locked(c => c.Query("PRAGMA table_info(locations)", r => r.GetString(1)));
            Assert.Contains("container", columns);
            Assert.Contains("crc32", columns);
            Assert.Equal(1, db.Locked(c => c.Scalar("SELECT COUNT(*) FROM locations")));
        }
        finally
        {
            foreach (var f in new[] { file, file + "-wal", file + "-shm" }) File.Delete(f);
        }
    }
}
