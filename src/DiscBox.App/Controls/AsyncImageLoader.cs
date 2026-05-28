using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace DiscBox.Controls;

public static class AsyncImageLoader
{
    private static readonly HttpClient _httpClient = new();
    private static readonly ConcurrentDictionary<string, Bitmap> _cache = new();

    public static readonly AttachedProperty<string?> SourceUrlProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("SourceUrl", typeof(AsyncImageLoader));

    public static string? GetSourceUrl(Image element) => element.GetValue(SourceUrlProperty);

    public static void SetSourceUrl(Image element, string? value) => element.SetValue(SourceUrlProperty, value);

    static AsyncImageLoader()
    {
        SourceUrlProperty.Changed.AddClassHandler<Image>(OnSourceUrlChanged);
    }

    private static void OnSourceUrlChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        var url = e.NewValue as string;

        if (string.IsNullOrWhiteSpace(url))
        {
            image.Source = null;
            return;
        }

        if (_cache.TryGetValue(url, out var cachedBitmap))
        {
            image.Source = cachedBitmap;
            return;
        }

        // Temporarily clear or set placeholder
        image.Source = null;

        // Load asynchronously
        _ = LoadImageAsync(image, url);
    }

    private static async Task LoadImageAsync(Image image, string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            
            // Need to decode on a background thread but create Bitmap? 
            // In Avalonia, Bitmap creation might need to happen on UI thread or might be thread-safe depending on version.
            // Using MemoryStream ensures we have the full data before decoding.
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = new Bitmap(ms);

            _cache[url] = bitmap;

            // Apply only if the URL hasn't changed while downloading
            Dispatcher.UIThread.Post(() =>
            {
                if (GetSourceUrl(image) == url)
                {
                    image.Source = bitmap;
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load image from {url}: {ex.Message}");
        }
    }
}
