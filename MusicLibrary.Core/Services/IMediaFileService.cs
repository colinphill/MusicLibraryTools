using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Loads a single music file's metadata into an immutable <see cref="MediaFileModel"/>.
/// All parsing happens on a background thread (the underlying library is synchronous and not
/// thread-safe), so ViewModels can await without blocking the UI.
/// </summary>
public interface IMediaFileService
{
    Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Load a file's metadata, optionally skipping embedded artwork. Artwork is the heaviest part to
    /// read, so callers that only need the text fields (e.g. the batch tag editor) pass false.
    /// </summary>
    Task<OperationResult<MediaFileModel>> LoadAsync(string path, bool includeArtwork, CancellationToken ct = default);

    /// <summary>
    /// Parse a file directly instead of using cached normalized metadata. This is intended for
    /// editors that must expose format-native user strings that are not stored in the library cache.
    /// Implementations without a cache may use the normal load path.
    /// </summary>
    Task<OperationResult<MediaFileModel>> LoadDirectAsync(
        string path,
        bool includeArtwork,
        CancellationToken ct = default) => LoadAsync(path, includeArtwork, ct);
}
