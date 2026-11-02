using System.Globalization;
using System.Reflection;

namespace Xandrite.Library.Logging;

/// <summary>
/// Formatting for the statistics file (logs\statistics.log): one line for each scan, each rescan of several
/// folders and each thumbnail run, kept so that runs can be compared over time. Unlike the log it is small, so it
/// keeps months of history.
/// </summary>
public static class Stat
{
    /// <summary>A duration people can read: <c>45.2s</c>, <c>3m07s</c>, <c>2h05m09s</c>, <c>3d04h12m</c>.</summary>
    public static string Took(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        var c = CultureInfo.InvariantCulture;
        if (t.TotalDays >= 1) return string.Create(c, $"{(int)t.TotalDays}d{t.Hours:00}h{t.Minutes:00}m");
        if (t.TotalHours >= 1) return string.Create(c, $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s");
        if (t.TotalMinutes >= 1) return string.Create(c, $"{t.Minutes}m{t.Seconds:00}s");
        return string.Create(c, $"{t.TotalSeconds:0.0}s");
    }

    /// <summary>A local date and time, to the second: <c>2026-11-01T22:13:05</c>.</summary>
    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static double Seconds(long ms) => Math.Round(ms / 1000.0, 1);

    /// <summary>How many per second (or minute, with <paramref name="per"/> 60), or null when no time passed.</summary>
    public static double? Rate(long count, long ms, int per = 1) => ms > 0 ? Math.Round(count * 1000.0 * per / ms, 1) : null;

    public static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    /// <summary>The app's version, e.g. 1.4.0 (null in tests).</summary>
    public static string? Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);

    /// <summary>Where a folder is: network (a share or mapped drive), local, removable, optical, or unknown.</summary>
    public static string WhereIs(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) return "network";
        try
        {
            string? drive = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(drive)) return "unknown";
            return new DriveInfo(drive).DriveType switch
            {
                DriveType.Network => "network",
                DriveType.Removable => "removable",
                DriveType.CDRom => "optical",
                DriveType.Fixed or DriveType.Ram => "local",
                _ => "unknown",
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
