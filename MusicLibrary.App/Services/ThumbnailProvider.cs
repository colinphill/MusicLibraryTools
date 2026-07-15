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
    private const int MaxCachedThumbnails = 512;

    private readonly ILibraryService _library;
    private readonly IArtworkService _artwork;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<byte[]?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _rawBatchGate = new();
    private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pendingRaw = new(StringComparer.OrdinalIgnoreCase);
    private bool _rawBatchScheduled;

    public ThumbnailProvider(ILibraryService library, IArtworkService artwork)
    {
        _library = library;
        _artwork = artwork;
    }

    public async Task<Bitmap?> GetAsync(string path)
    {
        var data = await GetDataAsync(path);
        if (data is not { Length: > 0 })
            return null;

        using var ms = new MemoryStream(data, writable: false);
        return new Bitmap(ms);
    }

    public void Invalidate(string path)
    {
        lock (_gate)
        {
            _versions[path] = GetVersionLocked(path) + 1;
            RemoveCachedLocked(path);
            // Do not reuse an in-flight load that captured the old image bytes. It will notice the
            // version mismatch when it completes and will not repopulate the cache.
            _inflight.Remove(path);
        }
    }

    private async Task<byte[]?> GetDataAsync(string path)
    {
        Task<byte[]?> task;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                TouchLocked(path);
                return cached;
            }
            if (!_inflight.TryGetValue(path, out task!))
            {
                task = LoadDataAsync(path, GetVersionLocked(path));
                _inflight[path] = task;
            }
        }

        try
        {
            return await task;
        }
        finally
        {
            lock (_gate)
            {
                if (_inflight.TryGetValue(path, out var current) && ReferenceEquals(current, task))
                    _inflight.Remove(path);
            }
        }
    }

    private async Task<byte[]?> LoadDataAsync(string path, int version)
    {
        try
        {
            var raw = await GetRawDataAsync(path);
            byte[]? data = null;
            if (raw is { Length: > 0 })
            {
                var prepared = await _artwork.PrepareFromBytesAsync(raw, ThumbnailPixels);
                if (prepared is not null)
                    data = prepared.Data;
            }

            lock (_gate)
            {
                if (GetVersionLocked(path) != version)
                    return null;
                AddCachedLocked(path, data);
            }
            return data;
        }
        catch
        {
            lock (_gate)
                if (GetVersionLocked(path) == version)
                    AddCachedLocked(path, null);
            return null;
        }
    }

    private Task<byte[]?> GetRawDataAsync(string path)
    {
        lock (_rawBatchGate)
        {
            if (_pendingRaw.TryGetValue(path, out var existing))
                return existing.Task;

            var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRaw[path] = pending;
            if (!_rawBatchScheduled)
            {
                _rawBatchScheduled = true;
                _ = FlushRawBatchesAsync();
            }
            return pending.Task;
        }
    }

    private async Task FlushRawBatchesAsync()
    {
        // Let all cells created in the current UI turn join one database request.
        await Task.Yield();
        while (true)
        {
            KeyValuePair<string, TaskCompletionSource<byte[]?>>[] batch;
            lock (_rawBatchGate)
            {
                batch = _pendingRaw.Take(64).ToArray();
                foreach (var item in batch)
                    _pendingRaw.Remove(item.Key);
                if (batch.Length == 0)
                {
                    _rawBatchScheduled = false;
                    return;
                }
            }

            try
            {
                var paths = batch.Select(item => item.Key).ToArray();
                var images = await _library.GetFirstImagesAsync(paths);
                for (int i = 0; i < batch.Length; i++)
                    batch[i].Value.TrySetResult(i < images.Count ? images[i] : null);
            }
            catch (Exception ex)
            {
                foreach (var item in batch)
                    item.Value.TrySetException(ex);
            }
        }
    }

    private int GetVersionLocked(string path) => _versions.GetValueOrDefault(path);

    private void AddCachedLocked(string path, byte[]? data)
    {
        _cache[path] = data;
        TouchLocked(path);
        while (_cache.Count > MaxCachedThumbnails && _lru.First is { } oldest)
            RemoveCachedLocked(oldest.Value);
    }

    private void TouchLocked(string path)
    {
        if (_lruNodes.Remove(path, out var existing))
            _lru.Remove(existing);
        var node = _lru.AddLast(path);
        _lruNodes[path] = node;
    }

    private void RemoveCachedLocked(string path)
    {
        _cache.Remove(path);
        if (_lruNodes.Remove(path, out var node))
            _lru.Remove(node);
    }
}
