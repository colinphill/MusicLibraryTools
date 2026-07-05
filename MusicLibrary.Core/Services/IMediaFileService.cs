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
}
