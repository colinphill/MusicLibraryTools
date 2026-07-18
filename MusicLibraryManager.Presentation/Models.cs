using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public sealed record LibraryColumnState(string Key, double? Width, int DisplayIndex, bool Visible);

public sealed record LibrarySortState(string Key, bool Descending);

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

public sealed record SelectionContext(IReadOnlyList<string> Paths)
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
    public int? Disc => Record.DiscNumber;
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

    public void SetLoaded(string? value, bool mixed)
    {
        Value = value;
        IsMixed = mixed;
        IsModified = false;
    }

    partial void OnValueChanged(string? value)
    {
        if (!IsMixed || value is not null)
            IsModified = true;
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
    private string? _defaultOffset;

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
