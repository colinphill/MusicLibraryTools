using MusicFileUtilities;
using MusicLibrary.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IArtworkService"/>
public sealed class ArtworkService : IArtworkService
{
    public bool SupportsWrite(string musicPath)
    {
        try
        {
            var file = MediaFile.GetFile(musicPath);
            return file is IArtworkWriter || file.Tags.FirstOrDefault() is IArtworkWriter;
        }
        catch
        {
            return false;
        }
    }

    public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                var source = File.ReadAllBytes(imagePath);
                var (jpeg, w, h) = Encode(source, maxDimension);
                return ApplyCover(musicPath, jpeg, w, h);
            }
            catch (Exception ex)
            {
                return ArtworkOpResult.Fail(ex.Message);
            }
        }, ct);

    public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default)
        => Task.Run(() =>
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

    public Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default)
        => Task.Run(() =>
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

    public Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                if (ResolveWriter(file) is not { } writer)
                    return ArtworkOpResult.Fail("Artwork writing is not supported for this format.");

                writer.SetImages(images.Select(i => new ArtworkImage(i.Type, i.MimeType, "", i.Data)).ToList());
                file.SaveTags();
                return new ArtworkOpResult { Success = true };
            }
            catch (Exception ex)
            {
                return ArtworkOpResult.Fail(ex.Message);
            }
        }, ct);

    public Task<byte[]?> GetThumbnailJpegAsync(string musicPath, int maxDimension, CancellationToken ct = default)
        => Task.Run<byte[]?>(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                var current = file.Tags.FirstOrDefault()?.GetImageMetadata().FirstOrDefault();
                if (current is null || current.Data.Length == 0)
                    return null;

                var (jpeg, _, _) = Encode(current.Data, maxDimension);
                return jpeg;
            }
            catch
            {
                return null;
            }
        }, ct);

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
