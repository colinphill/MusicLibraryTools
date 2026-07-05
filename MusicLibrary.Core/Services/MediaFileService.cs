using System.Security.Cryptography;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IMediaFileService"/>
public sealed class MediaFileService : IMediaFileService
{
    private readonly ILibraryService? _library;

    // The library is optional so this service can still be constructed standalone (e.g. unit tests),
    // in which case it always parses the file directly.
    public MediaFileService(ILibraryService? library = null) => _library = library;

    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".dsf", ".flac", ".ogg", ".m4a", ".mp4", ".m4p", ".m4r", ".wv",
    };

    public Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default)
        => LoadAsync(path, includeArtwork: true, ct);

    public async Task<OperationResult<MediaFileModel>> LoadAsync(string path, bool includeArtwork, CancellationToken ct = default)
    {
        // Prefer the cache (no file I/O — important over a NAS); fall back to parsing the file when it
        // isn't indexed (e.g. a file opened outside the library, or not yet re-indexed).
        if (_library is { IsReady: true })
        {
            try
            {
                var details = await _library.GetFileDetailsAsync(path, includeArtwork, ct);
                if (details is not null)
                    return OperationResult<MediaFileModel>.Ok(MapFromCache(path, details, includeArtwork));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to a direct parse if the cache read fails for any reason.
            }
        }

        return await Task.Run(() => Load(path, includeArtwork), ct);
    }

    private static MediaFileModel MapFromCache(string path, FileDetails d, bool includeArtwork)
    {
        var e = d.Entry;

        // Known fields come from the cache. A database indexed before the KnownMetadata table existed
        // won't have them yet, so fall back to synthesizing the standard fields from the structured
        // columns (a full re-index restores the complete set, including Genre/Composer/etc.).
        var knownFields = d.KnownFields
            .Select(kv => new TagFieldValue(ParseField(kv.Key), kv.Value))
            .Where(f => f.Field != TagFields.NullField)
            .ToList();
        if (knownFields.Count == 0)
            knownFields = SynthesizeStandardFields(e);

        return new MediaFileModel
        {
            Path = path,
            Title = e.Title,
            Artist = e.Artist,
            AlbumArtist = e.AlbumArtist,
            Album = e.Album,
            TrackNumber = e.TrackNumber,
            TrackTotal = e.TrackTotal,
            DiscNumber = e.DiscNumber,
            DiscTotal = e.DiscTotal,
            ReleaseDate = e.ReleaseDate,
            TagType = d.TagType,
            IsWritable = WritableExtensions.Contains(Path.GetExtension(path)),
            KnownFields = knownFields,
            TextFields = d.TextFields.Select(kv => new TextField(kv.Key, kv.Value)).ToList(),
            Artwork = includeArtwork
                ? d.Images.Select(i => new ArtworkModel
                {
                    Description = i.Description,
                    Category = i.Category,
                    ImageType = i.ImageType,
                    Width = i.Width,
                    Height = i.Height,
                    Size = i.Size,
                    Hash = i.Hash,
                    Data = i.Data,
                }).ToList()
                : [],
            Codec = new CodecModel
            {
                CodecName = e.CodecName,
                CodecType = e.CodecType,
                AverageBitrate = e.AverageBitRate,
                MaxBitrate = e.MaxBitRate,
                BitsPerSample = e.BitsPerSample,
                Samplerate = e.SampleRate,
                Channels = e.Channels,
                DurationInSeconds = (uint)e.DurationInSeconds,
            },
        };
    }

    private static TagFields ParseField(string name)
        => Enum.TryParse<TagFields>(name, out var field) ? field : TagFields.NullField;

    // Reconstruct the standard known fields from the structured cache columns, for databases that
    // predate the KnownMetadata table.
    private static List<TagFieldValue> SynthesizeStandardFields(MetadataCacheEntry e)
    {
        var fields = new List<TagFieldValue>();
        void Add(TagFields f, string? v) { if (!string.IsNullOrEmpty(v)) fields.Add(new TagFieldValue(f, v)); }

        Add(TagFields.Title, e.Title);
        Add(TagFields.Artist, e.Artist);
        Add(TagFields.AlbumArtist, e.AlbumArtist);
        Add(TagFields.Album, e.Album);
        Add(TagFields.TrackNumber, e.TrackNumber?.ToString());
        Add(TagFields.TotalTracks, e.TrackTotal?.ToString());
        Add(TagFields.DiscNumber, e.DiscNumber?.ToString());
        Add(TagFields.TotalDiscs, e.DiscTotal?.ToString());
        Add(TagFields.Date, e.ReleaseDate);
        return fields;
    }

    private static OperationResult<MediaFileModel> Load(string path, bool includeArtwork)
    {
        try
        {
            // Pass a hash so embedded artwork gets hashed during the single parse pass.
            using var sha = SHA256.Create();
            var file = MediaFile.GetFile(path, includeArtwork ? sha : null);
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
                Artwork = tag is null || !includeArtwork ? [] : ProjectArtwork(tag),
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
