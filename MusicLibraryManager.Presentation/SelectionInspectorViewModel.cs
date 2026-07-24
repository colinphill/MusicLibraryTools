using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class SelectionInspectorViewModel : ObservableObject
{
    private const int MaxCommonValueSample = 200;
    private readonly IMediaFileService _media;
    private readonly ILibraryService _library;
    private readonly ITagWriteService _tags;
    private readonly IArtworkService _artwork;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IFieldsEditorService _fieldsEditor;
    private readonly IThumbnailService _thumbnails;
    private readonly IActivityService _activities;
    private readonly IMetadataOperationService? _metadataOperations;
    private readonly IMetadataDocumentService? _metadataDocuments;
    private int _generation;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _editCancellation;
    private bool _artworkSetModified;

    [ObservableProperty] private SelectionContext _selection = SelectionContext.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _overview = "Select a track to inspect its metadata.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfo))]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageTone _statusTone = MessageTone.Info;
    [ObservableProperty] private object? _artworkSource;
    [ObservableProperty] private bool _isArtworkMixed;
    [ObservableProperty] private string _artworkSummary = "No artwork loaded.";
    [ObservableProperty] private int _artworkMaxDimension = 600;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(UnsavedChangesSummary))]
    private bool _hasPendingArtworkChanges;

    public SelectionInspectorViewModel(
        IMediaFileService media,
        ILibraryService library,
        ITagWriteService tags,
        IArtworkService artwork,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IFieldsEditorService fieldsEditor,
        IThumbnailService thumbnails,
        IActivityService activities,
        IMetadataOperationService? metadataOperations = null,
        IMetadataDocumentService? metadataDocuments = null)
    {
        _media = media;
        _library = library;
        _tags = tags;
        _artwork = artwork;
        _files = files;
        _dialogs = dialogs;
        _fieldsEditor = fieldsEditor;
        _thumbnails = thumbnails;
        _activities = activities;
        _metadataOperations = metadataOperations;
        _metadataDocuments = metadataDocuments;
        foreach (var (field, label) in FieldDefinitions)
        {
            var item = new EditableTagField(field, label);
            item.PropertyChanged += OnFieldChanged;
            Fields.Add(item);
        }
    }

    public ObservableCollection<EditableTagField> Fields { get; } = [];
    public ObservableCollection<ArtworkPreviewItem> ArtworkItems { get; } = [];
    public IReadOnlyList<ID3v2Util.APICType> ArtworkTypes { get; } =
        Enum.GetValues<ID3v2Util.APICType>();
    public bool HasSelection => Selection.HasSelection;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsStatusInfo => StatusTone == MessageTone.Info;
    public bool IsStatusSuccess => StatusTone == MessageTone.Success;
    public bool IsStatusWarning => StatusTone == MessageTone.Warning;
    public bool IsStatusError => StatusTone == MessageTone.Error;
    public string StatusIcon => StatusTone switch
    {
        MessageTone.Success => "✓",
        MessageTone.Warning => "⚠",
        MessageTone.Error => "!",
        _ => "i",
    };
    public string SelectionSummary => Selection.Summary;
    public bool HasUnsavedChanges => Fields.Any(item => item.IsModified) ||
        HasPendingArtworkChanges || ArtworkItems.Any(item => item.IsModified);
    public string UnsavedChangesSummary
    {
        get
        {
            int tagCount = Fields.Count(item => item.IsModified);
            bool artwork = HasPendingArtworkChanges || ArtworkItems.Any(item => item.IsModified);
            return (tagCount, artwork) switch
            {
                (0, false) => "No unsaved changes",
                (1, false) => "1 unsaved tag change",
                (> 1, false) => $"{tagCount:N0} unsaved tag changes",
                (0, true) => "Unsaved artwork changes",
                _ => $"{tagCount:N0} unsaved tag changes and artwork changes",
            };
        }
    }
    public event Action? FilesChanged;

    public void ReportArtworkPreviewUnavailable()
    {
        StatusTone = MessageTone.Warning;
        StatusMessage = "The full-size artwork preview is unavailable because the image data is missing or invalid.";
    }

    private static readonly (TagFields Field, string Label)[] FieldDefinitions =
    [
        (TagFields.Title, "Title"),
        (TagFields.Artist, "Artist"),
        (TagFields.AlbumArtist, "Album artist"),
        (TagFields.Album, "Album"),
        (TagFields.TrackNumber, "Track"),
        (TagFields.TotalTracks, "Track total"),
        (TagFields.DiscNumber, "Disc"),
        (TagFields.TotalDiscs, "Disc total"),
        (TagFields.Date, "Release date"),
        (TagFields.Genre, "Genre"),
        (TagFields.Composer, "Composer"),
        (TagFields.Comment, "Comment"),
    ];

    public async Task<bool> TryLoadAsync(SelectionContext selection)
    {
        if (SameSelection(Selection, selection))
            return true;
        if (!await ConfirmDiscardChangesAsync())
            return false;
        await LoadAsync(selection);
        return true;
    }

    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!HasUnsavedChanges)
            return true;
        if (!await _dialogs.ConfirmAsync(
                "Discard unsaved metadata changes?",
                $"{UnsavedChangesSummary} for {SelectionSummary}. Discard them and continue?",
                "Discard changes"))
            return false;
        await LoadAsync(Selection);
        return true;
    }

    public async Task LoadAsync(SelectionContext selection)
    {
        int generation = ++_generation;
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Selection = selection;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        StatusMessage = null;
        StatusTone = MessageTone.Info;
        ArtworkSource = null;
        ClearArtworkItems();
        _artworkSetModified = false;
        HasPendingArtworkChanges = false;
        IsArtworkMixed = false;
        ArtworkSummary = "No embedded artwork.";
        foreach (EditableTagField field in Fields)
            field.SetLoaded([], false);
        NotifyUnsavedChangesChanged();
        NotifyCommands();

        if (!selection.HasSelection)
        {
            Overview = "Select a track to inspect its metadata.";
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            // Large selections use the complete cache-backed records below. Reading an arbitrary
            // first 200 files is both slow and incapable of proving a value is common, so fields
            // absent from the cache remain explicitly unverified until the user replaces them.
            IReadOnlyList<string> sample = selection.Paths.Count > MaxCommonValueSample
                ? []
                : selection.Paths;
            var models = new List<MediaFileModel>();
            var documents = new List<MediaDocument>();
            foreach (string path in sample)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_metadataDocuments is not null)
                {
                    try
                    {
                        documents.Add(
                            await _metadataDocuments.LoadAsync(
                                path,
                                includeArtwork: false,
                                cancellation.Token));
                    }
                    catch (Exception error) when (
                        error is not OperationCanceledException)
                    {
                        StatusTone = MessageTone.Warning;
                        StatusMessage =
                            $"Could not read '{path}': " +
                            error.Message;
                    }
                }
                else
                {
                    OperationResult<MediaFileModel> result =
                        await _media.LoadAsync(
                            path,
                            includeArtwork: false,
                            cancellation.Token);
                    if (result.Success &&
                        result.Value is not null)
                        models.Add(result.Value);
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _generation)
                return;

            if (_metadataDocuments is not null)
            {
                LoadFields(documents, selection);
                Overview = DescribeOverview(
                    selection,
                    documents);
            }
            else
            {
                LoadFields(models, selection);
                Overview = DescribeOverview(
                    selection,
                    models);
            }
            if (selection.Paths.Count > MaxCommonValueSample)
                Overview += Environment.NewLine + Environment.NewLine +
                    "Common values in cache-backed fields were checked across the full selection. " +
                    "Fields not stored in the cache are marked unverified and remain blank until you intentionally replace them.";

            IReadOnlyList<string> artworkSignatures = await _library.GetImageSignaturesAsync(
                selection.Paths, cancellation.Token);
            string[] distinctArtwork = artworkSignatures.Distinct(StringComparer.Ordinal).ToArray();
            IsArtworkMixed = distinctArtwork.Length > 1;
            if (IsArtworkMixed)
            {
                ArtworkSummary = "Mixed values — selected files have different embedded artwork.";
                return;
            }

            if (distinctArtwork.Length == 0 || string.IsNullOrEmpty(distinctArtwork[0]))
                return;

            ArtworkModel[] embeddedArtwork;
            if (_metadataDocuments is not null)
            {
                MediaDocument artwork =
                    await _metadataDocuments.LoadAsync(
                        selection.Paths[0],
                        includeArtwork: true,
                        cancellation.Token);
                embeddedArtwork =
                    artwork.Artwork.ToArray();
            }
            else
            {
                OperationResult<MediaFileModel> artwork =
                    await _media.LoadAsync(
                        selection.Paths[0],
                        includeArtwork: true,
                        cancellation.Token);
                embeddedArtwork =
                    artwork.Value?.Artwork.ToArray() ??
                    [];
            }
            if (generation != _generation)
                return;
            if (embeddedArtwork.Length == 0)
                return;
            int invalidArtwork = 0;
            for (int index = 0; index < embeddedArtwork.Length; index++)
            {
                ArtworkModel image = embeddedArtwork[index];
                object? source = null;
                try
                {
                    source = await _thumbnails.CreateImageSourceAsync(
                        image.Data,
                        cancellationToken: cancellation.Token);
                }
                catch (OperationCanceledException) when (
                    cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Keep malformed artwork visible so it can still be removed or replaced.
                    invalidArtwork++;
                }
                if (generation != _generation)
                    return;
                var preview = new ArtworkPreviewItem(
                    source,
                    ArtworkType(image, index),
                    ArtworkMimeType(image),
                    image.Data,
                    ArtworkDetails(image),
                    image.Description);
                preview.PropertyChanged += OnArtworkItemChanged;
                ArtworkItems.Add(preview);
            }
            ArtworkSource = ArtworkItems
                .FirstOrDefault(item => item.Source is not null)
                ?.Source;
            ArtworkSummary = embeddedArtwork.Length == 1
                ? ArtworkItems[0].Summary
                : $"{embeddedArtwork.Length:N0} embedded artworks";
            if (invalidArtwork > 0)
            {
                string warning =
                    $"{invalidArtwork:N0} embedded artwork " +
                    $"{(invalidArtwork == 1 ? "image" : "images")} could not be decoded. " +
                    "Invalid entries remain available to remove or replace.";
                StatusTone = MessageTone.Warning;
                StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
                    ? warning
                    : $"{StatusMessage} {warning}";
            }
            if (selection.Paths.Count > 1)
                ArtworkSummary += $" · shared by {selection.Paths.Count:N0} tracks";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = error.Message;
        }
        finally
        {
            if (generation == _generation)
            {
                _cancellation = null;
                IsBusy = false;
                NotifyCommands();
            }
            cancellation.Dispose();
        }
    }

    private void LoadFields(IReadOnlyList<MediaFileModel> models, SelectionContext selection)
    {
        bool completeMediaRead = models.Count == selection.Paths.Count &&
            models.Select(model => model.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(selection.Paths);
        Dictionary<string, TrackRecord>? cachedRecords = BuildCompleteRecordMap(selection);
        var maps = models.Select(model => model.KnownFields
            .GroupBy(value => value.Field)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.Value)
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray())).ToArray();
        foreach (EditableTagField field in Fields)
        {
            if (!completeMediaRead)
            {
                if (TryGetCompleteCachedValues(selection, cachedRecords, field.Field, out string[][] cachedValues))
                {
                    SetFieldValues(field, cachedValues, FieldValueVerification.Exact);
                    continue;
                }

                // A sample can indicate that a field varies, but it cannot prove that a common
                // value applies to every selected file. Do not show a representative value that
                // could be mistaken for an exact bulk-edit baseline.
                field.SetLoaded([], true, FieldValueVerification.Unverified);
                continue;
            }

            string[][] valuesByFile = maps
                .Select(map => map.GetValueOrDefault(field.Field) ?? [])
                .ToArray();
            SetFieldValues(field, valuesByFile, FieldValueVerification.Exact);
        }
        NotifyUnsavedChangesChanged();
    }

    private void LoadFields(
        IReadOnlyList<MediaDocument> documents,
        SelectionContext selection)
    {
        bool completeDocumentRead =
            documents.Count == selection.Paths.Count &&
            documents.Select(document => document.Path)
                .ToHashSet(PathComparer)
                .SetEquals(selection.Paths);
        Dictionary<string, TrackRecord>? cachedRecords =
            BuildCompleteRecordMap(selection);
        Dictionary<TagFields, string[]>[] maps =
            documents.Select(document =>
                    document.TagLayers
                        .SelectMany(layer => layer.Fields)
                        .Where(value =>
                            value.Field.KnownField is not null)
                        .GroupBy(value =>
                            value.Field.KnownField!.Value)
                        .ToDictionary(
                            group => group.Key,
                            group => group
                                .SelectMany(value =>
                                    value.Values)
                                .Where(value =>
                                    !string.IsNullOrEmpty(value))
                                .Distinct(
                                    StringComparer.Ordinal)
                                .ToArray()))
                .ToArray();
        foreach (EditableTagField field in Fields)
        {
            if (!completeDocumentRead)
            {
                if (TryGetCompleteCachedValues(
                        selection,
                        cachedRecords,
                        field.Field,
                        out string[][] cachedValues))
                {
                    SetFieldValues(
                        field,
                        cachedValues,
                        FieldValueVerification.Exact);
                    continue;
                }
                field.SetLoaded(
                    [],
                    true,
                    FieldValueVerification.Unverified);
                continue;
            }

            string[][] valuesByFile = maps
                .Select(map =>
                    map.GetValueOrDefault(
                        field.Field) ??
                    [])
                .ToArray();
            SetFieldValues(
                field,
                valuesByFile,
                FieldValueVerification.Exact);
        }
        NotifyUnsavedChangesChanged();
    }

    private static void SetFieldValues(
        EditableTagField field,
        IReadOnlyList<string[]> valuesByFile,
        FieldValueVerification verification)
    {
        if (valuesByFile.Count == 0)
        {
            field.SetLoaded([], false, verification);
            return;
        }
        bool mixed = valuesByFile.Skip(1).Any(values =>
            !values.SequenceEqual(valuesByFile[0], StringComparer.Ordinal));
        mixed |= valuesByFile.Any(values => values.Length > 1);
        string[] displayValues = valuesByFile
            .SelectMany(values => values.Length == 0 ? ["(missing)"] : values)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (displayValues.Length == 1 && displayValues[0] == "(missing)")
            displayValues = [];
        field.SetLoaded(displayValues, mixed, verification);
    }

    private static bool TryGetCompleteCachedValues(
        SelectionContext selection,
        IReadOnlyDictionary<string, TrackRecord>? recordsByPath,
        TagFields field,
        out string[][] values)
    {
        values = [];
        if (recordsByPath is null || !IsCacheBackedField(field))
            return false;
        values = selection.Paths
            .Select(path => CachedValues(recordsByPath[path], field))
            .ToArray();
        return true;
    }

    private static Dictionary<string, TrackRecord>? BuildCompleteRecordMap(SelectionContext selection)
    {
        if (selection.Records is not { } records || records.Count != selection.Paths.Count)
            return null;
        var recordsByPath = records
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return recordsByPath.Count == selection.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
               selection.Paths.All(recordsByPath.ContainsKey)
            ? recordsByPath
            : null;
    }

    private static bool IsCacheBackedField(TagFields field) => field is
        TagFields.Title or TagFields.Artist or TagFields.AlbumArtist or TagFields.Album or
        TagFields.TrackNumber or TagFields.TotalTracks or TagFields.DiscNumber or
        TagFields.TotalDiscs or TagFields.Date;

    private static string[] CachedValues(TrackRecord record, TagFields field)
    {
        string? value = field switch
        {
            TagFields.Title => record.Title,
            TagFields.Artist => record.Artist,
            TagFields.AlbumArtist => record.AlbumArtist,
            TagFields.Album => record.Album,
            TagFields.TrackNumber => record.TrackNumber?.ToString(),
            TagFields.TotalTracks => record.TrackTotal?.ToString(),
            TagFields.DiscNumber => record.DiscNumber?.ToString(),
            TagFields.TotalDiscs => record.DiscTotal?.ToString(),
            TagFields.Date => record.ReleaseDate,
            _ => null,
        };
        return string.IsNullOrEmpty(value) ? [] : [value];
    }

    private bool CanEdit() => HasSelection && !IsBusy;
    private bool CanRevert() => HasUnsavedChanges && !IsBusy;
    private bool CanSaveTags() => CanEdit() && Fields.Any(field => field.IsModified);
    private bool CanEditArtworkSet() => CanEdit() && !IsArtworkMixed;
    private bool CanSaveArtworkSet() => CanEditArtworkSet() &&
        (_artworkSetModified || ArtworkItems.Any(item => item.IsModified));

    [RelayCommand(CanExecute = nameof(CanSaveTags))]
    private async Task SaveTagsAsync()
    {
        TagEdit[] edits = Fields.Where(field => field.IsModified)
            .Select(field => new TagEdit(field.Field, string.IsNullOrWhiteSpace(field.Value) ? null : field.Value))
            .ToArray();
        if (edits.Length == 0)
        {
            StatusTone = MessageTone.Info;
            StatusMessage = "No tag changes to save.";
            return;
        }
        if (_metadataOperations is not null)
        {
            MetadataValueEdit[] valueEdits =
                edits.Select(edit =>
                    new MetadataValueEdit(
                        MetadataFieldKey.Known(
                            edit.Field),
                        edit.Value is null
                            ? []
                            : [edit.Value]))
                    .ToArray();
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>
                editsByPath = Selection.Paths.ToDictionary(
                    path => path,
                    _ => (IReadOnlyList<
                        MetadataValueEdit>)valueEdits,
                    PathComparer);
            await ApplyReviewedPlanAsync(
                "Save tags",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewValueEditsAsync(
                            editsByPath,
                            "Edit Library inspector fields",
                            progress,
                            ct),
                $"Apply {edits.Length:N0} tag change(s) to " +
                $"{Selection.Paths.Count:N0} selected track(s)?");
            return;
        }
        if (Selection.Paths.Count > 1 &&
            !await _dialogs.ConfirmAsync(
                "Save tag changes",
                $"Apply {edits.Length:N0} tag change(s) to {Selection.Paths.Count:N0} selected tracks? " +
                "Only the listed fields will be replaced. This writes the files directly; no recovery journal is created.",
                "Save tags"))
            return;
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start(
            "Save tags", $"Updating {Selection.Paths.Count:N0} track(s)", ShellDestination.Library);
        try
        {
            BatchWriteResult result = await _tags.ApplyAsync(Selection.Paths, edits);
            bool hasFailures = result.FailedCount > 0;
            StatusTone = hasFailures ? MessageTone.Error : MessageTone.Success;
            StatusMessage = hasFailures
                ? $"{result.Summary}. Proposed tag changes remain ready to retry."
                : result.Summary;
            _activities.Finish(activity, StatusMessage,
                result.FailedCount > 0 ? AppActivityState.Failed : AppActivityState.Completed);
            if (result.SavedCount > 0)
                FilesChanged?.Invoke();
            if (!hasFailures)
                await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = error.Message + " Proposed tag changes remain ready to retry.";
            _activities.Finish(activity, StatusMessage, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAllFieldsAsync()
    {
        if (!await _fieldsEditor.ShowAsync(Selection.Paths))
            return;
        StatusTone = MessageTone.Success;
        StatusMessage = "Metadata fields updated.";
        FilesChanged?.Invoke();
        await LoadAsync(Selection);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ReplaceArtworkAsync()
    {
        string? path = await _files.PickFileAsync("Choose cover artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;
        if (_metadataOperations is not null)
        {
            PreparedImage? prepared =
                await _artwork.PrepareFromFileAsync(
                    path,
                    ArtworkMaxDimension);
            if (prepared is null)
            {
                StatusTone = MessageTone.Error;
                StatusMessage =
                    "The selected image could not be prepared " +
                    "for embedding.";
                return;
            }
            var edits = Selection.Paths.ToDictionary(
                musicPath => musicPath,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    new(
                        ID3v2Util.APICType.FrontCover,
                        prepared.MimeType,
                        prepared.Data)),
                PathComparer);
            await ApplyReviewedPlanAsync(
                "Replace artwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkEditsAsync(
                            edits,
                            "Replace Library front cover",
                            progress,
                            ct),
                $"Replace the front cover on " +
                $"{Selection.Paths.Count:N0} selected track(s)?");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Replace artwork",
                $"Replace the front cover on {Selection.Paths.Count:N0} selected track(s)? " +
                "This writes the files directly; no recovery journal is created.",
                "Replace"))
            return;
        await ApplyArtworkAsync("Replace artwork", async musicPath =>
            await _artwork.SetCoverFromFileAsync(musicPath, path, ArtworkMaxDimension));
    }

    [RelayCommand(CanExecute = nameof(CanEditArtworkSet))]
    private async Task AddArtworkAsync()
    {
        string? path = await _files.PickFileAsync("Choose artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = "The selected image could not be prepared for embedding.";
            return;
        }

        ID3v2Util.APICType type = ArtworkItems.Any(item => item.Type == ID3v2Util.APICType.FrontCover)
            ? ID3v2Util.APICType.Other
            : ID3v2Util.APICType.FrontCover;
        object? source = await _thumbnails.CreateImageSourceAsync(prepared.Data);
        var item = new ArtworkPreviewItem(
            source,
            type,
            prepared.MimeType,
            prepared.Data,
            $"{prepared.MimeType} · {prepared.Width:N0} × {prepared.Height:N0} · {FormatBytes(prepared.Data.LongLength)}",
            null);
        item.PropertyChanged += OnArtworkItemChanged;
        ArtworkItems.Add(item);
        ArtworkSource ??= item.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyUnsavedChangesChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public void RemoveArtworkItem(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        item.PropertyChanged -= OnArtworkItemChanged;
        ArtworkItems.Remove(item);
        ArtworkSource = ArtworkItems.FirstOrDefault()?.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyUnsavedChangesChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public async Task ReplaceArtworkItemAsync(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        string? path = await _files.PickFileAsync("Choose replacement artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = "The selected image could not be prepared for embedding.";
            return;
        }

        object? source = await _thumbnails.CreateImageSourceAsync(prepared.Data);
        item.ReplaceContent(
            source,
            prepared.MimeType,
            prepared.Data,
            $"{prepared.MimeType} · {prepared.Width:N0} × {prepared.Height:N0} · {FormatBytes(prepared.Data.LongLength)}");
        if (ReferenceEquals(ArtworkItems.FirstOrDefault(), item))
            ArtworkSource = item.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        StatusTone = MessageTone.Info;
        StatusMessage = "Artwork replacement ready to save.";
        NotifyUnsavedChangesChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public async Task SaveArtworkItemToFileAsync(ArtworkPreviewItem item)
    {
        if (!ArtworkItems.Contains(item) || item.Data.Length == 0)
        {
            StatusTone = MessageTone.Warning;
            StatusMessage = "This artwork has no image data to save.";
            return;
        }

        string extension = ArtworkFileExtension(item.MimeType);
        string sourceName = FileNameWithoutExtension(
            Selection.Paths.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = "artwork";
        string typeName = string.Join('-', item.Label.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int typeCount = ArtworkItems.Count(candidate => candidate.Type == item.Type);
        int typeIndex = ArtworkItems.Take(ArtworkItems.IndexOf(item) + 1)
            .Count(candidate => candidate.Type == item.Type);
        string ordinal = typeCount > 1 ? $"-{typeIndex}" : "";
        string suggestedName = $"{sourceName}-{typeName}{ordinal}{extension}";
        string? path = await _files.SaveFileAsync(
            $"Save {item.Label.ToLowerInvariant()} artwork", suggestedName, extension);
        if (path is null)
            return;

        try
        {
            await File.WriteAllBytesAsync(path, item.Data);
            StatusTone = MessageTone.Success;
            StatusMessage = $"Artwork saved to {path}.";
        }
        catch (Exception error)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = $"Artwork could not be saved: {error.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveArtworkSet))]
    private async Task SaveArtworkSetAsync()
    {
        ArtworkInput[] images = ArtworkItems.Select(item => new ArtworkInput(
            item.Type,
            item.MimeType,
            item.Data,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim())).ToArray();
        if (_metadataOperations is not null)
        {
            var requests = Selection.Paths.ToDictionary(
                path => path,
                _ => new ArtworkSetPreviewRequest(
                    [.. images]),
                PathComparer);
            await ApplyReviewedPlanAsync(
                "Save artwork changes",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkSetsAsync(
                            requests,
                            "Edit Library artwork set",
                            progress,
                            ct),
                $"Replace the embedded artwork set on " +
                $"{Selection.Paths.Count:N0} selected track(s) " +
                $"with these {images.Length:N0} image(s)?");
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                "Save artwork changes",
                $"Replace the embedded artwork set on {Selection.Paths.Count:N0} selected tracks with these {ArtworkItems.Count:N0} image(s)? " +
                "This writes the files directly; no recovery journal is created.",
                "Save"))
            return;

        await ApplyArtworkAsync("Save artwork changes", musicPath =>
            _artwork.SaveImagesAsync(musicPath, images));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ScrubArtworkAsync()
    {
        if (_metadataOperations is not null)
        {
            var requests = new Dictionary<
                string,
                ArtworkSetPreviewRequest>(
                PathComparer);
            foreach (string path in Selection.Paths)
            {
                IReadOnlyList<ArtworkModel> artwork;
                if (_metadataDocuments is not null)
                {
                    MediaDocument loaded =
                        await _metadataDocuments.LoadAsync(
                            path,
                            includeArtwork: true);
                    artwork = loaded.Artwork;
                }
                else
                {
                    OperationResult<MediaFileModel> loaded =
                        await _media.LoadAsync(
                            path,
                            includeArtwork: true);
                    if (!loaded.Success ||
                        loaded.Value is null)
                    {
                        StatusTone = MessageTone.Error;
                        StatusMessage =
                            loaded.Error ??
                            $"Could not read artwork from '{path}'.";
                        return;
                    }
                    artwork = loaded.Value.Artwork;
                }
                requests[path] = new(
                    [
                        .. artwork.Select(
                            (image, index) =>
                                new ArtworkInput(
                                    ArtworkType(
                                        image,
                                        index),
                                    ArtworkMimeType(image),
                                    image.Data,
                                    image.Description)),
                    ],
                    ArtworkMaxDimension);
            }
            await ApplyReviewedPlanAsync(
                "Optimize artwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkSetsAsync(
                            requests,
                            "Optimize Library artwork",
                            progress,
                            ct),
                $"Re-encode and limit artwork to " +
                $"{ArtworkMaxDimension:N0}px on " +
                $"{Selection.Paths.Count:N0} track(s)?");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Optimize artwork",
                $"Re-encode and limit artwork to {ArtworkMaxDimension:N0}px on {Selection.Paths.Count:N0} track(s)? " +
                "This writes the files directly; no recovery journal is created.",
                "Optimize"))
            return;
        await ApplyArtworkAsync("Optimize artwork", async musicPath =>
            await _artwork.ScrubAsync(musicPath, ArtworkMaxDimension));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveArtworkAsync()
    {
        if (_metadataOperations is not null)
        {
            var edits = Selection.Paths.ToDictionary(
                path => path,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.RemoveAll),
                PathComparer);
            await ApplyReviewedPlanAsync(
                "Remove artwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkEditsAsync(
                            edits,
                            "Remove Library artwork",
                            progress,
                            ct),
                $"Remove all embedded artwork from " +
                $"{Selection.Paths.Count:N0} selected track(s)?");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Remove artwork",
                $"Remove all embedded artwork from {Selection.Paths.Count:N0} selected track(s)? " +
                "This writes the files directly; no recovery journal is created.",
                "Remove"))
            return;
        await ApplyArtworkAsync("Remove artwork", async musicPath =>
            await _artwork.RemoveAsync(musicPath));
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Revert unsaved metadata changes?",
                $"{UnsavedChangesSummary} for {SelectionSummary}. Revert to the values currently stored in the files?",
                "Revert changes"))
            return;
        await LoadAsync(Selection);
    }

    private async Task ApplyReviewedPlanAsync(
        string title,
        Func<
            IProgress<OperationProgress>,
            CancellationToken,
            Task<MetadataOperationPlan>> preview,
        string confirmation)
    {
        if (_metadataOperations is null)
            throw new InvalidOperationException(
                "The shared metadata operation service is unavailable.");
        IsBusy = true;
        NotifyCommands();
        _editCancellation =
            new CancellationTokenSource();
        Guid activity = _activities.Start(
            title,
            $"Previewing {Selection.Paths.Count:N0} track(s)",
            ShellDestination.Library,
            _editCancellation.Cancel);
        var progress =
            new Progress<OperationProgress>(update =>
            {
                double? fraction =
                    update.Total is > 0
                        ? Math.Clamp(
                            (double)update.Completed /
                            update.Total.Value,
                            0,
                            1)
                        : null;
                _activities.Report(
                    activity,
                    update.Message ??
                    update.CurrentPath ??
                    title,
                    fraction);
            });
        try
        {
            MetadataOperationPlan plan =
                await preview(
                    progress,
                    _editCancellation.Token);
            OperationIssue[] blockers = plan.Files
                .SelectMany(file => file.Issues)
                .Where(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker)
                .ToArray();
            if (!plan.CanApply)
            {
                StatusTone = blockers.Length > 0
                    ? MessageTone.Error
                    : MessageTone.Info;
                StatusMessage = blockers.Length > 0
                    ? $"Preview found {blockers.Length:N0} " +
                      $"blocker(s): {blockers[0].Message} " +
                      "No files were changed."
                    : "Preview found no applicable changes. " +
                      "No files were changed.";
                _activities.Finish(
                    activity,
                    StatusMessage,
                    blockers.Length > 0
                        ? AppActivityState.Failed
                        : AppActivityState.Completed);
                return;
            }
            if (!await _dialogs.ConfirmAsync(
                    $"Apply reviewed {title.ToLowerInvariant()}?",
                    confirmation + " " +
                    $"The reviewed plan changes " +
                    $"{plan.ChangedFileCount:N0} file(s) and " +
                    "uses stale-file checks, recovery journals, " +
                    "and undo.",
                    "Apply"))
            {
                StatusTone = MessageTone.Info;
                StatusMessage =
                    "Reviewed changes were not applied.";
                _activities.Finish(
                    activity,
                    StatusMessage,
                    AppActivityState.Cancelled);
                return;
            }
            MetadataApplyResult result =
                await _metadataOperations.ApplyAsync(
                    plan,
                    progress,
                    _editCancellation.Token);
            if (result.ChangedFiles > 0)
                FilesChanged?.Invoke();
            await LoadAsync(Selection);
            StatusTone = MessageTone.Success;
            StatusMessage =
                $"Updated {result.ChangedFiles:N0} track(s). " +
                "Originals are retained for undo.";
            _activities.Finish(
                activity,
                StatusMessage,
                AppActivityState.Completed);
        }
        catch (OperationCanceledException) when (
            _editCancellation.IsCancellationRequested)
        {
            StatusTone = MessageTone.Warning;
            StatusMessage =
                "Operation cancelled. Proposed changes remain " +
                "ready to retry.";
            _activities.Finish(
                activity,
                StatusMessage,
                AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            StatusTone = MessageTone.Error;
            StatusMessage =
                error.Message +
                " Proposed changes remain ready to retry.";
            _activities.Finish(
                activity,
                StatusMessage,
                AppActivityState.Failed);
        }
        finally
        {
            _editCancellation.Dispose();
            _editCancellation = null;
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ApplyArtworkAsync(string title, Func<string, Task<ArtworkOpResult>> apply)
    {
        bool hasRetryableChanges = HasPendingArtworkChanges ||
            ArtworkItems.Any(item => item.IsModified);
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start(
            title, $"Updating {Selection.Paths.Count:N0} track(s)", ShellDestination.Library);
        int saved = 0;
        string? firstError = null;
        try
        {
            foreach (string path in Selection.Paths)
            {
                ArtworkOpResult result = await apply(path);
                if (result.Success)
                    saved++;
                else
                    firstError ??= result.Error;
                _activities.Report(activity, $"{saved:N0} of {Selection.Paths.Count:N0} updated",
                    (double)saved / Selection.Paths.Count);
            }
            StatusMessage = saved == Selection.Paths.Count
                ? $"Updated artwork on {saved:N0} track(s)."
                : $"Updated {saved:N0} of {Selection.Paths.Count:N0}. {firstError}" +
                  (hasRetryableChanges ? " Proposed artwork changes remain ready to retry." : "");
            StatusTone = saved == Selection.Paths.Count ? MessageTone.Success : MessageTone.Error;
            _activities.Finish(activity, StatusMessage,
                saved == Selection.Paths.Count ? AppActivityState.Completed : AppActivityState.Failed);
            if (saved > 0)
                FilesChanged?.Invoke();
            if (saved == Selection.Paths.Count)
                await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = error.Message + (hasRetryableChanges
                ? " Proposed artwork changes remain ready to retry."
                : "");
            _activities.Finish(activity, StatusMessage, AppActivityState.Failed);
            if (saved > 0)
                FilesChanged?.Invoke();
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        SaveTagsCommand.NotifyCanExecuteChanged();
        EditAllFieldsCommand.NotifyCanExecuteChanged();
        ReplaceArtworkCommand.NotifyCanExecuteChanged();
        AddArtworkCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
        ScrubArtworkCommand.NotifyCanExecuteChanged();
        RemoveArtworkCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditableTagField.IsModified))
            return;
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
    }

    private void OnArtworkItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyUnsavedChangesChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUnsavedChangesChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedChangesSummary));
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void ClearArtworkItems()
    {
        foreach (ArtworkPreviewItem item in ArtworkItems)
            item.PropertyChanged -= OnArtworkItemChanged;
        ArtworkItems.Clear();
    }

    private static bool SameSelection(SelectionContext left, SelectionContext right) =>
        left.Paths.Count == right.Paths.Count &&
        left.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private void UpdateArtworkSummary()
    {
        ArtworkSummary = ArtworkItems.Count switch
        {
            0 => "No embedded artwork.",
            1 => ArtworkItems[0].Summary,
            _ => $"{ArtworkItems.Count:N0} embedded artworks",
        };
        if (Selection.Paths.Count > 1 && ArtworkItems.Count > 0)
            ArtworkSummary += $" · shared by {Selection.Paths.Count:N0} tracks";
    }

    private static string DescribeOverview(
        SelectionContext selection,
        IReadOnlyList<MediaFileModel> loadedModels)
    {
        int count = selection.Paths.Count;
        Dictionary<string, string?> codecsByPath = (selection.Records is { Count: > 0 }
                ? selection.Records.Select(record => (record.Path, record.CodecName))
                : loadedModels.Select(model => (model.Path, model.Codec?.CodecName)))
            .GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Item2, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> fileFormats = selection.Paths.Select(path =>
            FormatFileFormat(path, codecsByPath.GetValueOrDefault(path)));
        string[] knownTagFormats = (selection.Records is { Count: > 0 }
            ? selection.Records.Select(record => NormalizeTagFormat(record.TagType))
            : loadedModels.Select(model => NormalizeTagFormat(model.TagType)))
            .Take(count)
            .ToArray();
        IEnumerable<string> tagFormats = knownTagFormats.Concat(
            Enumerable.Repeat("Unknown", Math.Max(0, count - knownTagFormats.Length)));
        string scope = count == 1 ? "1 track selected" : $"{count:N0} tracks selected";

        return $"{scope}{Environment.NewLine}{Environment.NewLine}" +
               $"File formats{Environment.NewLine}{FormatDistribution(fileFormats, count)}" +
               $"{Environment.NewLine}{Environment.NewLine}Tag formats{Environment.NewLine}" +
               FormatDistribution(tagFormats, count);
    }

    private static string DescribeOverview(
        SelectionContext selection,
        IReadOnlyList<MediaDocument> documents)
    {
        int count = selection.Paths.Count;
        Dictionary<string, string?> codecsByPath =
            (selection.Records is { Count: > 0 }
                ? selection.Records.Select(record =>
                    (record.Path, record.CodecName))
                : documents.Select(document =>
                    (
                        document.Path,
                        document.Codec?.CodecName)))
            .GroupBy(
                value => value.Path,
                PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group.First().Item2,
                PathComparer);
        IEnumerable<string> fileFormats =
            selection.Paths.Select(path =>
                FormatFileFormat(
                    path,
                    codecsByPath.GetValueOrDefault(path)));
        string[] knownTagFormats =
            (selection.Records is { Count: > 0 }
                ? selection.Records.Select(record =>
                    NormalizeTagFormat(
                        record.TagType))
                : documents.Select(document =>
                    NormalizeTagFormat(
                        document.TagLayers
                            .FirstOrDefault()?.TagType)))
            .Take(count)
            .ToArray();
        IEnumerable<string> tagFormats =
            knownTagFormats.Concat(
                Enumerable.Repeat(
                    "Unknown",
                    Math.Max(
                        0,
                        count -
                        knownTagFormats.Length)));
        string scope = count == 1
            ? "1 track selected"
            : $"{count:N0} tracks selected";

        return $"{scope}{Environment.NewLine}" +
               $"{Environment.NewLine}File formats" +
               $"{Environment.NewLine}" +
               $"{FormatDistribution(fileFormats, count)}" +
               $"{Environment.NewLine}{Environment.NewLine}" +
               $"Tag formats{Environment.NewLine}" +
               FormatDistribution(tagFormats, count);
    }

    private static string ArtworkFileExtension(string? mimeType) =>
        mimeType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            "image/tiff" => ".tif",
            "image/avif" => ".avif",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            _ => ".bin",
        };

    private static string FileNameWithoutExtension(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        int separator = path.LastIndexOfAny(['\\', '/']);
        return Path.GetFileNameWithoutExtension(path[(separator + 1)..]);
    }

    private static string FormatFileFormat(string path, string? codec)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            return "Unknown";
        bool isMp4Family = extension is "MP4" or "M4A" or "M4P" or "M4R";
        return isMp4Family && !string.IsNullOrWhiteSpace(codec)
            ? $"{extension} ({codec})"
            : extension;
    }

    private static string NormalizeTagFormat(string? value) => value switch
    {
        null or "" => "Unknown",
        "Vorbis" => "Vorbis comments",
        _ => value,
    };

    private static string FormatDistribution(IEnumerable<string> values, int total)
    {
        string[] lines = values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count():N0} " +
                $"({(100d * group.Count() / total).ToString("0", CultureInfo.InvariantCulture)}%)")
            .ToArray();
        return lines.Length == 0 ? "Unknown: 0" : string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        >= 1024 => $"{bytes / 1024d:N0} KB",
        _ => $"{bytes:N0} bytes",
    };

    private static ID3v2Util.APICType ArtworkType(ArtworkModel image, int index) =>
        Enum.TryParse(image.Category, true, out ID3v2Util.APICType type)
            ? type
            : index == 0 ? ID3v2Util.APICType.FrontCover : ID3v2Util.APICType.Other;

    private static string ArtworkMimeType(ArtworkModel image)
    {
        if (string.IsNullOrWhiteSpace(image.ImageType))
            return "image/jpeg";
        if (image.ImageType.Contains('/'))
            return image.ImageType;
        string subtype = string.Equals(image.ImageType, "jpg", StringComparison.OrdinalIgnoreCase)
            ? "jpeg"
            : image.ImageType;
        return $"image/{subtype.ToLowerInvariant()}";
    }

    private static string ArtworkDetails(ArtworkModel image)
    {
        return $"{image.ImageType ?? "image"} · {image.Width:N0} × {image.Height:N0} · {FormatBytes(image.Size)}";
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
