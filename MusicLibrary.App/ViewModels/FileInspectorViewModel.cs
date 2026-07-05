using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>One metadata field aggregated across the selection: a shared value, or the mixed marker.</summary>
public sealed record AggregatedField(string Name, string Value, bool IsMixed);

/// <summary>
/// Read-only inspection of the selected file(s). For a single file it shows its metadata; for several
/// it aggregates each field — shared values are shown, and fields that differ across the files show a
/// "multiple values" marker (like the batch tag editor, but read-only).
/// </summary>
public partial class FileInspectorViewModel : ViewModelBase
{
    // DB reads are cheap, but a huge selection shouldn't stall the pane; aggregate a bounded sample.
    private const int MaxAggregate = 300;
    private const string MixedPlaceholder = "(multiple values)";

    private readonly IMediaFileService _media;
    private readonly IFileDialogService _dialogs;
    private readonly ILibraryService _library;

    // Guards against a slower, superseded load overwriting a newer selection's result.
    private int _generation;
    private IReadOnlyList<string> _targets = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True once a file (or selection) has been loaded and has content to show.</summary>
    [ObservableProperty]
    private bool _hasContent;

    [ObservableProperty]
    private Bitmap? _artwork;

    [ObservableProperty]
    private string? _artworkCaption;

    /// <summary>True when the selection's files don't all share the same artwork (show a placeholder).</summary>
    [ObservableProperty]
    private bool _artworkIsMixed;

    /// <summary>Set when several files are selected; explains the aggregation.</summary>
    [ObservableProperty]
    private string? _multiSelectionNote;

    // Aggregated header summary (shared value or the mixed marker).
    [ObservableProperty] private string? _headerTitle;
    [ObservableProperty] private string? _headerArtist;
    [ObservableProperty] private string? _headerAlbum;
    [ObservableProperty] private string? _headerInfo;

    public ObservableCollection<AggregatedField> KnownFields { get; } = [];
    public ObservableCollection<AggregatedField> TextFields { get; } = [];

    public FileInspectorViewModel(IMediaFileService media, IFileDialogService dialogs, ILibraryService library)
    {
        _media = media;
        _dialogs = dialogs;
        _library = library;
    }

    private static readonly IReadOnlyList<FilePickerFilter> AudioFilters =
    [
        new FilePickerFilter("Audio files", ["*.mp3", "*.flac", "*.ogg", "*.m4a", "*.mp4", "*.wv", "*.dsf"]),
        new FilePickerFilter("All files", ["*"]),
    ];

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Select a music file", AudioFilters);
        if (path is not null)
            await LoadFromPathsAsync([path]);
    }

    /// <summary>Load a single file (e.g. from Browse or an artwork refresh).</summary>
    public Task LoadFromPathAsync(string path) => LoadFromPathsAsync([path]);

    /// <summary>Show details for a selection of files, aggregating fields that differ.</summary>
    public async Task LoadFromPathsAsync(IReadOnlyList<string> paths)
    {
        _targets = paths;
        await ReloadAsync();
    }

    /// <summary>Re-aggregate the current selection (e.g. after its artwork was edited).</summary>
    public async Task ReloadAsync()
    {
        var paths = _targets;
        var gen = ++_generation;

        if (paths.Count == 0)
        {
            Reset();
            return;
        }

        var sample = paths.Count > MaxAggregate ? paths.Take(MaxAggregate).ToList() : paths;
        MultiSelectionNote = paths.Count <= 1
            ? null
            : paths.Count > MaxAggregate
                ? $"{paths.Count:N0} files selected — aggregated from the first {MaxAggregate:N0}; differing fields show “{MixedPlaceholder}”."
                : $"{paths.Count:N0} files selected — shared values shown; differing fields show “{MixedPlaceholder}”.";

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            // Load artwork only for the first (representative) file — that's what the cover preview shows.
            var models = new List<MediaFileModel>(sample.Count);
            for (int i = 0; i < sample.Count; i++)
            {
                var result = await _media.LoadAsync(sample[i], includeArtwork: i == 0);
                if (gen != _generation)
                    return;   // a newer selection has taken over
                if (result.Success)
                    models.Add(result.Value!);
            }

            if (models.Count == 0)
            {
                Reset();
                ErrorMessage = "Couldn't read the selected file(s).";
                return;
            }

            HeaderTitle = AggregateValue(models.Select(m => m.Title));
            HeaderArtist = AggregateValue(models.Select(m => m.Artist));
            HeaderAlbum = AggregateValue(models.Select(m => m.Album));
            HeaderInfo = $"Tag: {AggregateValue(models.Select(m => m.TagType))}    Codec: {AggregateValue(models.Select(m => m.Codec?.CodecName))}";

            RebuildAggregate(KnownFields, models.Select(m => m.KnownFields.Select(f => (f.Field.ToString(), f.Value))));
            RebuildAggregate(TextFields, models.Select(m => m.TextFields.Select(t => (t.Key, t.Value))));

            // Compare artwork across the selection by hash (no image data loaded). If the files don't
            // all share the same set, show a "differs" placeholder instead of one file's cover.
            var signatures = sample.Count > 1
                ? await _library.GetImageSignaturesAsync(sample)
                : [];
            if (gen != _generation)
                return;

            if (signatures.Count > 1 && signatures.Distinct().Count() > 1)
            {
                Artwork?.Dispose();
                Artwork = null;
                ArtworkIsMixed = true;
                var distinctImages = signatures.Where(s => s.Length > 0).Distinct().Count();
                ArtworkCaption = $"{distinctImages} different image(s) across the selection";
            }
            else
            {
                ArtworkIsMixed = false;
                LoadArtwork(models[0]);
            }

            HasContent = true;
        }
        finally
        {
            if (gen == _generation)
                IsLoading = false;
        }
    }

    // A value shared by every file → that value; otherwise the mixed marker.
    private static string AggregateValue(IEnumerable<string?> values)
    {
        var distinct = values.Select(v => v ?? "").Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : MixedPlaceholder;
    }

    // Aggregate a per-file (name → value) set: first value wins per field per file (mirrors the
    // parsers/editor); a field is "mixed" when its value isn't identical across all files.
    private static void RebuildAggregate(ObservableCollection<AggregatedField> target,
        IEnumerable<IEnumerable<(string Name, string Value)>> perFile)
    {
        var maps = perFile.Select(fields =>
        {
            var map = new Dictionary<string, string>();
            foreach (var (name, value) in fields)
                map.TryAdd(name, value);
            return map;
        }).ToList();

        target.Clear();
        foreach (var name in maps.SelectMany(m => m.Keys).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var values = maps.Select(m => m.TryGetValue(name, out var v) ? v : "").Distinct().ToList();
            target.Add(values.Count == 1
                ? new AggregatedField(name, values[0], false)
                : new AggregatedField(name, MixedPlaceholder, true));
        }
    }

    private void LoadArtwork(MediaFileModel model)
    {
        Artwork?.Dispose();
        Artwork = null;
        ArtworkCaption = null;

        var art = model.Artwork.FirstOrDefault();
        if (art is null || art.Data.Length == 0)
            return;

        try
        {
            using var ms = new MemoryStream(art.Data);
            Artwork = new Bitmap(ms);
            ArtworkCaption = $"{art.ImageType} {art.Width}x{art.Height}, {art.Size:N0} bytes";
        }
        catch
        {
            // Undecodable artwork shouldn't break the inspector.
            ArtworkCaption = "(unreadable image)";
        }
    }

    private void Reset()
    {
        HasContent = false;
        HeaderTitle = HeaderArtist = HeaderAlbum = HeaderInfo = null;
        MultiSelectionNote = null;
        KnownFields.Clear();
        TextFields.Clear();
        Artwork?.Dispose();
        Artwork = null;
        ArtworkCaption = null;
        ArtworkIsMixed = false;
    }
}
