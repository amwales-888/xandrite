namespace Xandrite.Library.Thumbnails;

/// <summary>How hard the thumbnail queue works. Chosen under Preferences; can be changed while it runs.</summary>
public enum ThumbnailSpeed
{
    /// <summary>Every worker, at normal priority; 7-Zip uses as many threads as it likes. The fastest.</summary>
    Full,

    /// <summary>Half the workers, below normal priority, and 7-Zip limited to a quarter of the cores.</summary>
    Balanced,

    /// <summary>One worker at the lowest priority, 7-Zip on one thread: other programs hardly notice it.</summary>
    Background,
}

/// <summary>What a speed means on this machine.</summary>
/// <param name="Workers">Models drawn at once.</param>
/// <param name="Priority">Priority of the threads that read, decode and draw a model.</param>
/// <param name="ArchiveThreads">Threads 7-Zip may use to decompress one archive; null leaves it to 7-Zip (all cores).</param>
public sealed record WorkLimits(int Workers, ThreadPriority Priority, int? ArchiveThreads)
{
    public static WorkLimits For(ThumbnailSpeed speed, int maxWorkers, int processors) => speed switch
    {
        ThumbnailSpeed.Background => new(1, ThreadPriority.Lowest, 1),
        ThumbnailSpeed.Balanced => new(Math.Max(1, maxWorkers / 2), ThreadPriority.BelowNormal, Math.Max(1, processors / 4)),
        _ => new(maxWorkers, ThreadPriority.Normal, null),
    };

    public static ThumbnailSpeed Parse(string? value) =>
        Enum.TryParse<ThumbnailSpeed>(value, ignoreCase: true, out var speed) && Enum.IsDefined(speed) ? speed : ThumbnailSpeed.Full;
}

/// <summary>
/// The limits of the thumbnail work going on in this async flow, so code far below the queue (the 7-Zip reader, which
/// runs on threads of its own) can keep to them. Null outside the queue: the viewer and the scanner work at full speed.
/// </summary>
public static class BackgroundWork
{
    private static readonly AsyncLocal<WorkLimits?> Limits = new();

    public static WorkLimits? Current
    {
        get => Limits.Value;
        set => Limits.Value = value;
    }

    /// <summary>Runs CPU-heavy work at the current priority, putting the thread's own priority back afterwards.</summary>
    public static T Run<T>(Func<T> work)
    {
        var priority = Current?.Priority ?? ThreadPriority.Normal;
        if (priority == ThreadPriority.Normal) return work();
        var thread = Thread.CurrentThread;
        var before = thread.Priority;
        try
        {
            thread.Priority = priority;
            return work();
        }
        finally
        {
            thread.Priority = before;
        }
    }
}
