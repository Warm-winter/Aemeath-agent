using Avalonia.Media.Imaging;
using Aemeath.Core.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Aemeath.Desktop.Services;

internal sealed class AttachmentThumbnailCache : IDisposable
{
    internal const int MaxCacheEntries = 24;
    internal const int MaxThumbnailDimension = 512;

    private sealed record CacheEntry(byte[] PngBytes, long LastAccess);

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Bitmap> _renderedBitmaps = new();
    private long _accessClock;
    private bool _disposed;

    internal int CachedEntryCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<Bitmap?> GetAsync(ChatAttachment attachment, CancellationToken cancellationToken = default)
    {
        if (_disposed || attachment.Kind != ChatAttachmentKind.Image || !File.Exists(attachment.Path))
        {
            return null;
        }

        var key = BuildCacheKey(attachment.Path);
        byte[]? pngBytes;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                pngBytes = cached.PngBytes;
                _entries[key] = cached with { LastAccess = ++_accessClock };
            }
            else
            {
                pngBytes = null;
            }
        }

        if (pngBytes is null)
        {
            pngBytes = await Task.Run(() => DecodeThumbnail(attachment.Path), cancellationToken);
            if (pngBytes is null)
            {
                return null;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return null;
                }

                _entries[key] = new CacheEntry(pngBytes, ++_accessClock);
                while (_entries.Count > MaxCacheEntries)
                {
                    var oldest = _entries.MinBy(pair => pair.Value.LastAccess).Key;
                    _entries.Remove(oldest);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(pngBytes, writable: false);
        var bitmap = new Bitmap(stream);
        lock (_sync)
        {
            if (_disposed)
            {
                bitmap.Dispose();
                return null;
            }
            _renderedBitmaps.Add(bitmap);
        }
        return bitmap;
    }

    public void ReleaseRenderedBitmaps()
    {
        List<Bitmap> bitmaps;
        lock (_sync)
        {
            bitmaps = _renderedBitmaps.ToList();
            _renderedBitmaps.Clear();
        }

        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _entries.Clear();
        }
        ReleaseRenderedBitmaps();
    }

    private static string BuildCacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static byte[]? DecodeThumbnail(string path)
    {
        try
        {
            // Preview decoding runs in the background and may overlap window shutdown or test cleanup.
            // Allow the source file to be deleted without waiting for ImageSharp to finish reading it.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var options = new DecoderOptions
            {
                MaxFrames = 1,
                SkipMetadata = true
            };
            using var image = ImageSharpImage.Load<Rgba32>(options, stream);
            image.Mutate(context => context.AutoOrient());

            var longestSide = Math.Max(image.Width, image.Height);
            if (longestSide > MaxThumbnailDimension)
            {
                var scale = MaxThumbnailDimension / (double)longestSide;
                image.Mutate(context => context.Resize(
                    Math.Max(1, (int)Math.Round(image.Width * scale)),
                    Math.Max(1, (int)Math.Round(image.Height * scale))));
            }

            using var output = new MemoryStream();
            image.SaveAsPng(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return null;
        }
    }
}
