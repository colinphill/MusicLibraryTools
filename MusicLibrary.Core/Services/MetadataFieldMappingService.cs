using System.Collections.Immutable;
using System.Text.Json;
using MusicFileUtilities;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Overrides the native text key used for a canonical field in one media-format family.
/// These are application preferences, not portable library policy.
/// </summary>
public sealed record MetadataFieldMapping(
    MediaFormatFamily Format,
    TagFields Field,
    string NativeFieldName);

public interface IMetadataFieldMappingService
{
    IReadOnlyList<MetadataFieldMapping> Load();
    void Save(IReadOnlyList<MetadataFieldMapping> mappings);
    bool TryGet(string path, TagFields field, out string nativeFieldName);
    IReadOnlyList<MetadataFieldMapping> GetForPath(string path);
    IMediaFile ProjectForCache(string path, IMediaFile file);
}

public sealed class MetadataFieldMappingService(
    IAppSettings settings,
    IMediaFormatRegistry formats) : IMetadataFieldMappingService
{
    internal const string PreferenceKey = "manager.metadata-field-mappings.v1";
    private const int MaximumMappings = 256;
    private readonly object _sync = new();
    private ImmutableArray<MetadataFieldMapping>? _cached;

    public IReadOnlyList<MetadataFieldMapping> Load()
    {
        lock (_sync)
            return GetMappings().ToArray();
    }

    public void Save(IReadOnlyList<MetadataFieldMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count > MaximumMappings)
            throw new ArgumentOutOfRangeException(
                nameof(mappings),
                $"At most {MaximumMappings} field mappings can be saved.");

        MetadataFieldMapping[] normalized = mappings
            .Select(Normalize)
            .ToArray();
        if (normalized
            .Select(mapping => (mapping.Format, mapping.Field))
            .Distinct()
            .Count() != normalized.Length)
            throw new ArgumentException(
                "A canonical field can have only one native name per format.",
                nameof(mappings));

        lock (_sync)
        {
            _cached = [.. normalized
                .OrderBy(mapping => mapping.Format)
                .ThenBy(mapping => mapping.Field)];
            settings.SetPreference(
                PreferenceKey,
                JsonSerializer.Serialize(new StoredMappings(1, _cached.Value)));
        }
    }

    public bool TryGet(
        string path,
        TagFields field,
        out string nativeFieldName)
    {
        nativeFieldName = "";
        if (!formats.TryGetForPath(path, out MediaFormatDefinition? format))
            return false;
        MetadataFieldMapping? mapping = GetMappings().FirstOrDefault(item =>
            item.Format == format.Family && item.Field == field);
        if (mapping is null)
            return false;
        nativeFieldName = mapping.NativeFieldName;
        return true;
    }

    public IReadOnlyList<MetadataFieldMapping> GetForPath(string path)
    {
        if (!formats.TryGetForPath(path, out MediaFormatDefinition? format))
            return [];
        return GetMappings()
            .Where(mapping => mapping.Format == format.Family)
            .ToArray();
    }

    public IMediaFile ProjectForCache(string path, IMediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        IReadOnlyList<MetadataFieldMapping> mappings = GetForPath(path);
        if (mappings.Count == 0)
            return file;
        IMetadataProvider[] tags = file.Tags
            .Select(tag => MappedMetadataProvider.Create(tag, mappings))
            .ToArray();
        return tags.Any(tag => tag is MappedMetadataProvider)
            ? new MappedMediaFile(file, tags)
            : file;
    }

    private ImmutableArray<MetadataFieldMapping> GetMappings()
    {
        lock (_sync)
        {
            if (_cached is { } cached)
                return cached;
            try
            {
                string? json = settings.GetPreference(PreferenceKey);
                StoredMappings? stored = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<StoredMappings>(json);
                _cached = stored?.Version == 1
                    ? [.. stored.Mappings
                        .Select(Normalize)
                        .DistinctBy(mapping => (mapping.Format, mapping.Field))
                        .Take(MaximumMappings)]
                    : [];
            }
            catch
            {
                _cached = [];
            }
            return _cached.Value;
        }
    }

    private static MetadataFieldMapping Normalize(
        MetadataFieldMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (!Enum.IsDefined(mapping.Format))
            throw new ArgumentOutOfRangeException(
                nameof(mapping), "The media format is not supported.");
        if (mapping.Field == TagFields.NullField ||
            !Enum.IsDefined(mapping.Field))
            throw new ArgumentOutOfRangeException(
                nameof(mapping), "A canonical metadata field is required.");
        string nativeName = mapping.NativeFieldName?.Trim() ?? "";
        if (nativeName.Length is 0 or > 128 ||
            nativeName.Any(char.IsControl) ||
            nativeName.Contains('='))
            throw new ArgumentException(
                "Native field names must be 1-128 printable characters and cannot contain '='.",
                nameof(mapping));
        return mapping with { NativeFieldName = nativeName };
    }

    private sealed record StoredMappings(
        int Version,
        ImmutableArray<MetadataFieldMapping> Mappings);

    private sealed class MappedMediaFile(
        IMediaFile source,
        IReadOnlyList<IMetadataProvider> tags) : IMediaFile
    {
        public IEnumerable<ICodecProvider> Codecs => source.Codecs;
        public IEnumerable<IMetadataProvider> Tags => tags;
        public void SaveTags(string outputPath) =>
            source.SaveTags(outputPath);
    }

    private sealed class MappedMetadataProvider :
        IMetadataProvider,
        IUserStringMetadata
    {
        private readonly IMetadataProvider _source;
        private readonly IUserStringMetadata _custom;
        private readonly KeyValuePair<TagFields, string>[] _known;
        private readonly KeyValuePair<string, string>[] _userStrings;

        private MappedMetadataProvider(
            IMetadataProvider source,
            IUserStringMetadata custom,
            IReadOnlyList<MetadataFieldMapping> mappings)
        {
            _source = source;
            _custom = custom;
            KeyValuePair<string, string>[] userStrings =
                custom.GetUserStrings().ToArray();
            var known = source.GetKnownMetadata().ToList();
            var mappedNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (MetadataFieldMapping mapping in mappings)
            {
                string[] values = userStrings
                    .Where(item => string.Equals(
                        item.Key,
                        mapping.NativeFieldName,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Value)
                    .ToArray();
                if (values.Length == 0)
                    continue;
                known.RemoveAll(item => item.Key == mapping.Field);
                known.AddRange(values.Select(value =>
                    KeyValuePair.Create(mapping.Field, value)));
                mappedNames.Add(mapping.NativeFieldName);
            }
            _known = known.ToArray();
            _userStrings = userStrings
                .Where(item => !mappedNames.Contains(item.Key))
                .ToArray();
        }

        public static IMetadataProvider Create(
            IMetadataProvider source,
            IReadOnlyList<MetadataFieldMapping> mappings) =>
            source is IUserStringMetadata custom &&
            mappings.Any(mapping => custom.GetUserStrings().Any(item =>
                string.Equals(
                    item.Key,
                    mapping.NativeFieldName,
                    StringComparison.OrdinalIgnoreCase)))
                ? new MappedMetadataProvider(source, custom, mappings)
                : source;

        public string Title => Value(TagFields.Title) ?? _source.Title;
        public string Artist => Value(TagFields.Artist) ?? _source.Artist;
        public string AlbumArtist =>
            Value(TagFields.AlbumArtist) ?? _source.AlbumArtist;
        public string Album => Value(TagFields.Album) ?? _source.Album;
        public int? TrackNumber => IntValue(TagFields.TrackNumber) ??
                                   _source.TrackNumber;
        public string TagType => _source.TagType;
        public string ReleaseDate => Value(TagFields.Date) ??
                                     _source.ReleaseDate;
        public int? TrackTotal => IntValue(TagFields.TotalTracks) ??
                                  _source.TrackTotal;
        public int? DiscNumber => IntValue(TagFields.DiscNumber) ??
                                  _source.DiscNumber;
        public int? DiscTotal => IntValue(TagFields.TotalDiscs) ??
                                 _source.DiscTotal;
        public bool HasAlbumArtist =>
            !string.IsNullOrWhiteSpace(AlbumArtist);

        public IEnumerable<KeyValuePair<TagFields, string>>
            GetKnownMetadata() => _known;

        public IEnumerable<KeyValuePair<string, string>>
            GetTextMetadata() => _source.GetTextMetadata();

        public IEnumerable<IMetadataImage> GetImageMetadata() =>
            _source.GetImageMetadata();

        public IEnumerable<KeyValuePair<string, string>>
            GetUserStrings() => _userStrings;

        public void SetUserString(string key, string value) =>
            _custom.SetUserString(key, value);

        public void RemoveUserString(string key) =>
            _custom.RemoveUserString(key);

        private string? Value(TagFields field) => _known
            .FirstOrDefault(item => item.Key == field)
            .Value;

        private int? IntValue(TagFields field) =>
            int.TryParse(Value(field), out int value) ? value : null;
    }
}
