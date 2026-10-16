using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace KsttkDl.App.Controls;

/// <summary> <c>ImageLoader.Path</c> on an Image decodes the file off the ui thread, scaled to <c>DecodeWidth</c>, so a gallery of a few hundred cards neither stalls scrolling nor holds full-size bitmaps </summary>
public static class ImageLoader
{
    private const int CacheLimit = 600;

    private static readonly ConcurrentDictionary<(string, int), BitmapSource> Cache = new();
    private static readonly ConcurrentQueue<(string, int)> CacheOrder = new();

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(0, OnChanged));

    public static string? GetPath(DependencyObject o) => (string?)o.GetValue(PathProperty);

    public static void SetPath(DependencyObject o, string? value) => o.SetValue(PathProperty, value);

    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject o, int value) => o.SetValue(DecodeWidthProperty, value);

    private static async void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Image image)
        {
            return;
        }

        var path = GetPath(image);
        var width = GetDecodeWidth(image);
        if (string.IsNullOrEmpty(path))
        {
            image.Source = null;
            return;
        }

        if (Cache.TryGetValue((path, width), out var cached))
        {
            image.Source = cached;
            return;
        }

        image.Source = null;
        var bitmap = await Task.Run(() => Decode(path, width));

        // the image may have been recycled for another file while this one decoded
        if (bitmap is not null && GetPath(image) == path)
        {
            image.Source = bitmap;
        }
    }

    public static BitmapSource? Decode(string path, int width)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path);
            if (width > 0)
            {
                bitmap.DecodePixelWidth = width;
            }

            bitmap.EndInit();
            bitmap.Freeze();

            if (width > 0 && Cache.TryAdd((path, width), bitmap))
            {
                CacheOrder.Enqueue((path, width));
                while (CacheOrder.Count > CacheLimit && CacheOrder.TryDequeue(out var old))
                {
                    Cache.TryRemove(old, out _);
                }
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>drops cached thumbnails of a file that was just rewritten.</summary>
    public static void Forget(string path)
    {
        foreach (var key in Cache.Keys.Where(k => k.Item1 == path))
        {
            Cache.TryRemove(key, out _);
        }
    }
}
