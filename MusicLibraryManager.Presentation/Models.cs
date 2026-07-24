using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public sealed record LibraryColumnState(string Key, double? Width, int DisplayIndex, bool Visible);

public sealed record LibrarySortState(string Key, bool Descending);

public enum LibraryPageState
{
    NoConfiguration,
    Loading,
    Ready,
    NotIndexed,
    NoResults,
    FilteredToZero,
    Error,
}

public enum FieldValueVerification
{
    Exact,
    Unverified,
}

public enum MessageTone
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LibraryViewDefinition(
    string Name,
    string? Filter,
    FilterMode FilterMode,
    IReadOnlyList<LibraryColumnState> Columns,
    LibrarySortState? Sort,
    LibraryVisualFilterNode? VisualFilter = null);

public partial class LibraryColumnChoice(string key, string header, bool isVisible) : ObservableObject
{
    public string Key { get; } = key;
    public string Header { get; } = header;

    [ObservableProperty]
    private bool _isVisible = isVisible;
}

public sealed record SelectionContext(
    IReadOnlyList<string> Paths,
    IReadOnlyList<TrackRecord>? Records = null)
{
    public static SelectionContext Empty { get; } = new([]);
    public bool HasSelection => Paths.Count > 0;
    public string Summary => Paths.Count switch
    {
        0 => "Nothing selected",
        1 => Path.GetFileName(Paths[0]),
        _ => $"{Paths.Count:N0} tracks selected",
    };
}

public partial class LibraryRow : ObservableObject
{
    public LibraryRow(TrackRecord record)
    {
        // Older caches can contain the FLAC tag implementation name (Vorbis) in the codec field.
        // The container extension is authoritative here: Vorbis comments are the tag format,
        // while the audio/file type remains FLAC.
        bool flacWithTagNameAsCodec =
            System.IO.Path.GetExtension(record.Path).Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(record.CodecName) ||
             record.CodecName.StartsWith("Vorbis", StringComparison.OrdinalIgnoreCase));
        Record = flacWithTagNameAsCodec
            ? record with { CodecName = "FLAC" }
            : record;
        MetadataValues = BuildMetadataValues(Record.Metadata);
        Details = new DetailsRow(Record);
        Details.RebuildSearchText(DetailsColumns.All.Select(column => column.Key).ToArray());
    }

    public TrackRecord Record { get; }
    public IReadOnlyDictionary<string, string> MetadataValues { get; }
    public DetailsRow Details { get; }
    public string Path => Record.Path;
    public string Title => Record.Title ?? System.IO.Path.GetFileNameWithoutExtension(Record.Path);
    public string Artist => Record.Artist ?? "";
    public string AlbumArtist => Record.AlbumArtist ?? "";
    public string Album => Record.Album ?? "";
    public string Genre => Record.Genre ?? "";
    public string Composer => Record.Composer ?? "";
    public string Grouping => Record.Grouping ?? "";
    public string Year => Record.Year?.ToString() ?? "";
    public int? Track => Record.TrackNumber;
    public int? TrackTotal => Record.TrackTotal;
    public int? Disc => Record.DiscNumber;
    public int? DiscTotal => Record.DiscTotal;
    public string Codec => Record.CodecName ?? "";
    public string TagType => Record.TagType ?? "";
    public string CodecType => Record.CodecType.ToString();
    public string SampleRate => Details["SampleRate"];
    public string BitsPerSample => Details["Bits"];
    public string Bitrate => Details["Bitrate"];
    public string Channels => Details["Channels"];
    public string Duration => Details["Duration"];
    public string FileSize => Details["FileSize"];
    public string Modified => Details["Modified"];
    public string SearchText => Details.SearchText;

    [ObservableProperty]
    private object? _thumbnailSource;

    [ObservableProperty]
    private bool _thumbnailLoaded;

    private static IReadOnlyDictionary<string, string>
        BuildMetadataValues(
            IReadOnlyDictionary<string, string[]> metadata)
    {
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string[] fieldValues) in metadata)
        {
            MetadataFieldKey? field = null;
            if (CachedMetadataKeys.TryGetCustomName(
                    key,
                    out string customName))
                field = MetadataFieldKey.Custom(customName);
            else if (Enum.TryParse(
                         key,
                         ignoreCase: true,
                         out TagFields known) &&
                     known != TagFields.NullField)
                field = MetadataFieldKey.Known(known);
            if (field is not null)
                values[MetadataGridValueKey.For(field)] =
                    string.Join("; ", fieldValues);
        }
        return values;
    }
}

public partial class EditableTagField(TagFields field, string label) : ObservableObject
{
    public TagFields Field { get; } = field;
    public string Label { get; } = label;

    [ObservableProperty]
    private string? _value;

    [ObservableProperty]
    private bool _isMixed;

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnverified))]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    [NotifyPropertyChangedFor(nameof(VerificationMessage))]
    private FieldValueVerification _verification = FieldValueVerification.Exact;

    public bool IsUnverified => Verification == FieldValueVerification.Unverified;
    public string PlaceholderText => IsUnverified
        ? "Not verified across the full selection - type to replace"
        : IsMixed
            ? "Mixed values - type to replace"
            : "No value";
    public string? VerificationMessage => IsUnverified
        ? "The cache does not contain this field for every selected track. Its current value is not shown; typing a value will intentionally replace it on the full selection."
        : null;

    public void SetLoaded(string? value, bool mixed)
        => SetLoaded(string.IsNullOrEmpty(value) ? [] : [value], mixed, FieldValueVerification.Exact);

    public void SetLoaded(
        IReadOnlyList<string> values,
        bool mixed,
        FieldValueVerification verification = FieldValueVerification.Exact)
    {
        string[] distinctValues = values
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Verification = verification;
        Value = verification == FieldValueVerification.Exact && !mixed && distinctValues.Length == 1
            ? distinctValues[0]
            : null;
        IsMixed = mixed || verification == FieldValueVerification.Unverified;
        IsModified = false;
        OnPropertyChanged(nameof(PlaceholderText));
    }

    partial void OnIsMixedChanged(bool value) => OnPropertyChanged(nameof(PlaceholderText));

    partial void OnValueChanged(string? value)
    {
        if (!IsMixed || value is not null)
            IsModified = true;
    }
}

public partial class ArtworkPreviewItem : ObservableObject
{
    private string _technicalSummary;

    public ArtworkPreviewItem(
        object? source,
        ID3v2Util.APICType type,
        string mimeType,
        byte[] data,
        string technicalSummary,
        string? description)
    {
        Source = source;
        _type = type;
        MimeType = mimeType;
        Data = data;
        _technicalSummary = technicalSummary;
        _description = description;
    }

    public object? Source { get; private set; }
    public string MimeType { get; private set; }
    public byte[] Data { get; private set; }
    public string Label => FormatType(Type);
    public string Summary => string.IsNullOrWhiteSpace(Description)
        ? _technicalSummary
        : $"{Description} · {_technicalSummary}";
    public bool IsModified { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private ID3v2Util.APICType _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _description;

    partial void OnTypeChanged(ID3v2Util.APICType value) => MarkModified();
    partial void OnDescriptionChanged(string? value) => MarkModified();

    public void AcceptChanges()
    {
        if (!IsModified)
            return;
        IsModified = false;
        OnPropertyChanged(nameof(IsModified));
    }

    public void ReplaceContent(
        object? source,
        string mimeType,
        byte[] data,
        string technicalSummary)
    {
        Source = source;
        MimeType = mimeType;
        Data = data;
        _technicalSummary = technicalSummary;
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(MimeType));
        OnPropertyChanged(nameof(Data));
        OnPropertyChanged(nameof(Summary));
        MarkModified();
    }

    private void MarkModified()
    {
        if (IsModified)
            return;
        IsModified = true;
        OnPropertyChanged(nameof(IsModified));
    }

    private static string FormatType(ID3v2Util.APICType value)
    {
        string text = value.ToString();
        return string.Concat(text.Select((character, index) =>
            index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
                ? $" {char.ToLowerInvariant(character)}"
                : character.ToString()));
    }
}

public partial class IndexTargetEditorRow : ObservableObject
{
    private bool _refreshingProfileChoices;
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string? _profileId;

    [ObservableProperty]
    private SettingsProfileChoice? _profileChoice;

    public ObservableCollection<SettingsProfileChoice> ProfileChoices { get; } = [];

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private string? _indexFormats;

    [ObservableProperty]
    private string? _indexIncludePatterns;

    [ObservableProperty]
    private string? _indexExcludePatterns;

    private LibraryRootPermissions _permissions;

    public LibraryRootPermissions Permissions
    {
        get => _permissions;
        set
        {
            if (!SetProperty(ref _permissions, value))
                return;
            OnPropertyChanged(nameof(AllowMetadataWrites));
            OnPropertyChanged(nameof(AllowArtworkWrites));
            OnPropertyChanged(nameof(AllowOrganization));
            OnPropertyChanged(nameof(AllowIngestOutput));
            OnPropertyChanged(nameof(AllowSynchronizationOutput));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(PermissionSummary));
        }
    }

    public bool AllowMetadataWrites
    {
        get => HasPermission(LibraryRootPermissions.WriteMetadata);
        set => SetPermission(LibraryRootPermissions.WriteMetadata, value);
    }

    public bool AllowArtworkWrites
    {
        get => HasPermission(LibraryRootPermissions.WriteArtwork);
        set => SetPermission(LibraryRootPermissions.WriteArtwork, value);
    }

    public bool AllowOrganization
    {
        get => HasPermission(LibraryRootPermissions.OrganizeFiles);
        set => SetPermission(LibraryRootPermissions.OrganizeFiles, value);
    }

    public bool AllowIngestOutput
    {
        get => HasPermission(LibraryRootPermissions.IngestOutput);
        set => SetPermission(LibraryRootPermissions.IngestOutput, value);
    }

    public bool AllowSynchronizationOutput
    {
        get => HasPermission(LibraryRootPermissions.SynchronizeOutput);
        set => SetPermission(LibraryRootPermissions.SynchronizeOutput, value);
    }

    public bool IsReadOnly => Permissions == LibraryRootPermissions.None;

    public string PermissionSummary => IsReadOnly
        ? "Catalog-only: this root is read-only."
        : "Allowed changes: " + string.Join(", ", PermissionLabels());

    [ObservableProperty]
    private bool _isSyncTarget;

    public ObservableCollection<IndexTargetSetEditorRow> Memberships { get; } = [];

    public IndexTargetEntry? Source { get; set; }

    partial void OnIsSyncTargetChanged(bool value)
    {
        if (value)
            AllowSynchronizationOutput = true;
    }

    partial void OnProfileChoiceChanged(SettingsProfileChoice? value)
    {
        if (!_refreshingProfileChoices && value is not null)
            ProfileId = value.Id;
    }

    partial void OnProfileIdChanged(string? value)
    {
        if (_refreshingProfileChoices)
            return;
        _refreshingProfileChoices = true;
        try
        {
            ProfileChoice = ProfileChoices.FirstOrDefault(choice => string.Equals(
                choice.Id, value, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshingProfileChoices = false;
        }
    }

    public void RefreshProfileChoices(IEnumerable<LibraryProfile> profiles)
    {
        _refreshingProfileChoices = true;
        try
        {
            ProfileChoices.Clear();
            foreach (LibraryProfile profile in profiles)
                ProfileChoices.Add(new(profile.Id, profile.Name));
            ProfileChoice = ProfileChoices.FirstOrDefault(choice => string.Equals(
                choice.Id, ProfileId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshingProfileChoices = false;
        }
    }

    private bool HasPermission(LibraryRootPermissions permission) =>
        Permissions.HasFlag(permission);

    private void SetPermission(LibraryRootPermissions permission, bool enabled) =>
        Permissions = enabled ? Permissions | permission : Permissions & ~permission;

    private IEnumerable<string> PermissionLabels()
    {
        if (AllowMetadataWrites) yield return "metadata";
        if (AllowArtworkWrites) yield return "artwork";
        if (AllowOrganization) yield return "organization";
        if (AllowIngestOutput) yield return "ingest output";
        if (AllowSynchronizationOutput) yield return "sync output";
    }
}

public partial class IndexTargetSetEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _offset;
}

public partial class SyncPlaylistEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";
}

public partial class PlaylistTargetEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private string _type = "m3u";

    [ObservableProperty]
    private string? _sets;

    [ObservableProperty] private string _pathStyle = "legacy";
    [ObservableProperty] private string _encoding = "utf-8";
    [ObservableProperty] private bool _emitByteOrderMark = true;
    [ObservableProperty] private string _lineEnding = "platform";
    [ObservableProperty] private bool _includeExtendedInfo = true;
    [ObservableProperty] private string _fileNameTransform = "legacy";
    [ObservableProperty] private int _maxTrackCount = 500;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy =
        LibraryPathCollisionPolicy.Stop;

    public PlaylistTargetEntry? Source { get; set; }
}

public partial class PlaylistSourceEditorRow : ObservableObject
{
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _type = "m3u";
    [ObservableProperty] private bool _recursive;

    public PlaylistSourceEntry? Source { get; set; }
}

public partial class MetadataFieldMappingEditorRow : ObservableObject
{
    [ObservableProperty] private MediaFormatFamily _format;
    [ObservableProperty] private TagFields _field = TagFields.Title;
    [ObservableProperty] private string _nativeFieldName = "";

    public MetadataFieldMapping Build() =>
        new(Format, Field, NativeFieldName);

    public static MetadataFieldMappingEditorRow From(
        MetadataFieldMapping mapping) =>
        new()
        {
            Format = mapping.Format,
            Field = mapping.Field,
            NativeFieldName = mapping.NativeFieldName,
        };
}

public partial class ExportProfileEditorRow : ObservableObject
{
    public required LibraryExportProfile Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ExportSelectionKind _selectionKind;
    [ObservableProperty] private string? _selectionValues;
    [ObservableProperty] private string? _selectionQuery;
    [ObservableProperty] private ExportTransformMode _transformMode;
    [ObservableProperty] private string? _transformRecipeId;
    [ObservableProperty] private string? _transformProviderId;
    [ObservableProperty] private string? _codec;
    [ObservableProperty] private string? _container;
    [ObservableProperty] private string? _namingProfileId;
    [ObservableProperty] private bool _preserveSourceLayout;
    [ObservableProperty] private string? _folderTemplate;
    [ObservableProperty] private string? _fileNameTemplate;
    [ObservableProperty] private bool _useNamingProfileCollision = true;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    [ObservableProperty] private ExportArtworkMode _artworkMode;
    [ObservableProperty] private bool _frontCoverOnly;
    [ObservableProperty] private bool _preserveArtworkEncoding;
    [ObservableProperty] private int? _artworkMaximumDimension;
    [ObservableProperty] private int? _artworkMaximumBytes;
    [ObservableProperty] private bool _playlistEnabled;
    [ObservableProperty] private string _playlistFormat = "m3u8";
    [ObservableProperty] private bool _playlistRelativePaths = true;
    [ObservableProperty] private bool _playlistIncludeExtendedInfo = true;
    [ObservableProperty] private string _playlistEncoding = "utf-8";
    [ObservableProperty] private bool _playlistWriteBom;
    [ObservableProperty] private string _playlistLineEnding = "platform";
    [ObservableProperty] private int? _playlistMaximumTracks;
    [ObservableProperty] private string _transportProviderId = "local-filesystem";
    [ObservableProperty] private string _transportDestination = "";
    [ObservableProperty] private string? _transportOptions;
    [ObservableProperty] private ExportExtraFileDisposition _extraFileDisposition;
    [ObservableProperty] private bool _replaceChangedFiles;
    [ObservableProperty] private bool _removeEmptyDirectories;
    [ObservableProperty] private int? _maximumRemovals;

    public static ExportProfileEditorRow From(LibraryExportProfile profile) => new()
    {
        Source = profile,
        Id = profile.Id,
        Name = profile.Name,
        Enabled = profile.Enabled,
        SelectionKind = profile.Selection.Kind,
        SelectionValues = string.Join(", ", profile.Selection.Values),
        SelectionQuery = profile.Selection.Query,
        TransformMode = profile.Transform.Mode,
        TransformRecipeId = profile.Transform.RecipeId,
        TransformProviderId = profile.Transform.ProviderId,
        Codec = profile.Transform.Codec,
        Container = profile.Transform.Container,
        NamingProfileId = profile.Naming.LibraryProfileId,
        PreserveSourceLayout = profile.Naming.PreserveSourceLayout,
        FolderTemplate = profile.Naming.FolderTemplate,
        FileNameTemplate = profile.Naming.FileNameTemplate,
        UseNamingProfileCollision = profile.Naming.CollisionPolicy is null,
        CollisionPolicy = profile.Naming.CollisionPolicy ??
            LibraryPathCollisionPolicy.Stop,
        ArtworkMode = profile.Artwork.Mode,
        FrontCoverOnly = profile.Artwork.FrontCoverOnly,
        PreserveArtworkEncoding = profile.Artwork.PreserveEncoding,
        ArtworkMaximumDimension = profile.Artwork.MaximumDimension,
        ArtworkMaximumBytes = profile.Artwork.MaximumBytes,
        PlaylistEnabled = profile.Playlists.Enabled,
        PlaylistFormat = profile.Playlists.Format,
        PlaylistRelativePaths = profile.Playlists.RelativePaths,
        PlaylistIncludeExtendedInfo = profile.Playlists.IncludeExtendedInfo,
        PlaylistEncoding = profile.Playlists.EncodingName,
        PlaylistWriteBom = profile.Playlists.WriteByteOrderMark,
        PlaylistLineEnding = profile.Playlists.LineEnding,
        PlaylistMaximumTracks = profile.Playlists.MaximumTracks,
        TransportProviderId = profile.Transport.ProviderId,
        TransportDestination = profile.Transport.Destination,
        TransportOptions = string.Join("; ", profile.Transport.Options.Select(pair =>
            $"{pair.Key}={pair.Value}")),
        ExtraFileDisposition = profile.Reconciliation.ExtraFiles,
        ReplaceChangedFiles = profile.Reconciliation.ReplaceChangedFiles,
        RemoveEmptyDirectories = profile.Reconciliation.RemoveEmptyDirectories,
        MaximumRemovals = profile.Reconciliation.MaximumRemovals,
    };

    public static ExportProfileEditorRow Create() => From(new(
        "export-" + Guid.NewGuid().ToString("N")[..12],
        "New export",
        false,
        ExportSelectionPolicy.EntireLibrary,
        new(),
        new(PreserveSourceLayout: true),
        new(),
        new(),
        new("local-filesystem", ""),
        new()));

    public LibraryExportProfile Build() => Source with
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Enabled = Enabled,
        Selection = new(SelectionKind, SplitValues(SelectionValues), Clean(SelectionQuery)),
        Transform = new(TransformMode, Clean(TransformRecipeId),
            Clean(TransformProviderId), Clean(Codec), Clean(Container)),
        Naming = new(Clean(NamingProfileId), PreserveSourceLayout,
            Clean(FolderTemplate), Clean(FileNameTemplate),
            UseNamingProfileCollision ? null : CollisionPolicy),
        Artwork = new(ArtworkMode, FrontCoverOnly, PreserveArtworkEncoding,
            ArtworkMaximumDimension, ArtworkMaximumBytes),
        Playlists = new(PlaylistEnabled, PlaylistFormat.Trim(), PlaylistRelativePaths,
            PlaylistIncludeExtendedInfo, PlaylistEncoding.Trim(), PlaylistWriteBom,
            PlaylistLineEnding.Trim(), PlaylistMaximumTracks),
        Transport = new(TransportProviderId.Trim(), TransportDestination.Trim(),
            ParseOptions(TransportOptions)),
        Reconciliation = new(ExtraFileDisposition, ReplaceChangedFiles,
            RemoveEmptyDirectories, MaximumRemovals),
    };

    private static ImmutableArray<string> SplitValues(string? values) =>
        (values ?? "").Split([',', ';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static ImmutableDictionary<string, string> ParseOptions(string? options)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string item in (options ?? "").Split([';', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = item.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException(
                    $"Export transport option '{item}' must use name=value syntax.");
            result.Add(item[..separator].Trim(), item[(separator + 1)..].Trim());
        }
        return result.ToImmutable();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class HealthRuleEditorRow : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibraryHealthSeverity _severity;
    [ObservableProperty] private bool _proposeRepair;
    [ObservableProperty] private bool _applyRepair;

    public LibraryHealthRulePolicy Build() => new(
        Id, Enabled, Severity, ProposeRepair, ApplyRepair);
}

public sealed record SettingsRootChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record SettingsProfileChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class IngestRecipeEditorRow : ObservableObject
{
    public required LibraryIngestRecipe Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _inputExtensions = "";
    [ObservableProperty] private bool? _requireLossless;
    [ObservableProperty] private int? _minimumSampleRateHz;
    [ObservableProperty] private int? _minimumBitsPerSample;
    [ObservableProperty] private SettingsChannelChoice _inputChannelChoice =
        SettingsChoiceLists.ChannelChoices[0];
    [ObservableProperty] private bool _matchAnyQualityMinimum;
    [ObservableProperty] private LibraryIngestAlbumCondition _albumCondition;
    [ObservableProperty] private LibraryIngestSourceSelection _sourceSelection;
    [ObservableProperty] private bool _requireFallbackApproval;
    [ObservableProperty] private LibraryIngestAction _action;
    [ObservableProperty] private Guid? _destinationRootId;
    [ObservableProperty] private SettingsRootChoice? _destinationRootChoice;
    [ObservableProperty] private string? _outputExtension;
    [ObservableProperty] private string? _codec;
    [ObservableProperty] private string? _encoder;
    [ObservableProperty] private string? _extraFfmpegOptions;
    [ObservableProperty] private bool _addToMediaCatalog;
    [ObservableProperty] private int? _bitrateKbps;
    [ObservableProperty] private int? _sampleRateHz;
    [ObservableProperty] private int? _bitsPerSample;
    [ObservableProperty] private SettingsChannelChoice _outputChannelChoice =
        SettingsChoiceLists.ChannelChoices[0];
    [ObservableProperty] private bool _preserveMetadata = true;
    [ObservableProperty] private bool _preserveArtwork = true;
    [ObservableProperty] private bool _useProfileCollision = true;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    private bool _refreshingDestinationRoots;

    public ObservableCollection<SettingsRootChoice> DestinationRootChoices { get; } = [];

    public string Summary => $"{Action}; {InputExtensions} to " +
        (DestinationRootId is not null
            ? DestinationRootChoice?.Label ?? DestinationRootId.Value.ToString("D")
            : AddToMediaCatalog ? "configured media catalog" : "no destination");

    public static IngestRecipeEditorRow From(LibraryIngestRecipe recipe) => new()
    {
        Source = recipe,
        Id = recipe.Id,
        Name = recipe.Name,
        Enabled = recipe.Enabled,
        InputExtensions = string.Join(", ", recipe.InputExtensions),
        RequireLossless = recipe.RequireLossless,
        MinimumSampleRateHz = recipe.MinimumSampleRateHz,
        MinimumBitsPerSample = recipe.MinimumBitsPerSample,
        InputChannelChoice = SettingsChoiceLists.ChannelChoice(recipe.InputChannels),
        MatchAnyQualityMinimum = recipe.MatchAnyQualityMinimum,
        AlbumCondition = recipe.AlbumCondition,
        SourceSelection = recipe.SourceSelection,
        RequireFallbackApproval = recipe.RequireFallbackApproval,
        Action = recipe.Action,
        DestinationRootId = recipe.DestinationRootId,
        OutputExtension = recipe.OutputExtension,
        Codec = recipe.Codec,
        Encoder = recipe.Encoder,
        ExtraFfmpegOptions = recipe.ExtraFfmpegOptions,
        AddToMediaCatalog = recipe.AddToMediaCatalog,
        BitrateKbps = recipe.BitrateKbps,
        SampleRateHz = recipe.SampleRateHz,
        BitsPerSample = recipe.BitsPerSample,
        OutputChannelChoice = SettingsChoiceLists.ChannelChoice(recipe.OutputChannels),
        PreserveMetadata = recipe.PreserveMetadata,
        PreserveArtwork = recipe.PreserveArtwork,
        UseProfileCollision = recipe.CollisionPolicy is null,
        CollisionPolicy = recipe.CollisionPolicy ?? LibraryPathCollisionPolicy.Stop,
    };

    public static IngestRecipeEditorRow Create() => From(new(
        Id: "recipe-" + Guid.NewGuid().ToString("N")[..12],
        Name: "New recipe",
        Enabled: false,
        InputExtensions: [".flac"],
        RequireLossless: null,
        MinimumSampleRateHz: null,
        MinimumBitsPerSample: null,
        InputChannels: LibraryChannelSelection.Stereo,
        MatchAnyQualityMinimum: false,
        Action: LibraryIngestAction.Copy,
        DestinationRootId: null,
        DestinationLegacyRole: LibraryIngestRole.None,
        OutputExtension: null,
        Codec: null,
        Encoder: null,
        BitrateKbps: null,
        SampleRateHz: null,
        BitsPerSample: null,
        OutputChannels: LibraryChannelSelection.Stereo,
        PreserveMetadata: true,
        PreserveArtwork: true,
        CollisionPolicy: null));

    public LibraryIngestRecipe Build()
    {
        return Source with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Enabled = Enabled,
            InputExtensions = InputExtensions.Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RequireLossless = RequireLossless,
            MinimumSampleRateHz = MinimumSampleRateHz,
            MinimumBitsPerSample = MinimumBitsPerSample,
            InputChannels = InputChannelChoice.Value,
            MatchAnyQualityMinimum = MatchAnyQualityMinimum,
            AlbumCondition = AlbumCondition,
            SourceSelection = SourceSelection,
            RequireFallbackApproval = RequireFallbackApproval,
            Action = Action,
            DestinationRootId = DestinationRootId,
            DestinationLegacyRole = LibraryIngestRole.None,
            OutputExtension = Clean(OutputExtension),
            Codec = Clean(Codec),
            Encoder = Clean(Encoder),
            ExtraFfmpegOptions = Clean(ExtraFfmpegOptions),
            AddToMediaCatalog = AddToMediaCatalog,
            BitrateKbps = BitrateKbps,
            SampleRateHz = SampleRateHz,
            BitsPerSample = BitsPerSample,
            OutputChannels = OutputChannelChoice.Value,
            PreserveMetadata = PreserveMetadata,
            PreserveArtwork = PreserveArtwork,
            CollisionPolicy = UseProfileCollision ? null : CollisionPolicy,
            OutputRepresentationRole = LibraryRepresentationRole.Ignore,
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    partial void OnDestinationRootChoiceChanged(SettingsRootChoice? value)
    {
        if (!_refreshingDestinationRoots)
            DestinationRootId = value?.Id;
    }

    public void RefreshDestinationRootChoices(
        IEnumerable<IndexTargetEditorRow> roots)
    {
        Guid? selectedId = DestinationRootId;
        _refreshingDestinationRoots = true;
        try
        {
            DestinationRootChoices.Clear();
            DestinationRootChoices.Add(new(null, "No direct root"));
            foreach (IndexTargetEditorRow root in roots)
            {
                string path = string.IsNullOrWhiteSpace(root.Path)
                    ? "New library root"
                    : root.Path.Trim();
                DestinationRootChoices.Add(new(root.Id, path));
            }
            SettingsRootChoice? selected = DestinationRootChoices.FirstOrDefault(choice =>
                choice.Id == selectedId);
            if (selected is null && selectedId is not null)
            {
                selected = new(selectedId,
                    $"Missing root ({selectedId.Value:D})");
                DestinationRootChoices.Add(selected);
            }
            DestinationRootChoice = selected ?? DestinationRootChoices[0];
        }
        finally
        {
            _refreshingDestinationRoots = false;
        }
    }
}

public partial class IngestProfileEditorRow : ObservableObject
{
    public required LibraryIngestProfile Source { get; init; }
    public string Id => Source.Id;
    public bool IsBuiltIn => LibraryIngestProfilePresets.All.Any(profile => string.Equals(
        profile.Id, Id, StringComparison.OrdinalIgnoreCase));
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibrarySourceDisposition _sourceDisposition;
    [ObservableProperty] private bool _preserveSidecars;
    public ObservableCollection<IngestRecipeEditorRow> Recipes { get; } = [];

    public static IngestProfileEditorRow From(LibraryIngestProfile profile)
    {
        var editor = new IngestProfileEditorRow
        {
            Source = profile,
            Name = profile.Name,
            Enabled = profile.Ingest.Enabled,
            SourceDisposition = profile.Ingest.SourceDisposition,
            PreserveSidecars = profile.Ingest.PreserveSidecars,
        };
        foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
            editor.Recipes.Add(IngestRecipeEditorRow.From(recipe));
        return editor;
    }

    public LibraryIngestProfile Build() => Source with
    {
        Name = Name.Trim(),
        Ingest = Source.Ingest with
        {
            Enabled = Enabled,
            SourceDisposition = SourceDisposition,
            PreserveSidecars = PreserveSidecars,
            Recipes = Recipes.Select(recipe => recipe.Build()).ToArray(),
        },
    };
}

public partial class SidecarRuleEditorRow : ObservableObject
{
    public required LibrarySidecarRule Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _patterns = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibrarySidecarDisposition _disposition;

    public static SidecarRuleEditorRow From(LibrarySidecarRule rule) => new()
    {
        Source = rule,
        Id = rule.Id,
        Name = rule.Name,
        Patterns = string.Join(", ", rule.Patterns),
        Enabled = rule.Enabled,
        Disposition = rule.Disposition,
    };

    public static SidecarRuleEditorRow Create() => From(new(
        "sidecar-" + Guid.NewGuid().ToString("N")[..12],
        "New sidecar rule",
        true,
        ["*.txt"],
        LibrarySidecarDisposition.Preserve));

    public LibrarySidecarRule Build() => Source with
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Patterns = Patterns.Split([',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Enabled = Enabled,
        Disposition = Disposition,
    };
}

public partial class LibraryProfileEditorRow : ObservableObject
{
    public required LibraryProfile Source { get; init; }
    public string Id => Source.Id;
    public bool IsCustom => Source.Preset == LibraryProfilePreset.Custom;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private string _directoryTemplate = "";
    [ObservableProperty] private string _fileNameTemplate = "";
    [ObservableProperty] private int _trackPadding;
    [ObservableProperty] private int _discPadding;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    [ObservableProperty] private bool _useItunesCanonicalNaming;
    [ObservableProperty] private bool _preserveUnicode;
    [ObservableProperty] private string _invalidCharacterReplacement = "_";
    [ObservableProperty] private string _missingArtistFallback = "Unknown Artist";
    [ObservableProperty] private string _missingAlbumFallback = "Unknown Album";
    [ObservableProperty] private string _missingTitleFallback = "Untitled";
    [ObservableProperty] private string _compilationValue = "Compilations";
    [ObservableProperty] private LibraryUnicodeNormalization _unicodeNormalization;
    [ObservableProperty] private int? _componentLengthLimit;
    [ObservableProperty] private int? _discAlbumLengthLimit;
    [ObservableProperty] private int? _completePathLengthLimit;
    [ObservableProperty] private LibraryDiscStrategy _discStrategy;
    [ObservableProperty] private LibraryTrackTotalScope _trackTotalScope;
    [ObservableProperty] private bool _inferAlbumSuffix;
    [ObservableProperty] private bool _identityUsesAlbumArtist;
    [ObservableProperty] private bool _identityStripsFormatSuffixes;
    [ObservableProperty] private bool _identityStripsDiscSuffixes;
    [ObservableProperty] private bool _identityIncludesReleaseYear;
    [ObservableProperty] private bool _preserveReplayGain;
    [ObservableProperty] private bool _preserveMusicBrainzIdentifiers;
    [ObservableProperty] private bool _preserveCustomFields;
    [ObservableProperty] private bool _preserveCompilationSemantics;
    [ObservableProperty] private int _highResolutionMinimumSampleRateHz;
    [ObservableProperty] private int _highResolutionMinimumBitsPerSample;
    [ObservableProperty] private LibraryArtworkStorage _artworkStorage;
    [ObservableProperty] private LibraryArtworkRoleSelection _artworkRoles;
    [ObservableProperty] private LibraryArtworkEncoding _artworkEncoding;
    [ObservableProperty] private int _artworkMaximumDimension;
    [ObservableProperty] private int _artworkMaximumEncodedBytes;
    [ObservableProperty] private int _artworkJpegQuality;
    [ObservableProperty] private string _artworkSidecarTemplate = "";
    [ObservableProperty] private LibrarySidecarDisposition _unknownSidecarDisposition;

    public ObservableCollection<HealthRuleEditorRow> HealthRules { get; } = [];
    public ObservableCollection<SidecarRuleEditorRow> SidecarRules { get; } = [];

    public static LibraryProfileEditorRow From(LibraryProfile profile)
    {
        var editor = new LibraryProfileEditorRow
        {
            Source = profile,
            Name = profile.Name,
            DirectoryTemplate = profile.Naming.DirectoryTemplate,
            FileNameTemplate = profile.Naming.FileNameTemplate,
            TrackPadding = profile.Naming.TrackPadding,
            DiscPadding = profile.Naming.DiscPadding,
            CollisionPolicy = profile.Naming.CollisionPolicy,
            UseItunesCanonicalNaming = profile.Naming.UseItunesCanonicalNaming,
            PreserveUnicode = profile.Naming.PreserveUnicode,
            InvalidCharacterReplacement = profile.Naming.InvalidCharacterReplacement,
            MissingArtistFallback = profile.Naming.MissingArtistFallback,
            MissingAlbumFallback = profile.Naming.MissingAlbumFallback,
            MissingTitleFallback = profile.Naming.MissingTitleFallback,
            CompilationValue = profile.Naming.CompilationValue,
            UnicodeNormalization = profile.Naming.UnicodeNormalization,
            ComponentLengthLimit = profile.Naming.ComponentLengthLimit,
            DiscAlbumLengthLimit = profile.Naming.DiscAlbumLengthLimit,
            CompletePathLengthLimit = profile.Naming.CompletePathLengthLimit,
            DiscStrategy = profile.Disc.Strategy,
            TrackTotalScope = profile.Disc.TrackTotalScope,
            InferAlbumSuffix = profile.Disc.InferAlbumSuffix,
            IdentityUsesAlbumArtist = profile.AlbumIdentity.UseAlbumArtist,
            IdentityStripsFormatSuffixes = profile.AlbumIdentity.StripFormatSuffixes,
            IdentityStripsDiscSuffixes = profile.AlbumIdentity.StripDiscSuffixes,
            IdentityIncludesReleaseYear = profile.AlbumIdentity.IncludeReleaseYear,
            PreserveReplayGain = profile.Metadata.PreserveReplayGain,
            PreserveMusicBrainzIdentifiers =
                profile.Metadata.PreserveMusicBrainzIdentifiers,
            PreserveCustomFields = profile.Metadata.PreserveCustomFields,
            PreserveCompilationSemantics =
                profile.Metadata.PreserveCompilationSemantics,
            HighResolutionMinimumSampleRateHz =
                profile.Quality.HighResolutionMinimumSampleRateHz,
            HighResolutionMinimumBitsPerSample =
                profile.Quality.HighResolutionMinimumBitsPerSample,
            ArtworkStorage = profile.Artwork.Storage,
            ArtworkRoles = profile.Artwork.Roles,
            ArtworkEncoding = profile.Artwork.Encoding,
            ArtworkMaximumDimension = profile.Artwork.MaximumDimension,
            ArtworkMaximumEncodedBytes = profile.Artwork.MaximumEncodedBytes,
            ArtworkJpegQuality = profile.Artwork.JpegQuality,
            ArtworkSidecarTemplate = profile.Artwork.SidecarFileNameTemplate,
            UnknownSidecarDisposition = profile.Sidecars.UnknownFileDisposition,
        };
        foreach (LibraryHealthRulePolicy rule in profile.Health.Rules)
            editor.HealthRules.Add(new()
            {
                Id = rule.Id,
                Enabled = rule.Enabled,
                Severity = rule.Severity,
                ProposeRepair = rule.ProposeRepair,
                ApplyRepair = rule.ApplyRepair,
            });
        foreach (LibrarySidecarRule rule in profile.Sidecars.Rules)
            editor.SidecarRules.Add(SidecarRuleEditorRow.From(rule));
        return editor;
    }

    public LibraryProfile Build() => Source with
    {
        Name = Name.Trim(),
        Naming = Source.Naming with
        {
            DirectoryTemplate = DirectoryTemplate,
            FileNameTemplate = FileNameTemplate,
            TrackPadding = TrackPadding,
            DiscPadding = DiscPadding,
            CollisionPolicy = CollisionPolicy,
            UseItunesCanonicalNaming = UseItunesCanonicalNaming,
            PreserveUnicode = PreserveUnicode,
            InvalidCharacterReplacement = InvalidCharacterReplacement,
            MissingArtistFallback = MissingArtistFallback,
            MissingAlbumFallback = MissingAlbumFallback,
            MissingTitleFallback = MissingTitleFallback,
            CompilationValue = CompilationValue,
            UnicodeNormalization = UnicodeNormalization,
            ComponentLengthLimit = ComponentLengthLimit,
            DiscAlbumLengthLimit = DiscAlbumLengthLimit,
            CompletePathLengthLimit = CompletePathLengthLimit,
        },
        Disc = Source.Disc with
        {
            Strategy = DiscStrategy,
            TrackTotalScope = TrackTotalScope,
            InferAlbumSuffix = InferAlbumSuffix,
        },
        AlbumIdentity = Source.AlbumIdentity with
        {
            UseAlbumArtist = IdentityUsesAlbumArtist,
            StripFormatSuffixes = IdentityStripsFormatSuffixes,
            StripDiscSuffixes = IdentityStripsDiscSuffixes,
            IncludeReleaseYear = IdentityIncludesReleaseYear,
        },
        Metadata = Source.Metadata with
        {
            PreserveReplayGain = PreserveReplayGain,
            PreserveMusicBrainzIdentifiers = PreserveMusicBrainzIdentifiers,
            PreserveCustomFields = PreserveCustomFields,
            PreserveCompilationSemantics = PreserveCompilationSemantics,
        },
        Quality = Source.Quality with
        {
            HighResolutionMinimumSampleRateHz = HighResolutionMinimumSampleRateHz,
            HighResolutionMinimumBitsPerSample = HighResolutionMinimumBitsPerSample,
        },
        Health = new(HealthRules.Select(rule => rule.Build()).ToArray()),
        Artwork = Source.Artwork with
        {
            Storage = ArtworkStorage,
            Roles = ArtworkRoles,
            Encoding = ArtworkEncoding,
            MaximumDimension = ArtworkMaximumDimension,
            MaximumEncodedBytes = ArtworkMaximumEncodedBytes,
            JpegQuality = ArtworkJpegQuality,
            SidecarFileNameTemplate = ArtworkSidecarTemplate,
        },
        Sidecars = Source.Sidecars with
        {
            UnknownFileDisposition = UnknownSidecarDisposition,
            Rules = SidecarRules.Select(rule => rule.Build()).ToArray(),
        },
    };
}
