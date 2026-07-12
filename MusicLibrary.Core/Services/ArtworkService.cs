using MusicFileUtilities;
using MusicLibrary.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IArtworkService"/>
public sealed class ArtworkService : IArtworkService
{
    private readonly IReindexService? _reindex;
    private readonly IFileMutationCoordinator _mutations;

    // The reindex service is optional so this service can be constructed standalone (unit tests).
    public ArtworkService(
        IReindexService? reindex = null,
        IFileMutationCoordinator? mutations = null)
    {
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
    }

    // Every audio format the toolkit tags can carry embedded artwork, so writability is decided by
    // extension — no need to open/parse the file just to answer.
    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".dsf", ".flac", ".ogg", ".m4a", ".mp4", ".m4p", ".m4r", ".wv",
    };

    public bool SupportsWrite(string musicPath) => WritableExtensions.Contains(Path.GetExtension(musicPath));

    public async Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default)
    {
        (byte[] Jpeg, int Width, int Height) prepared;
        try
        {
            prepared = await Task.Run(() => Encode(File.ReadAllBytes(imagePath), maxDimension), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ArtworkOpResult.Fail(ex.Message); }

        using var mutation = await _mutations.AcquireAsync(musicPath, ct);
        var result = await Task.Run(() => ApplyCover(
            musicPath, prepared.Jpeg, prepared.Width, prepared.Height), ct);
        return await ReindexIfSaved(result, musicPath);
    }

    public async Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default)
    {
        using var mutation = await _mutations.AcquireAsync(musicPath, ct);
        var result = await Task.Run(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                var current = file.Tags.FirstOrDefault()?.GetImageMetadata().FirstOrDefault();
                if (current is null || current.Data.Length == 0)
                    return ArtworkOpResult.Fail("No embedded artwork to scrub.");

                var (jpeg, w, h) = Encode(current.Data, maxDimension, quality);
                return ApplyCover(musicPath, jpeg, w, h);
            }
            catch (Exception ex)
            {
                return ArtworkOpResult.Fail(ex.Message);
            }
        }, ct);
        return await ReindexIfSaved(result, musicPath);
    }

    public async Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default)
    {
        using var mutation = await _mutations.AcquireAsync(musicPath, ct);
        var result = await Task.Run(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                if (ResolveWriter(file) is not { } writer)
                    return ArtworkOpResult.Fail("Artwork writing is not supported for this format.");
                writer.RemoveImages();
                file.SaveTags();
                return new ArtworkOpResult { Success = true };
            }
            catch (Exception ex)
            {
                return ArtworkOpResult.Fail(ex.Message);
            }
        }, ct);
        return await ReindexIfSaved(result, musicPath);
    }

    public Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0, CancellationToken ct = default)
        => Task.Run<PreparedImage?>(() =>
        {
            try
            {
                var (jpeg, w, h) = Encode(File.ReadAllBytes(imagePath), maxDimension);
                return new PreparedImage(jpeg, "image/jpeg", w, h);
            }
            catch { return null; }
        }, ct);

    public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0, int quality = 90, CancellationToken ct = default)
        => Task.Run<PreparedImage?>(() =>
        {
            try
            {
                var (jpeg, w, h) = Encode(data, maxDimension, quality);
                return new PreparedImage(jpeg, "image/jpeg", w, h);
            }
            catch { return null; }
        }, ct);

    public async Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default)
    {
        using var mutation = await _mutations.AcquireAsync(musicPath, ct);
        var result = await Task.Run(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                if (ResolveWriter(file) is not { } writer)
                    return ArtworkOpResult.Fail("Artwork writing is not supported for this format.");

                // A null description means the caller did not edit that property. Preserve it when
                // the image bytes came from the existing tag; an explicit empty string still clears it.
                var existingImages = file.Tags.FirstOrDefault()?.GetImageMetadata().ToList() ?? [];
                var consumed = new bool[existingImages.Count];
                string PreservedDescription(ArtworkInput input)
                {
                    if (input.Description is not null)
                        return input.Description;
                    for (var index = 0; index < existingImages.Count; index++)
                    {
                        if (!consumed[index] && existingImages[index].Data.AsSpan().SequenceEqual(input.Data))
                        {
                            consumed[index] = true;
                            return existingImages[index].Description ?? "";
                        }
                    }
                    return "";
                }

                writer.SetImages(images
                    .Select(i => new ArtworkImage(i.Type, i.MimeType, PreservedDescription(i), i.Data))
                    .ToList());
                file.SaveTags();
                return new ArtworkOpResult { Success = true };
            }
            catch (Exception ex)
            {
                return ArtworkOpResult.Fail(ex.Message);
            }
        }, ct);
        return await ReindexIfSaved(result, musicPath);
    }

    // Keep the cache in sync with what we just wrote to disk.
    private async Task<ArtworkOpResult> ReindexIfSaved(ArtworkOpResult result, string musicPath)
    {
        if (result.Success && _reindex is not null)
        {
            try
            {
                await _reindex.ReindexFileAsync(musicPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return result with { CacheError = ex.Message };
            }
        }
        return result;
    }

    private static ArtworkOpResult ApplyCover(string musicPath, byte[] jpeg, int w, int h)
    {
        var file = MediaFile.GetFile(musicPath);
        if (ResolveWriter(file) is not { } writer)
            return ArtworkOpResult.Fail("Artwork writing is not supported for this format.");

        writer.SetFrontCover(jpeg, "image/jpeg");
        file.SaveTags();
        return new ArtworkOpResult { Success = true, Width = w, Height = h, Size = jpeg.Length };
    }

    // The writer may live on the IMediaFile (MP3/DSF via ID3v2Tag, FLAC/Ogg via VorbisComments,
    // MP4 directly) or on the tag object (WavPack's APETag).
    private static IArtworkWriter? ResolveWriter(IMediaFile file)
        => file as IArtworkWriter ?? file.Tags.FirstOrDefault() as IArtworkWriter;

    private static (byte[] Jpeg, int Width, int Height) Encode(byte[] source, int maxDimension, int quality = 90)
    {
        using var image = Image.Load(source);
        if (maxDimension > 0 && (image.Width > maxDimension || image.Height > maxDimension))
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxDimension, maxDimension),
            }));
        }

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return (ms.ToArray(), image.Width, image.Height);
    }
}
