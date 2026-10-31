using System.IO.Compression;
using System.Security.Cryptography;

namespace Xandrite.Library.Scanning;

/// <summary>Protection against zip bombs and absurd archives. A model entry over these limits is reported and skipped.</summary>
public sealed record ArchiveLimits
{
    /// <summary>Directory entries examined in one archive.</summary>
    public int MaxEntries { get; init; } = 200_000;
    /// <summary>One model, uncompressed.</summary>
    public long MaxEntryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Uncompressed : compressed. Real models are far below this.</summary>
    public double MaxRatio { get; init; } = 1000;
    /// <summary>The ratio only matters for entries larger than this.</summary>
    public long RatioMinBytes { get; init; } = 200L * 1024 * 1024;

    public static ArchiveLimits Default { get; } = new();
}

/// <summary>A model inside an archive.</summary>
/// <param name="Crc32">The entry's CRC-32 from the archive's directory, if it records one (RAR 5 can use another hash).</param>
public sealed record ArchiveEntry(string EntryName, string Format, long Size, double MtimeMs, long? Crc32, Func<Stream> Open);

/// <summary>An entry that is no longer where the library found it, or an archive that is gone.</summary>
public sealed class ModelGoneException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads the models inside one kind of archive.</summary>
public interface IArchiveReader
{
    /// <summary>True for the archive files this reader handles (by name).</summary>
    bool CanRead(string archivePath);

    /// <summary>
    /// Goes through the archive. For each model entry, <paramref name="onEntry"/> is called one at a time. Problems with
    /// single entries (encrypted, too big...) go to <paramref name="onProblem"/> as (entry name, message).
    /// Returns how many archives nested inside were skipped. Throws if the archive itself cannot be read.
    /// </summary>
    Task<int> EachModelEntryAsync(string archivePath, Func<ArchiveEntry, Task> onEntry, Action<string, string> onProblem, ArchiveLimits limits, CancellationToken cancellation);

    /// <summary>Reads one entry into memory. Throws <see cref="ModelGoneException"/> if the archive or entry is gone.</summary>
    Task<byte[]> ReadEntryAsync(string archivePath, string entryName, long maxBytes, CancellationToken cancellation);

    /// <summary>
    /// Reads several entries, each reported to <paramref name="onEntry"/> once (with its bytes, or why it could not be
    /// read), one at a time: the next is not read until the call returns. A reader of solid archives does this in one
    /// pass, so the archive is decompressed once rather than once per entry. Throws if the archive itself cannot be
    /// read (<see cref="ModelGoneException"/> if it is gone).
    /// </summary>
    Task ReadEntriesAsync(string archivePath, IReadOnlyCollection<string> entryNames, long maxBytes, Func<EntryRead, Task> onEntry, CancellationToken cancellation) =>
        ReadEntriesOneByOneAsync(this, archivePath, entryNames, maxBytes, onEntry, cancellation);

    /// <summary>For archives that can jump straight to an entry (zip): the entries one by one.</summary>
    protected static async Task ReadEntriesOneByOneAsync(IArchiveReader reader, string archivePath, IReadOnlyCollection<string> entryNames, long maxBytes,
        Func<EntryRead, Task> onEntry, CancellationToken cancellation)
    {
        if (!File.Exists(archivePath)) throw new ModelGoneException("The archive is no longer there.");
        foreach (var name in entryNames)
        {
            cancellation.ThrowIfCancellationRequested();
            EntryRead read;
            try
            {
                read = new EntryRead(name, await reader.ReadEntryAsync(archivePath, name, maxBytes, cancellation), Gone: false, Problem: null);
            }
            catch (ModelGoneException)
            {
                read = new EntryRead(name, null, Gone: true, Problem: null);
            }
            catch (Exception e) when (e is InvalidDataException)
            {
                read = new EntryRead(name, null, Gone: false, Problem: e.Message);
            }
            await onEntry(read);
        }
    }
}

/// <summary>One entry from <see cref="IArchiveReader.ReadEntriesAsync"/>: its bytes, or that it is gone, or why it could not be read.</summary>
public sealed record EntryRead(string Name, byte[]? Bytes, bool Gone, string? Problem);

/// <summary>Entry names: which to read, which to skip quietly.</summary>
public static class ArchiveEntryNames
{
    public readonly record struct Kind(bool Skip, bool Unsafe = false, bool Nested = false, string? Format = null);

    /// <summary>Folder entries, hidden files and macOS junk are skipped; nested archives are counted; unsafe names reported.</summary>
    public static Kind Classify(string entryName)
    {
        if (entryName.EndsWith('/')) return new Kind(Skip: true);
        var segments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or "..")) return new Kind(Skip: true, Unsafe: true);
        if (segments.Any(s => s.StartsWith('.')) || segments[0] == "__MACOSX") return new Kind(Skip: true);
        string ext = Path.GetExtension(segments[^1]);
        if (FolderWalker.ArchiveExtensions.Contains(ext)) return new Kind(Skip: true, Nested: true);
        if (!FolderWalker.ModelExtensions.Contains(ext)) return new Kind(Skip: true);
        return new Kind(Skip: false, Format: ext[1..].ToLowerInvariant());
    }

    /// <summary>The path a model inside an archive is shown and stored under: the archive's own path, then the entry's folders.</summary>
    public static string VirtualPath(string archivePath, string entryName) =>
        Path.Join([archivePath, .. entryName.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
}

/// <summary>
/// Reads .stl and .3mf files inside .zip files with System.IO.Compression, without unpacking anything to disk.
/// Entry names are stored with forward slashes, as yauzl (the JS app's zip reader) gave them.
/// </summary>
public sealed class ZipArchiveReader : IArchiveReader
{
    public bool CanRead(string archivePath) => archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string NameOf(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    public async Task<int> EachModelEntryAsync(string archivePath, Func<ArchiveEntry, Task> onEntry, Action<string, string> onProblem, ArchiveLimits limits, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > limits.MaxEntries)
            throw new InvalidDataException($"the zip has too many entries (limit {limits.MaxEntries:N0})");

        int nested = 0;
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = NameOf(entry);
            var kind = ArchiveEntryNames.Classify(name);
            if (kind.Nested) nested++;
            if (kind.Unsafe) onProblem(name, "unsafe file name in the zip");
            if (kind.Skip) continue;

            if (entry.IsEncrypted)
                onProblem(name, "password-protected, so it cannot be read");
            else if (entry.Length > limits.MaxEntryBytes)
                onProblem(name, $"too large to read from a zip ({Math.Round(entry.Length / 1048576.0):N0} MB)");
            else if (entry.Length > limits.RatioMinBytes && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxRatio)
                onProblem(name, "skipped: it is compressed far more than a real model would be");
            else
                await onEntry(new ArchiveEntry(name, kind.Format!, entry.Length,
                    entry.LastWriteTime.ToUnixTimeMilliseconds(), entry.Crc32, entry.Open));
        }
        return nested;
    }

    public async Task<byte[]> ReadEntryAsync(string archivePath, string entryName, long maxBytes, CancellationToken cancellation)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ModelGoneException("The zip is no longer there.", e);
        }
        await using (stream)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => NameOf(e) == entryName)
                ?? throw new ModelGoneException("That file is no longer in the zip.");
            if (entry.Length > maxBytes) throw new InvalidDataException("The file is too large to read from the zip.");
            var bytes = new byte[entry.Length];
            await using var source = entry.Open();
            await source.ReadExactlyAsync(bytes, cancellation);
            return bytes;
        }
    }
}

/// <summary>SHA-256, as lowercase hex, read in large pieces so memory stays small however big the file is.</summary>
public static class Hashing
{
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(stream, cancellation);
    }

    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken cancellation) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
}

/// <summary>The bytes of a model, wherever it lives: a loose file or an entry inside an archive.</summary>
public sealed class ModelSource(IReadOnlyList<IArchiveReader> readers)
{
    /// <summary>Zip files in .NET; .7z and .rar with 7-Zip's library (7z.dll next to the app).</summary>
    public static ModelSource Default { get; } = new([new ZipArchiveReader(), new SevenZip.SevenZipArchiveReader()]);

    public IReadOnlyList<IArchiveReader> Readers { get; } = readers;

    public IArchiveReader? ReaderFor(string archivePath) => Readers.FirstOrDefault(r => r.CanRead(archivePath));

    /// <exception cref="ModelGoneException">The file, the archive or the entry is no longer there.</exception>
    public async Task<byte[]> ReadAsync(ModelLocation location, long maxBytes = 512L * 1024 * 1024, CancellationToken cancellation = default)
    {
        if (location.Container is { } container)
        {
            var reader = ReaderFor(container) ?? throw new NotSupportedException("Reading models from " + Path.GetExtension(container) + " archives is not available yet.");
            return await reader.ReadEntryAsync(container, location.Entry ?? "", maxBytes, cancellation);
        }
        try
        {
            return await File.ReadAllBytesAsync(location.Path, cancellation);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ModelGoneException("That file is no longer where the library found it.", e);
        }
    }
}
