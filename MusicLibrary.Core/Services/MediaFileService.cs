using System.Security.Cryptography;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IMediaFileService"/>
public sealed class MediaFileService : IMediaFileService
{
    public Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default)
        => Task.Run(() => Load(path), ct);

    private static OperationResult<MediaFileModel> Load(string path)
    {
        try
        {
            // Pass a hash so embedded artwork gets hashed during the single parse pass.
            using var sha = SHA256.Create();
            var file = MediaFile.GetFile(path, sha);
            var tag = file.Tags.FirstOrDefault();
            var codec = file.Codecs.FirstOrDefault();

            var model = new MediaFileModel
            {
                Path = path,
                Title = tag?.Title,
                Artist = tag?.Artist,
                AlbumArtist = tag?.AlbumArtist,
                Album = tag?.Album,
                TrackNumber = tag?.TrackNumber,
                TrackTotal = tag?.TrackTotal,
                DiscNumber = tag?.DiscNumber,
                DiscTotal = tag?.DiscTotal,
                ReleaseDate = tag?.ReleaseDate,
                TagType = tag?.TagType,
                IsWritable = tag is IMetadataWriter,
                KnownFields = tag is null
                    ? []
                    : tag.GetKnownMetadata().Select(kv => new TagFieldValue(kv.Key, kv.Value)).ToList(),
                TextFields = tag is null
                    ? []
                    : tag.GetTextMetadata().Select(kv => new TextField(kv.Key, kv.Value)).ToList(),
                Artwork = tag is null ? [] : ProjectArtwork(tag),
                Codec = codec is null ? null : new CodecModel
                {
                    CodecName = codec.CodecName,
                    CodecType = codec.CodecType,
                    AverageBitrate = codec.AverageBitrate,
                    MaxBitrate = codec.MaxBitrate,
                    BitsPerSample = codec.BitsPerSample,
                    Samplerate = codec.Samplerate,
                    Channels = codec.Channels,
                    DurationInSeconds = codec.DurationInSeconds,
                },
            };

            return OperationResult<MediaFileModel>.Ok(model);
        }
        catch (Exception ex)
        {
            return OperationResult<MediaFileModel>.Fail(ex.Message);
        }
    }

    private static List<ArtworkModel> ProjectArtwork(IMetadataProvider tag)
    {
        var list = new List<ArtworkModel>();
        foreach (var img in tag.GetImageMetadata())
        {
            list.Add(new ArtworkModel
            {
                Description = img.Description,
                Category = img.Category,
                ImageType = img.ImageType,
                Width = img.Width,
                Height = img.Height,
                Size = img.Size,
                Hash = img.Hash,
                Data = img.Data,
            });
        }
        return list;
    }
}
