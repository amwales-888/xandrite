using Xandrite.Library;

namespace Xandrite.Presentation;

public sealed record SortOption(string Label, SortOrder Value);

public sealed record SizeFilterOption(string Label, long? Min, long? Max);

/// <summary>An age range: files newer than <see cref="NewerThan"/>, or older than <see cref="OlderThan"/>.</summary>
public sealed record AgeFilterOption(string Label, TimeSpan? NewerThan, TimeSpan? OlderThan);

public sealed record AgeBasisOption(string Label, AgeBasis Value);

public sealed record RatingFilterOption(string Label, RatingFilter Value);

/// <summary>A filter that is on, shown as a chip under the toolbar; its button turns that one filter off.</summary>
public sealed record FilterChip(string Label, string RemoveLabel, RelayCommand Remove);

/// <summary>The choices in the filter bar, as in the Electron app.</summary>
public static class FilterOptions
{
    private const long KB = 1024, MB = 1024 * 1024;

    public static IReadOnlyList<SortOption> Sorts { get; } =
    [
        new("Name", SortOrder.Name),
        new("Folder and name", SortOrder.Path),
        new("Date modified", SortOrder.Modified),
        new("Date added", SortOrder.Added),
        new("File size", SortOrder.Size),
        new("Rating", SortOrder.Rating),
    ];

    public static IReadOnlyList<SizeFilterOption> Sizes { get; } =
    [
        new("Any size", null, null),
        new("Under 100 KB", null, 100 * KB),
        new("100 KB – 1 MB", 100 * KB, MB),
        new("1 – 10 MB", MB, 10 * MB),
        new("10 – 50 MB", 10 * MB, 50 * MB),
        new("Over 50 MB", 50 * MB, null),
    ];

    public static IReadOnlyList<AgeFilterOption> Ages { get; } =
    [
        new("Any age", null, null),
        new("Last 24 hours", TimeSpan.FromDays(1), null),
        new("Last 7 days", TimeSpan.FromDays(7), null),
        new("Last 30 days", TimeSpan.FromDays(30), null),
        new("Last year", TimeSpan.FromDays(365), null),
        new("Older than a year", null, TimeSpan.FromDays(365)),
    ];

    public static IReadOnlyList<AgeBasisOption> AgeBases { get; } =
    [
        new("modified", AgeBasis.Modified),
        new("added", AgeBasis.Added),
    ];

    public static IReadOnlyList<RatingFilterOption> Ratings { get; } =
    [
        new("Any rating", RatingFilter.Any),
        new("Unrated", RatingFilter.Unrated),
        new("Rated", RatingFilter.Rated),
        new("★ and up", RatingFilter.AtLeast1),
        new("★★ and up", RatingFilter.AtLeast2),
        new("★★★ and up", RatingFilter.AtLeast3),
        new("★★★★ and up", RatingFilter.AtLeast4),
        new("★★★★★", RatingFilter.Five),
    ];
}
