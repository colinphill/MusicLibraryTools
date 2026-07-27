using System.Collections.Immutable;
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
    private readonly IArtworkService _artwork;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IThumbnailService _thumbnails;
    private readonly IMetadataOperationService? _metadataOperations;
    private readonly IMetadataDocumentService? _metadataDocuments;
    private readonly ILocalizationService? _localization;
    private readonly Dictionary<
        ArtworkPreviewItem,
        Func<string>> _artworkSummaryFactories = [];
    private int _generation;
    private CancellationTokenSource? _cancellation;
    private ArtworkMutationLease?
        _artworkMutation;
    private ImmutableArray<ArtworkInput>
        _loadedArtworkInputs = [];
    private Dictionary<string, ArtworkSetPreviewRequest>?
        _pendingArtworkRequests;
    private IReadOnlyDictionary<
        string,
        MetadataEditSourceExpectation>
        _sourceExpectations =
            new Dictionary<
                string,
                MetadataEditSourceExpectation>(
                PathComparer);
    private string? _statusMessageKey;
    private object?[] _statusMessageArguments = [];
    private long? _statusMessageCount;
    private Func<string>? _overviewFactory;
    private readonly HashSet<string>
        _reservedEditPaths =
            new(PathComparer);
    private bool _isEditReserved;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasArtworkSharingSummary))]
    private string _artworkSharingSummary = "";
    [ObservableProperty] private int _artworkMaxDimension = 600;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedArtworkChanges))]
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
        ArgumentNullException.ThrowIfNull(tags);
        _artwork = artwork;
        _files = files;
        _dialogs = dialogs;
        ArgumentNullException.ThrowIfNull(fieldsEditor);
        _thumbnails = thumbnails;
        ArgumentNullException.ThrowIfNull(activities);
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
    public bool HasArtworkSharingSummary =>
        !string.IsNullOrWhiteSpace(
            ArtworkSharingSummary);
    public string StatusIcon => StatusTone switch
    {
        MessageTone.Success => "✓",
        MessageTone.Warning => "⚠",
        MessageTone.Error => "!",
        _ => "i",
    };
    public string SelectionSummary => Selection.Summary;
    public int PendingChangesVersion { get; private set; }
    public bool HasUnsavedMetadataChanges =>
        Fields.Any(item => item.IsModified);
    public bool HasUnsavedArtworkChanges =>
        HasArtworkSetEdits();
    public bool HasUnsavedChanges =>
        HasUnsavedMetadataChanges ||
        HasUnsavedArtworkChanges;
    internal bool IsEditReserved =>
        _isEditReserved;
    public string UnsavedChangesSummary
    {
        get
        {
            int tagCount = Fields.Count(item => item.IsModified);
            bool artwork =
                HasArtworkSetEdits();
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
                    field.Label,
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
                    PendingArtworkSummary()));
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
        IReadOnlyDictionary<
            string,
            MetadataEditSourceExpectation>
            sourceExpectations =
                CreatePendingSourceExpectations();
        return await PreviewInspectorChangesAsync(
            valueEdits,
            artworkEdits,
            sourceExpectations,
            progress,
            cancellationToken);
    }

    public Task DiscardPendingChangesAsync() =>
        HasUnsavedChanges
            ? LoadAsync(Selection)
            : Task.CompletedTask;

    /// <summary>
    /// Advances the editor baseline after the reviewed mutation service has
    /// durably committed the current draft. This intentionally performs no
    /// I/O: a presentation refresh is best-effort after the commit boundary
    /// and must not leave the same edits available to apply a second time.
    /// </summary>
    public void AcceptPendingChanges()
    {
        if (!HasUnsavedChanges)
            return;

        var captured =
            CreatePendingOperationInputs();
        AcceptPendingChanges(
            captured.ValueEdits,
            captured.ArtworkEdits);
    }

    /// <summary>
    /// Advances only the exact inspector draft captured before a durable apply.
    /// Values entered while the apply was running remain modified against the
    /// newly committed baseline.
    /// </summary>
    public void AcceptPendingChanges(
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>?
            capturedValueEdits,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>?
            capturedArtworkEdits) =>
        AcceptPendingChanges(
            capturedValueEdits,
            capturedArtworkEdits,
            appliedArtworkFingerprints: null);

    internal void AcceptPendingChanges(
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>?
            capturedValueEdits,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>?
            capturedArtworkEdits,
        IReadOnlyDictionary<string, string>?
            appliedArtworkFingerprints)
    {
        PendingChangesAcceptance? acceptance =
            AcceptPendingChangesState(
                capturedValueEdits,
                capturedArtworkEdits,
                appliedArtworkFingerprints);
        if (acceptance is null)
            return;
        PublishPendingChangesAcceptance(
            acceptance);
    }

    internal PendingChangesAcceptance?
        AcceptPendingChangesState(
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>?
                capturedValueEdits,
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>?
                capturedArtworkEdits,
            IReadOnlyDictionary<string, string>?
                appliedArtworkFingerprints = null)
    {
        if (capturedValueEdits is null &&
            capturedArtworkEdits is null)
            return null;

        Dictionary<MetadataFieldKey,
            ImmutableArray<string>>
            commonAppliedValues = [];
        if (capturedValueEdits is not null)
        {
            foreach (IGrouping<
                         MetadataFieldKey,
                         MetadataValueEdit> group in
                     capturedValueEdits.Values
                         .SelectMany(edits => edits)
                         .GroupBy(edit =>
                             edit.Field))
            {
                ImmutableArray<string>[] values =
                    [.. group.Select(edit =>
                        edit.Values)];
                if (values.Length > 0 &&
                    values.Skip(1).All(value =>
                        value.SequenceEqual(
                            values[0],
                            StringComparer.Ordinal)))
                    commonAppliedValues[group.Key] =
                        values[0];
            }
        }

        var acceptedFields =
            new List<EditableTagField>();
        foreach (EditableTagField field in Fields)
        {
            MetadataFieldKey fieldKey =
                MetadataFieldKey.Known(
                    field.Field);
            if (commonAppliedValues.TryGetValue(
                    fieldKey,
                    out ImmutableArray<string>
                        appliedValues))
            {
                field.AcceptAppliedValuesState(
                    appliedValues);
                acceptedFields.Add(field);
            }
        }

        var acceptedArtworkItems =
            new List<ArtworkPreviewItem>();
        bool artworkDraftUnchanged =
            capturedArtworkEdits is not null &&
            ArtworkRequestsEqual(
                capturedArtworkEdits,
                CurrentPendingArtworkRequests());
        if (capturedArtworkEdits is not null)
        {
            ArtworkSetPreviewRequest[] requests =
            [
                .. capturedArtworkEdits.Values,
            ];
            if (requests.Length > 0 &&
                requests.Skip(1).All(request =>
                    ArtworkSetsEqual(
                        request.Images,
                        requests[0].Images)))
                _loadedArtworkInputs =
                    requests[0].Images;

            if (artworkDraftUnchanged)
            {
                foreach (ArtworkPreviewItem item in
                         ArtworkItems)
                {
                    if (item.AcceptChangesState())
                        acceptedArtworkItems.Add(
                            item);
                }
                _loadedArtworkInputs =
                    [.. CurrentArtworkInputs()];
                _pendingArtworkRequests = null;
            }
        }

        RebaseSourceExpectations(
            capturedValueEdits,
            capturedArtworkEdits,
            appliedArtworkFingerprints);
        // This is the semantic phase of a durable commit. Publish the
        // observable changes only after every field and artwork baseline has
        // advanced so a fallible observer cannot leave half the draft
        // retryable.
#pragma warning disable MVVMTK0034
        _hasPendingArtworkChanges =
            HasArtworkSetEdits();
#pragma warning restore MVVMTK0034
        return new(
            [.. acceptedFields],
            [.. acceptedArtworkItems]);
    }

    internal void PublishPendingChangesAcceptance(
        PendingChangesAcceptance acceptance)
    {
        var errors = new List<Exception>();
        foreach (EditableTagField field in
                 acceptance.Fields)
            TryNotify(
                field.NotifyAppliedValuesAccepted,
                errors);
        foreach (ArtworkPreviewItem item in
                 acceptance.ArtworkItems)
            TryNotify(
                item.NotifyChangesAccepted,
                errors);
        TryNotify(
            NotifyPendingChangeRowsChanged,
            errors);
        TryNotify(
            NotifyUnsavedChangesChanged,
            errors);
        TryNotify(
            NotifyCommands,
            errors);
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

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
                    : LibraryMetadataValueMap
                        .ParseEditorValues(
                            field.Value)))
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
            artworkByPath = !HasArtworkSetEdits()
                ? null
                : _pendingArtworkRequests is { } pending
                    ? pending.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        PathComparer)
                    : !CanEditArtworkSet()
                        ? null
                        : Selection.Paths.ToDictionary(
                            path => path,
                            _ => new ArtworkSetPreviewRequest(
                                [.. CurrentArtworkInputs()]),
                            PathComparer);
        return (editsByPath, artworkByPath);
    }

    internal IReadOnlyDictionary<
        string,
        MetadataEditSourceExpectation>
        CreatePendingSourceExpectations()
    {
        var result = new Dictionary<
            string,
            MetadataEditSourceExpectation>(
            PathComparer);
        foreach (string path in Selection.Paths)
        {
            if (!_sourceExpectations.TryGetValue(
                    path,
                    out MetadataEditSourceExpectation?
                        expectation))
                continue;
            result[path] = expectation with
            {
                OriginalValues =
                    expectation.OriginalValues
                        .ToImmutableDictionary(),
            };
        }
        return result;
    }

    internal void SetPathEditReservation(
        IEnumerable<string> paths,
        bool reserved)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths.Where(path =>
                     !string.IsNullOrWhiteSpace(
                         path)))
        {
            string normalized =
                NormalizePath(path);
            if (reserved)
                _reservedEditPaths.Add(
                    normalized);
            else
                _reservedEditPaths.Remove(
                    normalized);
        }
        RefreshEditReservation();
    }

    private void RefreshEditReservation()
    {
        bool reserved =
            Selection.Paths.Any(path =>
                _reservedEditPaths.Contains(
                    NormalizePath(path)));
        bool changed =
            _isEditReserved != reserved;
        _isEditReserved = reserved;
        foreach (EditableTagField field in Fields)
            field.SetEditReservation(
                reserved);
        foreach (ArtworkPreviewItem item in
                 ArtworkItems)
            item.SetEditReservation(
                reserved);
        if (!changed)
            return;

        var errors = new List<Exception>();
        TryNotify(
            () => OnPropertyChanged(
                nameof(IsEditReserved)),
            errors);
        TryNotify(
            NotifyCommands,
            errors);
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    private string PendingArtworkSummary()
    {
        if (_pendingArtworkRequests is { Count: > 0 } pending)
        {
            if (pending.Values.All(request =>
                    request.Images.IsEmpty))
                return L("Inspector.Common.None");
            if (pending.Values.Any(request =>
                    request.MaxDimension > 0))
                return L("Inspector.View.Optimize");
            int[] counts =
            [
                .. pending.Values
                    .Select(request =>
                        request.Images.Length)
                    .Distinct(),
            ];
            if (counts.Length == 1)
                return LC(
                    "Inspector.Pending.Images",
                    counts[0]);
            return L("Inspector.Pending.Artwork");
        }

        return ArtworkItems.Count == 0
            ? L("Inspector.Common.None")
            : LC(
                "Inspector.Pending.Images",
                ArtworkItems.Count);
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
        if (!await _dialogs.ConfirmDestructiveAsync(
                L("Inspector.Dialog.Discard.Title"),
                L("Inspector.Dialog.Discard.Message"),
                L("Inspector.Dialog.Discard.Confirm")))
            return false;
        await LoadAsync(Selection);
        return true;
    }

    public async Task LoadAsync(SelectionContext selection)
    {
        int generation = ++_generation;
        CancelArtworkMutation();
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Selection = selection;
        RefreshEditReservation();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        ClearStatus();
        ArtworkSource = null;
        ClearArtworkItems();
        _loadedArtworkInputs = [];
        _pendingArtworkRequests = null;
        _sourceExpectations =
            new Dictionary<
                string,
                MetadataEditSourceExpectation>(
                PathComparer);
        HasPendingArtworkChanges = false;
        IsArtworkMixed = false;
        ArtworkSummary = L(
            "Inspector.Artwork.NoneEmbedded");
        ArtworkSharingSummary = "";
        foreach (EditableTagField field in Fields)
            field.SetLoaded([], false);
        NotifyUnsavedChangesChanged();
        NotifyCommands();

        if (!selection.HasSelection)
        {
            _overviewFactory = () => L(
                "Inspector.Overview.SelectTrack");
            Overview = _overviewFactory();
            if (ReferenceEquals(
                    _cancellation,
                    cancellation))
                _cancellation = null;
            IsBusy = false;
            NotifyCommands();
            cancellation.Dispose();
            return;
        }

        Dictionary<
            string,
            MetadataEditSourceExpectation>
            sourceExpectations =
                BuildRecordSourceExpectations(
                    selection);
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
                        MediaDocument document =
                            await _metadataDocuments
                                .LoadAsync(
                                    path,
                                    includeArtwork: false,
                                    cancellation.Token);
                        documents.Add(document);
                        MergeDocumentExpectation(
                            sourceExpectations,
                            document,
                            includeArtworkFingerprint:
                                false);
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
                    {
                        models.Add(result.Value);
                        MergeMediaModelExpectation(
                            sourceExpectations,
                            result.Value,
                            includeArtworkFingerprint:
                                false);
                    }
                }
            }
            await RefreshSourceIdentitiesAsync(
                sourceExpectations,
                selection.Paths,
                cancellation.Token);
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
                    MergeDocumentExpectation(
                        sourceExpectations,
                        document,
                        includeArtworkFingerprint:
                            true);
                    directlyLoadedArtwork ??= document;
                    signatures.Add(
                        MetadataDocumentService
                            .CreateArtworkFingerprint(
                                document));
                }
                artworkSignatures = signatures;
            }
            else
            {
                artworkSignatures = await _library.GetImageSignaturesAsync(
                    selection.Paths, cancellation.Token);
                if (artworkSignatures.Count ==
                    selection.Paths.Count)
                {
                    for (int index = 0;
                         index <
                         selection.Paths.Count;
                         index++)
                    {
                        MergeArtworkFingerprint(
                            sourceExpectations,
                            selection.Paths[index],
                            artworkSignatures[index]);
                    }
                }
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
                MergeDocumentExpectation(
                    sourceExpectations,
                    artwork,
                    includeArtworkFingerprint:
                        true);
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
                if (artwork.Value is not null)
                    MergeMediaModelExpectation(
                        sourceExpectations,
                        artwork.Value,
                        includeArtworkFingerprint:
                            true);
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
                preview.SetEditReservation(
                    _isEditReserved);
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
            UpdateArtworkSharingSummary();
            _loadedArtworkInputs =
                [.. CurrentArtworkInputs()];
            if (invalidArtwork > 0)
            {
                SetCountStatus(
                    MessageTone.Warning,
                    "Inspector.Status.InvalidArtwork",
                    invalidArtwork);
            }
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
                _sourceExpectations =
                    sourceExpectations
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            PathComparer);
                _cancellation = null;
                IsBusy = false;
                NotifyCommands();
            }
            cancellation.Dispose();
        }
    }

    private static Dictionary<
        string,
        MetadataEditSourceExpectation>
        BuildRecordSourceExpectations(
            SelectionContext selection)
    {
        var result = new Dictionary<
            string,
            MetadataEditSourceExpectation>(
            PathComparer);
        Dictionary<string, TrackRecord> records =
            (selection.Records ?? [])
                .GroupBy(
                    record => record.Path,
                    PathComparer)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    PathComparer);
        foreach (string path in selection.Paths
                     .Distinct(PathComparer))
        {
            if (records.TryGetValue(
                    path,
                    out TrackRecord? record))
            {
                result[path] = new(
                    record.Length > 0
                        ? record.Length
                        : null,
                    record.LastWriteTime == default
                        ? null
                        : NormalizeUtc(
                            record.LastWriteTime),
                    OriginalValues(record));
            }
            else
            {
                result[path] = new(
                    null,
                    null,
                    ImmutableDictionary<
                        MetadataFieldKey,
                        ImmutableArray<string>>
                        .Empty);
            }
        }
        return result;
    }

    private static void MergeDocumentExpectation(
        Dictionary<
            string,
            MetadataEditSourceExpectation>
            expectations,
        MediaDocument document,
        bool includeArtworkFingerprint)
    {
        expectations.TryGetValue(
            document.Path,
            out MetadataEditSourceExpectation?
                existing);
        expectations[document.Path] = new(
            document.Snapshot.Length,
            document.Snapshot.LastWriteTimeUtc,
            OriginalValues(document),
            existing?.MetadataHash ??
                MetadataDocumentService
                    .CreateMetadataFingerprint(
                        document),
            includeArtworkFingerprint
                ? MetadataDocumentService
                    .CreateArtworkFingerprint(
                        document)
                : existing
                    ?.ArtworkFingerprint);
    }

    private static void MergeMediaModelExpectation(
        Dictionary<
            string,
            MetadataEditSourceExpectation>
            expectations,
        MediaFileModel model,
        bool includeArtworkFingerprint)
    {
        expectations.TryGetValue(
            model.Path,
            out MetadataEditSourceExpectation?
                existing);
        expectations[model.Path] = new(
            existing?.Length,
            existing?.LastWriteTimeUtc,
            OriginalValues(model),
            existing?.MetadataHash,
            includeArtworkFingerprint
                ? MetadataDocumentService
                    .CreateArtworkFingerprint(
                        model.Artwork)
                : existing
                    ?.ArtworkFingerprint);
    }

    private static void MergeArtworkFingerprint(
        Dictionary<
            string,
            MetadataEditSourceExpectation>
            expectations,
        string path,
        string fingerprint)
    {
        if (!expectations.TryGetValue(
                path,
                out MetadataEditSourceExpectation?
                    existing))
        {
            existing = new(
                null,
                null,
                ImmutableDictionary<
                    MetadataFieldKey,
                    ImmutableArray<string>>
                    .Empty);
        }
        expectations[path] = existing with
        {
            ArtworkFingerprint =
                fingerprint,
        };
    }

    private static async Task
        RefreshSourceIdentitiesAsync(
            Dictionary<
                string,
                MetadataEditSourceExpectation>
                expectations,
            IReadOnlyList<string> paths,
            CancellationToken ct)
    {
        string[] targets = paths
            .Distinct(PathComparer)
            .ToArray();
        if (targets.Length == 0)
            return;
        (string Path, long? Length,
            DateTime? LastWriteTimeUtc)[] stats =
            await Task.Run(
                () =>
                {
                    var loaded = new List<(
                        string Path,
                        long? Length,
                        DateTime?
                            LastWriteTimeUtc)>(
                        targets.Length);
                    foreach (string path in targets)
                    {
                        ct.ThrowIfCancellationRequested();
                        var file = new FileInfo(path);
                        loaded.Add(
                            file.Exists
                                ? (
                                    path,
                                    file.Length,
                                    file.LastWriteTimeUtc)
                                : (
                                    path,
                                    null,
                                    null));
                    }
                    return loaded.ToArray();
                },
                ct);
        foreach ((string path, long? length,
                     DateTime? lastWriteTimeUtc) in
                 stats)
        {
            ct.ThrowIfCancellationRequested();
            if (!expectations.TryGetValue(
                    path,
                    out MetadataEditSourceExpectation?
                        existing))
            {
                existing = new(
                    null,
                    null,
                    ImmutableDictionary<
                        MetadataFieldKey,
                        ImmutableArray<string>>
                        .Empty);
            }
            expectations[path] = existing with
            {
                Length = length ??
                    existing.Length,
                LastWriteTimeUtc =
                    lastWriteTimeUtc ??
                    existing.LastWriteTimeUtc,
            };
        }
    }

    private static IReadOnlyDictionary<
        MetadataFieldKey,
        ImmutableArray<string>>
        OriginalValues(
            MediaDocument document)
    {
        var result = ImmutableDictionary
            .CreateBuilder<
                MetadataFieldKey,
                ImmutableArray<string>>();
        TagLayerDocument? primary =
            document.TagLayers.FirstOrDefault();
        if (primary is not null)
        {
            foreach (IGrouping<
                         MetadataFieldKey,
                         MetadataValueSet> group in
                     primary.Fields.GroupBy(
                         value => value.Field))
            {
                result[group.Key] =
                    [
                        .. group.SelectMany(
                            value => value.Values),
                    ];
            }
        }
        AddMissingInspectorValues(
            result,
            field => document
                .TagLayers
                .FirstOrDefault()
                ?.Fields
                .Where(value =>
                    value.Field.KnownField == field)
                .SelectMany(value => value.Values)
                .ToImmutableArray() ??
                []);
        return result.ToImmutable();
    }

    private static IReadOnlyDictionary<
        MetadataFieldKey,
        ImmutableArray<string>>
        OriginalValues(
            MediaFileModel model)
    {
        var result = ImmutableDictionary
            .CreateBuilder<
                MetadataFieldKey,
                ImmutableArray<string>>();
        foreach (IGrouping<
                     TagFields,
                     TagFieldValue> group in
                 model.KnownFields.GroupBy(
                     value => value.Field))
        {
            result[MetadataFieldKey.Known(
                group.Key)] =
                [
                    .. group.Select(
                        value => value.Value),
                ];
        }
        AddMissingInspectorValues(
            result,
            field => ModelValues(
                model,
                field));
        return result.ToImmutable();
    }

    private static IReadOnlyDictionary<
        MetadataFieldKey,
        ImmutableArray<string>>
        OriginalValues(
            TrackRecord record)
    {
        var result = ImmutableDictionary
            .CreateBuilder<
                MetadataFieldKey,
                ImmutableArray<string>>();
        foreach ((TagFields field, _) in
                 FieldDefinitions)
        {
            KeyValuePair<string, string[]>
                cached = record.Metadata
                    .FirstOrDefault(pair =>
                        string.Equals(
                            pair.Key,
                            field.ToString(),
                            StringComparison
                                .OrdinalIgnoreCase));
            result[MetadataFieldKey.Known(
                field)] =
                cached.Key is not null
                    ? [.. cached.Value]
                    : RecordValues(
                        record,
                        field);
        }
        return result.ToImmutable();
    }

    private static void AddMissingInspectorValues(
        ImmutableDictionary<
            MetadataFieldKey,
            ImmutableArray<string>>.Builder
            values,
        Func<
            TagFields,
            ImmutableArray<string>>
            fallback)
    {
        foreach ((TagFields field, _) in
                 FieldDefinitions)
        {
            MetadataFieldKey key =
                MetadataFieldKey.Known(field);
            if (!values.ContainsKey(key))
                values[key] = fallback(field);
        }
    }

    private static ImmutableArray<string>
        ModelValues(
            MediaFileModel model,
            TagFields field)
    {
        string? value = field switch
        {
            TagFields.Title => model.Title,
            TagFields.Artist => model.Artist,
            TagFields.AlbumArtist =>
                model.AlbumArtist,
            TagFields.Album => model.Album,
            TagFields.TrackNumber =>
                model.TrackNumber
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.TotalTracks =>
                model.TrackTotal
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.DiscNumber =>
                model.DiscNumber
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.TotalDiscs =>
                model.DiscTotal
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.Date =>
                model.ReleaseDate,
            _ => null,
        };
        return string.IsNullOrEmpty(value)
            ? []
            : [value];
    }

    private static ImmutableArray<string>
        RecordValues(
            TrackRecord record,
            TagFields field)
    {
        string? value = field switch
        {
            TagFields.Title => record.Title,
            TagFields.Artist => record.Artist,
            TagFields.AlbumArtist =>
                record.AlbumArtist,
            TagFields.Album => record.Album,
            TagFields.TrackNumber =>
                record.TrackNumber
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.TotalTracks =>
                record.TrackTotal
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.DiscNumber =>
                record.DiscNumber
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.TotalDiscs =>
                record.DiscTotal
                    ?.ToString(
                        CultureInfo
                            .InvariantCulture),
            TagFields.Date =>
                record.ReleaseDate,
            TagFields.Genre =>
                record.Genre,
            TagFields.Composer =>
                record.Composer,
            _ => null,
        };
        return string.IsNullOrEmpty(value)
            ? []
            : [value];
    }

    private static DateTime NormalizeUtc(
        DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

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
        string[] displayValues = mixed
            ? valuesByFile
                .SelectMany(values => values)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : valuesByFile[0];
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

    private bool CanEdit() =>
        HasSelection &&
        !IsBusy &&
        !_isEditReserved;
    private bool CanRevert() => HasUnsavedChanges && !IsBusy;
    private bool CanEditArtworkSet() => CanEdit() && !IsArtworkMixed;
    private bool HasArtworkSetEdits() =>
        _pendingArtworkRequests is { Count: > 0 }
            pending
            ? pending.Values.Any(request =>
                request.MaxDimension > 0 &&
                request.Images.Length > 0 ||
                !ArtworkSetsEqual(
                    request.Images,
                    _loadedArtworkInputs))
            : !ArtworkSetsEqual(
                CurrentArtworkInputs(),
                _loadedArtworkInputs);

    private IReadOnlyDictionary<
        string,
        ArtworkSetPreviewRequest>?
        CurrentPendingArtworkRequests()
    {
        if (!HasArtworkSetEdits())
            return null;
        if (_pendingArtworkRequests is not null)
            return _pendingArtworkRequests;
        if (IsArtworkMixed)
            return null;

        ArtworkSetPreviewRequest request =
            new([.. CurrentArtworkInputs()]);
        return Selection.Paths.ToDictionary(
            path => path,
            _ => request,
            PathComparer);
    }

    private static bool ArtworkRequestsEqual(
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest> left,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>? right)
    {
        if (right is null ||
            left.Count != right.Count)
            return false;
        foreach ((string path,
                     ArtworkSetPreviewRequest request)
                 in left)
        {
            if (!right.TryGetValue(
                    path,
                    out ArtworkSetPreviewRequest?
                        candidate) ||
                request.MaxDimension !=
                    candidate.MaxDimension ||
                !ArtworkSetsEqual(
                    request.Images,
                    candidate.Images))
                return false;
        }
        return true;
    }

    private void RebaseSourceExpectations(
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>?
            capturedValueEdits,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>?
            capturedArtworkEdits,
        IReadOnlyDictionary<string, string>?
            appliedArtworkFingerprints)
    {
        string[] paths =
        [
            .. (capturedValueEdits?.Keys ?? [])
                .Concat(
                    capturedArtworkEdits?.Keys ??
                    [])
                .Distinct(PathComparer),
        ];
        if (paths.Length == 0)
            return;

        var updated = _sourceExpectations
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                PathComparer);
        foreach (string path in paths)
        {
            updated.TryGetValue(
                path,
                out MetadataEditSourceExpectation?
                    existing);
            existing ??= new(
                null,
                null,
                ImmutableDictionary<
                    MetadataFieldKey,
                    ImmutableArray<string>>
                    .Empty);
            var originals =
                existing.OriginalValues
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
            IReadOnlyList<MetadataValueEdit>?
                edits = null;
            bool valuesChanged =
                capturedValueEdits is not null &&
                capturedValueEdits.TryGetValue(
                    path,
                    out edits);
            if (valuesChanged)
            {
                foreach (MetadataValueEdit edit in
                         edits!)
                    originals[edit.Field] =
                        edit.Values;
            }
            bool artworkChanged =
                capturedArtworkEdits?.ContainsKey(
                    path) == true;
            string? artworkFingerprint =
                existing.ArtworkFingerprint;
            if (artworkChanged)
            {
                if (appliedArtworkFingerprints
                        ?.TryGetValue(
                            path,
                            out string? applied) ==
                    true)
                    artworkFingerprint = applied;
                else if (capturedArtworkEdits!
                             .TryGetValue(
                                 path,
                                 out ArtworkSetPreviewRequest?
                                     captured))
                    artworkFingerprint =
                        CreateArtworkFingerprint(
                            captured.Images);
            }
            updated[path] = existing with
            {
                Length = null,
                LastWriteTimeUtc = null,
                OriginalValues =
                    originals.ToImmutableDictionary(),
                MetadataHash = valuesChanged
                    ? null
                    : existing.MetadataHash,
                ArtworkFingerprint =
                    artworkChanged
                        ? artworkFingerprint
                        : existing
                            .ArtworkFingerprint,
            };
        }
        _sourceExpectations = updated;
    }

    internal static string
        CreateArtworkFingerprint(
            IReadOnlyList<ArtworkInput> artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        return string.Join(
            "|",
            artwork.Select(image =>
                    Convert.ToBase64String(
                        SHA256.HashData(
                            image.Data)))
                .OrderBy(
                    hash => hash,
                    StringComparer.Ordinal));
    }

    private static bool ArtworkSetsEqual(
        IReadOnlyList<ArtworkInput> left,
        IReadOnlyList<ArtworkInput> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0;
             index < left.Count;
             index++)
        {
            ArtworkInput first = left[index];
            ArtworkInput second = right[index];
            if (first.Type != second.Type ||
                !StringComparer.OrdinalIgnoreCase
                    .Equals(
                        first.MimeType,
                        second.MimeType) ||
                !StringComparer.Ordinal.Equals(
                    first.Description ?? "",
                    second.Description ?? "") ||
                !first.Data.AsSpan()
                    .SequenceEqual(
                        second.Data))
                return false;
        }
        return true;
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
            IReadOnlyDictionary<
                string,
                MetadataEditSourceExpectation>
                sourceExpectations,
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
                    sourceExpectations,
                    L(
                        "Inspector.Operation.EditArtwork"),
                    progress,
                    ct);
        if (artworkByPath is null)
            return await _metadataOperations
                .PreviewValueEditsAsync(
                    editsByPath,
                    sourceExpectations,
                    L(
                        "Inspector.Operation.EditFields"),
                    progress,
                    ct);

        MetadataOperationPlan fields =
            await _metadataOperations
                .PreviewValueEditsAsync(
                    editsByPath,
                    sourceExpectations,
                    L(
                        "Inspector.Operation.EditFields"),
                    progress,
                    ct);
        MetadataOperationPlan artwork =
            await _metadataOperations
                .PreviewArtworkSetsAsync(
                    artworkByPath,
                    sourceExpectations,
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

    private ArtworkMutationLease
        BeginArtworkMutation()
    {
        CancelArtworkMutation();
        var lease = new ArtworkMutationLease(
            _generation,
            [.. Selection.Paths],
            new CancellationTokenSource());
        _artworkMutation = lease;
        IsBusy = true;
        NotifyCommands();
        return lease;
    }

    private bool CanPublishArtworkMutation(
        ArtworkMutationLease lease) =>
        ReferenceEquals(
            _artworkMutation,
            lease) &&
        !lease.Cancellation
            .IsCancellationRequested &&
        lease.Generation == _generation &&
        lease.Paths.SequenceEqual(
            Selection.Paths,
            PathComparer);

    private void CancelArtworkMutation()
    {
        ArtworkMutationLease? current =
            _artworkMutation;
        _artworkMutation = null;
        current?.Cancellation.Cancel();
    }

    private void CompleteArtworkMutation(
        ArtworkMutationLease lease)
    {
        bool stillCurrent = ReferenceEquals(
            _artworkMutation,
            lease);
        if (stillCurrent)
            _artworkMutation = null;
        lease.Cancellation.Dispose();
        if (stillCurrent)
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditArtworkSet))]
    private async Task AddArtworkAsync()
    {
        ArtworkMutationLease lease =
            BeginArtworkMutation();
        try
        {
            string? path = await _files.PickFileAsync(
                L("Inspector.Picker.ChooseArtwork"),
                [new FilePickerType(
                    L("Inspector.Picker.Images"),
                    [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
            if (path is null ||
                !CanPublishArtworkMutation(
                    lease))
                return;

            PreparedImage? prepared =
                await _artwork
                    .PrepareFromFileAsync(
                        path,
                        ArtworkMaxDimension,
                        lease.Cancellation.Token);
            if (!CanPublishArtworkMutation(
                    lease))
                return;
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
            object? source =
                await _thumbnails
                    .CreateImageSourceAsync(
                        prepared.Data,
                        cancellationToken:
                            lease.Cancellation
                                .Token);
            if (!CanPublishArtworkMutation(
                    lease))
                return;
            var item = new ArtworkPreviewItem(
                source,
                type,
                prepared.MimeType,
                prepared.Data,
                PreparedArtworkDetails(prepared),
                null);
            item.SetEditReservation(
                _isEditReserved);
            item.RefreshLocalizedText(
                ArtworkTypeLabel);
            _artworkSummaryFactories[item] =
                () => PreparedArtworkDetails(prepared);
            item.PropertyChanged += OnArtworkItemChanged;
            ArtworkItems.Add(item);
            ArtworkSource ??= item.Source;
            SynchronizePendingArtworkRequestsFromVisibleSet();
            HasPendingArtworkChanges = true;
            UpdateArtworkSummary();
            NotifyPendingChangeRowsChanged();
            NotifyUnsavedChangesChanged();
        }
        catch (OperationCanceledException) when (
            lease.Cancellation
                .IsCancellationRequested)
        {
        }
        finally
        {
            CompleteArtworkMutation(
                lease);
        }
    }

    public void RemoveArtworkItem(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        item.PropertyChanged -= OnArtworkItemChanged;
        _artworkSummaryFactories.Remove(item);
        ArtworkItems.Remove(item);
        ArtworkSource = ArtworkItems.FirstOrDefault()?.Source;
        SynchronizePendingArtworkRequestsFromVisibleSet();
        HasPendingArtworkChanges = true;
        UpdateArtworkSummary();
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
    }

    public async Task ReplaceArtworkItemAsync(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        ArtworkMutationLease lease =
            BeginArtworkMutation();
        try
        {
            string? path = await _files.PickFileAsync(
                L("Inspector.Picker.ChooseReplacementArtwork"),
                [new FilePickerType(
                    L("Inspector.Picker.Images"),
                    [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
            if (path is null ||
                !CanPublishArtworkMutation(
                    lease) ||
                !ArtworkItems.Contains(item))
                return;

            PreparedImage? prepared =
                await _artwork
                    .PrepareFromFileAsync(
                        path,
                        ArtworkMaxDimension,
                        lease.Cancellation.Token);
            if (!CanPublishArtworkMutation(
                    lease) ||
                !ArtworkItems.Contains(item))
                return;
            if (prepared is null)
            {
                SetStatus(
                    MessageTone.Error,
                    "Inspector.Status.ImagePreparationFailed");
                return;
            }

            object? source =
                await _thumbnails
                    .CreateImageSourceAsync(
                        prepared.Data,
                        cancellationToken:
                            lease.Cancellation
                                .Token);
            if (!CanPublishArtworkMutation(
                    lease) ||
                !ArtworkItems.Contains(item))
                return;
            item.ReplaceContent(
                source,
                prepared.MimeType,
                prepared.Data,
                PreparedArtworkDetails(prepared));
            _artworkSummaryFactories[item] =
                () => PreparedArtworkDetails(prepared);
            if (ReferenceEquals(ArtworkItems.FirstOrDefault(), item))
                ArtworkSource = item.Source;
            SynchronizePendingArtworkRequestsFromVisibleSet();
            HasPendingArtworkChanges = true;
            UpdateArtworkSummary();
            NotifyPendingChangeRowsChanged();
            SetStatus(
                MessageTone.Info,
                "Inspector.Status.ArtworkReplacementReady");
            NotifyUnsavedChangesChanged();
        }
        catch (OperationCanceledException) when (
            lease.Cancellation
                .IsCancellationRequested)
        {
        }
        finally
        {
            CompleteArtworkMutation(
                lease);
        }
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
        string typeName =
            InvariantArtworkTypeFileToken(
                item.Type);
        int typeCount = ArtworkItems.Count(candidate => candidate.Type == item.Type);
        int typeIndex = ArtworkItems.Take(ArtworkItems.IndexOf(item) + 1)
            .Count(candidate => candidate.Type == item.Type);
        string ordinal = typeCount > 1 ? $"-{typeIndex}" : "";
        string suggestedName = $"{sourceName}-{typeName}{ordinal}{extension}";
        string? path = await _files.SaveFileAsync(
            L("Inspector.Picker.SaveArtwork"),
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

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ScrubArtworkAsync()
    {
        ArtworkMutationLease lease =
            BeginArtworkMutation();
        try
        {
            var requests = new Dictionary<
                string,
                ArtworkSetPreviewRequest>(
                PathComparer);
            var updatedExpectations =
                CreatePendingSourceExpectations()
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        PathComparer);
            foreach (string path in lease.Paths)
            {
                lease.Cancellation.Token
                    .ThrowIfCancellationRequested();
                IReadOnlyList<ArtworkModel> artwork;
                if (_metadataDocuments is not null)
                {
                    MediaDocument loaded =
                        await _metadataDocuments.LoadAsync(
                            path,
                            includeArtwork: true,
                            lease.Cancellation.Token);
                    MergeDocumentExpectation(
                        updatedExpectations,
                        loaded,
                        includeArtworkFingerprint:
                            true);
                    artwork = loaded.Artwork;
                }
                else
                {
                    OperationResult<MediaFileModel> loaded =
                        await _media.LoadAsync(
                            path,
                            includeArtwork: true,
                            lease.Cancellation.Token);
                    if (!loaded.Success ||
                        loaded.Value is null)
                    {
                        if (!CanPublishArtworkMutation(
                                lease))
                            return;
                        SetStatusFailure(
                            "Inspector.Status.ReadArtworkFailed",
                            loaded.Error ??
                            path);
                        return;
                    }
                    MergeMediaModelExpectation(
                        updatedExpectations,
                        loaded.Value,
                        includeArtworkFingerprint:
                            true);
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
            await RefreshSourceIdentitiesAsync(
                updatedExpectations,
                lease.Paths,
                lease.Cancellation.Token);
            if (!CanPublishArtworkMutation(
                    lease))
                return;
            _sourceExpectations =
                updatedExpectations;
            _pendingArtworkRequests = requests;
            HasPendingArtworkChanges = true;
            NotifyPendingChangeRowsChanged();
            NotifyUnsavedChangesChanged();
        }
        catch (OperationCanceledException) when (
            lease.Cancellation
                .IsCancellationRequested)
        {
        }
        catch (Exception error) when (
            error is not OperationCanceledException)
        {
            if (CanPublishArtworkMutation(
                    lease))
                SetStatusFailure(
                    "Inspector.Status.ReadArtworkFailed",
                    error.Message);
        }
        finally
        {
            CompleteArtworkMutation(
                lease);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RemoveArtworkAsync()
    {
        _pendingArtworkRequests =
            Selection.Paths.ToDictionary(
                path => path,
                _ => new ArtworkSetPreviewRequest([]),
                PathComparer);
        ClearArtworkItems();
        ArtworkSource = null;
        IsArtworkMixed = false;
        ArtworkSummary = L(
            "Inspector.Artwork.NoneEmbedded");
        ArtworkSharingSummary = "";
        HasPendingArtworkChanges = true;
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
        NotifyCommands();
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (!await _dialogs.ConfirmDestructiveAsync(
                L("Inspector.Dialog.Revert.Title"),
                L("Inspector.Dialog.Revert.Message"),
                L("Inspector.Dialog.Revert.Confirm")))
            return;
        await LoadAsync(Selection);
    }

    private void NotifyCommands()
    {
        AddArtworkCommand.NotifyCanExecuteChanged();
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
    }

    private void OnArtworkItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        SynchronizePendingArtworkRequestsFromVisibleSet();
        NotifyPendingChangeRowsChanged();
        NotifyUnsavedChangesChanged();
    }

    private void SynchronizePendingArtworkRequestsFromVisibleSet()
    {
        if (_pendingArtworkRequests is null ||
            IsArtworkMixed)
            return;

        int maxDimension = _pendingArtworkRequests
            .Values
            .Select(request =>
                request.MaxDimension)
            .FirstOrDefault();
        var request = new ArtworkSetPreviewRequest(
            [.. CurrentArtworkInputs()],
            maxDimension);
        foreach (string path in Selection.Paths)
            _pendingArtworkRequests[path] = request;
    }

    private void NotifyUnsavedChangesChanged()
    {
        HasPendingArtworkChanges =
            HasArtworkSetEdits();
        OnPropertyChanged(
            nameof(HasUnsavedMetadataChanges));
        OnPropertyChanged(
            nameof(HasUnsavedArtworkChanges));
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
        UpdateArtworkSharingSummary();
    }

    private void UpdateArtworkSharingSummary()
    {
        ArtworkSharingSummary =
            Selection.Paths.Count > 1 &&
            ArtworkItems.Count > 0
                ? LC(
                    "Inspector.Artwork.Shared",
                    Selection.Paths.Count)
                : "";
    }

    internal static string
        InvariantArtworkTypeFileToken(
            ID3v2Util.APICType type) =>
        type switch
        {
            ID3v2Util.APICType.Other =>
                "other",
            ID3v2Util.APICType.FileIcon =>
                "file-icon",
            ID3v2Util.APICType.OtherFileIcon =>
                "other-file-icon",
            ID3v2Util.APICType.FrontCover =>
                "front-cover",
            ID3v2Util.APICType.BackCover =>
                "back-cover",
            ID3v2Util.APICType.LeafletPage =>
                "leaflet-page",
            ID3v2Util.APICType.Media =>
                "media",
            ID3v2Util.APICType.LeadArtist =>
                "lead-artist",
            ID3v2Util.APICType.Arist =>
                "arist",
            ID3v2Util.APICType.Conductor =>
                "conductor",
            ID3v2Util.APICType.Band =>
                "band",
            ID3v2Util.APICType.Composer =>
                "composer",
            ID3v2Util.APICType.Lyricist =>
                "lyricist",
            ID3v2Util.APICType.RecordingLocation =>
                "recording-location",
            ID3v2Util.APICType.DuringRecording =>
                "during-recording",
            ID3v2Util.APICType.DuringPerformance =>
                "during-performance",
            ID3v2Util.APICType.VideoScreenCapture =>
                "video-screen-capture",
            ID3v2Util.APICType.BrightColoredFish =>
                "bright-colored-fish",
            ID3v2Util.APICType.Illustration =>
                "illustration",
            ID3v2Util.APICType.BandLogo =>
                "band-logo",
            ID3v2Util.APICType.StudioLogo =>
                "studio-logo",
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unknown APIC artwork type."),
        };

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

    private static void TryNotify(
        Action notification,
        ICollection<Exception> errors)
    {
        try
        {
            notification();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private static string NormalizePath(
        string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
        }
        catch
        {
            return path.Trim();
        }
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
        {
            ArtworkSummary = L(
                "Inspector.Artwork.Mixed");
            ArtworkSharingSummary = "";
        }
        else
            UpdateArtworkSummary();
        OnPropertyChanged(
            nameof(SelectionSummary));
        OnPropertyChanged(
            nameof(UnsavedChangesSummary));
        NotifyPendingChangeRowsChanged();
    }

    private sealed record ArtworkMutationLease(
        int Generation,
        ImmutableArray<string> Paths,
        CancellationTokenSource Cancellation);

    internal sealed record
        PendingChangesAcceptance(
            ImmutableArray<EditableTagField> Fields,
            ImmutableArray<ArtworkPreviewItem>
                ArtworkItems);

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
