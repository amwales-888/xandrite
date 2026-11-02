using System.Text.RegularExpressions;
using Xandrite.Library.Logging;
using Xandrite.Library.Scanning;
using Xandrite.Library.SevenZip;

namespace Xandrite.Library.Tests;

/// <summary>
/// Developer logging (Preferences → Developer, with --developer): archives and files opened and closed, work started
/// and ended, thread ids. DevLog is shared, so each test sets it up and puts it back.
/// </summary>
public class DevLogTests
{
    private const string Stl = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", name);

    private sealed class Session : IDisposable
    {
        public Session(TempLibrary lib, bool archives = false, bool files = false, bool threads = false)
        {
            Log = new FileLog(Path.Combine(lib.Dir, "logs", "dev.log"));
            DevLog.Log = Log;
            DevLog.Archives = archives;
            DevLog.Files = files;
            DevLog.Threads = threads;
        }

        public FileLog Log { get; }

        public string Text
        {
            get
            {
                Log.Flush();
                return File.ReadAllText(Log.Path!);
            }
        }

        public void Dispose()
        {
            DevLog.Archives = DevLog.Files = DevLog.Threads = false;
            DevLog.Log = NullLog.Instance;
            Log.Dispose();
        }
    }

    [Fact]
    public async Task FilesOpenedAndClosedAreLoggedWithWhatWasReadOrWritten()
    {
        using var lib = new TempLibrary();
        string file = lib.Write("a.stl", Stl);
        using var s = new Session(lib, files: true);

        byte[] bytes = await DevLog.ReadAllBytesAsync(file, "model");
        await DevLog.WriteAllBytesAsync(lib.At("out.bin"), [1, 2, 3], "thumbnail");

        string text = s.Text;
        Assert.Equal(Stl.Length, bytes.Length);
        Assert.Matches(@" file\.open path=[^\n]*a\.stl""? access=read purpose=model open_ms=\d+ tid=\d+", text);
        Assert.Matches($@" file\.close path=[^\n]*a\.stl""? purpose=model read={Stl.Length} written=0 open_ms=\d+ tid=\d+", text);
        Assert.Matches(@" file\.open path=[^\n]*out\.bin""? access=write purpose=thumbnail", text);
        Assert.Matches(@" file\.close path=[^\n]*out\.bin""? purpose=thumbnail read=0 written=3 ", text);

        DevLog.Files = false;
        int before = Regex.Matches(s.Text, "file\\.open").Count;
        await DevLog.ReadAllBytesAsync(file, "model");
        Assert.Equal(before, Regex.Matches(s.Text, "file\\.open").Count);   // off: nothing more
    }

    [Fact]
    public async Task AFileThatCannotBeOpenedIsLoggedToo()
    {
        using var lib = new TempLibrary();
        using var s = new Session(lib, files: true);
        await Assert.ThrowsAsync<FileNotFoundException>(() => DevLog.ReadAllBytesAsync(lib.At("nothing.stl"), "model"));
        Assert.Matches(@" file\.open_failed path=[^\n]*nothing\.stl""? purpose=model message=", s.Text);
    }

    [Fact]
    public async Task ArchivesOpenedAndClosedAreLoggedOncePerSetWithTheirParts()
    {
        using var lib = new TempLibrary();
        lib.WriteZip("kit.zip", new Dictionary<string, string> { ["a.stl"] = Stl });
        bool sevenZip = SevenZipArchiveReader.IsAvailable;
        if (sevenZip)
        {
            foreach (var name in new[] { "solid.7z", "split.7z.001", "split.7z.002", "split.7z.003", "split.7z.004", "volumes.part1.rar", "volumes.part2.rar", "volumes.part3.rar" })
                File.Copy(Fixture(name), lib.At(name));
        }
        using var s = new Session(lib, archives: true, files: true);
        await lib.Scan();

        string text = s.Text;
        Assert.Matches(@" archive\.open path=[^\n]*kit\.zip""? kind=zip parts=1 tid=\d+", text);
        Assert.Matches(@" archive\.close path=[^\n]*kit\.zip""? kind=zip ms=\d+ tid=\d+", text);
        Assert.Matches(@" file\.open path=[^\n]*kit\.zip""? access=read purpose=zip", text);
        if (!sevenZip) return;
        Assert.Matches(@" archive\.open path=[^\n]*solid\.7z""? kind=7z parts=1 ", text);
        Assert.Matches(@" archive\.open path=[^\n]*split\.7z\.001""? kind=7z parts=4 ", text);
        Assert.Matches(@" archive\.open path=[^\n]*volumes\.part1\.rar""? kind=rar parts=3 ", text);
        Assert.Matches(@" file\.open path=[^\n]*volumes\.part2\.rar""? access=read purpose=""rar volume""", text);
        Assert.Matches(@" file\.open path=[^\n]*split\.7z\.003""? access=read purpose=""archive part""", text);
        foreach (var name in new[] { "solid.7z", "split.7z.001", "volumes.part1.rar" })
            Assert.Equal(Regex.Matches(text, $@"archive\.open path=[^\n]*{Regex.Escape(name)}").Count, Regex.Matches(text, $@"archive\.close path=[^\n]*{Regex.Escape(name)}").Count);
    }

    [Fact]
    public async Task WorkStartingAndEndingIsLoggedWithItsThreads()
    {
        using var lib = new TempLibrary();
        using var s = new Session(lib, threads: true);
        int ran = await DevLog.Run("test.work", () => 42);
        await DevLog.Run("test.async", async () => await Task.Delay(20));
        using (DevLog.Work("test.inline")) { }

        string text = s.Text;
        Assert.Equal(42, ran);
        Assert.Matches(@" work\.start name=test\.work tid=\d+ pool=true", text);
        Assert.Matches(@" work\.end name=test\.work ms=\d+ tid=\d+ start_tid=\d+", text);
        Assert.Matches(@" work\.end name=test\.async ms=\d+ tid=\d+ start_tid=\d+", text);
        Assert.Matches(@" work\.start name=test\.inline tid=\d+ pool=", text);

        DevLog.Threads = false;   // switching the pool listener off and on again is safe
        DevLog.Threads = true;
        DevLog.Threads = false;
        int before = Regex.Matches(s.Text, "work\\.start").Count;
        await DevLog.Run("test.quiet", () => 1);
        Assert.Equal(before, Regex.Matches(s.Text, "work\\.start").Count);
    }

    [Fact]
    public void EveryLineCanCarryTheIdOfTheThreadThatWroteIt()
    {
        using var lib = new TempLibrary();
        using var log = new FileLog(Path.Combine(lib.Dir, "logs", "x.log")) { ThreadIds = true };
        log.Info("one", ("a", 1));
        log.Info("two", ("tid", 7));   // an event that names its own thread is not given a second tid
        var t = new Thread(() => log.Info("three"));
        t.Start();
        t.Join();
        log.Flush();
        var lines = File.ReadAllLines(log.Path!);
        Assert.EndsWith($" a=1 tid={Environment.CurrentManagedThreadId}", lines[0]);
        Assert.EndsWith(" tid=7", lines[1]);
        Assert.Single(Regex.Matches(lines[1], "tid="));
        Assert.Matches($@" three tid=(?!{Environment.CurrentManagedThreadId}$)\d+$", lines[2]);
    }
}
