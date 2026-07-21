using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
    LibrarySortState? Sort);

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
        Details = new DetailsRow(Record);
        Details.RebuildSearchText(DetailsColumns.All.Select(column => column.Key).ToArray());
    }

    public TrackRecord Record { get; }
    public DetailsRow Details { get; }
    public string Path => Record.Path;
    public string Title => Record.Title ?? System.IO.Path.GetFileNameWithoutExtension(Record.Path);
    public string Artist => Record.Artist ?? "";
    public string AlbumArtist => Record.AlbumArtist ?? "";
    public string Album => Record.Album ?? "";
    public int? Track => Record.TrackNumber;
    public int? TrackTotal => Record.TrackTotal;
    public int? Disc => Record.DiscNumber;
    public int? DiscTotal => Record.DiscTotal;
    public string Codec => Record.CodecName ?? "";
    public string Duration => Details["Duration"];
    public string Modified => Details["Modified"];
    public string SearchText => Details.SearchText;

    [ObservableProperty]
    private object? _thumbnailSource;

    [ObservableProperty]
    private bool _thumbnailLoaded;
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
    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private bool _organize = true;

    [ObservableProperty]
    private bool _useItunesCanonicalNaming;

    [ObservableProperty]
    private LibraryIngestRole _ingestRole;

    [ObservableProperty]
    private bool _isSyncTarget;

    public ObservableCollection<IndexTargetSetEditorRow> Memberships { get; } = [];

    public IndexTargetEntry? Source { get; set; }
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
}
