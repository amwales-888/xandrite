using System.Text;
using Xandrite.Geometry;
using Xandrite.Library.Scanning;
using Xandrite.Library.SevenZip;
using Xandrite.Library.Thumbnails;
using Xandrite.Rendering;

namespace Xandrite.Library.Tests;

/// <summary>.obj files are found, drawn, and looked for inside archives scanned before they were read.</summary>
public class ObjScanTests
{
    private const string Obj = "o t\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n";
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    private sealed class FakeRenderer : IModelRenderer
    {
        public int Drawn;
        public string AdapterDescription => "Fake";
        public IGpuMesh Upload(Mesh mesh) => new FakeMesh();
        public RenderedImage Render(IGpuMesh mesh, in RenderRequest request)
        {
            Interlocked.Increment(ref Drawn);
            return new(request.Width, request.Height, new byte[request.Width * request.Height * 4]);
        }
        public void Dispose() { }
        private sealed class FakeMesh : IGpuMesh { public int TriangleCount => 1; public void Dispose() { } }
    }

    [Fact]
    public async Task ObjFilesAreFoundLooseAndInsideZips_AndDrawn()
    {
        using var lib = new TempLibrary();
        lib.Write("figure.obj", Obj);
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["parts/arm.OBJ"] = Obj.Replace("o t", "o arm"), ["base.stl"] = Stl });
        var summary = await lib.Scan();
        Assert.Equal(3, summary.Found);
        Assert.Contains(lib.Present(), r => r.Path.EndsWith("figure.obj") && r.Format == "obj");
        Assert.Contains(lib.Present(), r => r.Path.EndsWith("arm.OBJ") && r.Format == "obj");

        var renderer = new FakeRenderer();
        await using (var queue = new ThumbnailQueue(lib.Db, new LibraryLocation(lib.Dir), () => renderer, workers: 1))
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Idle += () => idle.TrySetResult();
            queue.Start();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(new ThumbnailCounts(3, 0, 0), lib.Db.GetThumbnailCounts(Look.RenderVersion));
    }

    [Fact]
    public async Task AnArchiveScannedBeforeObjFilesWereReadIsLookedThroughOnceMore()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["base.stl"] = Stl, ["hero.obj"] = Obj });
        await lib.Scan();
        // As an older version left it: the archive recorded as read, with no formats, and no .obj found in it.
        lib.Db.Locked(c =>
        {
            c.Execute("UPDATE containers SET formats = NULL");
            c.Execute("DELETE FROM locations WHERE path LIKE '%hero.obj'");
        });

        var again = await lib.Scan();
        Assert.Equal(1, again.Added);   // the .obj inside was found
        Assert.Equal(1, again.Reused);  // the .stl was known and not read again
        Assert.Contains(lib.Present(), r => r.Path.EndsWith("hero.obj"));
        Assert.Equal(LibraryScanner.ScannedFormats, lib.Db.Locked(c => c.QueryFirst("SELECT formats FROM containers", r => r.GetString(0))));
        Assert.Equal(".3mf,.obj,.stl", LibraryScanner.ScannedFormats);

        var third = await lib.Scan();
        Assert.Equal(2, third.Reused);  // now skipped unopened
        Assert.Equal(0, third.Hashed);
    }

    [Fact]
    public async Task SevenZipDoesNotExtractEntriesTheLibraryAlreadyHas()
    {
        if (!SevenZipArchiveReader.IsAvailable) return;
        string archive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "nonsolid.7z");
        var reader = new SevenZipArchiveReader();

        async Task<List<(string Name, bool Readable)>> Scan(Func<string, long, long?, bool>? unchanged)
        {
            var seen = new List<(string, bool)>();
            await reader.EachModelEntryAsync(archive, entry =>
            {
                bool readable;
                try { using var s = entry.Open(); readable = true; }
                catch (InvalidOperationException) { readable = false; }
                seen.Add((entry.EntryName, readable));
                return Task.CompletedTask;
            }, (_, _) => { }, ArchiveLimits.Default, unchanged, CancellationToken.None);
            return seen;
        }

        var all = await Scan(null);
        Assert.True(all.Count > 0, "the fixture has models");
        Assert.All(all, e => Assert.True(e.Readable));

        string first = all[0].Name;
        var some = await Scan((name, _, _) => name == first);
        Assert.Equal(all.Select(e => e.Name).Order(), some.Select(e => e.Name).Order());   // every entry still handed over
        Assert.False(some.Single(e => e.Name == first).Readable);                          // but the known one not extracted
        Assert.All(some.Where(e => e.Name != first), e => Assert.True(e.Readable));

        var none = await Scan((_, _, _) => true);
        Assert.Equal(all.Count, none.Count);
        Assert.All(none, e => Assert.False(e.Readable));
    }
}
