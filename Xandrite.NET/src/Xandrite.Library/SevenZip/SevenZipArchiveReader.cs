using System.Runtime.ExceptionServices;
using Xandrite.Library.Scanning;
using Xandrite.Library.Thumbnails;
using static Xandrite.Library.SevenZip.SevenZipNative;

namespace Xandrite.Library.SevenZip;

/// <summary>
/// Reads .stl and .3mf files inside .7z and .rar archives (RAR 4 and RAR 5, solid or not, split into numbered parts or
/// multi-volume) with 7-Zip's own library, in the app's process. It replaces the Electron app's sevenzip-reader.js,
/// which ran 7-Zip as a separate program and cut its output apart.
///
/// Entry names, sizes and CRC-32s are recorded exactly as the Electron app recorded them (names with forward slashes),
/// so a library it built is recognised as unchanged.
///
/// All wanted entries of an archive are extracted in one pass, in archive order, which is what keeps solid archives
/// (where reading one file means decompressing everything before it) fast. 7-Zip checks each file's CRC itself.
/// </summary>
public sealed class SevenZipArchiveReader : IArchiveReader
{
    /// <summary>Whether 7-Zip's library was found (7z.dll next to the app).</summary>
    public static bool IsAvailable => SevenZipNative.IsAvailable;

    public bool CanRead(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        if (FolderWalker.GetSplitRole(name) is { } split) return split.First;
        string ext = Path.GetExtension(name);
        return ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase);
    }

    /* ---------- opening ---------- */

    private sealed record Item(uint Index, string Name, bool IsDirectory, long Size, long PackedSize, double? MtimeMs, long? Crc32, bool Encrypted);

    /// <summary>An archive opened by 7-Zip, with the streams and callbacks it uses. Disposing closes everything.</summary>
    private sealed unsafe class OpenArchive : IDisposable
    {
        private nint _archive;
        private readonly InStream _stream;
        private readonly OpenCallback _callback;

        private OpenArchive(nint archive, InStream stream, OpenCallback callback)
        {
            _archive = archive;
            _stream = stream;
            _callback = callback;
        }

        public static OpenArchive Open(string path)
        {
            if (!SevenZipNative.IsAvailable) throw new NotSupportedException(LoadError);

            Stream stream;
            string name = Path.GetFileName(path);
            if (FolderWalker.GetSplitRole(name) is { Style: FolderWalker.SplitStyle.Numbered })
            {
                // name.7z.001, .002 ...: the parts are simply one file cut into pieces.
                string folder = Path.GetDirectoryName(path) ?? "";
                var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
                var (parts, problem) = FolderWalker.ArchiveParts(name, names);
                if (problem is not null) throw new InvalidDataException(problem);
                stream = new SplitStream(parts.Select(p => Path.Combine(folder, p)).ToList());
            }
            else
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }

            byte format;
            try
            {
                format = DetectFormat(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            nint archive = CreateArchive(format);
            if (archive == 0)
            {
                stream.Dispose();
                throw new NotSupportedException("this copy of 7-Zip cannot read " + (format == Format7z ? "7z" : "RAR") + " archives");
            }

            // In the thumbnail queue at a slower speed: decompress with fewer threads (7-Zip uses every core otherwise).
            if (BackgroundWork.Current?.ArchiveThreads is { } threads) SetThreads(archive, threads);

            var input = new InStream(stream);
            var callback = new OpenCallback(path);
            ulong maxCheck = 0;   // the archive starts at the beginning: no searching for a signature further in
            int hr = SevenZipNative.Open(archive, input.Pointer(IID_IInStream), &maxCheck, callback.Pointer(IID_IArchiveOpenCallback));
            if (hr != S_OK)
            {
                bool password = callback.PasswordRequested;
                var failure = callback.Failure ?? input.Failure;
                Release(archive);
                input.Dispose();
                callback.Dispose();
                if (password) throw new InvalidDataException("password-protected, so it cannot be read");
                if (failure is IOException io) throw io;
                throw new InvalidDataException("not a valid archive, or damaged");
            }
            return new OpenArchive(archive, input, callback);
        }

        private static byte DetectFormat(Stream stream)
        {
            Span<byte> head = stackalloc byte[8];
            int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = 0;
            if (n >= 6 && head[..6].SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])) return Format7z;
            if (n >= 8 && head.SequenceEqual((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00])) return FormatRar5;
            if (n >= 7 && head[..7].SequenceEqual((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00])) return FormatRar;
            throw new InvalidDataException("not a valid archive, or damaged");
        }

        public List<Item> Items(int maxEntries)
        {
            uint count;
            Check(GetNumberOfItems(_archive, &count));
            if (count > maxEntries) throw new InvalidDataException($"the archive has too many entries (limit {maxEntries:N0})");
            var items = new List<Item>((int)count);
            for (uint i = 0; i < count; i++)
            {
                string? path = String(i, kpidPath);
                items.Add(new Item(
                    i,
                    (path ?? "").Replace('\\', '/'),
                    Bool(i, kpidIsDir),
                    (long)(Number(i, kpidSize) ?? 0),
                    (long)(Number(i, kpidPackSize) ?? 0),
                    Time(i, kpidMTime),
                    Number(i, kpidCRC) is { } crc ? (long)crc : null,
                    Bool(i, kpidEncrypted)));
            }
            return items;
        }

        public long PhysicalSize
        {
            get
            {
                PropVariant value = default;
                if (GetArchiveProperty(_archive, kpidPhySize, &value) != S_OK) return 0;
                long size = (long)(value.AsUInt64 ?? 0);
                value.Clear();
                return size;
            }
        }

        private string? String(uint index, uint property)
        {
            PropVariant value = default;
            Check(GetProperty(_archive, index, property, &value));
            string? text = value.Type == VT_BSTR ? ReadWide(value.Pointer) : null;
            value.Clear();
            return text;
        }

        private ulong? Number(uint index, uint property)
        {
            PropVariant value = default;
            Check(GetProperty(_archive, index, property, &value));
            var number = value.AsUInt64;
            value.Clear();
            return number;
        }

        private bool Bool(uint index, uint property)
        {
            PropVariant value = default;
            Check(GetProperty(_archive, index, property, &value));
            bool result = value.AsBool;
            value.Clear();
            return result;
        }

        private double? Time(uint index, uint property)
        {
            PropVariant value = default;
            Check(GetProperty(_archive, index, property, &value));
            var ms = value.AsUnixMs;
            value.Clear();
            return ms;
        }

        private static void Check(int hr)
        {
            if (hr != S_OK) throw new InvalidDataException($"7-Zip could not read the archive's list of files (error 0x{hr:X8})");
        }

        /// <summary>
        /// Extracts the given items in one pass. <paramref name="completed"/> receives each item's content (or null with
        /// 7-Zip's result code). An exception thrown by it stops the extraction and is rethrown here.
        /// </summary>
        public void Extract(IReadOnlyList<uint> indices, long maxBytes, Action<uint, MemoryStream?, int, bool> completed, CancellationToken cancellation)
        {
            if (indices.Count == 0) return;
            var sorted = indices.Distinct().Order().ToArray();
            var wanted = sorted.ToHashSet();
            using var callback = new ExtractCallback(wanted.Contains, completed, maxBytes, cancellation);
            int hr;
            fixed (uint* list = sorted)
                hr = SevenZipNative.Extract(_archive, list, (uint)sorted.Length, 0, callback.Pointer(IID_IArchiveExtractCallback));
            if (callback.Failure is { } failure) ExceptionDispatchInfo.Throw(failure);
            cancellation.ThrowIfCancellationRequested();
            if (hr != S_OK)
            {
                if (callback.PasswordRequested) throw new InvalidDataException("password-protected, so it cannot be read");
                if (_stream.Failure is IOException io) throw io;
                throw new InvalidDataException($"the archive could not be read to the end (error 0x{hr:X8})");
            }
        }

        public void Dispose()
        {
            if (_archive != 0)
            {
                Close(_archive);
                Release(_archive);
                _archive = 0;
            }
            _stream.Dispose();
            _callback.Dispose();
        }
    }

    /// <summary>A readable sentence for 7-Zip's extraction result codes.</summary>
    private static string Describe(int result, bool tooLarge) => tooLarge ? "too large to read from an archive" : result switch
    {
        1 => "compressed with a method that is not supported",
        2 => "the file is damaged (data error)",
        3 => "the file is damaged (checksum does not match)",
        4 => "part of the file is missing from the archive",
        5 => "the archive ends before the file is complete",
        9 => "password-protected, so it cannot be read",
        _ => $"could not be extracted (7-Zip result {result})",
    };

    /* ---------- IArchiveReader ---------- */

    public Task<int> EachModelEntryAsync(string archivePath, Func<ArchiveEntry, Task> onEntry, Action<string, string> onProblem, ArchiveLimits limits, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var archive = OpenArchive.Open(archivePath);
            var items = archive.Items(limits.MaxEntries);

            int nested = 0;
            var eligible = new Dictionary<uint, (Item Item, string Format)>();
            foreach (var item in items)
            {
                if (item.IsDirectory) continue;
                var kind = ArchiveEntryNames.Classify(item.Name);
                if (kind.Nested) nested++;
                if (kind.Unsafe) onProblem(item.Name, "unsafe file name in the archive");
                if (kind.Skip) continue;
                if (item.Encrypted)
                    onProblem(item.Name, "password-protected, so it cannot be read");
                else if (item.Size > limits.MaxEntryBytes)
                    onProblem(item.Name, $"too large to read from an archive ({Math.Round(item.Size / 1048576.0):N0} MB)");
                else if (item.PackedSize > 0 && item.Size > limits.RatioMinBytes && (double)item.Size / item.PackedSize > limits.MaxRatio)
                    onProblem(item.Name, "skipped: it is compressed far more than a real model would be");
                else
                    eligible[item.Index] = (item, kind.Format!);
            }

            long total = eligible.Values.Sum(e => e.Item.Size);
            long physical = archive.PhysicalSize;
            if (total > limits.RatioMinBytes && physical > 0 && (double)total / physical > limits.MaxRatio)
                throw new InvalidDataException("the archive is compressed far more than real models would be");

            // An entry without a date of its own takes the archive's (as in the JS app).
            double archiveTime = FileTimes.ToNodeMs(File.GetLastWriteTimeUtc(archivePath));
            archive.Extract(eligible.Keys.ToList(), limits.MaxEntryBytes, (index, content, result, tooLarge) =>
            {
                var (item, format) = eligible[index];
                if (content is null)
                {
                    onProblem(item.Name, Describe(result, tooLarge));
                    return;
                }
                byte[] bytes = content.GetBuffer();
                int length = (int)content.Length;
                // The callback runs on 7-Zip's thread inside Extract; it is waited for here, one entry at a time.
                onEntry(new ArchiveEntry(item.Name, format, item.Size, item.MtimeMs ?? archiveTime, item.Crc32,
                    () => new MemoryStream(bytes, 0, length, writable: false))).GetAwaiter().GetResult();
            }, cancellation);
            return nested;
        }, cancellation);

    public Task<byte[]> ReadEntryAsync(string archivePath, string entryName, long maxBytes, CancellationToken cancellation) =>
        Task.Run(() => BackgroundWork.Run(() =>
        {
            if (!File.Exists(archivePath)) throw new ModelGoneException("The archive is no longer there.");
            using var archive = OpenArchive.Open(archivePath);
            var item = archive.Items(int.MaxValue).FirstOrDefault(i => !i.IsDirectory && i.Name == entryName)
                ?? throw new ModelGoneException("That file is no longer in the archive.");
            if (item.Encrypted) throw new InvalidDataException("The file is password-protected, so it cannot be read.");
            if (item.Size > maxBytes) throw new InvalidDataException("The file is too large to read from the archive.");

            byte[]? result = null;
            string? problem = null;
            archive.Extract([item.Index], maxBytes, (_, content, code, tooLarge) =>
            {
                if (content is null) problem = Describe(code, tooLarge);
                else result = content.ToArray();
            }, cancellation);
            return result ?? throw new InvalidDataException("The file could not be read: " + (problem ?? "nothing was extracted"));
        }), cancellation);

    /// <summary>
    /// Every entry asked for, in one Extract call: for a solid archive the stream is decompressed once, from the start
    /// to the last entry wanted, instead of once per entry.
    /// </summary>
    public Task ReadEntriesAsync(string archivePath, IReadOnlyCollection<string> entryNames, long maxBytes, Func<EntryRead, Task> onEntry, CancellationToken cancellation) =>
        Task.Run(() => BackgroundWork.Run(() =>
        {
            if (!File.Exists(archivePath)) throw new ModelGoneException("The archive is no longer there.");
            using var archive = OpenArchive.Open(archivePath);
            var wanted = new HashSet<string>(entryNames, StringComparer.Ordinal);
            var found = new Dictionary<uint, Item>();
            foreach (var item in archive.Items(int.MaxValue))
            {
                if (item.IsDirectory || !wanted.Remove(item.Name)) continue;   // the first entry of each name, as ReadEntryAsync
                if (item.Encrypted) Report(new EntryRead(item.Name, null, false, "The file is password-protected, so it cannot be read."));
                else if (item.Size > maxBytes) Report(new EntryRead(item.Name, null, false, "The file is too large to read from the archive."));
                else found[item.Index] = item;
            }
            foreach (var name in wanted) Report(new EntryRead(name, null, Gone: true, Problem: null));   // no longer in the archive

            if (found.Count > 0)
            {
                // The callback runs on 7-Zip's thread, one entry at a time; waiting in it holds back the next entry.
                archive.Extract(found.Keys.ToList(), maxBytes, (index, content, code, tooLarge) =>
                {
                    var item = found[index];
                    Report(content is null
                        ? new EntryRead(item.Name, null, false, "The file could not be read: " + Describe(code, tooLarge))
                        : new EntryRead(item.Name, content.ToArray(), false, null));
                }, cancellation);
            }
            return 0;

            void Report(EntryRead read)
            {
                cancellation.ThrowIfCancellationRequested();
                onEntry(read).GetAwaiter().GetResult();
            }
        }), cancellation);
}
