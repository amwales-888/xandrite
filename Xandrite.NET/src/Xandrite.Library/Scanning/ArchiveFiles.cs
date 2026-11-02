using Xandrite.Library.Logging;

namespace Xandrite.Library.Scanning;

/// <summary>
/// How archive files (zip, 7z, rar, and each part or volume of a set) are opened for reading:
///
///     file (unbuffered) → measured (IoMeter) → logged (Developer → archive reads) → read buffer → the reader
///
/// The readers ask for small pieces (the zip decompressor 8 KB at a time, RAR a few bytes for each header); the read
/// buffer turns those into reads of <see cref="ReadBufferBytes"/> from the disk or share. Because the measuring and
/// logging sit below the buffer, they show the reads that really reach the disk or network.
/// </summary>
public static class ArchiveFiles
{
    public const int DefaultReadBufferBytes = 1 << 20;   // 1 MB: few, large reads suit a network share
    private const int Smallest = 4 << 10, Largest = 16 << 20;

    private static int _readBufferBytes = DefaultReadBufferBytes;

    /// <summary>Bytes read from an archive file at a time (Preferences → Developer → Archive read size).</summary>
    public static int ReadBufferBytes
    {
        get => _readBufferBytes;
        set => _readBufferBytes = Math.Clamp(value, Smallest, Largest);
    }

    /// <summary>An archive file ready for a reader: buffered, measured and (when switched on) logged.</summary>
    public static Stream OpenRead(string path, string purpose, FileOptions options = FileOptions.None) =>
        Buffered(OpenUnbuffered(path, purpose, options));

    /// <summary>
    /// The file with no buffer of its own, measured and logged: for the parts of a split set, which are read through
    /// one buffer over the whole set. <paramref name="meter"/>: whose meter to use when called on 7-Zip's thread.
    /// </summary>
    internal static Stream OpenUnbuffered(string path, string purpose, FileOptions options = FileOptions.None, IoMeter? meter = null)
    {
        Stream stream = IoMeter.OpenFile(path, bufferSize: 0, options, purpose);   // 0: no FileStream buffer
        stream = meter is not null ? new MeteredStream(stream, meter, path) : IoMeter.Wrap(stream, path);
        return DevLog.ArchiveIoStream(stream, path);
    }

    /// <summary>Puts the read buffer over an unbuffered archive stream (a file, or a split set read as one).</summary>
    public static Stream Buffered(Stream unbuffered) => new BufferedStream(unbuffered, _readBufferBytes);
}
