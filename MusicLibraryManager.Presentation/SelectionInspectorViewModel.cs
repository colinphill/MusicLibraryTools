using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
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
    private readonly ILocalizationService? _localization;
    private readonly Dictionary<
        ArtworkPreviewItem,
        Func<string>> _artworkSummaryFactories = [];
    private int _generation;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _editCancellation;
    private bool _artworkSetModified;
    private string? _statusMessageKey;
    private object?[] _statusMessageArguments = [];
    private long? _statusMessageCount;
    private Func<string>? _overviewFactory;

    [ObservableProperty] private SelectionContext _selection = SelectionContext.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _overview = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfo))]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageTone _statusTone = MessageTone.Info;
    [ObservableProperty] private object? _artworkSource;
    [ObservableProperty] private bool _isArtworkMixed;
    [ObservableProperty] private string _artworkSummary = "";
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
        IMetadataDocumentService? metadataDocuments = null,
        ILocalizationService? localization = null)
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
        _localization = localization;
        Overview = L(
            "Inspector.Overview.SelectTrack");
        ArtworkSummary = L(
            "Inspector.Artwork.NotLoaded");
        foreach (var (field, labelKey) in FieldDefinitions)
        {
            var item = new EditableTagField(
                field,
                L(labelKey),
                labelKey);
            item.RefreshLocalizedText(L);
            item.PropertyChanged += OnFieldChanged;
            Fields.Add(item);
        }
        RefreshLocalizedChoices();
        _localization?.CultureChanged +=
            OnLocalizationCultureChanged;
    }

    public ObservableCollection<EditableTagField> Fields { get; } = [];
    public ObservableCollection<ArtworkPreviewItem> ArtworkItems { get; } = [];
    public IReadOnlyList<ID3v2Util.APICType> ArtworkTypes { get; } =
        Enum.GetValues<ID3v2Util.APICType>();
    public ObservableCollection<
        LocalizedChoice<ID3v2Util.APICType>>
        ArtworkTypeChoices { get; } = [];
    public bool HasSelection => Selection.HasSelection;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            StatusDiagnosticDetail);
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
    public int PendingChangesVersion { get; private set; }
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
                (0, false) => L(
                    "Inspector.Unsaved.None"),
                (_, false) => LC(
                    "Inspector.Unsaved.TagChanges",
                    tagCount),
                (0, true) => L(
                    "Inspector.Unsaved.Artwork"),
                _ => LC(
                    "Inspector.Unsaved.TagsAndArtwork",
                    tagCount),
            };
        }
    }
    public event Action? FilesChanged;

    public IReadOnlyList<MetadataPreviewRow>
        CreatePendingChangeRows()
    {
        var rows = new List<MetadataPreviewRow>();
        foreach (string path in Selection.Paths)
        {
            foreach (EditableTagField field in
                     Fields.Where(field => field.IsModified))
            {
                rows.Add(new(
                    Path.GetFileName(path),
                    MetadataFieldKey.Known(
                        field.Field).DisplayName,
                    field.OriginalDisplayValue,
                    string.IsNullOrWhiteSpace(field.Value)
                        ? ""
                        : field.Value));
            }

            if (HasArtworkSetEdits())
            {
                rows.Add(new(
                    Path.GetFileName(path),
                    L("Inspector.Pending.Artwork"),
                    L(
                        "Inspector.Pending.CurrentArtworkSet"),
                    ArtworkItems.Count == 0
                        ? L("Inspector.Common.None")
                        : LC(
                            "Inspector.Pending.Images",
                            ArtworkItems.Count)));
            }
        }
        return rows;
    }

    public async Task<MetadataOperationPlan?>
        PreviewPendingChangesAsync(
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
    {
        if (!HasUnsavedChanges)
            return null;
        (IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>? valueEdits,
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>? artworkEdits) =
            CreatePendingOperationInputs();
        if (valueEdits is null && artworkEdits is null)
            return null;
        return await PreviewInspectorChangesAsync(
            valueEdits,
            artworkEdits,
            progress,
            cancellationToken);
    }

    public Task DiscardPendingChangesAsync() =>
        HasUnsavedChanges
            ? LoadAsync(Selection)
            : Task.CompletedTask;

    internal (
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>? ValueEdits,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>? ArtworkEdits)
        CreatePendingOperationInputs()
    {
        MetadataValueEdit[] valueEdits = Fields
            .Where(field => field.IsModified)
            .Select(field => new MetadataValueEdit(
                MetadataFieldKey.Known(field.Field),
                string.IsNullOrWhiteSpace(field.Value)
                    ? []
                    : [field.Value]))
            .ToArray();
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>?
            editsByPath = valueEdits.Length == 0
                ? null
                : Selection.Paths.ToDictionary(
                    path => path,
                    _ => (IReadOnlyList<
                        MetadataValueEdit>)valueEdits,
                    PathComparer);
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>?
            artworkByPath = !CanEditArtworkSet() ||
                            !HasArtworkSetEdits()
                ? null
                : Selection.Paths.ToDictionary(
                    path => path,
                    _ => new ArtworkSetPreviewRequest(
                        [.. CurrentArtworkInputs()]),
                    PathComparer);
        return (editsByPath, artworkByPath);
    }

    public void ReportArtworkPreviewUnavailable()
    {
        SetStatus(
            MessageTone.Warning,
            "Inspector.Status.ArtworkPreviewUnavailable");
    }

    private static readonly (
        TagFields Field,
        string LabelKey)[] FieldDefinitions =
    [
        (TagFields.Title, "Inspector.Field.Title"),
        (TagFields.Artist, "Inspector.Field.Artist"),
        (TagFields.AlbumArtist, "Inspector.Field.AlbumArtist"),
        (TagFields.Album, "Inspector.Field.Album"),
        (TagFields.TrackNumber, "Inspector.Field.Track"),
        (TagFields.TotalTracks, "Inspector.Field.TrackTotal"),
        (TagFields.DiscNumber, "Inspector.Field.Disc"),
        (TagFields.TotalDiscs, "Inspector.Field.DiscTotal"),
        (TagFields.Date, "Inspector.Field.ReleaseDate"),
        (TagFields.Genre, "Inspector.Field.Genre"),
        (TagFields.Composer, "Inspector.Field.Composer"),
        (TagFields.Comment, "Inspector.Field.Comment"),
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
                L("Inspector.Dialog.Discard.Title"),
                LF(
                    "Inspector.Dialog.Discard.Message",
                    UnsavedChangesSummary,
                    SelectionSummary),
                L("Inspector.Dialog.Discard.Confirm")))
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
        ClearStatus();
        ArtworkSource = null;
        ClearArtworkItems();
        _artworkSetModified = false;
        HasPendingArtworkChanges = false;
        IsArtworkMixed = false;
        ArtworkSummary = L(
            "Inspector.Artwork.NoneEmbedded");
        foreach (EditableTagField field in Fields)
            field.SetLoaded([], false);
        NotifyUnsavedChangesChanged();
        NotifyCommands();

        if (!selection.HasSelection)
        {
            _overviewFactory = () => L(
                "Inspector.Overview.SelectTrack");
            Overview = _overviewFactory();
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
                        SetStatus(
                            MessageTone.Warning,
                            "Inspector.Status.ReadFailed");
                        StatusDiagnosticDetail =
                            LF(
                                "Inspector.Diagnostic.PathError",
                                path,
                                error.Message);
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
                _overviewFactory = () =>
                    DescribeOverview(
                        selection,
                        documents);
            }
            else
            {
                LoadFields(models, selection);
                _overviewFactory = () =>
                    DescribeOverview(
                        selection,
                        models);
            }
            if (selection.Paths.Count > MaxCommonValueSample)
            {
                Func<string> baseOverview =
                    _overviewFactory;
                _overviewFactory = () =>
                    baseOverview() +
                    Environment.NewLine +
                    Environment.NewLine +
                    L(
                        "Inspector.Overview.LargeSelectionNote");
            }
            Overview = _overviewFactory();

            MediaDocument? directlyLoadedArtwork = null;
            IReadOnlyList<string> artworkSignatures;
            if (selection.ReadArtworkDirectly && _metadataDocuments is not null)
            {
                var signatures = new List<string>(selection.Paths.Count);
                foreach (string path in selection.Paths)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    MediaDocument document = await _metadataDocuments.LoadAsync(
                        path, includeArtwork: true, cancellation.Token);
                    directlyLoadedArtwork ??= document;
                    signatures.Add(ArtworkSignature(document.Artwork));
                }
                artworkSignatures = signatures;
            }
            else
            {
                artworkSignatures = await _library.GetImageSignaturesAsync(
                    selection.Paths, cancellation.Token);
            }
            string[] distinctArtwork = artworkSignatures.Distinct(StringComparer.Ordinal).ToArray();
            IsArtworkMixed = distinctArtwork.Length > 1;
            if (IsArtworkMixed)
            {
                ArtworkSummary = L(
                    "Inspector.Artwork.Mixed");
                return;
            }

            if (distinctArtwork.Length == 0 || string.IsNullOrEmpty(distinctArtwork[0]))
                return;

            ArtworkModel[] embeddedArtwork;
            if (directlyLoadedArtwork is not null)
            {
                embeddedArtwork = directlyLoadedArtwork.Artwork.ToArray();
            }
            else if (_metadataDocuments is not null)
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
                preview.RefreshLocalizedText(
                    ArtworkTypeLabel);
                _artworkSummaryFactories[preview] =
                    () => ArtworkDetails(image);
                preview.PropertyChanged += OnArtworkItemChanged;
                ArtworkItems.Add(preview);
            }
            ArtworkSource = ArtworkItems
                .FirstOrDefault(item => item.Source is not null)
                ?.Source;
            ArtworkSummary = embeddedArtwork.Length == 1
                ? ArtworkItems[0].Summary
                : LC(
                    "Inspector.Artwork.Embedded",
                    embeddedArtwork.Length);
            if (invalidArtwork > 0)
            {
                SetCountStatus(
                    MessageTone.Warning,
                    "Inspector.Status.InvalidArtwork",
                    invalidArtwork);
            }
            if (selection.Paths.Count > 1)
                ArtworkSummary += LC(
                    "Inspector.Artwork.Shared",
                    selection.Paths.Count);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatusFailure(
                "Inspector.Status.LoadFailed",
                error.Message);
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
            models.Select(model => model.Path).ToHashSet(PathComparer)
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
            .SelectMany(values => values)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
            .GroupBy(record => record.Path, PathComparer)
            .ToDictionary(group => group.Key, group => group.First(), PathComparer);
        return recordsByPath.Count == selection.Paths.Distinct(PathComparer).Count() &&
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
    private bool CanSaveTags() => CanEdit() &&
        (Fields.Any(field => field.IsModified) ||
         CanEditArtworkSet() && HasArtworkSetEdits());
    private bool CanEditArtworkSet() => CanEdit() && !IsArtworkMixed;
    private bool CanSaveArtworkSet() => CanEditArtworkSet() &&
        HasArtworkSetEdits();
    private bool HasArtworkSetEdits() =>
        _artworkSetModified ||
        ArtworkItems.Any(item => item.IsModified);

    [RelayCommand(CanExecute = nameof(CanSaveTags))]
    private async Task SaveTagsAsync()
    {
        TagEdit[] edits = Fields.Where(field => field.IsModified)
            .Select(field => new TagEdit(field.Field, string.IsNullOrWhiteSpace(field.Value) ? null : field.Value))
            .ToArray();
        bool artworkChanged =
            CanEditArtworkSet() &&
            HasArtworkSetEdits();
        ArtworkInput[] images = artworkChanged
            ? CurrentArtworkInputs()
            : [];
        if (edits.Length == 0 && !artworkChanged)
        {
            SetStatus(
                MessageTone.Info,
                "Inspector.Status.NoChanges");
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
            var editsByPath = (IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>?)
                (edits.Length == 0
                    ? null
                    : Selection.Paths.ToDictionary(
                        path => path,
                        _ => (IReadOnlyList<
                            MetadataValueEdit>)valueEdits,
                        PathComparer));
            var artworkByPath = (IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>?)
                (!artworkChanged
                    ? null
                    : Selection.Paths.ToDictionary(
                        path => path,
                        _ => new ArtworkSetPreviewRequest(
                            [.. images]),
                        PathComparer));
            await ApplyReviewedPlanAsync(
                "Inspector.Activity.SaveTags",
                (progress, ct) =>
                    PreviewInspectorChangesAsync(
                        editsByPath,
                        artworkByPath,
                        progress,
                        ct),
                DescribeSaveConfirmation(
                    edits.Length,
                    artworkChanged,
                    images.Length));
            return;
        }
        if ((Selection.Paths.Count > 1 ||
             artworkChanged) &&
            !await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.SaveTags.Title"),
                DescribeSaveConfirmation(
                    edits.Length,
                    artworkChanged,
                    images.Length) +
                " " +
                L("Inspector.Dialog.DirectWriteWarning"),
                L("Inspector.Dialog.SaveTags.Confirm")))
            return;
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start(
            L("Inspector.Activity.SaveTags"),
            LC(
                "Inspector.Activity.UpdatingTracks",
                Selection.Paths.Count),
            ShellDestination.Library);
        try
        {
            BatchWriteResult? tagResult =
                edits.Length == 0
                    ? null
                    : await _tags.ApplyAsync(
                        Selection.Paths,
                        edits);
            int artworkSaved = 0;
            string? artworkError = null;
            if (artworkChanged)
            {
                foreach (string path in Selection.Paths)
                {
                    ArtworkOpResult result =
                        await _artwork.SaveImagesAsync(
                            path,
                            images);
                    if (result.Success)
                        artworkSaved++;
                    else
                        artworkError ??= result.Error;
                }
            }
            bool hasFailures =
                tagResult?.FailedCount > 0 ||
                artworkChanged &&
                artworkSaved != Selection.Paths.Count;
            if (hasFailures)
            {
                SetStatus(
                    MessageTone.Error,
                    "Inspector.Status.SavePartialFailure");
                StatusDiagnosticDetail = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        tagResult?.FailedCount > 0
                            ? tagResult.Summary
                            : null,
                        artworkError,
                    }.Where(value =>
                        !string.IsNullOrWhiteSpace(value)));
            }
            else if (artworkChanged && edits.Length > 0)
            {
                SetCountStatus(
                    MessageTone.Success,
                    "Inspector.Status.UpdatedTagsAndArtwork",
                    Selection.Paths.Count);
            }
            else if (artworkChanged)
            {
                SetCountStatus(
                    MessageTone.Success,
                    "Inspector.Status.UpdatedArtwork",
                    Selection.Paths.Count);
            }
            else
            {
                SetStatus(
                    MessageTone.Success,
                    "Inspector.Status.UpdatedTagsSummary",
                    tagResult!.SavedCount,
                    tagResult.SkippedCount,
                    tagResult.FailedCount,
                    tagResult.CacheFailedCount);
            }
            _activities.Finish(activity, StatusMessage!,
                hasFailures
                    ? AppActivityState.Failed
                    : AppActivityState.Completed);
            if ((tagResult?.SavedCount ?? 0) > 0 ||
                artworkSaved > 0)
                FilesChanged?.Invoke();
            if (!hasFailures)
                await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            SetStatusFailure(
                "Inspector.Status.SaveFailed",
                error.Message);
            _activities.Finish(
                activity,
                StatusMessage!,
                AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task<MetadataOperationPlan>
        PreviewInspectorChangesAsync(
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>?
                editsByPath,
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>?
                artworkByPath,
            IProgress<OperationProgress> progress,
            CancellationToken ct)
    {
        if (_metadataOperations is null)
            throw new InvalidOperationException(
                L("Inspector.Error.MetadataServiceUnavailable"));
        if (editsByPath is null)
            return await _metadataOperations
                .PreviewArtworkSetsAsync(
                    artworkByPath!,
                    L(
                        "Inspector.Operation.EditArtwork"),
                    progress,
                    ct);
        if (artworkByPath is null)
            return await _metadataOperations
                .PreviewValueEditsAsync(
                    editsByPath,
                    L(
                        "Inspector.Operation.EditFields"),
                    progress,
                    ct);

        MetadataOperationPlan fields =
            await _metadataOperations
                .PreviewValueEditsAsync(
                    editsByPath,
                    L(
                        "Inspector.Operation.EditFields"),
                    progress,
                    ct);
        MetadataOperationPlan artwork =
            await _metadataOperations
                .PreviewArtworkSetsAsync(
                    artworkByPath,
                    L(
                        "Inspector.Operation.EditArtwork"),
                    progress,
                    ct);
        Dictionary<string, MetadataFilePlan>
            fieldsByPath = fields.Files.ToDictionary(
                file => file.Path,
                PathComparer);
        Dictionary<string, MetadataFilePlan>
            artworkPlansByPath = artwork.Files.ToDictionary(
                file => file.Path,
                PathComparer);
        var combined = new List<MetadataFilePlan>(
            Selection.Paths.Count);
        foreach (string path in Selection.Paths)
        {
            MetadataFilePlan fieldPlan =
                fieldsByPath[path];
            MetadataFilePlan artworkPlan =
                artworkPlansByPath[path];
            var issues = fieldPlan.Issues
                .Concat(artworkPlan.Issues)
                .Distinct()
                .ToList();
            combined.Add(new(
                path,
                artworkPlan.Snapshot,
                fieldPlan.Differences,
                fieldPlan.Edits,
                [.. issues],
                artworkPlan.ArtworkEdit,
                artworkPlan.ArtworkDifference));
        }
        progress.Report(new(
            OperationPhase.Completed,
            combined.Count,
            combined.Count,
            Message:
                LC(
                    "Inspector.Progress.PreviewedCombined",
                    combined.Count)));
        return new(
            Guid.NewGuid(),
            L("Inspector.Operation.EditTagsAndArtwork"),
            [.. combined],
            DateTimeOffset.UtcNow);
    }

    private string DescribeSaveConfirmation(
        int tagChanges,
        bool artworkChanged,
        int artworkCount) =>
        (tagChanges > 0, artworkChanged) switch
        {
            (true, true) =>
                LF(
                    "Inspector.Dialog.SaveConfirmation.TagsAndArtwork",
                    tagChanges,
                    artworkCount,
                    Selection.Paths.Count),
            (true, false) =>
                LF(
                    "Inspector.Dialog.SaveConfirmation.TagsOnly",
                    tagChanges,
                    Selection.Paths.Count),
            _ =>
                LF(
                    "Inspector.Dialog.SaveConfirmation.ArtworkOnly",
                    Selection.Paths.Count,
                    artworkCount),
        };

    private ArtworkInput[] CurrentArtworkInputs() =>
        ArtworkItems.Select(item =>
            new ArtworkInput(
                item.Type,
                item.MimeType,
                item.Data,
                string.IsNullOrWhiteSpace(
                    item.Description)
                    ? null
                    : item.Description.Trim()))
            .ToArray();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAllFieldsAsync()
    {
        if (!await _fieldsEditor.ShowAsync(Selection.Paths))
            return;
        SetStatus(
            MessageTone.Success,
            "Inspector.Status.MetadataFieldsUpdated");
        FilesChanged?.Invoke();
        await LoadAsync(Selection);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ReplaceArtworkAsync()
    {
        string? path = await _files.PickFileAsync(
            L("Inspector.Picker.ChooseCoverArtwork"),
            [new FilePickerType(
                L("Inspector.Picker.Images"),
                [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
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
                SetStatus(
                    MessageTone.Error,
                    "Inspector.Status.ImagePreparationFailed");
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
                "Inspector.Activity.ReplaceArtwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkEditsAsync(
                            edits,
                            L(
                                "Inspector.Operation.ReplaceFrontCover"),
                            progress,
                            ct),
                LC(
                    "Inspector.Dialog.ReplaceFrontCover.Message",
                    Selection.Paths.Count));
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.ReplaceArtwork.Title"),
                LC(
                    "Inspector.Dialog.ReplaceFrontCover.DirectMessage",
                    Selection.Paths.Count),
                L("Inspector.Dialog.ReplaceArtwork.Confirm")))
            return;
        await ApplyArtworkAsync(
            "Inspector.Activity.ReplaceArtwork",
            async musicPath =>
            await _artwork.SetCoverFromFileAsync(musicPath, path, ArtworkMaxDimension));
    }

    [RelayCommand(CanExecute = nameof(CanEditArtworkSet))]
    private async Task AddArtworkAsync()
    {
        string? path = await _files.PickFileAsync(
            L("Inspector.Picker.ChooseArtwork"),
            [new FilePickerType(
                L("Inspector.Picker.Images"),
                [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            SetStatus(
                MessageTone.Error,
                "Inspector.Status.ImagePreparationFailed");
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
            PreparedArtworkDetails(prepared),
            null);
        item.RefreshLocalizedText(
            ArtworkTypeLabel);
        _artworkSummaryFactories[item] =
            () => PreparedArtworkDetails(prepared);
        item.PropertyChanged += OnArtworkItemChanged;
        ArtworkItems.Add(item);
        ArtworkSource ??= item.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public void RemoveArtworkItem(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        item.PropertyChanged -= OnArtworkItemChanged;
        _artworkSummaryFactories.Remove(item);
        ArtworkItems.Remove(item);
        ArtworkSource = ArtworkItems.FirstOrDefault()?.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public async Task ReplaceArtworkItemAsync(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        string? path = await _files.PickFileAsync(
            L("Inspector.Picker.ChooseReplacementArtwork"),
            [new FilePickerType(
                L("Inspector.Picker.Images"),
                [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            SetStatus(
                MessageTone.Error,
                "Inspector.Status.ImagePreparationFailed");
            return;
        }

        object? source = await _thumbnails.CreateImageSourceAsync(prepared.Data);
        item.ReplaceContent(
            source,
            prepared.MimeType,
            prepared.Data,
            PreparedArtworkDetails(prepared));
        _artworkSummaryFactories[item] =
            () => PreparedArtworkDetails(prepared);
        if (ReferenceEquals(ArtworkItems.FirstOrDefault(), item))
            ArtworkSource = item.Source;
        _artworkSetModified = true;
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyPendingChangeRowsChanged();
        SetStatus(
            MessageTone.Info,
            "Inspector.Status.ArtworkReplacementReady");
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public async Task SaveArtworkItemToFileAsync(ArtworkPreviewItem item)
    {
        if (!ArtworkItems.Contains(item) || item.Data.Length == 0)
        {
            SetStatus(
                MessageTone.Warning,
                "Inspector.Status.NoArtworkData");
            return;
        }

        string extension = ArtworkFileExtension(item.MimeType);
        string sourceName = FileNameWithoutExtension(
            Selection.Paths.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = L(
                "Inspector.Artwork.DefaultFileName");
        string typeName = string.Join('-', item.Label.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int typeCount = ArtworkItems.Count(candidate => candidate.Type == item.Type);
        int typeIndex = ArtworkItems.Take(ArtworkItems.IndexOf(item) + 1)
            .Count(candidate => candidate.Type == item.Type);
        string ordinal = typeCount > 1 ? $"-{typeIndex}" : "";
        string suggestedName = $"{sourceName}-{typeName}{ordinal}{extension}";
        string? path = await _files.SaveFileAsync(
            LF(
                "Inspector.Picker.SaveArtwork",
                item.Label),
            suggestedName,
            extension);
        if (path is null)
            return;

        try
        {
            await File.WriteAllBytesAsync(path, item.Data);
            SetStatus(
                MessageTone.Success,
                "Inspector.Status.ArtworkSaved",
                path);
        }
        catch (Exception error)
        {
            SetStatusFailure(
                "Inspector.Status.ArtworkSaveFailed",
                error.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveArtworkSet))]
    private async Task SaveArtworkSetAsync()
    {
        ArtworkInput[] images =
            CurrentArtworkInputs();
        if (_metadataOperations is not null)
        {
            var requests = Selection.Paths.ToDictionary(
                path => path,
                _ => new ArtworkSetPreviewRequest(
                    [.. images]),
                PathComparer);
            await ApplyReviewedPlanAsync(
                "Inspector.Activity.SaveArtworkChanges",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkSetsAsync(
                            requests,
                            L(
                                "Inspector.Operation.EditLibraryArtworkSet"),
                            progress,
                            ct),
                LF(
                    "Inspector.Dialog.SaveArtworkSet.Message",
                    Selection.Paths.Count,
                    images.Length));
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.SaveArtworkSet.Title"),
                LF(
                    "Inspector.Dialog.SaveArtworkSet.DirectMessage",
                    Selection.Paths.Count,
                    ArtworkItems.Count),
                L("Common.Save")))
            return;

        await ApplyArtworkAsync(
            "Inspector.Activity.SaveArtworkChanges",
            musicPath =>
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
                        SetStatusFailure(
                            "Inspector.Status.ReadArtworkFailed",
                            loaded.Error ??
                            path);
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
                "Inspector.Activity.OptimizeArtwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkSetsAsync(
                            requests,
                            L(
                                "Inspector.Operation.OptimizeLibraryArtwork"),
                            progress,
                            ct),
                LF(
                    "Inspector.Dialog.OptimizeArtwork.Message",
                    ArtworkMaxDimension,
                    Selection.Paths.Count));
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.OptimizeArtwork.Title"),
                LF(
                    "Inspector.Dialog.OptimizeArtwork.DirectMessage",
                    ArtworkMaxDimension,
                    Selection.Paths.Count),
                L("Inspector.Dialog.OptimizeArtwork.Confirm")))
            return;
        await ApplyArtworkAsync(
            "Inspector.Activity.OptimizeArtwork",
            async musicPath =>
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
                "Inspector.Activity.RemoveArtwork",
                (progress, ct) =>
                    _metadataOperations
                        .PreviewArtworkEditsAsync(
                            edits,
                            L(
                                "Inspector.Operation.RemoveLibraryArtwork"),
                            progress,
                            ct),
                LC(
                    "Inspector.Dialog.RemoveArtwork.Message",
                    Selection.Paths.Count));
            return;
        }
        if (!await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.RemoveArtwork.Title"),
                LC(
                    "Inspector.Dialog.RemoveArtwork.DirectMessage",
                    Selection.Paths.Count),
                L("Common.Remove")))
            return;
        await ApplyArtworkAsync(
            "Inspector.Activity.RemoveArtwork",
            async musicPath =>
            await _artwork.RemoveAsync(musicPath));
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                L("Inspector.Dialog.Revert.Title"),
                LF(
                    "Inspector.Dialog.Revert.Message",
                    UnsavedChangesSummary,
                    SelectionSummary),
                L("Inspector.Dialog.Revert.Confirm")))
            return;
        await LoadAsync(Selection);
    }

    private async Task ApplyReviewedPlanAsync(
        string titleKey,
        Func<
            IProgress<OperationProgress>,
            CancellationToken,
            Task<MetadataOperationPlan>> preview,
        string confirmation)
    {
        if (_metadataOperations is null)
            throw new InvalidOperationException(
                L("Inspector.Error.MetadataServiceUnavailable"));
        string title = L(titleKey);
        IsBusy = true;
        NotifyCommands();
        _editCancellation =
            new CancellationTokenSource();
        Guid activity = _activities.Start(
            title,
            LC(
                "Inspector.Activity.PreviewingTracks",
                Selection.Paths.Count),
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
                if (blockers.Length > 0)
                {
                    SetCountStatus(
                        MessageTone.Error,
                        "Inspector.Status.PreviewBlockers",
                        blockers.Length);
                    StatusDiagnosticDetail =
                        blockers[0].Message;
                }
                else
                {
                    SetStatus(
                        MessageTone.Info,
                        "Inspector.Status.PreviewNoChanges");
                }
                _activities.Finish(
                    activity,
                    StatusMessage!,
                    blockers.Length > 0
                        ? AppActivityState.Failed
                        : AppActivityState.Completed);
                return;
            }
            if (!await _dialogs.ConfirmAsync(
                    LF(
                        "Inspector.Dialog.ApplyReviewed.Title",
                        title),
                    LF(
                        "Inspector.Dialog.ApplyReviewed.Message",
                        confirmation,
                        plan.ChangedFileCount),
                    L("Common.Apply")))
            {
                SetStatus(
                    MessageTone.Info,
                    "Inspector.Status.ReviewedNotApplied");
                _activities.Finish(
                    activity,
                    StatusMessage!,
                    AppActivityState.Cancelled);
                return;
            }
            MetadataApplyResult result =
                await _metadataOperations.ApplyAsync(
                    plan,
                    progress,
                    _editCancellation.Token);
            string completionMessage =
                LC(
                    "Inspector.Status.ReviewedUpdated",
                    result.ChangedFiles);
            _activities.Finish(
                activity,
                completionMessage,
                AppActivityState.Completed);
            if (result.ChangedFiles > 0)
                FilesChanged?.Invoke();
            await LoadAsync(Selection);
            SetCountStatus(
                MessageTone.Success,
                "Inspector.Status.ReviewedUpdated",
                result.ChangedFiles);
        }
        catch (OperationCanceledException) when (
            _editCancellation.IsCancellationRequested)
        {
            SetStatus(
                MessageTone.Warning,
                "Inspector.Status.OperationCancelled");
            _activities.Finish(
                activity,
                StatusMessage!,
                AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            SetStatusFailure(
                "Inspector.Status.OperationFailed",
                error.Message);
            _activities.Finish(
                activity,
                StatusMessage!,
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

    private async Task ApplyArtworkAsync(
        string titleKey,
        Func<string, Task<ArtworkOpResult>> apply)
    {
        bool hasRetryableChanges = HasPendingArtworkChanges ||
            ArtworkItems.Any(item => item.IsModified);
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start(
            L(titleKey),
            LC(
                "Inspector.Activity.UpdatingTracks",
                Selection.Paths.Count),
            ShellDestination.Library);
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
                _activities.Report(
                    activity,
                    LF(
                        "Inspector.Progress.UpdatedOfTotal",
                        saved,
                        Selection.Paths.Count),
                    (double)saved / Selection.Paths.Count);
            }
            if (saved == Selection.Paths.Count)
            {
                SetCountStatus(
                    MessageTone.Success,
                    "Inspector.Status.UpdatedArtwork",
                    saved);
            }
            else
            {
                SetStatus(
                    MessageTone.Error,
                    hasRetryableChanges
                        ? "Inspector.Status.ArtworkPartialFailureRetry"
                        : "Inspector.Status.ArtworkPartialFailure",
                    saved,
                    Selection.Paths.Count);
                StatusDiagnosticDetail =
                    firstError;
            }
            _activities.Finish(activity, StatusMessage!,
                saved == Selection.Paths.Count ? AppActivityState.Completed : AppActivityState.Failed);
            if (saved > 0)
                FilesChanged?.Invoke();
            if (saved == Selection.Paths.Count)
                await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            SetStatusFailure(
                hasRetryableChanges
                    ? "Inspector.Status.ArtworkSaveFailedRetry"
                    : "Inspector.Status.ArtworkSaveFailed",
                error.Message);
            _activities.Finish(
                activity,
                StatusMessage!,
                AppActivityState.Failed);
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
        if (e.PropertyName is not (
                nameof(EditableTagField.IsModified) or
                nameof(EditableTagField.Value)))
            return;
        NotifyPendingChangeRowsChanged();
        if (e.PropertyName !=
            nameof(EditableTagField.IsModified))
            return;
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
    }

    private void OnArtworkItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
        SaveTagsCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUnsavedChangesChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedChangesSummary));
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPendingChangeRowsChanged()
    {
        PendingChangesVersion++;
        OnPropertyChanged(
            nameof(PendingChangesVersion));
    }

    private void ClearArtworkItems()
    {
        foreach (ArtworkPreviewItem item in ArtworkItems)
            item.PropertyChanged -= OnArtworkItemChanged;
        ArtworkItems.Clear();
        _artworkSummaryFactories.Clear();
    }

    private static bool SameSelection(SelectionContext left, SelectionContext right) =>
        left.Paths.Count == right.Paths.Count &&
        left.Paths.OrderBy(path => path, PathComparer)
            .SequenceEqual(
                right.Paths.OrderBy(path => path, PathComparer),
                PathComparer);

    private static string ArtworkSignature(
        IReadOnlyList<ArtworkModel> artwork) =>
        string.Join("|", artwork.Select(image =>
            $"{image.Category}\u001f{image.Description}\u001f{image.ImageType}\u001f" +
            $"{Convert.ToHexString(SHA256.HashData(image.Data))}"));

    private void UpdateArtworkSummary()
    {
        ArtworkSummary = ArtworkItems.Count switch
        {
            0 => L(
                "Inspector.Artwork.NoneEmbedded"),
            1 => ArtworkItems[0].Summary,
            _ => LC(
                "Inspector.Artwork.Embedded",
                ArtworkItems.Count),
        };
        if (Selection.Paths.Count > 1 && ArtworkItems.Count > 0)
            ArtworkSummary += LC(
                "Inspector.Artwork.Shared",
                Selection.Paths.Count);
    }

    private string DescribeOverview(
        SelectionContext selection,
        IReadOnlyList<MediaFileModel> loadedModels)
    {
        int count = selection.Paths.Count;
        Dictionary<string, string?> codecsByPath = (selection.Records is { Count: > 0 }
                ? selection.Records.Select(record => (record.Path, record.CodecName))
                : loadedModels.Select(model => (model.Path, model.Codec?.CodecName)))
            .GroupBy(value => value.Path, PathComparer)
            .ToDictionary(group => group.Key, group => group.First().Item2, PathComparer);
        IEnumerable<string> fileFormats = selection.Paths.Select(path =>
            FormatFileFormat(path, codecsByPath.GetValueOrDefault(path)));
        string[] knownTagFormats = (selection.Records is { Count: > 0 }
            ? selection.Records.Select(record => NormalizeTagFormat(record.TagType))
            : loadedModels.Select(model => NormalizeTagFormat(model.TagType)))
            .Take(count)
            .ToArray();
        IEnumerable<string> tagFormats = knownTagFormats.Concat(
            Enumerable.Repeat(
                L("Inspector.Common.Unknown"),
                Math.Max(0, count - knownTagFormats.Length)));
        string scope = LC(
            "Inspector.Selection.TracksSelected",
            count);

        return $"{scope}{Environment.NewLine}{Environment.NewLine}" +
               $"{L("Inspector.Overview.FileFormats")}{Environment.NewLine}{FormatDistribution(fileFormats, count)}" +
               $"{Environment.NewLine}{Environment.NewLine}{L("Inspector.Overview.TagFormats")}{Environment.NewLine}" +
               FormatDistribution(tagFormats, count);
    }

    private string DescribeOverview(
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
                    L("Inspector.Common.Unknown"),
                    Math.Max(
                        0,
                        count -
                        knownTagFormats.Length)));
        string scope = LC(
            "Inspector.Selection.TracksSelected",
            count);

        return $"{scope}{Environment.NewLine}" +
               $"{Environment.NewLine}{L("Inspector.Overview.FileFormats")}" +
               $"{Environment.NewLine}" +
               $"{FormatDistribution(fileFormats, count)}" +
               $"{Environment.NewLine}{Environment.NewLine}" +
               $"{L("Inspector.Overview.TagFormats")}{Environment.NewLine}" +
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

    private string FormatFileFormat(string path, string? codec)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            return L("Inspector.Common.Unknown");
        bool isMp4Family = extension is "MP4" or "M4A" or "M4P" or "M4R";
        return isMp4Family && !string.IsNullOrWhiteSpace(codec)
            ? $"{extension} ({codec})"
            : extension;
    }

    private string NormalizeTagFormat(string? value) => value switch
    {
        null or "" => L(
            "Inspector.Common.Unknown"),
        "Vorbis" => L(
            "Inspector.TagFormat.VorbisComments"),
        _ => value,
    };

    private string FormatDistribution(
        IEnumerable<string> values,
        int total)
    {
        string[] lines = values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count():N0} " +
                $"({(100d * group.Count() / total).ToString("0", CultureInfo.InvariantCulture)}%)")
            .ToArray();
        return lines.Length == 0
            ? LF(
                "Inspector.Overview.EmptyDistribution",
                L("Inspector.Common.Unknown"))
            : string.Join(
                Environment.NewLine,
                lines);
    }

    private string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => LF(
            "Inspector.Size.Megabytes",
            bytes / 1024d / 1024d),
        >= 1024 => LF(
            "Inspector.Size.Kilobytes",
            bytes / 1024d),
        _ => LC(
            "Inspector.Size.Bytes",
            bytes),
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

    private string ArtworkDetails(ArtworkModel image)
    {
        return LF(
            "Inspector.Artwork.Details",
            image.ImageType ??
            L("Inspector.Artwork.Image"),
            image.Width,
            image.Height,
            FormatBytes(image.Size));
    }

    private string PreparedArtworkDetails(
        PreparedImage image) =>
        LF(
            "Inspector.Artwork.Details",
            image.MimeType,
            image.Width,
            image.Height,
            FormatBytes(
                image.Data.LongLength));

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private string LC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);

    private void SetStatus(
        MessageTone tone,
        string key,
        params object?[] arguments)
    {
        _statusMessageKey = key;
        _statusMessageArguments = arguments;
        _statusMessageCount = null;
        StatusTone = tone;
        StatusMessage = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatus(
        MessageTone tone,
        string key,
        long count,
        params object?[] arguments)
    {
        _statusMessageKey = key;
        _statusMessageArguments = arguments;
        _statusMessageCount = count;
        StatusTone = tone;
        StatusMessage = LC(
            key,
            count,
            arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetStatusFailure(
        string key,
        string? diagnosticDetail,
        params object?[] arguments)
    {
        SetStatus(
            MessageTone.Error,
            key,
            arguments);
        StatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void ClearStatus()
    {
        _statusMessageKey = null;
        _statusMessageArguments = [];
        _statusMessageCount = null;
        StatusMessage = null;
        StatusDiagnosticDetail = null;
        StatusTone = MessageTone.Info;
    }

    private void RefreshLocalizedChoices()
    {
        foreach (ID3v2Util.APICType value in
                 ArtworkTypes)
        {
            LocalizedChoice<ID3v2Util.APICType>?
                choice = ArtworkTypeChoices
                    .FirstOrDefault(item =>
                        item.Value == value);
            string label = ArtworkTypeLabel(value);
            if (choice is null)
                ArtworkTypeChoices.Add(
                    new(value, label));
            else
                choice.Label = label;
        }
    }

    private string ArtworkTypeLabel(
        ID3v2Util.APICType value) =>
        L($"Inspector.Artwork.Type.{value}.Label");

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        foreach (EditableTagField field in Fields)
            field.RefreshLocalizedText(L);
        foreach (ArtworkPreviewItem item in
                 ArtworkItems)
        {
            item.RefreshLocalizedText(
                ArtworkTypeLabel);
            if (_artworkSummaryFactories.TryGetValue(
                    item,
                    out Func<string>? factory))
                item.RefreshTechnicalSummary(
                    factory());
        }
        if (_overviewFactory is not null)
            Overview = _overviewFactory();
        if (_statusMessageKey is not null)
            StatusMessage =
                _statusMessageCount is { } count
                    ? LC(
                        _statusMessageKey,
                        count,
                        _statusMessageArguments)
                    : LF(
                        _statusMessageKey,
                        _statusMessageArguments);
        if (IsArtworkMixed)
            ArtworkSummary = L(
                "Inspector.Artwork.Mixed");
        else
            UpdateArtworkSummary();
        OnPropertyChanged(
            nameof(SelectionSummary));
        OnPropertyChanged(
            nameof(UnsavedChangesSummary));
        NotifyPendingChangeRowsChanged();
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

/// <summary>
/// A Workbench-local inspector. Keeping it distinct from the Library singleton prevents
/// selections and unsaved edits from leaking between the two pages.
/// </summary>
public sealed class WorkbenchSelectionInspectorViewModel : SelectionInspectorViewModel
{
    public WorkbenchSelectionInspectorViewModel(
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
        IMetadataDocumentService? metadataDocuments = null,
        ILocalizationService? localization = null)
        : base(media, library, tags, artwork, files, dialogs, fieldsEditor,
            thumbnails, activities, metadataOperations, metadataDocuments,
            localization)
    {
    }
}
