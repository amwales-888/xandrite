using Xandrite.Library.Logging;
namespace Xandrite.Library.Thumbnails;

/// <summary>Writing thumbnail images safely: the queue and the viewer can both write the same model's image.</summary>
public static class ThumbnailFiles
{
    // A few locks shared out by name, rather than one per model, so there is never more than this many.
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Holds a model's thumbnail while its image and its saved view are changed together, so the queue never puts
    /// back an automatic image after the viewer has saved a view (or the other way round).
    /// </summary>
    public static async Task<IDisposable> LockAsync(string sha256)
    {
        var gate = Locks[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(sha256) % Locks.Length)];
        await gate.WaitAsync().ConfigureAwait(false);
        return new Release(gate);
    }

    /// <summary>The same, for code that cannot wait asynchronously.</summary>
    public static IDisposable Lock(string sha256)
    {
        var gate = Locks[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(sha256) % Locks.Length)];
        gate.Wait();
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }

    /// <summary>
    /// Writes the PNG to a temporary file of its own, then puts it in place under the real name, so no one ever sees
    /// half an image. Each write has its own temporary name (the queue and the viewer may write the same model at
    /// once), and the final rename is tried a few times, because on Windows it fails while someone is reading the old
    /// image (the grid loading it, for example).
    /// </summary>
    public static async Task WriteAsync(string final, byte[] png)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        string temporary = final + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await DevLog.WriteAllBytesAsync(temporary, png, "thumbnail").ConfigureAwait(false);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, final, overwrite: true);
                    return;
                }
                catch (Exception e) when (attempt < 10 && e is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(25 * attempt).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }   // only left over if every attempt failed
        }
    }
}
