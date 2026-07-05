using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// The tag editor. Operates on a target set of one or more files; shows each common field with a
/// "mixed" marker when the selection disagrees, and writes only the fields the user actually edits.
/// </summary>
public partial class TagEditorViewModel : ViewModelBase
{
    // Parsing every selected file just to show common/mixed field values would freeze the editor on
    // large selections (especially over a NAS). We only need to sample enough files to tell whether a
    // field is shared or mixed; Save still writes to the whole selection (_targets).
    private const int MaxCommonValueSample = 200;

    private readonly IMediaFileService _media;
    private readonly ITagWriteService _writer;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<string> _targets = [];

    // A newer selection supersedes an in-flight load: the generation guards state ownership and the
    // token cancels the file parsing promptly.
    private int _generation;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private string? _targetSummary = "No selection.";

    [ObservableProperty]
    private bool _hasTargets;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _resultMessage;

    public ObservableCollection<EditableField> Fields { get; } = [];

    // The curated set of editable fields, in display order.
    private static readonly (TagFields Field, string Label)[] FieldDefs =
    [
        (TagFields.Title, "Title"),
        (TagFields.Artist, "Artist"),
        (TagFields.AlbumArtist, "Album Artist"),
        (TagFields.Album, "Album"),
        (TagFields.TrackNumber, "Track #"),
        (TagFields.TotalTracks, "Total Tracks"),
        (TagFields.DiscNumber, "Disc #"),
        (TagFields.TotalDiscs, "Total Discs"),
        (TagFields.Date, "Date"),
        (TagFields.Genre, "Genre"),
        (TagFields.Composer, "Composer"),
        (TagFields.Comment, "Comment"),
    ];

    public TagEditorViewModel(IMediaFileService media, ITagWriteService writer, IDialogService dialogs)
    {
        _media = media;
        _writer = writer;
        _dialogs = dialogs;
        foreach (var (field, label) in FieldDefs)
            Fields.Add(new EditableField(field, label));
    }

    /// <summary>Point the editor at a set of files and load their current field values.</summary>
    public async Task SetTargetsAsync(IReadOnlyList<string> paths)
    {
        var gen = ++_generation;   // supersede any in-flight load
        _loadCts?.Cancel();

        _targets = paths;
        HasTargets = paths.Count > 0;
        ResultMessage = null;

        if (paths.Count == 0)
        {
            _loadCts = null;
            TargetSummary = "No selection.";
            foreach (var f in Fields)
                f.SetLoaded("", mixed: false);
            IsBusy = false;
            NotifyEditCommands();
            return;
        }

        // Sample enough files to detect shared vs mixed values without parsing an unbounded number.
        var sample = paths.Count > MaxCommonValueSample ? paths.Take(MaxCommonValueSample).ToList() : paths;

        TargetSummary = paths.Count == 1
            ? System.IO.Path.GetFileName(paths[0])
            : paths.Count > MaxCommonValueSample
                ? $"{paths.Count:N0} files selected (values sampled from {MaxCommonValueSample:N0})"
                : $"{paths.Count:N0} files selected";

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        IsBusy = true;
        NotifyEditCommands();
        try
        {
            // Load each file's field map (first value wins per field), then fold across the sample.
            var maps = new List<Dictionary<TagFields, string>>(sample.Count);
            foreach (var path in sample)
            {
                ct.ThrowIfCancellationRequested();
                var result = await _media.LoadAsync(path, includeArtwork: false, ct);
                if (result.Success)
                    maps.Add(BuildMap(result.Value!));
            }
            ct.ThrowIfCancellationRequested();

            foreach (var field in Fields)
            {
                var values = maps.Select(m => m.TryGetValue(field.Field, out var v) ? v : "").Distinct().ToList();
                if (values.Count <= 1)
                    field.SetLoaded(values.Count == 1 ? values[0] : "", mixed: false);
                else
                    field.SetLoaded("", mixed: true);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection has taken over; leave its state alone.
        }
        finally
        {
            if (gen == _generation)
            {
                _loadCts = null;
                IsBusy = false;
                NotifyEditCommands();
            }
            cts.Dispose();
        }
    }

    private void NotifyEditCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        EditAllFieldsCommand.NotifyCanExecuteChanged();
    }

    private static Dictionary<TagFields, string> BuildMap(MediaFileModel model)
    {
        var map = new Dictionary<TagFields, string>();
        foreach (var kv in model.KnownFields)
            map.TryAdd(kv.Field, kv.Value);   // first value wins, mirroring the parsers
        return map;
    }

    private bool CanSave() => HasTargets && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        // Only fields the user actually changed become edits; a blank value clears the field.
        var edits = Fields
            .Where(f => f.IsModified)
            .Select(f => new TagEdit(f.Field, string.IsNullOrEmpty(f.Value) ? null : f.Value))
            .ToList();

        if (edits.Count == 0)
        {
            ResultMessage = "No changes to save.";
            return;
        }

        IsBusy = true;
        SaveCommand.NotifyCanExecuteChanged();
        EditAllFieldsCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _writer.ApplyAsync(_targets, edits);
            ResultMessage = result.Summary
                + (result.Files.Any(f => f.UnsupportedFields.Count > 0)
                    ? " (some fields unsupported by a file's tag format)"
                    : "");
            // Reload so the editor reflects what's now on disk.
            await SetTargetsAsync(_targets);
        }
        catch (Exception ex)
        {
            ResultMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            EditAllFieldsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task RevertAsync() => await SetTargetsAsync(_targets);

    // Opens the arbitrary-field editor (every writable TagFields, not just the curated set).
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task EditAllFieldsAsync()
    {
        if (_targets.Count == 0)
            return;
        var saved = await _dialogs.ShowFieldsEditorAsync(_targets);
        if (saved)
        {
            ResultMessage = "Fields updated.";
            await SetTargetsAsync(_targets);   // reflect changes in the curated view
        }
    }
}
