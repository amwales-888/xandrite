namespace Xandrite.Library;

/// <summary>A folder in the library, with how many present files it holds.</summary>
public sealed record RootInfo(string Path, long? LastScannedAt, long Files);

/// <summary>What the file list can be ordered by.</summary>
public enum SortOrder
{
    /// <summary>The file's own name, across folders.</summary>
    Name,
    /// <summary>Folder by folder.</summary>
    Path,
    /// <summary>When the file last changed on disk.</summary>
    Modified,
    /// <summary>When the library first saw this content.</summary>
    Added,
    Size,
    /// <summary>Unrated files count as lower than 0 stars.</summary>
    Rating,
}

public enum AgeBasis { Modified, Added }

public enum RatingFilter { Any, Unrated, Rated, Zero, AtLeast1, AtLeast2, AtLeast3, AtLeast4, Five }

/// <summary>Models by their thumbnail's view: one saved in the viewer (custom), or the automatic one (default).</summary>
public enum ViewFilter { Any, Custom, Default }

/// <summary>Which files to list. Mirrors the options of listLocations in the JS app.</summary>
public sealed record ListQuery
{
    /// <summary>Matches part of the path or of the hash.</summary>
    public string Search { get; init; } = "";
    public int Limit { get; init; } = 200;
    public int Offset { get; init; }
    public bool IncludeMissing { get; init; } = true;
    /// <summary>Only files inside this folder.</summary>
    public string? Folder { get; init; }
    /// <summary>With <see cref="Folder"/>: include subfolders, or only the folder's own files.</summary>
    public bool Recursive { get; init; } = true;
    public SortOrder Sort { get; init; } = SortOrder.Name;
    /// <summary>True ascending, false descending, null for the order's natural direction.</summary>
    public bool? Ascending { get; init; }
    /// <summary>Bytes, inclusive.</summary>
    public long? SizeMin { get; init; }
    /// <summary>Bytes, exclusive.</summary>
    public long? SizeMax { get; init; }
    /// <summary>Milliseconds since 1970, inclusive, applied to <see cref="AgeBasis"/>.</summary>
    public double? Since { get; init; }
    /// <summary>Milliseconds since 1970, exclusive.</summary>
    public double? Before { get; init; }
    public AgeBasis AgeBasis { get; init; } = AgeBasis.Modified;
    public RatingFilter Rating { get; init; } = RatingFilter.Any;
    public ViewFilter View { get; init; } = ViewFilter.Any;
}

public enum ThumbnailState { Pending, Ok, Failed }

/// <summary>One place a model was found, with everything the thumbnail grid shows.</summary>
public sealed record LocationRow
{
    public required string Path { get; init; }
    public required string Root { get; init; }
    /// <summary>Path relative to the root.</summary>
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    /// <summary>The folder relative to the root, "" for the root itself.</summary>
    public required string Folder { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public required string Format { get; init; }
    public bool Missing { get; init; }
    public ThumbnailState ThumbnailState { get; init; }
    public string? ThumbnailError { get; init; }
    public long? ThumbnailCreatedAt { get; init; }
    public bool CustomView { get; init; }
    /// <summary>0 to 5, or null for unrated.</summary>
    public int? Rating { get; init; }
    public double ModifiedMs { get; init; }
    /// <summary>The archive this model is inside, or null for a loose file.</summary>
    public string? Container { get; init; }
    /// <summary>Its name inside that archive.</summary>
    public string? Entry { get; init; }
    public string? ArchiveName { get; init; }
    public long AddedAt { get; init; }
}

public sealed record ListPage(long Total, IReadOnlyList<LocationRow> Rows);

/// <summary>A node of the folder tree. <see cref="Count"/> includes subfolders; <see cref="Direct"/> is the folder's own files.</summary>
public sealed record FolderNode(string Name, string Path, int Direct, int Count, IReadOnlyList<FolderNode> Children);

/// <summary>A model waiting for a thumbnail, with one present place to read it from.</summary>
public sealed record ThumbnailJobRow(string Sha256, string Format, long Size, string Path, string? Container, string? Entry);

public sealed record ThumbnailCounts(long Ok, long Failed, long Pending);

public sealed record LibraryStats(long Files, long UniqueModels, long Missing, long DuplicateGroups);

public sealed record RemoveRootResult(int RemovedLocations, IReadOnlyList<string> PurgedShas);

/// <summary>Where a model's bytes are: a loose file (no container) or an entry inside an archive.</summary>
public sealed record ModelLocation(string Path, string? Container = null, string? Entry = null);

/// <summary>A view saved in the viewer, in the form the database stores it.</summary>
public sealed record SavedView(double[] Position, double[] Target, double FovDeg, bool ZUp);
