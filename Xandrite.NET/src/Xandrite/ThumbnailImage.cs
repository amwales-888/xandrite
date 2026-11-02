using Xandrite.Library.Logging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xandrite.Presentation;

namespace Xandrite.App;

/// <summary>
/// Loads a tile's thumbnail into an Image without blocking the UI: the PNG is decoded on a background thread at the
/// size it is shown (not the full 512 px), and recent images are kept in a small cache so scrolling back is instant.
///
/// Usage: &lt;Image local:ThumbnailImage.Tile="{Binding}" local:ThumbnailImage.Version="{Binding ImageVersion}"
///                  local:ThumbnailImage.DecodeSize="{Binding ...ImageSize}" /&gt;
/// With a recycling list, the same Image is reused for other tiles; a load that finishes after its Image has moved on
/// is simply dropped.
/// </summary>
public static class ThumbnailImage
{
    /// <summary>The tile whose image to show. Bound to the tile itself, so a new tile always loads, even for the same file.</summary>
    public static readonly DependencyProperty TileProperty = DependencyProperty.RegisterAttached(
        "Tile", typeof(TileViewModel), typeof(ThumbnailImage), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.RegisterAttached(
        "Version", typeof(int), typeof(ThumbnailImage), new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty DecodeSizeProperty = DependencyProperty.RegisterAttached(
        "DecodeSize", typeof(double), typeof(ThumbnailImage), new PropertyMetadata(0.0, OnChanged));

    private static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(object), typeof(ThumbnailImage), new PropertyMetadata(null));

    public static TileViewModel? GetTile(DependencyObject d) => (TileViewModel?)d.GetValue(TileProperty);
    public static void SetTile(DependencyObject d, TileViewModel? value) => d.SetValue(TileProperty, value);
    public static int GetVersion(DependencyObject d) => (int)d.GetValue(VersionProperty);
    public static void SetVersion(DependencyObject d, int value) => d.SetValue(VersionProperty, value);
    public static double GetDecodeSize(DependencyObject d) => (double)d.GetValue(DecodeSizeProperty);
    public static void SetDecodeSize(DependencyObject d, double value) => d.SetValue(DecodeSizeProperty, value);

    private sealed record Key(string Path, int Version, int Pixels);

    private static readonly SemaphoreSlim Decoders = new(4);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, ImageSource Image)>> CacheIndex = [];
    private static readonly LinkedList<(Key Key, ImageSource Image)> CacheOrder = new();
    private const int CacheSize = 800;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        string? path = GetTile(image)?.ImagePath;
        double size = GetDecodeSize(image);
        if (string.IsNullOrEmpty(path) || size <= 0)
        {
            image.Source = null;
            return;
        }

        // Decode at the size on screen, allowing for display scaling, in steps of 64 px so the cache is reused.
        double scale = VisualTreeHelper.GetDpi(image).DpiScaleX;
        int pixels = Math.Clamp((int)Math.Ceiling(size * scale / 64) * 64, 64, 1024);
        // A redraw makes every cached copy of this file out of date (tiles made later start again at version 0).
        if (e.Property == VersionProperty) Forget(path);
        var key = new Key(path, GetVersion(image), pixels);
        var request = new object();   // this load, as opposed to any earlier one for the same Image
        image.SetValue(RequestProperty, request);

        if (TryCached(key, out var cached))
        {
            image.Source = cached;
            return;
        }
        // Keep the previous picture only for a redraw of the same tile; otherwise clear it at once.
        if (e.Property != VersionProperty) image.Source = null;
        _ = LoadAsync(image, key, request);
    }

    private static async Task LoadAsync(Image image, Key key, object request)
    {
        ImageSource? source;
        await Decoders.WaitAsync();
        try
        {
            if (!ReferenceEquals(image.GetValue(RequestProperty), request)) return;   // the tile scrolled away meanwhile
            source = await DevLog.Run("tile.decode", () => Decode(key));
        }
        finally
        {
            Decoders.Release();
        }
        if (source is not null) Remember(key, source);
        if (ReferenceEquals(image.GetValue(RequestProperty), request)) image.Source = source;
    }

    private static ImageSource? Decode(Key key)
    {
        try
        {
            if (!File.Exists(key.Path)) return null;
            // Read the bytes first: the file can be replaced by a new drawing at any moment, and must not stay locked.
            byte[] bytes = DevLog.ReadAllBytes(key.Path, "tile image");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = key.Pixels;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;   // not there yet, or half-written: the placeholder stays
        }
    }

    private static bool TryCached(Key key, out ImageSource? image)
    {
        lock (CacheLock)
        {
            if (CacheIndex.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    private static void Forget(string path)
    {
        lock (CacheLock)
        {
            foreach (var key in CacheIndex.Keys.Where(k => k.Path == path).ToList())
            {
                CacheOrder.Remove(CacheIndex[key]);
                CacheIndex.Remove(key);
            }
        }
    }

    private static void Remember(Key key, ImageSource image)
    {
        lock (CacheLock)
        {
            if (CacheIndex.ContainsKey(key)) return;
            CacheIndex[key] = CacheOrder.AddFirst((key, image));
            while (CacheOrder.Count > CacheSize)
            {
                var last = CacheOrder.Last!;
                CacheOrder.RemoveLast();
                CacheIndex.Remove(last.Value.Key);
            }
        }
    }
}
