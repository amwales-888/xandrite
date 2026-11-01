using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xandrite.Library.Logging;

/// <summary>
/// The app's log. One event per line:
/// <code>2026-10-28T16:49:12.345+00:00 INFO  hash path="C:\Models\a b.stl" sha256=ab12… size=1284 ms=3</code>
/// The same format as the Electron app's src/logger.js, so old and new logs read alike.
/// </summary>
public interface ILog
{
    /// <summary>The log file, or null when nothing is written.</summary>
    string? Path { get; }

    void Info(string evt, params ReadOnlySpan<(string Key, object? Value)> fields);

    void Warn(string evt, params ReadOnlySpan<(string Key, object? Value)> fields);

    void Error(string evt, params ReadOnlySpan<(string Key, object? Value)> fields);

    /// <summary>Writes what is waiting now (before showing the file, and when the app closes).</summary>
    void Flush();
}

public static class LogExtensions
{
    /// <summary>A warning when something went wrong (errors, cancelled), otherwise information.</summary>
    public static void InfoOrWarn(this ILog log, bool warn, string evt, params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        if (warn) log.Warn(evt, fields);
        else log.Info(evt, fields);
    }
}

/// <summary>A log that writes nothing. The default everywhere, and what the app falls back to if the file cannot be made.</summary>
public sealed class NullLog : ILog
{
    public static NullLog Instance { get; } = new();

    public string? Path => null;
    public void Info(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) { }
    public void Warn(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) { }
    public void Error(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) { }
    public void Flush() { }
}

/// <summary>
/// A plain-text log file. Lines are collected and written a few times a second; the file is rolled over when it
/// reaches <see cref="MaxBytes"/> (xandrite.log → xandrite.log.1 → .2 …), keeping <see cref="Keep"/> old files, so
/// it cannot fill a disk. A log that cannot be written is given up on quietly: it must never stop the app.
/// </summary>
public sealed partial class FileLog : ILog, IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private readonly Timer _timer;
    private bool _timerSet;
    private long _size;
    private bool _broken;

    /// <exception cref="IOException">The folder for the log cannot be made. Use <see cref="NullLog"/> instead.</exception>
    public FileLog(string file, long maxBytes = 10 * 1024 * 1024, int keep = 5, int flushMs = 250)
    {
        Path = file;
        MaxBytes = maxBytes;
        Keep = keep;
        FlushDelay = TimeSpan.FromMilliseconds(flushMs);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        _size = File.Exists(file) ? new FileInfo(file).Length : 0;
        _timer = new Timer(_ => Flush());
    }

    public string Path { get; }

    string? ILog.Path => Path;

    public long MaxBytes { get; }

    public int Keep { get; }

    private TimeSpan FlushDelay { get; }

    public void Info(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) => Write("INFO", evt, fields);

    public void Warn(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) => Write("WARN", evt, fields);

    public void Error(string evt, params ReadOnlySpan<(string Key, object? Value)> fields) => Write("ERROR", evt, fields);

    /// <summary>
    /// Adds the managed id of the thread that wrote it to every line (<c>tid=12</c>, at the end, so the fields before
    /// it keep their places). A developer option.
    /// </summary>
    public bool ThreadIds { get; set; }

    private void Write(string level, string evt, ReadOnlySpan<(string Key, object? Value)> fields)
    {
        string line = FormatLine(level, evt, fields, DateTimeOffset.Now);
        if (ThreadIds && !line.Contains(" tid=", StringComparison.Ordinal)) line += " tid=" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool flushNow;
        lock (_gate)
        {
            _pending.Add(line);
            flushNow = _pending.Count >= 500;
            if (!flushNow && !_timerSet)
            {
                _timerSet = true;
                _timer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
            }
        }
        if (flushNow) Flush();
    }

    public void Flush()
    {
        lock (_gate)
        {
            _timerSet = false;
            if (_pending.Count == 0 || _broken)
            {
                _pending.Clear();
                return;
            }
            try
            {
                var batch = new StringBuilder();
                long batchBytes = 0;
                foreach (var line in _pending)
                {
                    string text = line + Environment.NewLine;
                    int bytes = Encoding.UTF8.GetByteCount(text);
                    if (_size + batchBytes > 0 && _size + batchBytes + bytes > MaxBytes)
                    {
                        Append(batch, ref batchBytes);
                        Rotate();   // start a fresh file
                    }
                    batch.Append(text);
                    batchBytes += bytes;
                }
                Append(batch, ref batchBytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _broken = true;   // a log that cannot be written must never stop the app
            }
            _pending.Clear();
        }
    }

    private void Append(StringBuilder batch, ref long batchBytes)
    {
        if (batch.Length == 0) return;
        File.AppendAllText(Path, batch.ToString(), Encoding.UTF8);
        _size += batchBytes;
        batch.Clear();
        batchBytes = 0;
    }

    private void Rotate()
    {
        for (int i = Keep - 1; i >= 1; i--)
        {
            if (File.Exists(Path + "." + i)) File.Move(Path + "." + i, Path + "." + (i + 1), overwrite: true);
        }
        if (File.Exists(Path)) File.Move(Path, Path + ".1", overwrite: true);
        File.Delete(Path + "." + (Keep + 1));
        _size = 0;
    }

    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }

    /* ---------- the line format ---------- */

    /// <summary>"2026-10-28T16:49:12.345+00:00": local time with its offset.</summary>
    public static string Timestamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^[\w.\-/\\:+@%]+$", RegexOptions.ECMAScript)]
    private static partial Regex PlainValue();

    /// <summary>Numbers and booleans as they are; plain words as they are; anything with spaces, quotes or line breaks as a JSON string.</summary>
    public static string FormatValue(object? value)
    {
        switch (value)
        {
            case null: return "-";
            case bool b: return b ? "true" : "false";
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case IFormattable n when value is int or long or uint or ulong or short or ushort or byte or decimal:
                return n.ToString(null, CultureInfo.InvariantCulture);
        }
        string text = value is Exception e ? e.Message : value.ToString() ?? "";
        return PlainValue().IsMatch(text) ? text : JsonSerializer.Serialize(text, Json);   // JSON also protects against line breaks
    }

    public static string FormatLine(string level, string evt, ReadOnlySpan<(string Key, object? Value)> fields, DateTimeOffset time)
    {
        var line = new StringBuilder(Timestamp(time)).Append(' ').Append(level.PadRight(5)).Append(' ').Append(evt);
        foreach (var (key, value) in fields) line.Append(' ').Append(key).Append('=').Append(FormatValue(value));
        return line.ToString();
    }
}
