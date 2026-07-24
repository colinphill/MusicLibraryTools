using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Cross-platform embedded-artwork operations via SkiaSharp: replace the cover from an image file,
/// scrub (re-encode/downscale) the existing cover, or remove it. Writing uses the uniform
/// IArtworkWriter in MusicFileUtilities (MP3/DSF/FLAC/Ogg/MP4/WavPack).
/// </summary>
public interface IArtworkService
{
    /// <summary>Whether embedded artwork can be written for this file's format.</summary>
    bool SupportsWrite(string musicPath);

    /// <summary>Replace the front cover with an image file, optionally downscaled to maxDimension px.</summary>
    Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default);

    /// <summary>Re-encode the existing cover to JPEG, downscaled to maxDimension px (0 = no resize).</summary>
    Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default);

    /// <summary>Remove all embedded images.</summary>
    Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default);

    /// <summary>Encode an image file to JPEG (optionally downscaled) ready to embed.</summary>
    Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0, CancellationToken ct = default);

    /// <summary>Re-encode raw image bytes to JPEG (optionally downscaled) — used to scrub a cover.</summary>
    Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0, int quality = 90, CancellationToken ct = default);

    /// <summary>Replace the entire embedded-image set (each image carrying its picture type).</summary>
    Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default);
}
