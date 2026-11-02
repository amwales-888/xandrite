using Xandrite.Library.Logging;
namespace Xandrite.Library;

/// <summary>
/// Where the app keeps its library: %APPDATA%\Xandrite.NET\library.sqlite, with the thumbnails in a "thumbnails"
/// folder next to it (one &lt;sha256&gt;.png per model). Set XANDRITE_NET_DATA to use another folder.
///
/// This is deliberately separate from the Electron app's %APPDATA%\xandrite, so the two apps never touch each
/// other's files. See <see cref="ElectronImport"/> for the one-time copy.
/// </summary>
public sealed record LibraryLocation(string DataFolder)
{
    public const string FolderName = "Xandrite.NET";

    public string DatabaseFile => Path.Combine(DataFolder, "library.sqlite");

    public string ThumbnailFolder => Path.Combine(DataFolder, "thumbnails");

    public string ThumbnailPath(string sha256) => Path.Combine(ThumbnailFolder, sha256 + ".png");

    /// <summary>The log: logs\xandrite.log, with up to five older files beside it (xandrite.log.1 …).</summary>
    public string LogFile => Path.Combine(DataFolder, "logs", "xandrite.log");

    /// <summary>The statistics file: logs\statistics.log, one line for each scan and each thumbnail run.</summary>
    public string StatisticsFile => Path.Combine(DataFolder, "logs", "statistics.log");

    public static LibraryLocation Default() =>
        new(Environment.GetEnvironmentVariable("XANDRITE_NET_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName));
}

/// <summary>
/// Brings the Electron app's library across, once: the database first (quick, before the library opens), then the
/// thumbnail images in the background. The Electron app's folder is only ever read.
///
/// The old thumbnails were drawn by three.js (render version 1), so the thumbnail queue redraws them with Direct3D;
/// until it gets to each one, the old image is shown. If the app is closed while images are still being copied, the
/// copy carries on at the next start (images that already exist are never overwritten).
/// </summary>
public static class ElectronImport
{
    /// <summary>Setting that records how far the import got: "database" (images still to copy) or "done".</summary>
    public const string SettingKey = "electronImport";

    /// <summary>The Electron app's data folder, if it has a library: %APPDATA%\xandrite, or %APPDATA%\model-library before 0.2.</summary>
    public static string? FindElectronDataFolder()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "xandrite", "model-library" })
        {
            string folder = Path.Combine(appData, name);
            if (File.Exists(Path.Combine(folder, "library.sqlite"))) return folder;
        }
        return null;
    }

    /// <summary>
    /// Copies the database if this library does not exist yet and there is one to copy. Call before opening the library.
    /// Returns true if a database was copied (the images still need <see cref="CopyThumbnailsAsync"/>).
    /// </summary>
    public static bool CopyDatabaseIfNew(LibraryLocation target, string? electronFolder)
    {
        if (electronFolder is null || File.Exists(target.DatabaseFile)) return false;
        string source = Path.Combine(electronFolder, "library.sqlite");
        if (!File.Exists(source)) return false;

        Directory.CreateDirectory(target.DataFolder);
        // The write-ahead log may hold the most recent changes, so it comes too. Read with sharing, in case the
        // Electron app is open; write to temporary names and rename, so a half-copied library is never opened.
        var copied = new List<(string Temp, string Final)>();
        foreach (var suffix in new[] { "", "-wal" })
        {
            if (!File.Exists(source + suffix)) continue;
            string temp = target.DatabaseFile + suffix + ".importing";
            using (var from = new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = File.Create(temp))
                from.CopyTo(to);
            copied.Add((temp, target.DatabaseFile + suffix));
        }
        foreach (var (temp, final) in copied.OrderByDescending(c => c.Final.Length))   // the -wal first, the database last
            File.Move(temp, final, overwrite: true);

        using var db = new LibraryDatabase(target.DatabaseFile);
        db.SetSetting(SettingKey, "database");
        return true;
    }

    /// <summary>True when images are still to be copied (the import started but did not finish).</summary>
    public static bool ThumbnailsPending(LibraryDatabase db) => db.GetSetting(SettingKey) == "database";

    /// <summary>Copies the thumbnail images that do not exist here yet, then marks the import done.</summary>
    public static async Task<int> CopyThumbnailsAsync(LibraryDatabase db, LibraryLocation target, string electronFolder,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        string sourceFolder = Path.Combine(electronFolder, "thumbnails");
        int copied = 0;
        if (Directory.Exists(sourceFolder))
        {
            Directory.CreateDirectory(target.ThumbnailFolder);
            var files = Directory.GetFiles(sourceFolder, "*.png");
            for (int i = 0; i < files.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(files[i]);
                if (name.Length != 68) continue;   // only <sha256>.png
                string destination = Path.Combine(target.ThumbnailFolder, name);
                if (!File.Exists(destination))
                {
                    try
                    {
                        string temp = destination + ".importing";
                        await using (var from = DevLog.OpenRead(files[i], 1 << 16, FileOptions.Asynchronous, "import"))
                        await using (var to = DevLog.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous, "import"))
                            await from.CopyToAsync(to, cancellation);
                        File.Move(temp, destination, overwrite: false);   // a newer image drawn meanwhile wins
                        copied++;
                    }
                    catch (IOException)
                    {
                        // Already there (the queue drew it first) or unreadable: either way, nothing to do.
                        try { File.Delete(destination + ".importing"); } catch (IOException) { }
                    }
                }
                if (i % 50 == 0 || i == files.Length - 1) progress?.Report((i + 1, files.Length));
            }
        }
        db.SetSetting(SettingKey, "done");
        return copied;
    }
}
