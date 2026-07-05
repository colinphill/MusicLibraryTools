using Avalonia.Media.Imaging;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.Services;

/// <summary>Supplies small artwork thumbnails for file paths, cached, for the details grid.</summary>
public interface IThumbnailProvider
{
    /// <summary>
    /// Get the (cached) thumbnail for a file, or null if it has no readable artwork. Safe to call
    /// repeatedly for the same path — decode happens once. Must be called on the UI thread.
    /// </summary>
    Task<Bitmap?> GetAsync(string path);
}

/// <summary>
/// Loads and caches artwork thumbnails. The heavy decode/downscale runs off the UI thread in
/// <see cref="IArtworkService.GetThumbnailJpegAsync"/>; wrapping the resulting small JPEG in an
/// Avalonia <see cref="Bitmap"/> is cheap and done on the UI thread. In-flight loads are de-duped
/// so a path is only decoded once even if several grid cells request it at the same time.
/// </summary>
public sealed class ThumbnailProvider : IThumbnailProvider
{
    private const int ThumbnailPixels = 64;

    private readonly IArtworkService _artwork;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public ThumbnailProvider(IArtworkService artwork) => _artwork = artwork;

    public Task<Bitmap?> GetAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
            return Task.FromResult(cached);
        if (_inflight.TryGetValue(path, out var pending))
            return pending;

        var task = LoadAsync(path);
        _inflight[path] = task;
        return task;
    }

    private async Task<Bitmap?> LoadAsync(string path)
    {
        try
        {
            var bytes = await _artwork.GetThumbnailJpegAsync(path, ThumbnailPixels);
            Bitmap? bmp = null;
            if (bytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            _cache[path] = bmp;
            return bmp;
        }
        catch
        {
            _cache[path] = null;
            return null;
        }
        finally
        {
            _inflight.Remove(path);
        }
    }
}
