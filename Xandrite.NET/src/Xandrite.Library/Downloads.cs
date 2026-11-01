using Xandrite.Library.Logging;
using System.Security.Cryptography;
using Xandrite.Library.Scanning;

namespace Xandrite.Library;

public sealed record DownloadProblem(string Path, string Message);

public sealed record DownloadResult(int Copied, IReadOnlyList<DownloadProblem> Failed, int Changed, bool Cancelled, int Total);

public sealed record DownloadProgress(int Done, int Total, string? Current);

/// <summary>
/// "Download…": copies models to a folder the person picks. Loose files are copied; models inside archives are
/// extracted straight into the folder, and models from the same archive are read in one pass. Nothing is overwritten:
/// a name that is taken gets " (2)", " (3)"... A port of downloadRows in the Electron app's src/selection-actions.js.
/// </summary>
public static class Downloads
{
    /// <summary>"name.stl", or "name (2).stl", "name (3).stl"... whichever is free. Adds the result to <paramref name="taken"/>.</summary>
    public static string UniqueName(string wanted, ISet<string> taken)
    {
        string stem = Path.GetFileNameWithoutExtension(wanted), ext = Path.GetExtension(wanted);
        string name = wanted;
        for (int n = 2; taken.Contains(name); n++) name = $"{stem} ({n}){ext}";
        taken.Add(name);
        return name;
    }

    private sealed class StopReading : Exception;

    /// <summary>
    /// <see cref="DownloadResult.Changed"/> counts copies whose content no longer matches the hash in the library
    /// (the file was edited after the last scan); they are kept.
    /// </summary>
    public static async Task<DownloadResult> CopyAsync(IReadOnlyList<LocationRow> rows, string destination, ModelSource? source = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        source ??= ModelSource.Default;
        var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(destination).Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
        var failed = new List<DownloadProblem>();
        int copied = 0, changed = 0, done = 0;
        void Step(string? current) => progress?.Report(new DownloadProgress(done, rows.Count, current));

        async Task Write(LocationRow row, Stream content)
        {
            for (;;)
            {
                string name = UniqueName(row.Name, taken);
                string target = Path.Combine(destination, name);
                FileStream output;
                try
                {
                    output = DevLog.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous, "download");
                }
                catch (IOException) when (File.Exists(target))
                {
                    continue;   // appeared since the folder was listed: take the next name
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    await using (output)
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while ((read = await content.ReadAsync(buffer, cancellation)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        }
                    }
                }
                catch
                {
                    try { File.Delete(target); } catch (IOException) { }   // never leave half a file behind
                    throw;
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != row.Sha256) changed++;
                try { File.SetLastWriteTimeUtc(target, DateTime.UnixEpoch.AddMilliseconds(row.ModifiedMs)); } catch (Exception e) when (e is IOException or ArgumentException) { }
                copied++;
                return;
            }
        }

        try
        {
            foreach (var row in rows.Where(r => r.Container is null))
            {
                cancellation.ThrowIfCancellationRequested();
                Step(row.Path);
                try
                {
                    await using var input = DevLog.OpenRead(row.Path, 1 << 16, FileOptions.Asynchronous, "download");
                    await Write(row, input);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed.Add(new DownloadProblem(row.Path, e.Message));
                }
                done++;
            }

            foreach (var group in rows.Where(r => r.Container is not null).GroupBy(r => r.Container!))
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = group.GroupBy(r => r.Entry ?? "").ToDictionary(g => g.Key, g => g.First());
                var reader = source.ReaderFor(group.Key);
                try
                {
                    if (reader is null) throw new NotSupportedException("reading " + Path.GetExtension(group.Key) + " archives is not available yet");
                    await reader.EachModelEntryAsync(group.Key,
                        async entry =>
                        {
                            if (!remaining.Remove(entry.EntryName, out var row)) return;
                            Step(row.Path);
                            try
                            {
                                await using var input = entry.Open();
                                await Write(row, input);
                            }
                            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                            {
                                failed.Add(new DownloadProblem(row.Path, e.Message));
                            }
                            done++;
                            if (remaining.Count == 0) throw new StopReading();   // everything wanted from this archive is done
                        },
                        (entryName, message) =>
                        {
                            if (remaining.Remove(entryName, out var row)) { failed.Add(new DownloadProblem(row.Path, message)); done++; }
                        },
                        ArchiveLimits.Default, cancellation);
                }
                catch (StopReading)
                {
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    foreach (var row in remaining.Values) { failed.Add(new DownloadProblem(row.Path, "could not read the archive: " + e.Message)); done++; }
                    remaining.Clear();
                }
                foreach (var row in remaining.Values) { failed.Add(new DownloadProblem(row.Path, "it is no longer in the archive")); done++; }
            }
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(copied, failed, changed, Cancelled: true, rows.Count);
        }
        Step(null);
        return new DownloadResult(copied, failed, changed, Cancelled: false, rows.Count);
    }
}
