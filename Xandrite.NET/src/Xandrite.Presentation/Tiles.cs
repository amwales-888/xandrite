using System.Collections.ObjectModel;
using System.Globalization;
using Xandrite.Library;

namespace Xandrite.Presentation;

/// <summary>One thumbnail in the grid.</summary>
public sealed class TileViewModel : ObservableObject
{
    private LocationRow _row;
    private bool _isSelected;
    private int _imageVersion;
    private string? _caption;

    internal TileViewModel(LocationRow row, int index, string imagePath)
    {
        _row = row;
        Index = index;
        ImagePath = imagePath;
    }

    /// <summary>Position in the current list (for shift-click ranges and arrow keys).</summary>
    public int Index { get; }

    public LocationRow Row
    {
        get => _row;
        internal set
        {
            if (!Set(ref _row, value)) return;
            OnPropertyChanged(nameof(RatingText));
            OnPropertyChanged(nameof(HasCustomView));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(PlaceholderText));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string Name => _row.Name;

    /// <summary>The PNG for this model (it may not exist yet).</summary>
    public string ImagePath { get; }

    /// <summary>Goes up whenever the image file is redrawn, so the view reloads it.</summary>
    public int ImageVersion
    {
        get => _imageVersion;
        internal set => Set(ref _imageVersion, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>A date under the name when the list is ordered by date; otherwise nothing.</summary>
    public string? Caption
    {
        get => _caption;
        internal set => Set(ref _caption, value);
    }

    public string RatingText => _row.Rating is { } stars ? new string('★', stars) + new string('☆', 5 - stars) : "";

    public bool HasCustomView => _row.CustomView;

    public bool IsMissing => _row.Missing;

    public bool IsFailed => _row.ThumbnailState == ThumbnailState.Failed;

    public string? ArchiveName => _row.ArchiveName;

    /// <summary>Shown where the image goes until there is one.</summary>
    public string PlaceholderText => _row.ThumbnailState == ThumbnailState.Failed ? "Could not draw" : "Waiting for thumbnail";

    public string ToolTip
    {
        get
        {
            var lines = new List<string> { _row.Path, Format.Bytes(_row.Size) + " · " + _row.Format.ToUpperInvariant() };
            if (_row.ArchiveName is { } archive) lines.Add("Inside " + archive);
            if (_row.Missing) lines.Add("Missing: not found at the last scan");
            if (_row.ThumbnailState == ThumbnailState.Failed && _row.ThumbnailError is { } error) lines.Add("Thumbnail failed: " + error);
            if (_row.CustomView) lines.Add("Custom view");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>A row of tiles. The grid is a virtualised list of rows, so only the rows on screen exist as controls.</summary>
public sealed class TileRowViewModel(IReadOnlyList<TileViewModel> tiles)
{
    public IReadOnlyList<TileViewModel> Tiles { get; } = tiles;
}

/// <summary>A folder in the tree on the left. "All files" is a node with no path.</summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private readonly Action<FolderNodeViewModel>? _onSelected;

    internal FolderNodeViewModel(string name, string? path, int count, bool isRoot, Action<FolderNodeViewModel>? onSelected)
    {
        Name = name;
        Path = path;
        Count = count;
        IsRoot = isRoot;
        _onSelected = onSelected;
    }

    public string Name { get; }

    /// <summary>Null for "All files".</summary>
    public string? Path { get; }

    public int Count
    {
        get;
        internal set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }

    public bool IsRoot { get; }

    public string Label => $"{Name}  ({Count.ToString("N0", CultureInfo.CurrentCulture)})";

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) _onSelected?.Invoke(this);
        }
    }

    internal IEnumerable<FolderNodeViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public static string Date(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
