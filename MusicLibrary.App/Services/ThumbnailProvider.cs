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

    /// <summary>Drop the cached thumbnail for a path so it is re-read next time (e.g. after an edit).</summary>
    void Invalidate(string path);
}

/// <summary>
/// Loads and caches artwork thumbnails. The image bytes come straight from the metadata cache
/// (<see cref="ILibraryService.GetFirstImageAsync"/>) — no file parsing — and are downscaled off the
/// UI thread via <see cref="IArtworkService.PrepareFromBytesAsync"/>. In-flight loads are de-duped so
/// a path is only decoded once even if several grid cells request it at the same time.
/// </summary>
public sealed class ThumbnailProvider : IThumbnailProvider
{
    private const int ThumbnailPixels = 64;

    private readonly ILibraryService _library;
    private readonly IArtworkService _artwork;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public ThumbnailProvider(ILibraryService library, IArtworkService artwork)
    {
        _library = library;
        _artwork = artwork;
    }

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

    public void Invalidate(string path) => _cache.Remove(path);

    private async Task<Bitmap?> LoadAsync(string path)
    {
        try
        {
            var raw = await _library.GetFirstImageAsync(path);
            Bitmap? bmp = null;
            if (raw is { Length: > 0 })
            {
                var prepared = await _artwork.PrepareFromBytesAsync(raw, ThumbnailPixels);
                if (prepared is not null)
                {
                    using var ms = new MemoryStream(prepared.Data);
                    bmp = new Bitmap(ms);
                }
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
