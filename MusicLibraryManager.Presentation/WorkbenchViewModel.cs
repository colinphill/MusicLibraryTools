using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public enum WorkbenchFieldEditMode
{
    Replace,
    Append,
    RemoveValues,
    RemoveField,
}

public enum WorkbenchSection
{
    Session = 0,
    BulkOperation = 1,
    AllFields = 2,
    Files = 3,
    OnlineMetadata = 4,
    Reports = 5,
    Playlists = 6,
    Tools = 7,
    Shortcuts = 8,
}

public enum WorkbenchOnlineMetadataScope
{
    SelectedFile,
    AllFiles,
}

public enum WorkbenchOnlineMetadataProvider
{
    MusicBrainz,
    Discogs,
}

public enum WorkbenchOnlineMetadataResultStep
{
    AudioCandidates = 0,
    MusicBrainzReleases = 1,
    DiscogsReleases = 2,
    TrackMapping = 3,
    Artwork = 4,
}

public sealed record WorkbenchSectionOption(
    WorkbenchSection Section,
    string Group,
    string Label)
{
    public bool HasGroupHeader => Section is
        WorkbenchSection.Session or
        WorkbenchSection.BulkOperation or
        WorkbenchSection.OnlineMetadata or
        WorkbenchSection.Reports or
        WorkbenchSection.Tools;
}

public sealed record WorkbenchOnlineMetadataScopeOption(
    WorkbenchOnlineMetadataScope Scope,
    string Label);

public sealed record WorkbenchOnlineMetadataProviderOption(
    WorkbenchOnlineMetadataProvider Provider,
    string Label);

public sealed record WorkbenchMetadataFieldRow(
    MetadataFieldKey Field,
    string Layers,
    ImmutableArray<string> Values,
    bool IsMixed = false,
    int SelectedFileCount = 1,
    int PresentFileCount = 1)
{
    public string Name => Field.DisplayName;
    public string Kind => Field.IsKnown
        ? LocalizedText.Get("Workbench.Metadata.Kind.Known")
        : LocalizedText.Get("Workbench.Metadata.Kind.Custom");
    public string DisplayValue => IsMixed
        ? LocalizedText.FormatCount(
            "Workbench.Metadata.Mixed",
            SelectedFileCount)
        : string.Join("; ", Values);
    public string Coverage => SelectedFileCount <= 1
        ? ""
        : LocalizedText.Format(
            "Workbench.Metadata.Coverage",
            PresentFileCount,
            SelectedFileCount);
}

public partial class WorkbenchViewModel :
    ObservableObject,
    INavigationGuard,
    IWorkbenchPendingChangeCoordinator
{
    private const string RecentLocationsPreference = "manager.workbench.recentLocations.v1";
    private const string UiPreference = "manager.workbench.ui.v1";
    private const int RecentLocationLimit = 12;
    private readonly IWorkbenchService _workbench;
    private readonly IMetadataOperationService _operations;
    private readonly IAcoustIdDiscoveryService _audioDiscovery;
    private readonly IMusicBrainzMetadataProvider _musicBrainz;
    private readonly IMusicBrainzReleaseMappingService _releaseMapping;
    private readonly ICoverArtArchiveProvider _coverArt;
    private readonly IDiscogsMetadataProvider? _discogs;
    private readonly IDiscogsReleaseMappingService? _discogsMapping;
    private readonly IReportExportService? _reports;
    private readonly IPlaylistWorkspaceService? _playlists;
    private readonly IExternalToolService? _externalTools;
    private readonly IDelimitedMetadataImportService? _delimitedImports;
    private readonly IThumbnailService _thumbnails;
    private readonly IEditHistoryService _history;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IAppSettings _settings;
    private readonly IPlatformService? _platform;
    private readonly WorkbenchSelectionInspectorViewModel? _inspector;
    private readonly IIngestSourceHandoff? _ingestHandoff;
    private readonly INavigationService? _navigation;
    private readonly ILocalizationService? _localization;
    private readonly IAudioTranscodeService? _transcodes;
    private readonly IReviewedFileOperationService?
        _fileOperations;
    private readonly IReviewedChangeHistoryService? _reviewedHistory;
    private readonly IMetadataDocumentService? _metadataDocuments;
    private readonly List<ReviewedFileOperationPlan>
        _fileOperationPlans = [];
    private readonly List<ReviewedMetadataMutationIntent>
        _metadataIntents = [];
    private readonly WorkbenchLocalizedStatusState
        _statusState;
    private MetadataOperationPlan? _plan;
    private ReportExportPlan? _reportPlan;
    private PlaylistWorkspacePlan? _playlistPlan;
    private ExternalToolPlan? _externalToolPlan;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource?
        _artworkHydrationCancellation;
    private IReadOnlyList<WorkbenchTrackViewModel> _selectedFiles = [];
    private long _operationGeneration;
    private int _artworkGeneration;
    private bool _stagedArtworkDirty;
    private string? _stagedArtworkPath;
    private bool _settingArtworkMaxDimension;
    private bool _loadingUiPreferences;
    private readonly Dictionary<
        string,
        ArtworkSetPreviewRequest> _artworkDrafts =
            new(PathComparer);
    private readonly List<AudioTranscodePlan>
        _transcodePlans = [];
    private ImmutableDictionary<Guid, string>
        _transcodeStageDiagnostics =
            ImmutableDictionary<Guid, string>.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewEditsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportDelimitedMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverSelectedAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAllAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverOnlineAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAudioIdentifiersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveSelectedRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchMusicBrainzReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchDiscogsReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadDiscogsReleaseDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildDiscogsReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewDiscogsReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewDiscogsReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseReportOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowsePlaylistOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseExternalToolExecutableCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseExternalToolWorkingDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLocalArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveFrontCoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveAllArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddId3LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveId3LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddApeLayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveApeLayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddId3v1LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveId3v1LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3VersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3v2ToId3v1Command))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3v1ToId3v2Command))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3EncodingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStagedArtworkUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStagedArtworkDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendSelectedToIngestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertPendingChangesCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLocalArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveFrontCoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveAllArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddId3LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveId3LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddApeLayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveApeLayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewAddId3v1LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveId3v1LayerCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3VersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3v2ToId3v1Command))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3v1ToId3v2Command))]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3EncodingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverOnlineAudioCommand))]
    private WorkbenchTrackViewModel? _selectedFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewFieldValuesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyMetadataFieldCommand))]
    private WorkbenchMetadataFieldRow? _selectedMetadataField;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewAudioIdentifiersCommand))]
    private AudioDiscoveryRow? _selectedAudioMatch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewReleaseArtworkCommand))]
    private MusicBrainzReleaseRow? _selectedRelease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadDiscogsReleaseDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildDiscogsReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewDiscogsReleaseArtworkCommand))]
    private DiscogsReleaseRow? _selectedDiscogsRelease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewFieldValuesCommand))]
    private MetadataFieldChoice? _selectedNewKnownField;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewFieldValuesCommand))]
    private string? _customFieldName;

    [ObservableProperty]
    private string? _fieldValuesText;

    [ObservableProperty]
    private WorkbenchFieldEditMode _selectedFieldEditMode =
        WorkbenchFieldEditMode.Replace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportStagedArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStagedArtworkUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStagedArtworkDownCommand))]
    private ArtworkPreviewItem? _selectedStagedArtwork;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewStagedArtworkCommand))]
    private int _artworkMaxDimension;

    [ObservableProperty]
    private DelimitedMetadataEmptyCellMode _importEmptyCellMode =
        DelimitedMetadataEmptyCellMode.Ignore;

    [ObservableProperty]
    private bool _copyPrimaryMetadataToNewLayer = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3VersionCommand))]
    private ID3v2Version _targetId3Version = ID3v2Version.V24;

    [ObservableProperty]
    private bool _dropUnsupportedId3Frames;

    [ObservableProperty]
    private bool _coalesceId3TextValues;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewId3EncodingCommand))]
    private ID3TextEncodingPolicy _selectedId3EncodingPolicy =
        ID3TextEncodingPolicy.Automatic;

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private WorkbenchSection _selectedSection =
        WorkbenchSection.Session;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverOnlineAudioCommand))]
    private WorkbenchOnlineMetadataScopeOption
        _selectedOnlineMetadataScope =
            new(
                WorkbenchOnlineMetadataScope.SelectedFile,
                "");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineReleasesCommand))]
    private WorkbenchOnlineMetadataProviderOption
        _selectedOnlineMetadataProvider =
            new(
                WorkbenchOnlineMetadataProvider.MusicBrainz,
                "MusicBrainz");

    [ObservableProperty]
    private WorkbenchOnlineMetadataResultStep
        _selectedOnlineMetadataResultStep =
            WorkbenchOnlineMetadataResultStep.AudioCandidates;

    [ObservableProperty]
    private bool _hasCompletedOnlineDiscovery;

    [ObservableProperty]
    private bool _hasCompletedOnlineSearch;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;

    public WorkbenchViewModel(
        IWorkbenchService workbench,
        IMetadataOperationService operations,
        IMetadataOperationCatalog operationCatalog,
        IOperationRecipeStore recipeStore,
        IAcoustIdDiscoveryService audioDiscovery,
        IMusicBrainzMetadataProvider musicBrainz,
        IMusicBrainzReleaseMappingService releaseMapping,
        ICoverArtArchiveProvider coverArt,
        IThumbnailService thumbnails,
        IEditHistoryService history,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IAppSettings settings,
        IDiscogsMetadataProvider? discogs = null,
        IDiscogsReleaseMappingService? discogsMapping = null,
        IReportExportService? reports = null,
        IPlaylistWorkspaceService? playlists = null,
        IExternalToolService? externalTools = null,
        IExternalToolStore? externalToolStore = null,
        IWorkbenchShortcutStore? shortcutStore = null,
        IMetadataGridColumnStore? metadataColumns = null,
        IDelimitedMetadataImportService? delimitedImports = null,
        IPlatformService? platform = null,
        IReviewedFileOperationService? fileOperations = null,
        WorkbenchSelectionInspectorViewModel? inspector = null,
        IIngestSourceHandoff? ingestHandoff = null,
        INavigationService? navigation = null,
        ILocalizationService? localization = null,
        IAudioTranscodeService? transcodes = null,
        IAudioTranscodeCapabilityService? transcodeCapabilities = null,
        ITranscodePresetStore? transcodePresets = null,
        ITranscodeWorkScheduler? transcodeScheduler = null,
        IReviewedChangeHistoryService? reviewedHistory = null,
        IMetadataDocumentService? metadataDocuments = null)
    {
        _workbench = workbench;
        _operations = operations;
        _audioDiscovery = audioDiscovery;
        _musicBrainz = musicBrainz;
        _releaseMapping = releaseMapping;
        _coverArt = coverArt;
        _discogs = discogs;
        _discogsMapping = discogsMapping;
        _reports = reports;
        _playlists = playlists;
        _externalTools = externalTools;
        _delimitedImports = delimitedImports;
        _thumbnails = thumbnails;
        _history = history;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _platform = platform;
        _inspector = inspector;
        _ingestHandoff = ingestHandoff;
        _navigation = navigation;
        _localization = localization;
        _statusState = new(localization);
        _statusState.TextChanged +=
            (_, _) => StatusText = _statusState.Text;
        _transcodes = transcodes;
        _fileOperations = fileOperations;
        _reviewedHistory = reviewedHistory;
        _metadataDocuments = metadataDocuments;
        ReleaseSearch = new(localization);
        DiscogsSearch = new(localization);
        if (_inspector is not null)
        {
            _inspector.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is
                    nameof(SelectionInspectorViewModel.HasUnsavedChanges) or
                    nameof(SelectionInspectorViewModel.PendingChangesVersion))
                {
                    RebuildPendingChanges();
                    OnPropertyChanged(nameof(HasUnsavedChanges));
                    ApplyCommand.NotifyCanExecuteChanged();
                }
            };
        }
        PreviewChanges.CollectionChanged +=
            (_, _) => RebuildPendingChanges();
        OperationEditor = new(
            operationCatalog,
            MetadataOperationSurface.Workbench,
            recipeStore,
            localization);
        RepresentativePreview =
            new(_operations, localization);
        FileOperations = fileOperations is null
            ? null
            : new(
                fileOperations,
                files,
                () => EditTargets
                    .Select(file => file.Path)
                    .ToArray(),
                AddPendingFileOperationAsync,
                localization: localization);
        if (FileOperations is not null)
            FileOperations.PropertyChanged +=
                (_, args) =>
                {
                    if (args.PropertyName ==
                        nameof(
                            ReviewedFileOperationEditorViewModel
                                .HasUnsavedChanges))
                        OnPropertyChanged(
                            nameof(HasUnsavedChanges));
                };
        TranscodeEditor =
            transcodes is not null &&
            transcodeCapabilities is not null &&
            transcodePresets is not null &&
            transcodeScheduler is not null
                ? new(
                    transcodes,
                    transcodeCapabilities,
                    transcodePresets,
                    transcodeScheduler,
                    files,
                    dialogs,
                    this,
                    localization)
                : null;
        OperationEditor.Changed +=
            ScheduleRepresentativePreview;
        ShortcutEditor = new(
            shortcutStore,
            recipeStore,
            localization);
        ColumnEditor = new(
            metadataColumns,
            MetadataGridSurface.Workbench,
            localization);
        ReleaseImport.PropertyChanged += OnReleaseImportChanged;
        ReleaseSearch.PropertyChanged += (_, _) =>
        {
            HasCompletedOnlineSearch = false;
            SearchMusicBrainzReleasesCommand.NotifyCanExecuteChanged();
            SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
        };
        DiscogsSearch.PropertyChanged += (_, _) =>
        {
            HasCompletedOnlineSearch = false;
            SearchDiscogsReleasesCommand.NotifyCanExecuteChanged();
            SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
        };
        DiscogsImport.PropertyChanged += (_, _) =>
        {
            CancelPlan();
            PreviewDiscogsReleaseMetadataCommand.NotifyCanExecuteChanged();
        };
        ReportEditor = new(localization);
        PlaylistEditor = new(localization);
        ReportEditor.Changed += InvalidateReportPlan;
        PlaylistEditor.Changed += InvalidatePlaylistPlan;
        ExternalToolEditor = new(
            externalToolStore,
            localization);
        ExternalToolEditor.Changed += InvalidateExternalToolPlan;
        RefreshLocalizedChoices();
        SelectedNewKnownField = KnownFieldChoices[0];
        SetStatus("Workbench.Status.Ready");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
        LoadUiPreferences();
        LoadRecentLocations();
    }

    public ObservableCollection<WorkbenchTrackViewModel> Files { get; } = [];
    public ObservableCollection<MetadataPreviewRow> PreviewChanges { get; } = [];
    public ObservableCollection<MetadataPreviewRow> PendingChanges { get; } = [];
    public bool HasPendingChanges =>
        PendingChanges.Count > 0;
    public ImmutableArray<ReviewedMediaMutationUnit>
        PendingMutationUnits =>
        BuildPendingMutationUnits();
    public ObservableCollection<PendingMetadataOperationRow>
        PendingOperations { get; } = [];
    public ObservableCollection<WorkbenchMetadataFieldRow> MetadataFields { get; } = [];
    public ObservableCollection<AudioDiscoveryRow> AudioMatches { get; } = [];
    public ObservableCollection<MusicBrainzReleaseRow> ReleaseMatches { get; } = [];
    public ObservableCollection<MusicBrainzTrackMappingRow> ReleaseTrackMappings { get; } = [];
    public ObservableCollection<CoverArtCandidateRow> ArtworkMatches { get; } = [];
    public ObservableCollection<ArtworkPreviewItem>
        StagedArtworkItems { get; } = [];
    public ObservableCollection<DiscogsReleaseRow> DiscogsMatches { get; } = [];
    public ObservableCollection<DiscogsTrackMappingRow>
        DiscogsTrackMappings { get; } = [];
    public ObservableCollection<ReportOutputRow> ReportOutputs { get; } = [];
    public ObservableCollection<PlaylistOutputRow>
        PlaylistOutputs { get; } = [];
    public ObservableCollection<ExternalToolInvocationRow>
        ExternalToolInvocations { get; } = [];
    public ObservableCollection<string> RecentLocations { get; } = [];
    public ObservableCollection<WorkbenchSectionOption>
        SectionOptions { get; } = [];
    public ObservableCollection<WorkbenchOnlineMetadataScopeOption>
        OnlineMetadataScopeOptions { get; } = [];
    public ObservableCollection<WorkbenchOnlineMetadataProviderOption>
        OnlineMetadataProviderOptions { get; } = [];
    public bool IsMusicBrainzOnlineMetadataProvider =>
        SelectedOnlineMetadataProvider.Provider ==
        WorkbenchOnlineMetadataProvider.MusicBrainz;
    public bool IsDiscogsOnlineMetadataProvider =>
        SelectedOnlineMetadataProvider.Provider ==
        WorkbenchOnlineMetadataProvider.Discogs;
    public int SelectedOnlineMetadataResultIndex
    {
        get => (int)SelectedOnlineMetadataResultStep;
        set
        {
            if (Enum.IsDefined(
                    typeof(WorkbenchOnlineMetadataResultStep),
                    value))
                SelectedOnlineMetadataResultStep =
                    (WorkbenchOnlineMetadataResultStep)value;
        }
    }
    public WorkbenchSectionOption SelectedSectionOption
    {
        get => SectionOptions.First(option =>
            option.Section == SelectedSection);
        set
        {
            if (value is not null)
                SelectedSection = value.Section;
        }
    }
    public MusicBrainzImportSelectionViewModel ReleaseImport { get; } = new();
    public MusicBrainzReleaseSearchViewModel ReleaseSearch { get; }
    public DiscogsReleaseSearchViewModel DiscogsSearch { get; }
    public DiscogsImportSelectionViewModel DiscogsImport { get; } = new();
    public ReportEditorViewModel ReportEditor { get; }
    public PlaylistEditorViewModel PlaylistEditor { get; }
    public ExternalToolEditorViewModel ExternalToolEditor { get; }
    public WorkbenchShortcutEditorViewModel ShortcutEditor { get; }
    public MetadataGridColumnEditorViewModel ColumnEditor { get; }
    public MetadataOperationEditorViewModel OperationEditor { get; }
    public RepresentativeMetadataPreviewViewModel
        RepresentativePreview { get; }
    public ReviewedFileOperationEditorViewModel?
        FileOperations { get; }
    public TranscodeEditorViewModel?
        TranscodeEditor { get; }
    public SelectionInspectorViewModel? Inspector => _inspector;
    public ObservableCollection<MetadataFieldChoice>
        KnownFieldChoices { get; } = [];
    public IReadOnlyList<WorkbenchFieldEditMode> FieldEditModes { get; } =
        Enum.GetValues<WorkbenchFieldEditMode>();
    public ObservableCollection<LocalizedChoice<WorkbenchFieldEditMode>>
        FieldEditModeChoices { get; } = [];
    public IReadOnlyList<DelimitedMetadataEmptyCellMode>
        ImportEmptyCellModes { get; } =
            Enum.GetValues<DelimitedMetadataEmptyCellMode>();
    public ObservableCollection<
        LocalizedChoice<DelimitedMetadataEmptyCellMode>>
        ImportEmptyCellModeChoices { get; } = [];
    public IReadOnlyList<ID3v2Version> Id3Versions { get; } =
        Enum.GetValues<ID3v2Version>();
    public ObservableCollection<LocalizedChoice<ID3v2Version>>
        Id3VersionChoices { get; } = [];
    public IReadOnlyList<ID3TextEncodingPolicy> Id3EncodingPolicies { get; } =
        Enum.GetValues<ID3TextEncodingPolicy>();
    public ObservableCollection<LocalizedChoice<ID3TextEncodingPolicy>>
        Id3EncodingPolicyChoices { get; } = [];
    public IReadOnlyList<ID3v2Util.APICType> ArtworkTypes { get; } =
        Enum.GetValues<ID3v2Util.APICType>();
    public ObservableCollection<LocalizedChoice<ID3v2Util.APICType>>
        ArtworkTypeChoices { get; } = [];
    public bool HasFiles => Files.Count > 0;
    public IReadOnlyList<WorkbenchTrackViewModel> SelectedFiles =>
        _selectedFiles;
    public int SelectedFileCount => EditTargets.Count;
    public string FieldSelectionSummary => SelectedFileCount switch
    {
        0 => "",
        1 => EditTargets[0].Path,
        _ => LC(
            "Workbench.Selection.Files",
            SelectedFileCount),
    };
    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(StatusDiagnosticDetail);
    public bool HasPreview => PreviewChanges.Count > 0;
    public bool HasUnsavedChanges =>
        _plan is not null ||
        _reportPlan is not null ||
        _playlistPlan is not null ||
        _externalToolPlan is not null ||
        _stagedArtworkDirty ||
        _artworkDrafts.Count > 0 ||
        _inspector?.HasUnsavedChanges == true ||
        FileOperations?.HasUnsavedChanges == true ||
        _fileOperationPlans.Count > 0 ||
        _transcodePlans.Count > 0 ||
        Files.Any(file => file.HasChanges);
    public bool CanUndoLatest =>
        (_history.CanUndo || _reviewedHistory?.CanUndo == true) &&
        !IsBusy;
    public bool CanRedoLatest =>
        (_history.CanRedo || _reviewedHistory?.CanRedo == true) &&
        !IsBusy;
    public bool CanRepeatLatest =>
        _history.Entries.FirstOrDefault()?.Recipe is not null &&
        Files.Count > 0 && !IsBusy;

    public event EventHandler? ReviewChangesRequested;

    public event Func<
        IReadOnlyList<ReviewedFileOperationPlan>,
        Task>? FileOperationsCommitted;

    public event Func<
        IReadOnlyList<ReviewedFileOperationPlan>,
        Task<string?>>? FileOperationsPreflight;

    public event Func<
        IReadOnlyList<ReviewedFileOperationPlan>,
        Task>? FileOperationsApplyFinished;

    public async Task ExecuteShortcutAsync(
        WorkbenchShortcutBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.TargetKind ==
            WorkbenchShortcutTargetKind.Recipe)
        {
            OperationRecipe? recipe =
                OperationEditor.SavedRecipes.FirstOrDefault(candidate =>
                    candidate.Id == binding.RecipeId);
            if (recipe is null)
            {
                SetStatus(
                    "Workbench.Status.ShortcutRecipeMissing",
                    binding.TargetLabel);
                return;
            }
            if (IsBusy || Files.Count == 0)
                return;
            await PreviewAsync((progress, ct) =>
                _operations.PreviewAsync(
                    Files.Select(file => file.Path).ToArray(),
                    recipe,
                    progress,
                    ct));
            SetStatus(
                "Workbench.Status.ShortcutRecipeRegenerated",
                recipe.Name);
            return;
        }

        switch (binding.Command)
        {
            case WorkbenchShortcutCommand.AddFiles:
                if (BrowseFilesCommand.CanExecute(null))
                    await BrowseFilesCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.AddFolder:
                if (BrowseFolderCommand.CanExecute(null))
                    await BrowseFolderCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.PreviewInlineEdits:
                if (PreviewEditsCommand.CanExecute(null))
                    await PreviewEditsCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.PreviewCurrentRecipe:
                if (PreviewOperationCommand.CanExecute(null))
                    await PreviewOperationCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.ApplyReviewedChanges:
                if (!IsBusy &&
                    (PendingChanges.Count > 0 ||
                     HasUnsavedChanges))
                    ReviewChangesRequested?.Invoke(
                        this,
                        EventArgs.Empty);
                break;
            case WorkbenchShortcutCommand.UndoLastApply:
                if (UndoCommand.CanExecute(null))
                    await UndoCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.Redo:
                if (RedoCommand.CanExecute(null))
                    await RedoCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.RepeatLastRecipe:
                if (RepeatCommand.CanExecute(null))
                    await RepeatCommand.ExecuteAsync(null);
                break;
            case WorkbenchShortcutCommand.CancelCurrentOperation:
                if (CancelCommand.CanExecute(null))
                    CancelCommand.Execute(null);
                break;
        }
    }

    partial void OnSelectedFileChanged(WorkbenchTrackViewModel? value)
    {
        if (SelectedOnlineMetadataScope.Scope ==
            WorkbenchOnlineMetadataScope.SelectedFile)
        {
            HasCompletedOnlineDiscovery = false;
            HasCompletedOnlineSearch = false;
        }
        RebuildMetadataFields();
        _ = RebuildStagedArtworkAsync(value);
        ScheduleRepresentativePreview();
        DiscoverSelectedAudioCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedOnlineMetadataProviderChanged(
        WorkbenchOnlineMetadataProviderOption value)
    {
        HasCompletedOnlineSearch = false;
        OnPropertyChanged(
            nameof(IsMusicBrainzOnlineMetadataProvider));
        OnPropertyChanged(
            nameof(IsDiscogsOnlineMetadataProvider));
        SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedOnlineMetadataScopeChanged(
        WorkbenchOnlineMetadataScopeOption value)
    {
        HasCompletedOnlineDiscovery = false;
        HasCompletedOnlineSearch = false;
    }

    partial void OnSelectedOnlineMetadataResultStepChanged(
        WorkbenchOnlineMetadataResultStep value) =>
        OnPropertyChanged(
            nameof(SelectedOnlineMetadataResultIndex));

    partial void OnSelectedMetadataFieldChanged(WorkbenchMetadataFieldRow? value)
    {
        if (value is not null)
            FieldValuesText = value.IsMixed
                ? ""
                : string.Join(Environment.NewLine, value.Values);
    }

    public void SetSelectedFiles(
        IEnumerable<WorkbenchTrackViewModel> files)
    {
        WorkbenchTrackViewModel[] selected = files
            .Where(Files.Contains)
            .Distinct()
            .OrderBy(Files.IndexOf)
            .ToArray();
        if (_selectedFiles.SequenceEqual(selected))
            return;
        ApplySelectedFiles(selected);
        if (_inspector is not null)
            _ = _inspector.TryLoadAsync(CreateInspectorSelection(selected));
    }

    public async Task<bool> TrySetSelectedFilesAsync(
        IEnumerable<WorkbenchTrackViewModel> files)
    {
        WorkbenchTrackViewModel[] selected = files
            .Where(Files.Contains)
            .Distinct()
            .OrderBy(Files.IndexOf)
            .ToArray();
        if (_selectedFiles.SequenceEqual(selected))
            return true;
        if (_inspector is not null &&
            !await _inspector.TryLoadAsync(CreateInspectorSelection(selected)))
            return false;
        ApplySelectedFiles(selected);
        return true;
    }

    public async Task<bool> OpenTranscodeAsync(
        CancellationToken ct = default)
    {
        if (TranscodeEditor is null ||
            IsBusy ||
            EditTargets.Count == 0)
            return false;
        await TranscodeEditor.OpenAsync(
            EditTargets.Select(file => file.Path),
            ct);
        return true;
    }

    public Task<bool> AddPendingMutationAsync(
        ReviewedMediaMutationIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent switch
        {
            ReviewedTranscodeMutationIntent transcode =>
                AddPendingTranscodeAsync(
                    transcode.Plan,
                    ct),
            ReviewedFileOperationMutationIntent fileOperation =>
                AddPendingFileOperationPlanAsync(
                    fileOperation.Plan,
                    ct),
            ReviewedMetadataMutationIntent metadata =>
                AddPendingMetadataPlanAsync(
                    metadata,
                    ct),
            _ => Task.FromResult(false),
        };
    }

    private async Task<bool> AddPendingMetadataPlanAsync(
        ReviewedMetadataMutationIntent intent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_plan is not null &&
            MetadataPlansConflict(
                _plan,
                intent.Plan))
        {
            await _dialogs.ShowMessageAsync(
                L("Workbench.Dialog.PendingConflict.Title"),
                L("Workbench.Dialog.PendingConflict.MetadataPlan"));
            return false;
        }
        if (_plan is not null &&
            MetadataPlanFullyRepresents(
                _plan,
                intent.Plan))
            return true;

        _metadataIntents.Add(intent);
        RecomposeMetadataPlan();
        NotifySessionChanged();
        return true;
    }

    private Task<bool> AddPendingFileOperationAsync(
        ReviewedFileOperationPlan plan) =>
        AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(plan));

    private async Task<bool> AddPendingFileOperationPlanAsync(
        ReviewedFileOperationPlan plan,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        HashSet<string> incoming = plan.Items
            .Select(item => item.SourcePath)
            .ToHashSet(PathComparer);
        bool overlaps = _fileOperationPlans.Any(existing =>
            existing.Items.Any(item =>
                incoming.Contains(item.SourcePath)));
        if (overlaps &&
            !await _dialogs.ConfirmAsync(
                L("Workbench.Dialog.ReplacePendingFileOperation.Title"),
                L("Workbench.Dialog.ReplacePendingFileOperation.Message"),
                L("Workbench.Dialog.ReplacePendingFileOperation.Confirm")))
            return false;

        for (int index = _fileOperationPlans.Count - 1;
             index >= 0;
             index--)
        {
            ReviewedFileOperationPlan existing =
                _fileOperationPlans[index];
            if (!existing.Items.Any(item =>
                    incoming.Contains(
                        item.SourcePath)))
                continue;
            ReviewedFileOperationItem[] retainedItems =
            [
                .. existing.Items.Where(item =>
                    !incoming.Contains(item.SourcePath)),
            ];
            if (retainedItems.Length == 0)
            {
                _fileOperationPlans.RemoveAt(index);
                continue;
            }
            FileMutationAction[] retainedActions =
            [
                .. existing.MutationPlan.Actions.Where(action =>
                    !incoming.Contains(action.SourcePath)),
            ];
            _fileOperationPlans[index] =
                existing with
                {
                    Request = existing.Request with
                    {
                        SourcePaths =
                        [
                            .. retainedItems.Select(item =>
                                item.SourcePath),
                        ],
                    },
                    Items = retainedItems,
                    MutationPlan =
                        existing.MutationPlan with
                        {
                            Actions = retainedActions,
                        },
                };
        }

        _fileOperationPlans.Add(plan);
        RebuildPendingChanges();
        HasApplicablePreview = true;
        SetCountStatus(
            "ReviewedFileOperation.Status.AddedToReview",
            plan.MutationPlan.Actions.Count);
        NotifySessionChanged();
        return true;
    }

    public async Task<bool> AddPendingTranscodeAsync(
        AudioTranscodePlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HashSet<string> incoming =
            plan.Items.Select(item => item.SourcePath)
                .ToHashSet(PathComparer);
        HashSet<Guid> replacedItemIds = _transcodePlans
            .SelectMany(existing => existing.Items)
            .Where(item =>
                incoming.Contains(item.SourcePath))
            .Select(item => item.Id)
            .ToHashSet();
        bool overlaps = _transcodePlans.Any(existing =>
            existing.Items.Any(item =>
                incoming.Contains(item.SourcePath)));
        if (overlaps &&
            !await _dialogs.ConfirmAsync(
                L("Transcode.Dialog.ReplacePending.Title"),
                L("Transcode.Dialog.ReplacePending.Message"),
                L("Transcode.Action.ReplacePending")))
            return false;

        for (int index = _transcodePlans.Count - 1;
             index >= 0;
             index--)
        {
            AudioTranscodePlan existing =
                _transcodePlans[index];
            if (!existing.Items.Any(item =>
                    incoming.Contains(
                        item.SourcePath)))
                continue;
            ImmutableArray<AudioTranscodePlanItem> retained =
            [
                .. existing.Items.Where(item =>
                    !incoming.Contains(item.SourcePath)),
            ];
            if (retained.Length == 0)
                _transcodePlans.RemoveAt(index);
            else
                _transcodePlans[index] =
                    existing with
                    {
                        Items = retained,
                        Request = existing.Request with
                        {
                            SourcePaths =
                            [
                                .. retained.Select(item =>
                                    item.SourcePath),
                            ],
                        },
                    };
        }
        _transcodeStageDiagnostics =
            _transcodeStageDiagnostics.RemoveRange(
                replacedItemIds.Concat(
                    plan.Items.Select(item =>
                        item.Id)));
        _transcodePlans.Add(plan);
        RebuildPendingChanges();
        HasApplicablePreview = true;
        SetCountStatus(
            "Transcode.Status.PendingAdded",
            plan.Items.Length);
        NotifySessionChanged();
        return true;
    }

    private void ApplySelectedFiles(
        WorkbenchTrackViewModel[] selected)
    {
        _selectedFiles = selected;
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(FieldSelectionSummary));
        SendSelectedToIngestCommand
            .NotifyCanExecuteChanged();
        RemoveCurrentCommand
            .NotifyCanExecuteChanged();
        FileOperations?.InvalidateTargets();
        RebuildMetadataFields();
        PreviewFieldValuesCommand.NotifyCanExecuteChanged();
        PasteMetadataFieldCommand.NotifyCanExecuteChanged();
    }

    private static SelectionContext CreateInspectorSelection(
        IEnumerable<WorkbenchTrackViewModel> selected) =>
        new(
            selected.Select(file => file.Path).ToArray(),
            ReadArtworkDirectly: true);

    partial void OnSelectedAudioMatchChanged(AudioDiscoveryRow? value)
    {
        PreviewAudioIdentifiersCommand.NotifyCanExecuteChanged();
        ResolveSelectedRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReleaseChanged(MusicBrainzReleaseRow? value)
    {
        ClearReleaseTrackMappings();
        ArtworkMatches.Clear();
        SelectedArtworkMatch = null;
        BuildReleaseMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDiscogsReleaseChanged(
        DiscogsReleaseRow? value)
    {
        ClearDiscogsTrackMappings();
        BuildDiscogsReleaseMappingCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewReleaseArtworkCommand))]
    private CoverArtCandidateRow? _selectedArtworkMatch;

    partial void OnSelectedArtworkMatchChanged(CoverArtCandidateRow? value) =>
        CancelPlan();

    [RelayCommand]
    private void BeginNewKnownField()
    {
        SelectedMetadataField = null;
        CustomFieldName = null;
        FieldValuesText = "";
    }

    [RelayCommand]
    private void BeginNewCustomField()
    {
        SelectedMetadataField = null;
        FieldValuesText = "";
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFilesAsync()
    {
        IReadOnlyList<string> paths = await _files.PickFilesAsync(
            L("Workbench.Picker.AddSources.Title"),
            [new(L("Workbench.Picker.SupportedSources"),
                [".mp3", ".flac", ".ogg", ".wv", ".m4a", ".mp4", ".m4p", ".m4r",
                 ".m4b", ".m4v", ".wma", ".asf", ".wmv",
                 ".dsf", ".m3u", ".m3u8", ".cue"])]);
        if (paths.Count > 0)
            await AddSourcesAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFolderAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Workbench.Picker.AddFolder.Title"));
        if (path is not null)
            await AddSourcesAsync([path]);
    }

    [RelayCommand]
    private async Task AddRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            await AddSourcesAsync([path]);
    }

    public async Task AddSourcesAsync(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0 || IsBusy)
            return;
        CancelPlan();
        ClearReleaseTrackMappings();
        BeginOperation(L("Workbench.Activity.ScanningSources"));
        try
        {
            WorkbenchLoadResult loaded = await _workbench.LoadAsync(
                new(sources, Recursive),
                CreateProgress(),
                _cancellation!.Token);
            var existing = Files.Select(file => file.Path)
                .ToHashSet(PathComparer);
            int added = 0;
            foreach (MediaDocument document in loaded.Documents)
            {
                if (!existing.Add(document.Path))
                    continue;
                AddTrack(new(document));
                added++;
            }
            foreach (string source in sources)
                AddRecentLocation(source);
            if (loaded.Issues.Length == 0)
                SetCountStatus(
                    "Workbench.Status.SourcesAdded",
                    added,
                    Files.Count);
            else
            {
                SetStatus(
                    WorkbenchStatusTexts
                        .SourcesAddedWithWarnings(
                            added,
                            Files.Count,
                            loaded.Issues.Length));
                StatusDiagnosticDetail =
                    loaded.Issues[0].Message;
            }
            if (_metadataDocuments is null &&
                SelectedFile is
                {
                    HasAuthoritativeArtwork: false,
                })
                AppendStatusDiagnostics(
                [
                    L(
                        "Inspector.Error.MetadataServiceUnavailable"),
                ]);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Workbench.Status.LoadingCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.LoadingFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    public async Task<bool> AcceptHandoffAsync(
        WorkbenchHandoffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CapturedPaths.IsDefaultOrEmpty || IsBusy)
            return false;

        await AddSourcesAsync(request.CapturedPaths);

        var requestedPaths = request.CapturedPaths
            .ToHashSet(PathComparer);
        WorkbenchTrackViewModel[] capturedFiles =
        [
            .. Files.Where(file =>
                requestedPaths.Contains(file.Path)),
        ];
        if (capturedFiles.Length == 0)
            return false;

        if (!await TrySetSelectedFilesAsync(capturedFiles))
            return false;

        SelectedSection = request.DestinationSection;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCurrent))]
    private void RemoveCurrent()
    {
        WorkbenchTrackViewModel[] removing =
        [
            .. SelectedFiles.Where(Files.Contains),
        ];
        if (removing.Length == 0 &&
            SelectedFile is { } current &&
            Files.Contains(current))
        {
            removing =
            [
                current,
            ];
        }
        if (removing.Length == 0)
            return;
        int index = removing
            .Select(Files.IndexOf)
            .Where(candidate => candidate >= 0)
            .DefaultIfEmpty(0)
            .Min();
        HashSet<string> removedPaths =
            removing
                .Select(file => file.Path)
                .ToHashSet(PathComparer);
        foreach (WorkbenchTrackViewModel file in
                 removing)
        {
            file.PropertyChanged -= OnTrackChanged;
            Files.Remove(file);
            _artworkDrafts.Remove(file.Path);
        }
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        foreach (AudioDiscoveryRow row in AudioMatches
                     .Where(row =>
                         removedPaths.Contains(row.Path))
                     .ToArray())
            AudioMatches.Remove(row);
        foreach (MusicBrainzReleaseRow row in ReleaseMatches
                     .Where(row =>
                         removedPaths.Contains(
                             row.SourcePath))
                     .ToArray())
            ReleaseMatches.Remove(row);
        if (SelectedRelease is not null &&
            removedPaths.Contains(
                SelectedRelease.SourcePath))
            SelectedRelease = null;
        ClearReleaseTrackMappings();
        WorkbenchTrackViewModel? next =
            Files.Count == 0
            ? null
            : Files[Math.Min(index, Files.Count - 1)];
        SelectedFile = next;
        SetSelectedFiles(
            next is null
                ? []
                :
                [
                    next,
                ]);
        CancelPlan();
        SetCountStatus(
            "Workbench.Status.SessionFileCount",
            Files.Count);
        NotifySessionChanged();
    }

    private bool CanSendSelectedToIngest() =>
        !IsBusy &&
        _ingestHandoff is not null &&
        _navigation is not null &&
        SelectedFiles.Count > 0;

    [RelayCommand(
        CanExecute =
            nameof(CanSendSelectedToIngest))]
    private void SendSelectedToIngest()
    {
        if (_ingestHandoff is null ||
            _navigation is null)
            return;
        string[] paths = SelectedFiles
            .Select(file => file.Path)
            .ToArray();
        IngestSourceHandoffResult result =
            _ingestHandoff.SetSourceFiles(paths);
        if (!result.Accepted)
        {
            SetFailure(
                "Workbench.Status.SendToIngestFailed",
                result.Error);
            return;
        }
        SetCountStatus(
            "Workbench.Status.SentToIngest",
            paths.Length);
        _navigation.Navigate(
            ShellDestination.Ingest);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Files.Count == 0)
            return;
        if (HasUnsavedChanges && !await _dialogs.ConfirmDestructiveAsync(
                L("Workbench.Dialog.Clear.Title"),
                L("Workbench.Dialog.Clear.Message"),
                L("Common.Clear")))
            return;
        foreach (WorkbenchTrackViewModel file in Files)
            file.PropertyChanged -= OnTrackChanged;
        Files.Clear();
        _artworkDrafts.Clear();
        HasCompletedOnlineDiscovery = false;
        HasCompletedOnlineSearch = false;
        AudioMatches.Clear();
        ReleaseMatches.Clear();
        SelectedRelease = null;
        ClearReleaseTrackMappings();
        PreviewChanges.Clear();
        PendingChanges.Clear();
        PendingOperations.Clear();
        _metadataIntents.Clear();
        _plan = null;
        _fileOperationPlans.Clear();
        _transcodePlans.Clear();
        _transcodeStageDiagnostics =
            ImmutableDictionary<Guid, string>.Empty;
        RebuildPendingChanges();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        SelectedFile = null;
        ApplySelectedFiles([]);
        if (_inspector is not null)
            await _inspector.LoadAsync(SelectionContext.Empty);
        SetStatus("Workbench.Status.Cleared");
        NotifySessionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        int index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        if (index <= 0)
            return;
        Files.Move(index, index - 1);
        FileOperations?.InvalidateTargets();
        CancelPlan();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        int index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        if (index < 0 || index >= Files.Count - 1)
            return;
        Files.Move(index, index + 1);
        FileOperations?.InvalidateTargets();
        CancelPlan();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPreviewEdits))]
    private async Task PreviewEditsAsync()
    {
        var edits = Files.Where(file => file.HasChanges)
            .ToDictionary(
                file => file.Path,
                file => (IReadOnlyList<TagEdit>)file.CreateEdits(),
                PathComparer);
        if (edits.Count == 0)
            return;
        await PreviewAsync((progress, ct) => _operations.PreviewEditsAsync(
            edits,
            L("Workbench.Operation.FieldEdits"),
            progress,
            ct));
    }

    [RelayCommand(CanExecute = nameof(CanImportDelimitedMetadata))]
    private async Task ImportDelimitedMetadataAsync()
    {
        if (_delimitedImports is null)
            return;
        string? path = await _files.PickFileAsync(
            L("Workbench.Picker.ImportMetadata.Title"),
            [new(
                L("Workbench.Picker.DelimitedMetadata"),
                [".csv", ".tsv", ".txt"])]);
        if (path is null)
            return;
        DelimitedMetadataImportResult? imported = null;
        await PreviewAsync(async (progress, ct) =>
        {
            imported = await _delimitedImports.ImportAsync(
                path,
                Files.Select(file => file.Path).ToArray(),
                new(EmptyCellMode: ImportEmptyCellMode),
                progress: progress,
                ct: ct);
            if (!imported.CanPreview)
            {
                string reason = imported.Issues
                    .FirstOrDefault(issue =>
                        issue.Severity ==
                            DelimitedMetadataImportIssueSeverity.Blocker)
                    ?.Message ??
                    L("Workbench.Error.NoImportRowsMatched");
                throw new InvalidDataException(reason);
            }
            return await _operations.PreviewValueEditsAsync(
                imported.EditsByPath,
                LF(
                    "Workbench.Operation.ImportMetadata",
                    Path.GetFileName(path)),
                progress,
                ct);
        });
        if (_plan is not null && imported is not null)
        {
            int warnings = imported.Issues.Count(issue =>
                issue.Severity ==
                    DelimitedMetadataImportIssueSeverity.Warning);
            SetStatus(
                warnings == 0
                    ? WorkbenchStatusTexts.ImportMapped(
                        imported.MatchedRows,
                        imported.DataRows)
                    : WorkbenchStatusTexts
                        .ImportMappedWithWarnings(
                            imported.MatchedRows,
                            imported.DataRows,
                            warnings));
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewOperation))]
    private async Task PreviewOperationAsync()
    {
        OperationRecipe recipe = OperationEditor.CreateRecipe();
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            Files.Select(file => file.Path).ToArray(), recipe, progress, ct));
    }

    [RelayCommand(CanExecute = nameof(CanPreviewFieldValues))]
    private async Task PreviewFieldValuesAsync()
    {
        IReadOnlyList<WorkbenchTrackViewModel> targets =
            EditTargets;
        if (targets.Count == 0)
            return;
        MetadataFieldKey field = ResolveEditedField()!;

        ImmutableArray<string> entered = (FieldValuesText ?? "")
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(value => value.Length > 0)
            .ToImmutableArray();
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>> edits =
                BuildValueEdits(
                    targets,
                    field,
                    SelectedFieldEditMode,
                    entered);
        await PreviewAsync((progress, ct) => _operations.PreviewValueEditsAsync(
            edits,
            LF(
                "Workbench.Operation.EditFieldValues",
                field.DisplayName,
                targets.Count),
            progress,
            ct));
    }

    [RelayCommand(CanExecute = nameof(CanCopyMetadataField))]
    private async Task CopyMetadataFieldAsync()
    {
        if (_platform is null ||
            SelectedMetadataField is not { } selected)
            return;
        ImmutableArray<string> values =
            SelectedFile?.Document.Values(selected.Field) ??
            selected.Values;
        string text = MetadataClipboardCodec.Encode(
            new(selected.Field, values));
        await _platform.CopyTextAsync(text);
        SetCountStatus(
            "Workbench.Status.CopiedFieldValues",
            values.Length,
            selected.Name);
    }

    [RelayCommand(CanExecute = nameof(CanPasteMetadataField))]
    private async Task PasteMetadataFieldAsync()
    {
        if (_platform is null)
            return;
        IReadOnlyList<WorkbenchTrackViewModel> targets =
            EditTargets;
        string? text = await _platform.ReadTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            SetStatus(
                "Workbench.Status.ClipboardHasNoMetadata");
            return;
        }

        try
        {
            MetadataFieldKey? fallback = ResolveEditedField();
            if (!MetadataClipboardCodec.TryDecode(
                    text,
                    out MetadataClipboardPayload? payload) &&
                fallback is null)
            {
                SetStatus(
                    "Workbench.Status.SelectPasteDestination");
                return;
            }
            payload ??= MetadataClipboardCodec.DecodeOrPlainText(
                text,
                fallback!);
            FieldValuesText = string.Join(
                Environment.NewLine,
                payload.Values);
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>> edits =
                    BuildValueEdits(
                        targets,
                        payload.Field,
                        WorkbenchFieldEditMode.Replace,
                        payload.Values);
            await PreviewAsync((progress, ct) =>
                _operations.PreviewValueEditsAsync(
                    edits,
                    LF(
                        "Workbench.Operation.PasteFieldValues",
                        payload.Field.DisplayName,
                        targets.Count),
                    progress,
                    ct));
        }
        catch (InvalidDataException error)
        {
            SetFailure(
                "Workbench.Status.PasteFailed",
                error.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewAddId3Layer))]
    private Task PreviewAddId3LayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.Id3v2, TagLayerEditMode.Add);

    [RelayCommand(CanExecute = nameof(CanPreviewRemoveId3Layer))]
    private Task PreviewRemoveId3LayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.Id3v2, TagLayerEditMode.Remove);

    [RelayCommand(CanExecute = nameof(CanPreviewAddApeLayer))]
    private Task PreviewAddApeLayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.ApeV2, TagLayerEditMode.Add);

    [RelayCommand(CanExecute = nameof(CanPreviewRemoveApeLayer))]
    private Task PreviewRemoveApeLayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.ApeV2, TagLayerEditMode.Remove);

    [RelayCommand(CanExecute = nameof(CanPreviewAddId3v1Layer))]
    private Task PreviewAddId3v1LayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.Id3v1, TagLayerEditMode.Add);

    [RelayCommand(CanExecute = nameof(CanPreviewRemoveId3v1Layer))]
    private Task PreviewRemoveId3v1LayerAsync() =>
        PreviewTagLayerAsync(TagLayerKind.Id3v1, TagLayerEditMode.Remove);

    private async Task PreviewTagLayerAsync(
        TagLayerKind kind,
        TagLayerEditMode mode)
    {
        if (SelectedFile is null)
            return;
        var edits = new Dictionary<string, IReadOnlyList<TagLayerEdit>>(
            PathComparer)
        {
            [SelectedFile.Path] =
            [
                new(
                    kind,
                    mode,
                    CopyPrimaryMetadataToNewLayer
                        ? TagLayerCopyMode.CopyPrimary
                        : TagLayerCopyMode.Empty),
            ],
        };
        string layerName = kind switch
        {
            TagLayerKind.Id3v2 => "ID3v2",
            TagLayerKind.Id3v1 => "ID3v1",
            TagLayerKind.ApeV2 => "APEv2",
            _ => kind.ToString(),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewTagLayerEditsAsync(
                edits,
                LF(
                    mode == TagLayerEditMode.Add
                        ? "Workbench.Operation.AddTagLayer"
                        : "Workbench.Operation.RemoveTagLayer",
                    layerName),
                progress,
                ct),
            ReviewedMediaMutationKind.TagLayers);
    }

    private bool CanPreviewAddId3Layer() =>
        CanEditTagLayer(TagLayerKind.Id3v2, add: true);

    private bool CanPreviewRemoveId3Layer() =>
        CanEditTagLayer(TagLayerKind.Id3v2, add: false);

    private bool CanPreviewAddApeLayer() =>
        CanEditTagLayer(TagLayerKind.ApeV2, add: true);

    private bool CanPreviewRemoveApeLayer() =>
        CanEditTagLayer(TagLayerKind.ApeV2, add: false);

    private bool CanPreviewAddId3v1Layer() =>
        CanEditTagLayer(TagLayerKind.Id3v1, add: true);

    private bool CanPreviewRemoveId3v1Layer() =>
        CanEditTagLayer(TagLayerKind.Id3v1, add: false);

    private bool CanEditTagLayer(TagLayerKind kind, bool add)
    {
        TagLayerDescriptor? layer =
            SelectedFile?.Document.EditableTagLayers.FirstOrDefault(
                candidate => candidate.Kind == kind);
        return !IsBusy && layer is not null &&
            (add ? layer.CanAdd : layer.CanRemove);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewId3Version))]
    private async Task PreviewId3VersionAsync()
    {
        if (SelectedFile is null)
            return;
        var edits = new Dictionary<string, Id3VersionEdit>(
            PathComparer)
        {
            [SelectedFile.Path] = new(
                TargetId3Version,
                DropUnsupportedId3Frames,
                CoalesceId3TextValues),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewId3VersionEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.ConvertId3Version",
                    (int)TargetId3Version),
                progress,
                ct),
            ReviewedMediaMutationKind.TagLayers);
    }

    private bool CanPreviewId3Version() =>
        !IsBusy &&
        SelectedFile?.Document.Id3Version is { } source &&
        source != TargetId3Version;

    [RelayCommand(CanExecute = nameof(CanPreviewId3v2ToId3v1))]
    private Task PreviewId3v2ToId3v1Async() =>
        PreviewTagLayerConversionAsync(
            TagLayerKind.Id3v2, TagLayerKind.Id3v1);

    [RelayCommand(CanExecute = nameof(CanPreviewId3v1ToId3v2))]
    private Task PreviewId3v1ToId3v2Async() =>
        PreviewTagLayerConversionAsync(
            TagLayerKind.Id3v1, TagLayerKind.Id3v2);

    private async Task PreviewTagLayerConversionAsync(
        TagLayerKind source,
        TagLayerKind target)
    {
        if (SelectedFile is null)
            return;
        var edits = new Dictionary<string, TagLayerConversionEdit>(
            PathComparer)
        {
            [SelectedFile.Path] = new(source, target),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewTagLayerConversionsAsync(
                edits,
                LF(
                    "Workbench.Operation.ConvertTagLayer",
                    source,
                    target),
                progress,
                ct),
            ReviewedMediaMutationKind.TagLayers);
    }

    private bool CanPreviewId3v2ToId3v1() =>
        !IsBusy &&
        SelectedFile?.HasId3Tag == true &&
        SelectedFile.Document.EditableTagLayers.Any(layer =>
            layer.Kind == TagLayerKind.Id3v1);

    private bool CanPreviewId3v1ToId3v2() =>
        !IsBusy &&
        SelectedFile?.Document.EditableTagLayers.Any(layer =>
            layer.Kind == TagLayerKind.Id3v1 &&
            layer.IsPresent) == true;

    [RelayCommand(CanExecute = nameof(CanPreviewId3Encoding))]
    private async Task PreviewId3EncodingAsync()
    {
        if (SelectedFile?.Document.Id3Version is not { } version)
            return;
        var edits = new Dictionary<string, Id3VersionEdit>(
            PathComparer)
        {
            [SelectedFile.Path] = new(
                version,
                TextEncodingPolicy: SelectedId3EncodingPolicy),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewId3VersionEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.ReencodeId3",
                    L(
                        TechnicalLabelResourceKeys.For(
                            SelectedId3EncodingPolicy) ??
                        $"Workbench.Choice.Id3EncodingPolicy.{SelectedId3EncodingPolicy}")),
                progress,
                ct),
            ReviewedMediaMutationKind.TagLayers);
    }

    private bool CanPreviewId3Encoding() =>
        !IsBusy &&
        SelectedFile?.Document.Id3Version is { } version &&
        (SelectedId3EncodingPolicy != ID3TextEncodingPolicy.Utf8 ||
         version == ID3v2Version.V24);

    private async Task PreviewAsync(
        Func<IProgress<OperationProgress>, CancellationToken,
            Task<MetadataOperationPlan>> action,
        ReviewedMediaMutationKind mutationKind =
            ReviewedMediaMutationKind.Metadata)
    {
        BeginOperation(L("Workbench.Activity.BuildingPreview"));
        try
        {
            MetadataOperationPlan plan = await action(
                CreateProgress(), _cancellation!.Token);
            bool accepted = await AddPendingMutationAsync(
                ReviewedMetadataMutationIntent.Create(
                    plan,
                    mutationKind),
                _cancellation.Token);
            if (!accepted)
            {
                SetFailure(
                    "Workbench.Status.PendingChangesBlocked",
                    L(
                        "Workbench.Dialog.PendingConflict.PendingMutationRejected"));
                return;
            }
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountStatus(
                    "Workbench.Status.PreviewBlocked",
                    blockers);
            else
                SetStatus(
                    WorkbenchStatusTexts.PreviewReady(
                        plan.ChangeCount,
                        plan.ChangedFileCount));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Workbench.Status.PreviewCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ImmutableArray<ReviewedMetadataMutationIntent>
            metadataIntentsInApplyBatch =
            [
                .. _metadataIntents,
            ];
        ImmutableArray<ReviewedFileOperationPlan>
            filePlansInApplyBatch =
                CaptureFileOperationApplyBatch();
        ImmutableArray<AudioTranscodePlan>
            transcodePlansInApplyBatch =
            [
                .. OrderedTranscodePlans(),
            ];
        PendingDirectChangesCapture?
            directChangesCapture =
                HasDirectPendingChanges
                    ? CaptureDirectPendingChanges()
                    : null;
        MetadataOperationPlan? metadataPlanInApplyBatch =
            ComposeMetadataPlan(
                metadataIntentsInApplyBatch);

        if (metadataPlanInApplyBatch is null &&
            directChangesCapture is null &&
            filePlansInApplyBatch.Length == 0 &&
            transcodePlansInApplyBatch.Length == 0)
            return;
        if (!TryPrevalidatePendingMutations(
                metadataPlanInApplyBatch,
                filePlansInApplyBatch,
                transcodePlansInApplyBatch,
                out string? preflightFailure))
        {
            SetFailure(
                "Workbench.Status.PendingChangesBlocked",
                preflightFailure);
            return;
        }
        BeginOperation(
            L("Workbench.Activity.ApplyingReviewedChanges"));
        MetadataOperationStageResult? metadataStage = null;
        var postCommitWarnings = new List<string>();
        var postCommitReconciliations =
            new List<PostCommitReconciliationHandle>();
        bool committedAny = false;
        int changed = 0;
        try
        {
            IProgress<OperationProgress> progress = CreateProgress();
            if (filePlansInApplyBatch.Length > 0)
            {
                filePlansInApplyBatch =
                    await RefreshPendingFileOperationPlansAsync(
                    filePlansInApplyBatch,
                    progress,
                    _cancellation!.Token);
                if (!TryPrevalidatePendingMutations(
                        metadataPlanInApplyBatch,
                        filePlansInApplyBatch,
                        transcodePlansInApplyBatch,
                        out preflightFailure))
                {
                    SetFailure(
                        "Workbench.Status.PendingChangesBlocked",
                        preflightFailure);
                    return;
                }
            }
            if (metadataPlanInApplyBatch is not null ||
                directChangesCapture is not null)
            {
                if (directChangesCapture is not null)
                {
                    ReviewedMetadataMutationIntent?
                        directIntent =
                        await PreviewDirectPendingChangesAsync(
                            directChangesCapture,
                            progress,
                            _cancellation!.Token);
                    if (directIntent is not null)
                    {
                        if (metadataPlanInApplyBatch is not null &&
                            MetadataPlansConflict(
                                metadataPlanInApplyBatch,
                                directIntent.Plan))
                        {
                            SetFailure(
                                "Workbench.Status.PendingChangesBlocked",
                                L(
                                    "Workbench.Dialog.PendingConflict.PendingMutationRejected"));
                            return;
                        }
                        if (metadataPlanInApplyBatch is null ||
                            !MetadataPlanFullyRepresents(
                                metadataPlanInApplyBatch,
                                directIntent.Plan))
                        {
                            // Persist the captured direct intent so a
                            // pre-commit failure or partial transcode keeps
                            // exactly the uncommitted portion retryable.
                            // Public intents added after Apply entry are not
                            // included in this immutable batch.
                            _metadataIntents.Add(
                                directIntent);
                            metadataIntentsInApplyBatch =
                            [
                                .. metadataIntentsInApplyBatch,
                                directIntent,
                            ];
                            metadataPlanInApplyBatch =
                                ComposeMetadataPlan(
                                    metadataIntentsInApplyBatch);
                        }
                    }
                }
                if (!TryPrevalidatePendingMutations(
                        metadataPlanInApplyBatch,
                        filePlansInApplyBatch,
                        transcodePlansInApplyBatch,
                        out preflightFailure))
                {
                    SetFailure(
                        "Workbench.Status.PendingChangesBlocked",
                        preflightFailure);
                    return;
                }
                if (metadataPlanInApplyBatch is not null)
                {
                    MetadataPreviewRowBuilder.Populate(
                        PreviewChanges,
                        metadataPlanInApplyBatch,
                        _localization);
                    PendingMetadataOperationRowBuilder.Populate(
                        PendingOperations,
                        metadataPlanInApplyBatch,
                        _localization);
                    HasApplicablePreview =
                        metadataPlanInApplyBatch.CanApply;
                    if (!metadataPlanInApplyBatch.CanApply)
                    {
                        OperationIssue? blocker =
                            metadataPlanInApplyBatch.Files
                            .SelectMany(file => file.Issues)
                            .FirstOrDefault(issue =>
                                issue.Severity ==
                                OperationIssueSeverity.Blocker);
                        if (blocker is null)
                            SetStatus(
                                "Workbench.Status.NoApplicableChanges");
                        else
                            SetFailure(
                                "Workbench.Status.PendingChangesBlocked",
                                blocker.Message);
                        return;
                    }
                    if (transcodePlansInApplyBatch.Length > 0)
                    {
                        metadataStage =
                            await _operations.StageAsync(
                                metadataPlanInApplyBatch,
                                progress,
                                _cancellation!.Token);
                    }
                    else
                    {
                        MetadataOperationPlan committedPlan =
                            metadataPlanInApplyBatch;
                        MetadataApplyResult result =
                            await _operations.ApplyAsync(
                                committedPlan,
                                progress,
                                _cancellation!.Token);
                        string[] paths = committedPlan.Files
                            .Where(file => file.HasChanges)
                            .Select(file => file.Path)
                            .ToArray();
                        changed += result.ChangedFiles;
                        committedAny = true;
                        AddPostCommitIssues(
                            result.Issues,
                            postCommitWarnings);
                        RemoveCapturedMetadataIntents(
                            metadataIntentsInApplyBatch);
                        _plan = ComposeMetadataPlan();
                        ConsumeCommittedArtworkDrafts(paths);
                        AcceptCapturedDirectPendingChanges(
                            directChangesCapture,
                            paths,
                            postCommitWarnings);
                        await CapturePostCommitWarningAsync(
                            () =>
                            {
                                RefreshMetadataPlanPresentation();
                                return Task.CompletedTask;
                            },
                            postCommitWarnings);
                        await FinalizeMetadataCommitAsync(
                            paths,
                            progress,
                            postCommitWarnings);
                    }
                }
            }

            IReadOnlySet<string>? retainedTranscodeSources =
                null;
            if (transcodePlansInApplyBatch.Length > 0)
            {
                IReadOnlyDictionary<string, string>
                    sourceOverrides = metadataStage?.Files
                        .ToDictionary(
                            file => file.LivePath,
                            file => file.StagedPath,
                            PathComparer) ??
                        new Dictionary<string, string>(
                            PathComparer);
                PendingTranscodeApplyOutcome outcome =
                    await ApplyPendingTranscodesAsync(
                    progress,
                    _cancellation!.Token,
                    metadataStage,
                    sourceOverrides,
                    postCommitWarnings,
                    directChangesCapture,
                    metadataIntentsInApplyBatch,
                    filePlansInApplyBatch,
                    transcodePlansInApplyBatch,
                    postCommitReconciliations);
                if (!outcome.Proceed)
                {
                    SetStatus(
                        "Workbench.Status.ApplyFailed");
                    AppendStatusDiagnostics(
                    [
                        L("Common.Back"),
                    ]);
                    return;
                }
                committedAny |=
                    outcome.Committed;
                changed += outcome.ChangedFiles;
                if (outcome.AppliedMetadata is not null)
                    await CapturePostCommitWarningAsync(
                        () => _operations
                            .CompleteStagedApplyAsync(
                                outcome.AppliedMetadata,
                                [],
                                recordHistory: false,
                                CancellationToken.None),
                        postCommitWarnings);
                if (outcome.ConsumedMetadataPaths.Length > 0)
                {
                    string[] appliedPaths =
                    [
                        .. outcome
                            .ConsumedMetadataPaths,
                    ];
                    await FinalizeMetadataCommitAsync(
                        [
                            .. appliedPaths.Where(
                                File.Exists),
                        ],
                        progress,
                        postCommitWarnings);
                }
                retainedTranscodeSources =
                    outcome.RetainedSourcePaths
                        .ToHashSet(PathComparer);
            }

            if (filePlansInApplyBatch.Length > 0)
            {
                int fileChanges =
                    await ApplyPendingFileOperationsAsync(
                        progress,
                        _cancellation!.Token,
                        retainedTranscodeSources,
                        postCommitWarnings,
                        filePlansInApplyBatch,
                        postCommitReconciliations);
                committedAny |= fileChanges > 0;
                changed +=
                    fileChanges;
            }

            CapturePostCommitWarning(
                RebuildPendingChanges,
                postCommitWarnings);
            HasApplicablePreview =
                _transcodePlans.Count > 0 ||
                _fileOperationPlans.Count > 0;
            SetCountStatus(
                "Workbench.Status.Applied",
                changed);
            AppendStatusDiagnostics(
                postCommitWarnings);
        }
        catch (OperationCanceledException)
        {
            if (committedAny)
            {
                SetCountStatus(
                    "Workbench.Status.Applied",
                    changed);
                AppendStatusDiagnostics(
                [
                    L(
                        "Workbench.Status.ApplyCancelled"),
                ]);
            }
            else
                SetStatus(
                    "Workbench.Status.ApplyCancelled");
        }
        catch (PendingFileOperationPreflightException error)
        {
            if (committedAny)
            {
                SetCountStatus(
                    "Workbench.Status.Applied",
                    changed);
                AppendStatusDiagnostics(
                    [error.Message]);
            }
            else
                SetFailure(
                    "Workbench.Status.PendingChangesBlocked",
                    error.Message);
        }
        catch (Exception error)
        {
            if (committedAny)
            {
                SetCountStatus(
                    "Workbench.Status.Applied",
                    changed);
                AppendStatusDiagnostics(
                [error.Message]);
            }
            else
                SetFailure(
                    "Workbench.Status.ApplyFailed",
                    error.Message);
        }
        finally
        {
            if (postCommitReconciliations.Count > 0)
                EndCancellablePhase();
            if (metadataStage is not null)
            {
                try
                {
                    await _operations.DiscardStageAsync(
                        metadataStage,
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    postCommitWarnings.Add(
                        error.Message);
                }
            }
            await ObservePostCommitReconciliationsAsync(
                postCommitReconciliations,
                postCommitWarnings);
            // A composed review can commit the existing metadata/transcode
            // transaction before a later file-operation transaction fails.
            // Rebuild from the retained semantic intents so the drawer never
            // presents already-committed rows as still pending.
            CapturePostCommitWarning(
                RebuildPendingChanges,
                postCommitWarnings);
            try
            {
                EndOperation();
            }
            catch (Exception error)
            {
                postCommitWarnings.Add(
                    error.Message);
            }
            CapturePostCommitWarning(
                NotifySessionChanged,
                postCommitWarnings);
            CapturePostCommitWarning(
                () => AppendStatusDiagnostics(
                    postCommitWarnings),
                postCommitWarnings);
        }
    }

    private bool TryPrevalidatePendingMutations(
        MetadataOperationPlan? metadataPlan,
        IReadOnlyCollection<ReviewedFileOperationPlan>
            fileOperationPlans,
        IReadOnlyCollection<AudioTranscodePlan>
            transcodePlans,
        out string? failure)
    {
        failure = null;
        if (fileOperationPlans.Count > 0 &&
            _fileOperations is null)
        {
            failure =
                L(
                    "Workbench.Dialog.PendingConflict.FileOperationServiceUnavailable");
            return false;
        }
        if (transcodePlans.Count > 0 &&
            _transcodes is null)
        {
            failure =
                L(
                    "Workbench.Dialog.PendingConflict.TranscodeServiceUnavailable");
            return false;
        }
        OperationIssue? metadataBlocker = metadataPlan?.Files
            .SelectMany(file => file.Issues)
            .FirstOrDefault(issue =>
                issue.Severity ==
                OperationIssueSeverity.Blocker);
        if (metadataBlocker is not null)
        {
            failure = metadataBlocker.Message;
            return false;
        }

        foreach (ReviewedFileOperationPlan plan in
                 fileOperationPlans.OrderBy(
                     plan => plan.Items
                         .Select(item => item.SourcePath)
                         .OrderBy(path =>
                             path,
                             PathComparer)
                         .FirstOrDefault() ?? "",
                     PathComparer))
        {
            OperationIssue? blocker = plan.MutationPlan.Issues
                .FirstOrDefault(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
            if (blocker is not null ||
                !plan.CanApply ||
                plan.MutationPlan.Actions.Count == 0)
            {
                failure = blocker?.Message ??
                    L(
                        "Workbench.Dialog.PendingConflict.FileOperationCannotApply");
                return false;
            }
            foreach (FileMutationAction action in
                     plan.MutationPlan.Actions)
            {
                if (!SnapshotMatches(
                        action.ExpectedSource) ||
                    !SnapshotMatches(
                        action.ExpectedDestination))
                {
                    failure =
                        $"A reviewed path changed before apply: " +
                        $"{action.SourcePath}";
                    return false;
                }
            }
            if (!PolicyMatches(
                    plan.MutationPlan))
            {
                failure =
                    "The library policy changed after preview. " +
                    "Preview the operation again before applying it.";
                return false;
            }
        }

        foreach (AudioTranscodePlan plan in
                 transcodePlans.OrderBy(
                     plan => plan.Items
                         .Select(item => item.SourcePath)
                         .OrderBy(path =>
                             path,
                             PathComparer)
                         .FirstOrDefault() ?? "",
                     PathComparer))
        {
            OperationIssue? blocker = plan.Issues
                .Concat(plan.Items.SelectMany(item =>
                    item.Issues))
                .FirstOrDefault(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
            if (blocker is not null ||
                plan.Items.Any(item =>
                    !item.CanApply))
            {
                failure = blocker?.Message ??
                    L(
                        "Workbench.Dialog.PendingConflict.TranscodeCannotApply");
                return false;
            }
            foreach (AudioTranscodePlanItem item in
                     plan.Items)
            {
                if (!SnapshotMatches(
                        item.SourceSnapshot) ||
                    !SnapshotMatches(
                        item.DestinationSnapshot) ||
                    (!item.Sidecars.IsDefaultOrEmpty &&
                     item.Sidecars.Any(sidecar =>
                         !SnapshotMatches(
                             sidecar.DestinationSnapshot))))
                {
                    failure =
                        $"A reviewed path changed before apply: " +
                        $"{item.SourcePath}";
                    return false;
                }
            }
        }

        HashSet<string> fileOperationSources =
            fileOperationPlans
                .SelectMany(plan =>
                    plan.Items)
                .Select(item =>
                    item.SourcePath)
                .ToHashSet(PathComparer);
        foreach (AudioTranscodePlan transcode in
                 transcodePlans)
        {
            string? conflictSource = transcode.Items
                .Select(item => item.SourcePath)
                .FirstOrDefault(
                    fileOperationSources.Contains);
            if (conflictSource is not null &&
                transcode.Request.Destination.Mode ==
                    AudioTranscodeDestinationMode
                        .ReplaceOriginal)
            {
                failure =
                    L(
                        "Workbench.Dialog.PendingConflict.ReplaceOriginalConflict");
                return false;
            }
        }

        var destinations = new HashSet<string>(
            PathComparer);
        foreach (string destination in
                 fileOperationPlans
                     .SelectMany(plan =>
                         plan.MutationPlan.Actions)
                     .Where(action =>
                         action.Kind is
                             FileMutationKind.Copy or
                             FileMutationKind.Move or
                             FileMutationKind.Replace or
                             FileMutationKind.Write or
                             FileMutationKind
                                 .ReplaceGenerated)
                     .Select(action =>
                         action.DestinationPath)
                      .Concat(
                          transcodePlans
                             .SelectMany(plan =>
                                 plan.Items)
                             .SelectMany(item =>
                                 new[]
                                 {
                                     item.DestinationPath,
                                 }.Concat(
                                     item.Sidecars.IsDefaultOrEmpty
                                         ? []
                                         : item.Sidecars.Select(
                                             sidecar =>
                                                 sidecar
                                                     .DestinationPath)))))
        {
            string normalized =
                Path.GetFullPath(destination);
            if (!destinations.Add(normalized))
            {
                failure =
                    $"More than one reviewed change targets " +
                    $"'{normalized}'.";
                return false;
            }
        }
        return true;
    }

    private ImmutableArray<ReviewedMediaMutationUnit>
        BuildPendingMutationUnits()
    {
        var kindsByPath = new Dictionary<
            string,
            HashSet<ReviewedMediaMutationKind>>(
            PathComparer);
        foreach (ReviewedMetadataMutationIntent intent in
                 _metadataIntents)
        {
            foreach (string path in intent.Paths)
            {
                foreach (ReviewedMediaMutationKind kind
                         in intent.MutationKinds)
                    Add(path, kind);
            }
        }
        foreach (WorkbenchTrackViewModel file in
                 Files.Where(file => file.HasChanges))
            Add(
                file.Path,
                ReviewedMediaMutationKind.Metadata);
        if (_inspector?.HasUnsavedChanges == true)
        {
            foreach (string path in
                     _inspector.Selection.Paths)
            {
                if (_inspector
                    .HasUnsavedMetadataChanges)
                {
                    Add(
                        path,
                        ReviewedMediaMutationKind
                            .Metadata);
                }
                if (_inspector
                    .HasUnsavedArtworkChanges)
                {
                    Add(
                        path,
                        ReviewedMediaMutationKind
                            .Artwork);
                }
            }
        }
        foreach (ReviewedFileOperationItem item in
                 _fileOperationPlans.SelectMany(plan =>
                     plan.Items))
            Add(
                item.SourcePath,
                ReviewedMediaMutationKind.FileOperation);
        foreach (AudioTranscodePlanItem item in
                 _transcodePlans.SelectMany(plan =>
                     plan.Items))
            Add(
                item.SourcePath,
                ReviewedMediaMutationKind.Transcode);

        return
        [
            .. kindsByPath
                .OrderBy(pair =>
                    pair.Key,
                    PathComparer)
                .Select(pair =>
                    new ReviewedMediaMutationUnit(
                        pair.Key,
                        [
                            .. pair.Value.OrderBy(kind =>
                                (int)kind),
                        ])),
        ];

        void Add(
            string path,
            ReviewedMediaMutationKind kind)
        {
            string normalized =
                Path.GetFullPath(path);
            if (!kindsByPath.TryGetValue(
                    normalized,
                    out HashSet<
                        ReviewedMediaMutationKind>? kinds))
            {
                kinds = [];
                kindsByPath[normalized] = kinds;
            }
            kinds.Add(kind);
        }
    }

    private IEnumerable<ReviewedFileOperationPlan>
        OrderedFileOperationPlans() =>
        _fileOperationPlans
            .OrderBy(
                plan => plan.Items
                    .Select(item =>
                        Path.GetFullPath(
                            item.SourcePath))
                    .OrderBy(path =>
                        path,
                        PathComparer)
                    .FirstOrDefault() ?? "",
                PathComparer)
            .ThenBy(
                plan => plan.Request.Kind);

    private ImmutableArray<ReviewedFileOperationPlan>
        CaptureFileOperationApplyBatch()
    {
        ReviewedFileOperationPlan[] originals =
        [
            .. OrderedFileOperationPlans(),
        ];
        var frozenByOriginal =
            new Dictionary<
                ReviewedFileOperationPlan,
                ReviewedFileOperationPlan>(
                ReferenceEqualityComparer.Instance);
        ImmutableArray<ReviewedFileOperationPlan>.Builder
            frozen =
                ImmutableArray.CreateBuilder<
                    ReviewedFileOperationPlan>(
                        originals.Length);
        foreach (ReviewedFileOperationPlan original in
                 originals)
        {
            ReviewedFileOperationPlan snapshot =
                FreezeFileOperationPlan(
                    original);
            frozenByOriginal[original] =
                snapshot;
            frozen.Add(
                snapshot);
        }
        for (int index = 0;
             index < _fileOperationPlans.Count;
             index++)
        {
            if (frozenByOriginal.TryGetValue(
                    _fileOperationPlans[index],
                    out ReviewedFileOperationPlan?
                        snapshot))
                _fileOperationPlans[index] =
                    snapshot;
        }
        return frozen.MoveToImmutable();
    }

    private static ReviewedFileOperationPlan
        FreezeFileOperationPlan(
            ReviewedFileOperationPlan plan) =>
        plan with
        {
            Request = plan.Request with
            {
                SourcePaths =
                [
                    .. plan.Request.SourcePaths,
                ],
            },
            Items =
            [
                .. plan.Items.Select(item =>
                    item with
                    {
                        Issues =
                        [
                            .. item.Issues,
                        ],
                    }),
            ],
            MutationPlan = plan.MutationPlan with
            {
                Actions =
                [
                    .. plan.MutationPlan.Actions,
                ],
                Issues =
                [
                    .. plan.MutationPlan.Issues,
                ],
            },
        };

    private IEnumerable<AudioTranscodePlan>
        OrderedTranscodePlans() =>
        _transcodePlans
            .OrderBy(
                plan => plan.Items
                    .Select(item =>
                        Path.GetFullPath(
                            item.SourcePath))
                    .OrderBy(path =>
                        path,
                        PathComparer)
                    .FirstOrDefault() ?? "",
                PathComparer)
            .ThenBy(plan =>
                plan.Request.Settings.FormatId,
                StringComparer.Ordinal);

    private bool PolicyMatches(
        FileMutationPlan plan)
    {
        if (plan.PolicyFingerprint is null)
            return true;
        AppConfigurationSnapshot snapshot =
            _settings.GetSnapshot();
        return snapshot.Configuration is
            { } configuration &&
            (plan.LibraryId is not Guid libraryId ||
             configuration.LibraryId == libraryId) &&
            StringComparer.Ordinal.Equals(
                plan.PolicyFingerprint,
                configuration.PolicySnapshot.Fingerprint);
    }

    private static bool SnapshotMatches(
        OperationPathSnapshot? snapshot)
    {
        if (snapshot?.Path is not { } path)
            return true;
        bool fileExists = File.Exists(path);
        bool directoryExists =
            Directory.Exists(path);
        bool exists =
            fileExists || directoryExists;
        if (exists != snapshot.Exists)
            return false;
        if (!exists)
            return true;
        if (directoryExists !=
            snapshot.IsDirectory)
            return false;
        if (directoryExists)
            return Directory
                .GetLastWriteTimeUtc(path) ==
                snapshot.LastWriteTimeUtc;
        var info = new FileInfo(path);
        return info.Length == snapshot.Length &&
            info.LastWriteTimeUtc ==
                snapshot.LastWriteTimeUtc;
    }

    private async Task<ImmutableArray<ReviewedFileOperationPlan>>
        RefreshPendingFileOperationPlansAsync(
        IReadOnlyList<ReviewedFileOperationPlan> plansInApplyBatch,
        IProgress<OperationProgress> progress,
        CancellationToken ct)
    {
        if (_fileOperations is null ||
            plansInApplyBatch.Count == 0)
            return [];

        ReviewedFileOperationPlan[] requested =
        [
            .. plansInApplyBatch,
        ];
        var refreshedByOriginal =
            new Dictionary<
                ReviewedFileOperationPlan,
                ReviewedFileOperationPlan?>(
                ReferenceEqualityComparer.Instance);
        foreach (ReviewedFileOperationPlan plan in requested)
        {
            HashSet<string> pendingSources = plan.Items
                .Select(item => item.SourcePath)
                .ToHashSet(PathComparer);
            ReviewedFileOperationPlan refreshed =
                await _fileOperations.PreviewAsync(
                    plan.Request,
                    progress,
                    ct);
            refreshedByOriginal[plan] =
                FilterFileOperationPlan(
                    refreshed,
                    pendingSources.Contains,
                    preserveRequestContext: true);
        }

        ReviewedFileOperationPlan[] nextPending =
        [
            .. _fileOperationPlans
                .Select(plan =>
                    refreshedByOriginal.TryGetValue(
                        plan,
                        out ReviewedFileOperationPlan?
                            refreshed)
                        ? refreshed
                        : plan)
                .Where(plan => plan is not null)
                .Select(plan => plan!),
        ];
        _fileOperationPlans.Clear();
        _fileOperationPlans.AddRange(nextPending);
        RebuildPendingChanges();
        return
        [
            .. requested
                .Select(plan =>
                    refreshedByOriginal[plan])
                .Where(plan =>
                    plan is not null)
                .Select(plan =>
                    plan!),
        ];
    }

    private async Task<int>
        ApplyPendingFileOperationsAsync(
            IProgress<OperationProgress> progress,
            CancellationToken ct,
            IReadOnlySet<string>? deferredSources,
            List<string> postCommitWarnings,
            IReadOnlyCollection<ReviewedFileOperationPlan>
                plansInApplyBatch,
            List<PostCommitReconciliationHandle>
                postCommitReconciliations)
    {
        if (_fileOperations is null ||
            plansInApplyBatch.Count == 0)
            return 0;

        var requested =
            new List<ReviewedFileOperationPlan>();
        var retainedByOriginal =
            new Dictionary<
                ReviewedFileOperationPlan,
                ReviewedFileOperationPlan?>(
                    ReferenceEqualityComparer.Instance);
        foreach (ReviewedFileOperationPlan plan in
                 plansInApplyBatch)
        {
            ReviewedFileOperationPlan? applicable =
                FilterFileOperationPlan(
                    plan,
                    source =>
                        deferredSources?.Contains(source) !=
                        true);
            if (applicable is not null)
                requested.Add(plan);
            ReviewedFileOperationPlan? retained =
                FilterFileOperationPlan(
                    plan,
                    source =>
                        deferredSources?.Contains(source) ==
                        true,
                    preserveRequestContext: true);
            retainedByOriginal[plan] =
                retained;
        }
        if (requested.Count == 0)
            return 0;

        var refreshed =
            new List<ReviewedFileOperationPlan>(
                requested.Count);
        // Earlier kinds in a composed unit can intentionally change the
        // source timestamp. Re-preview every semantic file request after
        // those stages commit, and validate the complete refreshed set
        // before the first file-operation write.
        foreach (ReviewedFileOperationPlan plan in
                 requested)
        {
            HashSet<string> pendingSources = plan.Items
                .Select(item => item.SourcePath)
                .ToHashSet(PathComparer);
            ReviewedFileOperationPlan fullRefresh =
                await _fileOperations.PreviewAsync(
                    plan.Request,
                    progress,
                    ct);
            ReviewedFileOperationPlan? current =
                FilterFileOperationPlan(
                    fullRefresh,
                    source =>
                        pendingSources.Contains(source) &&
                        deferredSources?.Contains(source) !=
                        true);
            if (current is null)
                continue;
            OperationIssue? blocker =
                current.MutationPlan.Issues
                    .FirstOrDefault(issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker);
            if (!current.CanApply ||
                current.MutationPlan.Actions.Count == 0)
                throw new InvalidOperationException(
                    blocker?.Message ??
                    L(
                        "Workbench.Dialog.PendingConflict.FileOperationRefreshFailed"));
            refreshed.Add(current);
        }

        ReviewedFileOperationPlan[] pending =
        [
            .. refreshed,
        ];
        if (pending.Length == 0)
            return 0;
        ReviewedFileOperationPlan first = pending[0];
        ReviewedFileOperationItem[] items =
        [
            .. pending.SelectMany(plan => plan.Items)
                .OrderBy(item =>
                    item.SourcePath,
                    PathComparer)
                .ThenBy(item =>
                    item.DestinationPath ?? "",
                    PathComparer),
        ];
        FileMutationAction[] actions =
        [
            .. pending.SelectMany(plan =>
                    plan.MutationPlan.Actions)
                .OrderBy(action =>
                    action.SourcePath,
                    PathComparer)
                .ThenBy(action =>
                    action.DestinationPath,
                    PathComparer)
                .ThenBy(action =>
                    action.Kind),
        ];
        OperationIssue[] issues =
        [
            .. pending.SelectMany(plan =>
                    plan.MutationPlan.Issues)
                .OrderBy(issue =>
                    issue.Path ?? "",
                    PathComparer)
                .ThenBy(issue =>
                    issue.Code,
                    StringComparer.Ordinal),
        ];
        var combined = first with
        {
            Request = first.Request with
            {
                SourcePaths =
                [
                    .. items.Select(item =>
                        item.SourcePath),
                ],
            },
            Items = items,
            MutationPlan =
                first.MutationPlan with
                {
                    Actions = actions,
                    Issues = issues,
                },
        };

        bool preflightEntered = false;
        try
        {
            preflightEntered = true;
            string? preflightFailure =
                await InvokeFileOperationsPreflightAsync(
                    pending);
            if (!string.IsNullOrWhiteSpace(
                    preflightFailure))
                throw new
                    PendingFileOperationPreflightException(
                        preflightFailure);

            FileMutationSummary summary =
                await _fileOperations.ApplyAsync(
                combined,
                progress,
                ct);

            // The mutation is durable once ApplyAsync returns. Remove
            // precisely those semantic requests before any subscriber or
            // session reload can fail; sources retained by a partial
            // transcode stay retryable.
            ReviewedFileOperationPlan[] nextPending =
            [
                .. _fileOperationPlans
                    .Select(plan =>
                        retainedByOriginal.TryGetValue(
                            plan,
                            out ReviewedFileOperationPlan?
                                retained)
                            ? retained
                            : plan)
                    .Where(plan => plan is not null)
                    .Select(plan => plan!),
            ];
            _fileOperationPlans.Clear();
            _fileOperationPlans.AddRange(nextPending);
            AddPostCommitIssues(
                summary.Issues,
                postCommitWarnings);
            if (summary.PostCommitReconciliation is { } reconciliation)
                postCommitReconciliations.Add(
                    reconciliation);
            await CapturePostCommitWarningAsync(
                () =>
                {
                    RebuildPendingChanges();
                    return Task.CompletedTask;
                },
                postCommitWarnings);

            await NotifyFileOperationsCommittedAsync(
                pending,
                postCommitWarnings);
            foreach (ReviewedFileOperationPlan plan in
                     pending)
                await CapturePostCommitWarningAsync(
                    () => RefreshAfterFileOperationAsync(
                        plan,
                        postCommitWarnings),
                    postCommitWarnings);
            return actions.Length;
        }
        finally
        {
            if (preflightEntered)
                await NotifyFileOperationsApplyFinishedAsync(
                    pending,
                    postCommitWarnings);
        }
    }

    private static ReviewedFileOperationPlan?
        FilterFileOperationPlan(
            ReviewedFileOperationPlan plan,
            Func<string, bool> includeSource,
            bool preserveRequestContext = false)
    {
        ReviewedFileOperationItem[] items =
        [
            .. plan.Items.Where(item =>
                includeSource(item.SourcePath)),
        ];
        if (items.Length == 0)
            return null;

        HashSet<string> sources = items
            .Select(item => item.SourcePath)
            .ToHashSet(PathComparer);
        FileMutationAction[] actions =
        [
            .. plan.MutationPlan.Actions.Where(action =>
                sources.Contains(action.SourcePath)),
        ];
        HashSet<string> relevantPaths =
            sources.ToHashSet(PathComparer);
        relevantPaths.UnionWith(
            actions.Select(action =>
                action.DestinationPath));
        OperationIssue[] issues =
        [
            .. plan.MutationPlan.Issues.Where(issue =>
                issue.Path is null ||
                relevantPaths.Contains(issue.Path)),
        ];
        string[] requestSources =
        [
            .. plan.Request.SourcePaths
                .Where(sources.Contains)
                .Concat(
                    items.Select(item =>
                        item.SourcePath))
                .Distinct(PathComparer),
        ];
        ReviewedFileOperationRequest request =
            preserveRequestContext
                ? plan.Request
                : plan.Request with
                {
                    SourcePaths = requestSources,
                };
        return plan with
        {
            Request = request,
            Items = items,
            MutationPlan = plan.MutationPlan with
            {
                Actions = actions,
                Issues = issues,
            },
        };
    }

    private async Task NotifyFileOperationsCommittedAsync(
        IReadOnlyList<ReviewedFileOperationPlan> committed,
        List<string> postCommitWarnings)
    {
        Func<
            IReadOnlyList<ReviewedFileOperationPlan>,
            Task>? handlers = FileOperationsCommitted;
        if (handlers is null)
            return;

        ImmutableArray<ReviewedFileOperationPlan> snapshot =
        [
            .. committed,
        ];
        foreach (Func<
                     IReadOnlyList<ReviewedFileOperationPlan>,
                     Task> handler in handlers
                     .GetInvocationList()
                     .Cast<Func<
                         IReadOnlyList<
                             ReviewedFileOperationPlan>,
                         Task>>())
            await CapturePostCommitWarningAsync(
                () => handler(snapshot),
                postCommitWarnings);
    }

    private async Task<string?>
        InvokeFileOperationsPreflightAsync(
            IReadOnlyList<ReviewedFileOperationPlan> plans)
    {
        Func<
            IReadOnlyList<ReviewedFileOperationPlan>,
            Task<string?>>? handlers =
                FileOperationsPreflight;
        if (handlers is null)
            return null;

        ImmutableArray<ReviewedFileOperationPlan> snapshot =
        [
            .. plans,
        ];
        var failures = new List<string>();
        foreach (Func<
                     IReadOnlyList<ReviewedFileOperationPlan>,
                     Task<string?>> handler in handlers
                     .GetInvocationList()
                     .Cast<Func<
                         IReadOnlyList<
                             ReviewedFileOperationPlan>,
                         Task<string?>>>())
        {
            try
            {
                string? failure =
                    await handler(snapshot);
                if (!string.IsNullOrWhiteSpace(
                        failure))
                    failures.Add(failure);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Add(
                    error.Message);
            }
        }
        return failures.Count == 0
            ? null
            : string.Join(
                Environment.NewLine,
                failures.Distinct(
                    StringComparer.Ordinal));
    }

    private async Task
        NotifyFileOperationsApplyFinishedAsync(
            IReadOnlyList<ReviewedFileOperationPlan> plans,
            List<string> postCommitWarnings)
    {
        Func<
            IReadOnlyList<ReviewedFileOperationPlan>,
            Task>? handlers =
                FileOperationsApplyFinished;
        if (handlers is null)
            return;

        ImmutableArray<ReviewedFileOperationPlan> snapshot =
        [
            .. plans,
        ];
        foreach (Func<
                     IReadOnlyList<ReviewedFileOperationPlan>,
                     Task> handler in handlers
                     .GetInvocationList()
                     .Cast<Func<
                         IReadOnlyList<
                             ReviewedFileOperationPlan>,
                         Task>>())
            await CapturePostCommitWarningAsync(
                () => handler(snapshot),
                postCommitWarnings);
    }

    private async Task<PendingTranscodeApplyOutcome>
        ApplyPendingTranscodesAsync(
        IProgress<OperationProgress> progress,
        CancellationToken ct,
        MetadataOperationStageResult? metadataStage = null,
        IReadOnlyDictionary<string, string>?
            sourceOverrides = null,
        List<string>? postCommitWarnings = null,
        PendingDirectChangesCapture?
            directChangesCapture = null,
        IReadOnlyList<ReviewedMetadataMutationIntent>?
            stagedMetadataIntents = null,
        IReadOnlyCollection<ReviewedFileOperationPlan>?
            filePlansInApplyBatch = null,
        IReadOnlyList<AudioTranscodePlan>?
            transcodePlansInApplyBatch = null,
        List<PostCommitReconciliationHandle>?
            postCommitReconciliations = null)
    {
        IReadOnlyList<AudioTranscodePlan> requestedBatch =
            transcodePlansInApplyBatch ??
            OrderedTranscodePlans().ToArray();
        if (_transcodes is null ||
            requestedBatch.Count == 0)
            return new(
                0,
                Proceed: false,
                Committed: false,
                null,
                [],
                []);
        postCommitWarnings ??= [];
        var stages = new List<AudioTranscodeStageResult>();
        bool stageOwnershipTransferred = false;
        AudioTranscodePlan[] requestedPlans =
        [
            .. requestedBatch,
        ];
        try
        {
            foreach (AudioTranscodePlan plan in
                     requestedPlans)
                stages.Add(
                    sourceOverrides is { Count: > 0 }
                        ? await _transcodes
                            .StageWithSourceOverridesAsync(
                                plan,
                                sourceOverrides,
                                progress,
                                ct)
                        : await _transcodes.StageAsync(
                            plan,
                            progress,
                            ct));

            CaptureTranscodeStageDiagnostics(
                stages);
            RebuildPendingChanges();
            int failed = stages.Sum(stage =>
                stage.Items.Count(item =>
                    item.State !=
                    AudioTranscodeStageState.Ready));
            HashSet<string> failedSources = stages
                .SelectMany(stage => stage.Items)
                .Where(item =>
                    item.State !=
                    AudioTranscodeStageState.Ready)
                .Select(item => item.PlanItem.SourcePath)
                .ToHashSet(PathComparer);
            HashSet<string> replacementSources =
                new(PathComparer);
            foreach (AudioTranscodeStageResult stage in
                     stages.Where(stage =>
                         stage.Plan.Request.Destination.Mode ==
                         AudioTranscodeDestinationMode
                             .ReplaceOriginal))
            {
                foreach (AudioTranscodeStagedItem readyItem in
                         stage.ReadyItems)
                    replacementSources.Add(
                        readyItem.PlanItem.SourcePath);
            }
            HashSet<string> excludedMetadataSources =
                failedSources.ToHashSet(PathComparer);
            excludedMetadataSources.UnionWith(
                replacementSources);
            MetadataOperationStageResult? appliedMetadata =
                FilterMetadataStage(
                    metadataStage,
                    excludedMetadataSources);
            ImmutableArray<string> consumedMetadataPaths =
                metadataStage is null
                    ? []
                    :
                    [
                        .. (appliedMetadata?.Files
                                .Select(file =>
                                    file.LivePath) ??
                            [])
                            .Concat(
                                metadataStage.Files
                                    .Select(file =>
                                        file.LivePath)
                                    .Where(path =>
                                        replacementSources
                                            .Contains(path)))
                            .Distinct(PathComparer),
                    ];
            HashSet<string> readyReviewedSources = stages
                .SelectMany(stage =>
                    stage.ReadyItems)
                .Select(item =>
                    item.PlanItem.SourcePath)
                .ToHashSet(PathComparer);
            readyReviewedSources.UnionWith(
                appliedMetadata?.Files.Select(file =>
                    file.LivePath) ?? []);
            readyReviewedSources.UnionWith(
                (filePlansInApplyBatch ??
                 _fileOperationPlans)
                    .SelectMany(plan =>
                        plan.Items)
                    .Where(item =>
                        !failedSources.Contains(
                            item.SourcePath))
                    .Select(item =>
                        item.SourcePath));
            if (failed > 0)
            {
                if (readyReviewedSources.Count == 0)
                {
                    string[] stageDiagnostics =
                    [
                        .. stages
                            .SelectMany(stage =>
                                stage.Items)
                            .Where(item =>
                                item.State !=
                                AudioTranscodeStageState.Ready)
                            .SelectMany(item =>
                                new[]
                                {
                                    item.DiagnosticDetail,
                                    item.ErrorCode,
                                })
                            .Where(detail =>
                                !string.IsNullOrWhiteSpace(
                                    detail))
                            .Select(detail =>
                                detail!)
                            .Distinct(
                                StringComparer.Ordinal)
                            .Take(8),
                    ];
                    throw new InvalidOperationException(
                        string.Join(
                            Environment.NewLine,
                            new[]
                                {
                                    L(
                                        "Transcode.Error.NoReadyFiles"),
                                }
                                .Concat(
                                    stageDiagnostics)));
                }
                bool applyReady =
                    await _dialogs.ConfirmAsync(
                        L("Transcode.Dialog.Partial.Title"),
                        LC(
                            "Transcode.Dialog.Partial.Message",
                            failedSources.Count,
                            readyReviewedSources.Count),
                        L("Transcode.Action.ApplyReady"));
                ct.ThrowIfCancellationRequested();
                if (!applyReady)
                {
                    return new(
                        0,
                        Proceed: false,
                        Committed: false,
                        null,
                        [],
                        [.. failedSources]);
                }
            }

            int changed = 0;
            var retainedByOriginal =
                new Dictionary<
                    AudioTranscodePlan,
                    AudioTranscodePlan?>(
                    ReferenceEqualityComparer.Instance);
            for (int index = 0;
                 index < stages.Count;
                 index++)
            {
                AudioTranscodeStageResult stage =
                    stages[index];
                AudioTranscodePlan original =
                    requestedPlans[index];
                AudioTranscodeStagedItem[] retainedItems =
                [
                    .. stage.Items.Where(item =>
                        item.State !=
                        AudioTranscodeStageState.Ready),
                ];
                if (retainedItems.Length == 0)
                {
                    retainedByOriginal[original] =
                        null;
                    continue;
                }
                ImmutableArray<AudioTranscodePlanItem>
                    failedItems =
                [
                    .. retainedItems.Select(item =>
                        item.PlanItem),
                ];
                retainedByOriginal[original] =
                    stage.Plan with
                    {
                        Items = failedItems,
                        Request =
                            stage.Plan.Request with
                            {
                                SourcePaths =
                                [
                                    .. failedItems.Select(item =>
                                        item.SourcePath),
                                ],
                            },
                    };
            }
            var appliedPairs = new List<(
                string Source,
                string Destination,
                AudioTranscodeDestinationMode Mode)>(
                stages.Sum(stage =>
                    stage.ReadyItems.Length));
            appliedPairs.AddRange(
                stages.SelectMany(stage =>
                    stage.ReadyItems.Select(item =>
                        (
                            item.PlanItem.SourcePath,
                            item.PlanItem.DestinationPath,
                            stage.Plan.Request.Destination.Mode
                        ))));
            HashSet<Guid> readyIds = stages
                .SelectMany(stage => stage.ReadyItems)
                .Select(item => item.PlanItem.Id)
                .ToHashSet();
            bool transcodeCommitted =
                readyIds.Count > 0;
            bool metadataCommitted = false;
            if (transcodeCommitted)
            {
                stageOwnershipTransferred = true;
                AudioTranscodeApplyResult result =
                    await _transcodes.ApplyReviewedBatchAsync(
                        stages,
                        appliedMetadata?.Participants ?? [],
                        readyIds,
                        progress,
                        ct);
                changed = result.ChangedFiles;
                AddPostCommitIssues(
                    result.Issues,
                    postCommitWarnings);
                if (result.PostCommitReconciliation is { } reconciliation)
                    postCommitReconciliations?.Add(
                        reconciliation);
            }
            else if (appliedMetadata is not null)
            {
                MetadataApplyResult result =
                    await _operations.ApplyAsync(
                        appliedMetadata.Plan,
                        progress,
                        ct);
                changed = result.ChangedFiles;
                metadataCommitted = true;
                AddPostCommitIssues(
                    result.Issues,
                    postCommitWarnings);
            }

            // The reviewed batch owns both the successful transcodes and its
            // staged metadata participants. Consume their semantic intents
            // immediately after that durable transaction returns.
            AudioTranscodePlan[] nextPending =
            [
                .. _transcodePlans
                    .Select(plan =>
                        retainedByOriginal.TryGetValue(
                            plan,
                            out AudioTranscodePlan?
                                retained)
                            ? retained
                            : plan)
                    .Where(plan => plan is not null)
                    .Select(plan => plan!),
            ];
            _transcodePlans.Clear();
            _transcodePlans.AddRange(
                nextPending);
            RetainTranscodeStageDiagnosticsForPendingItems();
            ConsumeCommittedMetadataPaths(
                consumedMetadataPaths,
                postCommitWarnings,
                directChangesCapture,
                stagedMetadataIntents);
            await CapturePostCommitWarningAsync(
                () => RefreshSessionAfterTranscodeAsync(
                    appliedPairs,
                    progress,
                    CancellationToken.None,
                    postCommitWarnings),
                postCommitWarnings);
            return new(
                changed,
                Proceed: true,
                Committed:
                    transcodeCommitted ||
                    metadataCommitted,
                transcodeCommitted
                    ? appliedMetadata
                    : null,
                consumedMetadataPaths,
                [.. failedSources]);
        }
        finally
        {
            if (!stageOwnershipTransferred)
                await DiscardTranscodeStagesAsync(
                    stages,
                    postCommitWarnings);
        }
    }

    private async Task DiscardTranscodeStagesAsync(
        IEnumerable<AudioTranscodeStageResult> stages,
        List<string>? postCommitWarnings = null)
    {
        if (_transcodes is null)
            return;
        foreach (AudioTranscodeStageResult stage in stages)
        {
            try
            {
                await _transcodes.DiscardStageAsync(
                    stage,
                    CancellationToken.None);
            }
            catch (Exception error)
            {
                postCommitWarnings?.Add(
                    error.Message);
            }
        }
    }

    private void CaptureTranscodeStageDiagnostics(
        IEnumerable<AudioTranscodeStageResult> stages)
    {
        ImmutableDictionary<Guid, string>.Builder diagnostics =
            _transcodeStageDiagnostics.ToBuilder();
        foreach (AudioTranscodeStagedItem item in
                 stages.SelectMany(stage =>
                     stage.Items))
        {
            diagnostics.Remove(
                item.PlanItem.Id);
            if (item.State ==
                AudioTranscodeStageState.Ready)
                continue;
            string[] details =
            [
                .. new[]
                    {
                        item.ErrorCode,
                        item.DiagnosticDetail,
                    }
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(
                            value))
                    .Select(value =>
                        value!)
                    .Distinct(
                        StringComparer.Ordinal),
            ];
            diagnostics[item.PlanItem.Id] =
                details.Length == 0
                    ? L("Transcode.Issue.StageFailed")
                    : string.Join(
                        Environment.NewLine,
                        details);
        }
        _transcodeStageDiagnostics =
            diagnostics.ToImmutable();
    }

    private void
        RetainTranscodeStageDiagnosticsForPendingItems()
    {
        HashSet<Guid> pendingIds = _transcodePlans
            .SelectMany(plan =>
                plan.Items)
            .Select(item =>
                item.Id)
            .ToHashSet();
        _transcodeStageDiagnostics =
            _transcodeStageDiagnostics
                .Where(pair =>
                    pendingIds.Contains(pair.Key))
                .ToImmutableDictionary();
    }

    private static MetadataOperationStageResult?
        FilterMetadataStage(
            MetadataOperationStageResult? stage,
            IReadOnlySet<string> excludedPaths)
    {
        if (stage is null)
            return null;
        ImmutableArray<MetadataStagedFile> files =
        [
            .. stage.Files.Where(file =>
                !excludedPaths.Contains(
                    file.LivePath)),
        ];
        if (files.Length == 0)
            return null;
        HashSet<string> included = files
            .Select(file => file.LivePath)
            .ToHashSet(PathComparer);
        ImmutableArray<FileMutationPlan> participants =
        [
            .. stage.Participants
                .Select(participant => participant with
                {
                    Actions =
                    [
                        .. participant.Actions.Where(action =>
                            included.Contains(
                                action.DestinationPath)),
                    ],
                })
                .Where(participant =>
                    participant.Actions.Count > 0),
        ];
        return stage with
        {
            Plan = stage.Plan with
            {
                Files =
                [
                    .. stage.Plan.Files.Where(file =>
                        included.Contains(file.Path)),
                ],
            },
            Participants = participants,
            Files = files,
        };
    }

    private void ConsumeCommittedMetadataPaths(
        IReadOnlyCollection<string> committedPaths,
        List<string> postCommitWarnings,
        PendingDirectChangesCapture?
            directChangesCapture,
        IReadOnlyList<ReviewedMetadataMutationIntent>?
            stagedMetadataIntents)
    {
        if (committedPaths.Count == 0)
            return;
        HashSet<string> committed =
            committedPaths.ToHashSet(PathComparer);
        string[] retainedPaths =
        [
            .. (_plan?.Files ?? [])
                .Where(file =>
                    !committed.Contains(file.Path))
                .Select(file =>
                    file.Path),
        ];

        RetainMetadataPlansForPaths(
            retainedPaths,
            stagedMetadataIntents,
            refreshPresentation: false);
        ConsumeCommittedArtworkDrafts(
            committedPaths);

        // Direct grid/inspector edits have already been captured as semantic
        // metadata intents. The retained plan above is now the single source
        // of truth, so observable editor cleanup can only add warnings.
        AcceptCapturedDirectPendingChanges(
            directChangesCapture,
            committedPaths,
            postCommitWarnings);
        try
        {
            RefreshMetadataPlanPresentation();
        }
        catch (Exception error)
        {
            postCommitWarnings.Add(
                error.Message);
        }
    }

    private void AcceptCapturedDirectPendingChanges(
        PendingDirectChangesCapture? capture,
        IReadOnlyCollection<string> committedPaths,
        List<string> postCommitWarnings)
    {
        if (capture is null)
            return;
        HashSet<string> committed =
            committedPaths.ToHashSet(PathComparer);
        foreach ((string path,
                     ImmutableArray<TagEdit> edits) in
                 capture.TrackEdits)
        {
            if (!committed.Contains(path))
                continue;
            WorkbenchTrackViewModel? file =
                Files.FirstOrDefault(candidate =>
                    PathComparer.Equals(
                        candidate.Path,
                        path));
            if (file is null)
                continue;
            try
            {
                file.AcceptPendingChanges(
                    edits);
            }
            catch (Exception error)
            {
                postCommitWarnings.Add(
                    error.Message);
            }
        }
        if (_inspector is not null &&
            capture.InspectorPaths.Length ==
            _inspector.Selection.Paths.Count &&
            capture.InspectorPaths.All(path =>
                _inspector.Selection.Paths.Contains(
                    path,
                    PathComparer)))
        {
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>?
                committedValueEdits =
                    capture.InspectorValueEdits?
                        .Where(pair =>
                            committed.Contains(
                                pair.Key))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            PathComparer);
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>?
                committedArtworkEdits =
                    capture.InspectorArtworkEdits?
                        .Where(pair =>
                            committed.Contains(
                                pair.Key))
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            PathComparer);
            if (committedValueEdits?.Count == 0)
                committedValueEdits = null;
            if (committedArtworkEdits?.Count == 0)
                committedArtworkEdits = null;
            try
            {
                _inspector.AcceptPendingChanges(
                    committedValueEdits,
                    committedArtworkEdits);
            }
            catch (Exception error)
            {
                postCommitWarnings.Add(
                    error.Message);
            }
        }
    }

    private void ConsumeCommittedArtworkDrafts(
        IReadOnlyCollection<string> committedPaths)
    {
        HashSet<string> committed =
            committedPaths.ToHashSet(PathComparer);
        foreach (string path in committed)
            _artworkDrafts.Remove(path);
        if (_stagedArtworkPath is not null &&
            committed.Contains(_stagedArtworkPath))
            _stagedArtworkDirty = false;
    }

    private async Task FinalizeMetadataCommitAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress> progress,
        List<string> postCommitWarnings)
    {
        string[] reloadPaths =
        [
            .. paths.Where(path =>
                Files.FirstOrDefault(file =>
                    PathComparer.Equals(
                        file.Path,
                        path))?.HasChanges != true),
        ];
        if (reloadPaths.Length > 0)
            await CapturePostCommitWarningAsync(
                () => ReloadAsync(
                    reloadPaths,
                    progress,
                    CancellationToken.None,
                    postCommitWarnings),
                postCommitWarnings);
        if (_inspector is not null &&
            !_inspector.HasUnsavedChanges)
            await CapturePostCommitWarningAsync(
                () => _inspector.LoadAsync(
                    _inspector.Selection),
                postCommitWarnings);
    }

    private static async Task CapturePostCommitWarningAsync(
        Func<Task> action,
        List<string> postCommitWarnings)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            postCommitWarnings.Add(
                error.Message);
        }
    }

    private static void CapturePostCommitWarning(
        Action action,
        List<string> postCommitWarnings)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            postCommitWarnings.Add(
                error.Message);
        }
    }

    private static void AddPostCommitIssues(
        IEnumerable<OperationIssue> issues,
        List<string> postCommitWarnings)
    {
        postCommitWarnings.AddRange(
            issues
                .Where(issue =>
                    issue.Severity !=
                    OperationIssueSeverity.Information)
                .Select(issue =>
                    issue.Message)
                .Where(message =>
                    !string.IsNullOrWhiteSpace(message)));
    }

    private static async Task
        ObservePostCommitReconciliationsAsync(
            IEnumerable<PostCommitReconciliationHandle>
                reconciliations,
            List<string> postCommitWarnings)
    {
        foreach (PostCommitReconciliationHandle reconciliation in
                 reconciliations.Distinct())
        {
            try
            {
                IReadOnlyList<OperationIssue> issues =
                    await reconciliation.Completion;
                AddPostCommitIssues(
                    issues,
                    postCommitWarnings);
            }
            catch (Exception error)
            {
                // Defensive isolation: the handle contract returns issues,
                // but a nonconforming implementation cannot reclassify a
                // durable commit as failed or cancelled.
                postCommitWarnings.Add(
                    error.Message);
            }
        }
    }

    private async Task RefreshSessionAfterTranscodeAsync(
        IReadOnlyList<(
            string Source,
            string Destination,
            AudioTranscodeDestinationMode Mode)> applied,
        IProgress<OperationProgress> progress,
        CancellationToken ct,
        List<string> postCommitWarnings)
    {
        if (applied.Count == 0)
            return;
        string[] destinations =
        [
            .. applied.Select(item => item.Destination)
                .Distinct(PathComparer),
        ];
        WorkbenchLoadResult loaded =
            await _workbench.LoadAsync(
                new(destinations, Recursive: false),
                progress,
                ct);
        AddPostCommitIssues(
            loaded.Issues,
            postCommitWarnings);
        Dictionary<string, MediaDocument> documents =
            loaded.Documents.ToDictionary(
                document => document.Path,
                PathComparer);
        foreach ((string source,
                     string destination,
                     AudioTranscodeDestinationMode mode)
                 in applied)
        {
            WorkbenchTrackViewModel? sourceRow =
                Files.FirstOrDefault(file =>
                    PathComparer.Equals(
                        file.Path,
                        source));
            if (mode ==
                    AudioTranscodeDestinationMode.ReplaceOriginal &&
                sourceRow is not null)
            {
                sourceRow.PropertyChanged -= OnTrackChanged;
                Files.Remove(sourceRow);
            }
            if (!documents.TryGetValue(
                    destination,
                    out MediaDocument? document) ||
                Files.Any(file =>
                    PathComparer.Equals(
                        file.Path,
                        destination)))
                continue;
            AddTrack(new(document));
        }
        SetSelectedFiles(
            Files.Where(file =>
                destinations.Contains(
                    file.Path,
                    PathComparer)));
    }

    [RelayCommand(CanExecute = nameof(CanRevertPendingChanges))]
    private async Task RevertPendingChangesAsync()
    {
        if (_plan is null &&
            PendingChanges.Count == 0 &&
            _fileOperationPlans.Count == 0 &&
            _transcodePlans.Count == 0)
            return;

        foreach (WorkbenchTrackViewModel file in
                 Files.Where(file => file.HasChanges).ToArray())
            file.RevertPendingChanges();
        if (_inspector?.HasUnsavedChanges == true)
            await _inspector.DiscardPendingChangesAsync();
        _fileOperationPlans.Clear();
        _transcodePlans.Clear();
        _transcodeStageDiagnostics =
            ImmutableDictionary<Guid, string>.Empty;
        CancelPlan();
        SetStatus("Workbench.Status.PendingReverted");
        NotifySessionChanged();
    }

    private bool CanRevertPendingChanges() =>
        !IsBusy &&
        HasPendingChanges;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                L("Workbench.Dialog.Restore.Title"),
                L("Workbench.Dialog.Restore.Message"),
                L("Common.Restore")))
            return;
        BeginOperation(
            L("Workbench.Activity.RestoringLatest"));
        var postCommitWarnings = new List<string>();
        bool undoCommitted = false;
        int restored = 0;
        try
        {
            IProgress<int> restoreProgress =
                CreateRestoreProgress(
                    Files.Count);
            EditHistoryEntry? metadataEntry =
                _history.Entries.FirstOrDefault();
            ReviewedChangeHistoryEntry? reviewedEntry =
                _reviewedHistory?.Entries.FirstOrDefault();
            bool undoReviewed =
                reviewedEntry is not null &&
                (metadataEntry is null ||
                 reviewedEntry.AppliedAtUtc >=
                 metadataEntry.AppliedAtUtc);
            if (undoReviewed)
            {
                ReviewedChangeUndoResult result =
                    await _reviewedHistory!.UndoLatestAsync(
                        restoreProgress,
                        _cancellation!.Token);
                restored = result.RestoredFiles;
                undoCommitted = true;
                EndCancellablePhase();
                AddPostCommitIssues(
                    result.Issues,
                    postCommitWarnings);
                AddPostCommitIssues(
                    _reviewedHistory
                        .LastReconciliationIssues,
                    postCommitWarnings);
                await CapturePostCommitWarningAsync(
                    () =>
                        RefreshSessionAfterReviewedUndoAsync(
                            reviewedEntry!,
                            CreateProgress(),
                            CancellationToken.None,
                            postCommitWarnings),
                    postCommitWarnings);
            }
            else
            {
                restored = await _history.UndoLatestAsync(
                    restoreProgress,
                    _cancellation!.Token);
                undoCommitted = true;
                Task<IReadOnlyList<OperationIssue>>
                    reconciliation =
                        _history.LastUndoReconciliation;
                EndCancellablePhase();
                try
                {
                    IReadOnlyList<OperationIssue>
                        reconciliationIssues =
                            await reconciliation;
                    AddPostCommitIssues(
                        reconciliationIssues,
                        postCommitWarnings);
                }
                catch (Exception error)
                {
                    postCommitWarnings.Add(
                        error.Message);
                }
                AddPostCommitIssues(
                    _history.LastUndoIssues,
                    postCommitWarnings);
                await CapturePostCommitWarningAsync(
                    () => ReloadAsync(
                        Files.Select(file =>
                            file.Path).ToArray(),
                        CreateProgress(),
                        CancellationToken.None,
                        postCommitWarnings),
                    postCommitWarnings);
                if (_inspector is not null)
                    await CapturePostCommitWarningAsync(
                        () => _inspector.LoadAsync(
                            _inspector.Selection),
                        postCommitWarnings);
                AddPostCommitIssues(
                    _history.LastUndoIssues,
                    postCommitWarnings);
            }
            SetCountStatus(
                "Workbench.Status.Restored",
                restored);
            AppendStatusDiagnostics(
                postCommitWarnings);
        }
        catch (OperationCanceledException)
        {
            if (undoCommitted)
            {
                SetCountStatus(
                    "Workbench.Status.Restored",
                    restored);
                AppendStatusDiagnostics(
                [
                    L(
                        "Workbench.Status.RestoreCancelled"),
                ]);
            }
            else
                SetStatus(
                    "Workbench.Status.RestoreCancelled");
        }
        catch (Exception error)
        {
            if (undoCommitted)
            {
                SetCountStatus(
                    "Workbench.Status.Restored",
                    restored);
                AppendStatusDiagnostics(
                    [error.Message]);
            }
            else
                SetFailure(
                    "Workbench.Status.RestoreFailed",
                    error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        ReviewedChangeHistoryEntry? reviewed =
            _reviewedHistory?.RedoEntries.FirstOrDefault();
        EditHistoryEntry? candidate = _history.RedoEntries
            .FirstOrDefault(entry => entry.Recipe is not null);
        if (reviewed is not null &&
            (candidate is null ||
             reviewed.AppliedAtUtc >=
             candidate.AppliedAtUtc))
        {
            if (_transcodes is null)
                return;
            BeginOperation(
                L("Transcode.Status.Previewing"));
            try
            {
                foreach (AudioTranscodeRequest request in
                         reviewed.EffectiveRedoRequests)
                {
                    AudioTranscodePlan plan =
                        await _transcodes.PreviewAsync(
                            request,
                            CreateProgress(),
                            _cancellation!.Token);
                    if (!plan.CanApply)
                        throw new InvalidOperationException(
                            plan.Issues.Concat(
                                    plan.Items.SelectMany(item =>
                                        item.Issues))
                                .FirstOrDefault(issue =>
                                    issue.Severity ==
                                    OperationIssueSeverity.Blocker)
                                ?.Message ??
                            L("Transcode.Status.PreviewBlocked"));
                    await AddPendingTranscodeAsync(
                        plan,
                        _cancellation.Token);
                }
                SetStatus(
                    "Workbench.Status.RedoRegenerated");
            }
            catch (Exception error)
            {
                SetFailure(
                    "Workbench.Status.PreviewFailed",
                    error.Message);
            }
            finally
            {
                EndOperation();
                NotifySessionChanged();
            }
            return;
        }
        if (candidate?.Recipe is null)
            return;
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            candidate.Paths, candidate.Recipe, progress, ct));
        SetStatus("Workbench.Status.RedoRegenerated");
    }

    private async Task RefreshSessionAfterReviewedUndoAsync(
        ReviewedChangeHistoryEntry entry,
        IProgress<OperationProgress> progress,
        CancellationToken ct,
        List<string> postCommitWarnings)
    {
        foreach (WorkbenchTrackViewModel file in
                 Files.Where(file =>
                     !File.Exists(file.Path))
                     .ToArray())
        {
            file.PropertyChanged -= OnTrackChanged;
            Files.Remove(file);
        }
        string[] existingSources =
        [
            .. entry.SourcePaths
                .Where(File.Exists)
                .Distinct(PathComparer),
        ];
        if (existingSources.Length > 0)
        {
            WorkbenchLoadResult loaded =
                await _workbench.LoadAsync(
                    new(existingSources, Recursive: false),
                    progress,
                    ct);
            AddPostCommitIssues(
                loaded.Issues,
                postCommitWarnings);
            foreach (MediaDocument document in
                     loaded.Documents)
            {
                WorkbenchTrackViewModel? current =
                    Files.FirstOrDefault(file =>
                        PathComparer.Equals(
                            file.Path,
                            document.Path));
                if (current is not null)
                {
                    int index = Files.IndexOf(current);
                    current.PropertyChanged -= OnTrackChanged;
                    var replacement =
                        new WorkbenchTrackViewModel(document);
                    replacement.PropertyChanged +=
                        OnTrackChanged;
                    Files[index] = replacement;
                }
                else
                {
                    AddTrack(new(document));
                }
            }
        }
        WorkbenchTrackViewModel[] selected =
        [
            .. Files.Where(file =>
                existingSources.Contains(
                    file.Path,
                    PathComparer)),
        ];
        ApplySelectedFiles(selected);
        if (_inspector is not null)
            await _inspector.LoadAsync(
                CreateInspectorSelection(
                    selected));
    }

    [RelayCommand(CanExecute = nameof(CanRepeat))]
    private async Task RepeatAsync()
    {
        OperationRecipe? recipe = _history.Entries.FirstOrDefault()?.Recipe;
        if (recipe is null)
            return;
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            Files.Select(file => file.Path).ToArray(), recipe, progress, ct));
        SetStatus("Workbench.Status.RecipeRegenerated");
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanDiscoverSelectedAudio))]
    private async Task DiscoverSelectedAudioAsync()
    {
        if (SelectedFile is not null)
        {
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.AudioCandidates;
            await DiscoverAudioAsync([SelectedFile.Path]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverAllAudio))]
    private async Task DiscoverAllAudioAsync()
    {
        SelectedOnlineMetadataResultStep =
            WorkbenchOnlineMetadataResultStep.AudioCandidates;
        await DiscoverAudioAsync(
            Files.Select(file => file.Path).ToArray());
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverOnlineAudio))]
    private async Task DiscoverOnlineAudioAsync()
    {
        SelectedOnlineMetadataResultStep =
            WorkbenchOnlineMetadataResultStep.AudioCandidates;
        if (SelectedOnlineMetadataScope.Scope ==
            WorkbenchOnlineMetadataScope.SelectedFile)
        {
            if (SelectedFile is not null)
                await DiscoverAudioAsync([SelectedFile.Path]);
            return;
        }

        await DiscoverAudioAsync(
            Files.Select(file => file.Path).ToArray());
    }

    [RelayCommand(CanExecute = nameof(CanPreviewAudioIdentifiers))]
    private async Task PreviewAudioIdentifiersAsync()
    {
        if (SelectedAudioMatch is null)
            return;
        OperationRecipe recipe =
            AudioDiscoveryRows.CreateTagRecipe(
                SelectedAudioMatch,
                _localization);
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            [SelectedAudioMatch.Path], recipe, progress, ct));
        SetStatus(
            "Workbench.Status.AudioIdentifiersPreviewed");
    }

    [RelayCommand(CanExecute = nameof(CanResolveSelectedRecording))]
    private async Task ResolveSelectedRecordingAsync()
    {
        if (SelectedAudioMatch is null ||
            SelectedAudioMatch.MusicBrainzRecordingIdValues.Length != 1)
            return;
        BeginOperation(
            L("Workbench.Activity.ResolvingMusicBrainz"));
        try
        {
            MusicBrainzReleaseResult result =
                await _musicBrainz.ResolveRecordingAsync(
                    SelectedAudioMatch.MusicBrainzRecordingIdValues[0],
                    CreateProgress(),
                    _cancellation!.Token);
            ReleaseMatches.Clear();
            foreach (MusicBrainzReleaseRow row in MusicBrainzReleaseRows.Create(
                         SelectedAudioMatch.Path, result))
                ReleaseMatches.Add(row);
            SelectedRelease = ReleaseMatches.FirstOrDefault();
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.MusicBrainzReleases;
            SetCountStatus(
                "Workbench.Status.MusicBrainzResolved",
                ReleaseMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.MusicBrainzLookupCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.MusicBrainzLookupFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchMusicBrainzReleases))]
    private async Task SearchMusicBrainzReleasesAsync()
    {
        HasCompletedOnlineSearch = false;
        SelectedOnlineMetadataResultStep =
            WorkbenchOnlineMetadataResultStep.MusicBrainzReleases;
        BeginOperation(
            L("Workbench.Activity.SearchingMusicBrainz"));
        try
        {
            MusicBrainzReleaseSearchResult result =
                await _musicBrainz.SearchReleasesAsync(
                    ReleaseSearch.CreateQuery(),
                    CreateProgress(),
                    _cancellation!.Token);
            SelectedRelease = null;
            ReleaseMatches.Clear();
            ClearReleaseTrackMappings();
            string sourcePath = SelectedFile?.Path ?? "";
            foreach (MusicBrainzReleaseRow row in
                     MusicBrainzReleaseRows.CreateSearch(sourcePath, result))
                ReleaseMatches.Add(row);
            SelectedRelease = ReleaseMatches.FirstOrDefault();
            HasCompletedOnlineSearch = true;
            SetCountStatus(
                "Workbench.Status.MusicBrainzFound",
                ReleaseMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.MusicBrainzSearchCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.MusicBrainzSearchFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchOnlineReleases))]
    private async Task SearchOnlineReleasesAsync()
    {
        if (SelectedOnlineMetadataProvider.Provider ==
            WorkbenchOnlineMetadataProvider.MusicBrainz)
        {
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.MusicBrainzReleases;
            await SearchMusicBrainzReleasesAsync();
        }
        else
        {
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.DiscogsReleases;
            await SearchDiscogsReleasesAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchDiscogsReleases))]
    private async Task SearchDiscogsReleasesAsync()
    {
        if (_discogs is null)
            return;
        HasCompletedOnlineSearch = false;
        SelectedOnlineMetadataResultStep =
            WorkbenchOnlineMetadataResultStep.DiscogsReleases;
        BeginOperation(
            L("Workbench.Activity.SearchingDiscogs"));
        try
        {
            DiscogsReleaseSearchResult result =
                await _discogs.SearchReleasesAsync(
                    DiscogsSearch.CreateQuery(),
                    CreateProgress(),
                    _cancellation!.Token);
            SelectedDiscogsRelease = null;
            DiscogsMatches.Clear();
            string source = result.OfflineFallback
                ? L("Workbench.Online.Source.OfflineCache")
                : result.FromCache
                    ? L("Workbench.Online.Source.Cache")
                    : "Discogs";
            foreach (DiscogsReleaseCandidate candidate in result.Releases)
                DiscogsMatches.Add(
                    DiscogsReleaseRow.Create(candidate, source));
            SelectedDiscogsRelease = DiscogsMatches.FirstOrDefault();
            HasCompletedOnlineSearch = true;
            SetCountStatus(
                "Workbench.Status.DiscogsFound",
                DiscogsMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.DiscogsSearchCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.DiscogsSearchFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadDiscogsReleaseDetails))]
    private async Task LoadDiscogsReleaseDetailsAsync()
    {
        if (_discogs is null || SelectedDiscogsRelease is null)
            return;
        BeginOperation(
            L("Workbench.Activity.LoadingDiscogsDetails"));
        try
        {
            DiscogsReleaseRow selected = SelectedDiscogsRelease;
            DiscogsReleaseCandidate release =
                await _discogs.GetReleaseAsync(
                    selected.ReleaseId,
                    CreateProgress(),
                    _cancellation!.Token);
            var detailed = DiscogsReleaseRow.Create(
                release, selected.Source);
            int index = DiscogsMatches.IndexOf(selected);
            if (index >= 0)
                DiscogsMatches[index] = detailed;
            SelectedDiscogsRelease = detailed;
            SetCountStatus(
                "Workbench.Status.DiscogsDetailsLoaded",
                release.Tracks.Length,
                release.ReleaseId);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.DiscogsDetailsCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.DiscogsDetailsFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuildDiscogsReleaseMapping))]
    private async Task BuildDiscogsReleaseMappingAsync()
    {
        if (_discogsMapping is null || SelectedDiscogsRelease is null)
            return;
        BeginOperation(
            L("Workbench.Activity.MappingDiscogsTracks"));
        try
        {
            DiscogsReleaseCandidate release =
                await EnsureSelectedDiscogsReleaseDetailsAsync(
                    CreateProgress(), _cancellation!.Token);
            DiscogsSourceFile[] sources = Files.Select(file =>
                new DiscogsSourceFile(
                    file.Path,
                    file.Title,
                    file.Artist,
                    ParsePositive(file.Disc),
                    ParsePositive(file.Track),
                    file.Document.Codec is null
                        ? null
                        : TimeSpan.FromSeconds(
                            file.Document.Codec.DurationInSeconds)))
                .ToArray();
            DiscogsReleaseMapping mapping =
                await _discogsMapping.MapAsync(
                    release,
                    sources,
                    CreateProgress(),
                    _cancellation!.Token);
            ClearDiscogsTrackMappings();
            foreach (DiscogsTrackMatch match in mapping.Files)
            {
                var row = new DiscogsTrackMappingRow(
                    match,
                    _localization);
                row.PropertyChanged += OnDiscogsMappingChanged;
                DiscogsTrackMappings.Add(row);
            }
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.TrackMapping;
            SetStatus(
                WorkbenchStatusTexts.DiscogsMappingReady(
                    mapping.SuggestedCount,
                    mapping.Files.Length,
                    mapping.AmbiguousCount));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.DiscogsMappingCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.DiscogsMappingFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            PreviewDiscogsReleaseMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewDiscogsReleaseMetadata))]
    private async Task PreviewDiscogsReleaseMetadataAsync()
    {
        if (_discogsMapping is null || SelectedDiscogsRelease is null)
            return;
        DiscogsConfirmedTrack[] confirmed = DiscogsTrackMappings
            .Where(row => row.IsIncluded && row.SelectedTrack is not null)
            .Select(row => new DiscogsConfirmedTrack(
                row.Path, row.SelectedTrack!.Track))
            .ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> edits =
            _discogsMapping.CreateEdits(
                SelectedDiscogsRelease.Candidate,
                confirmed,
                DiscogsImport.CreateOptions());
        await PreviewAsync((progress, ct) =>
            _operations.PreviewValueEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.DiscogsMetadata",
                    SelectedDiscogsRelease.Title),
                progress,
                ct));
        SetStatus(
            "Workbench.Status.DiscogsMetadataPreviewed");
    }

    [RelayCommand(CanExecute = nameof(CanPreviewDiscogsReleaseArtwork))]
    private async Task PreviewDiscogsReleaseArtworkAsync()
    {
        if (_discogs is null || SelectedDiscogsRelease is null)
            return;
        DiscogsReleaseRow selected = SelectedDiscogsRelease;
        string[] paths = ConfirmedDiscogsReleasePaths();
        string releaseTitle = selected.Title;
        await PreviewAsync(async (progress, ct) =>
        {
            CoverArtDownload download =
                await _discogs.DownloadPrimaryArtworkAsync(
                    selected.Candidate,
                    progress,
                    ct);
            var image = new ArtworkInput(
                ID3v2Util.APICType.FrontCover,
                download.ContentType,
                download.Data,
                LF(
                    "Workbench.Online.DiscogsArtworkDescription",
                    selected.ReleaseId));
            var edits = paths.ToDictionary(
                path => path,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    image),
                PathComparer);
            return await _operations.PreviewArtworkEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.DiscogsArtwork",
                    releaseTitle),
                progress,
                ct);
        }, ReviewedMediaMutationKind.Artwork);
        if (_plan is not null)
        {
            SetStatus(
                "Workbench.Status.DiscogsArtworkPreviewed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseReportOutput))]
    private async Task BrowseReportOutputAsync()
    {
        string? path = ReportEditor.OneFilePerGroup
            ? await _files.PickFolderAsync(
                L("Workbench.Picker.ReportFolder.Title"))
            : await _files.SaveFileAsync(
                L("Workbench.Picker.ReportOutput.Title"),
                "music-library-report." +
                ReportEditor.SuggestedExtension,
                ReportEditor.SuggestedExtension);
        if (!string.IsNullOrWhiteSpace(path))
            ReportEditor.OutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreviewReport))]
    private async Task PreviewReportAsync()
    {
        if (_reports is null)
            return;
        BeginOperation(
            L("Workbench.Activity.BuildingReportPreview"));
        try
        {
            ReportExportPlan plan = await _reports.PreviewAsync(
                new(
                    Files.Select(file => file.Path).ToArray(),
                    ReportEditor.CreateConfiguration()),
                CreateProgress(),
                _cancellation!.Token);
            _reportPlan = plan;
            ReportOutputs.Clear();
            foreach (ReportFilePlan file in plan.Files)
                ReportOutputs.Add(new(
                    string.IsNullOrWhiteSpace(file.Group)
                        ? L("Common.All")
                        : file.Group,
                    file.DestinationPath,
                    file.RowCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountStatus(
                    "Workbench.Status.ReportPreviewBlocked",
                    blockers);
            else
                SetCountStatus(
                    "Workbench.Status.ReportPreviewReady",
                    plan.Files.Count);
            ApplyReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateReportPlan();
            SetStatus(
                "Workbench.Status.ReportPreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidateReportPlan();
            SetFailure(
                "Workbench.Status.ReportPreviewFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyReport))]
    private async Task ApplyReportAsync()
    {
        if (_reports is null || _reportPlan is null)
            return;
        BeginOperation(
            L("Workbench.Activity.WritingReport"));
        try
        {
            ReportExportResult result = await _reports.ApplyAsync(
                _reportPlan,
                CreateProgress(),
                _cancellation!.Token);
            _reportPlan = null;
            SetStatus(
                WorkbenchStatusTexts.ReportWritten(
                    result.FileCount,
                    result.RowCount));
            ApplyReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.ReportOutputCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.ReportOutputFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowsePlaylistOutput))]
    private async Task BrowsePlaylistOutputAsync()
    {
        string? path = PlaylistEditor.OnePlaylistPerGroup
            ? await _files.PickFolderAsync(
                L("Workbench.Picker.PlaylistFolder.Title"))
            : await _files.SaveFileAsync(
                L("Workbench.Picker.PlaylistOutput.Title"),
                "music-playlist." +
                PlaylistEditor.SuggestedExtension,
                PlaylistEditor.SuggestedExtension);
        if (!string.IsNullOrWhiteSpace(path))
            PlaylistEditor.OutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreviewPlaylist))]
    private async Task PreviewPlaylistAsync()
    {
        if (_playlists is null)
            return;
        BeginOperation(
            L("Workbench.Activity.BuildingPlaylistPreview"));
        try
        {
            PlaylistWorkspacePlan plan =
                await _playlists.PreviewAsync(
                    new(
                        Files.Select(file => file.Path).ToArray(),
                        PlaylistEditor.CreateConfiguration()),
                    CreateProgress(),
                    _cancellation!.Token);
            _playlistPlan = plan;
            PlaylistOutputs.Clear();
            foreach (PlaylistWorkspaceFilePlan file in plan.Files)
                PlaylistOutputs.Add(new(
                    string.IsNullOrWhiteSpace(file.Group)
                        ? L("Common.All")
                        : file.Group,
                    file.DestinationPath,
                    file.TrackCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountStatus(
                    "Workbench.Status.PlaylistPreviewBlocked",
                    blockers);
            else
                SetCountStatus(
                    "Workbench.Status.PlaylistPreviewReady",
                    plan.Files.Count);
            ApplyPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidatePlaylistPlan();
            SetStatus(
                "Workbench.Status.PlaylistPreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidatePlaylistPlan();
            SetFailure(
                "Workbench.Status.PlaylistPreviewFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyPlaylist))]
    private async Task ApplyPlaylistAsync()
    {
        if (_playlists is null || _playlistPlan is null)
            return;
        BeginOperation(
            L("Workbench.Activity.WritingPlaylist"));
        try
        {
            PlaylistWorkspaceResult result =
                await _playlists.ApplyAsync(
                    _playlistPlan,
                    CreateProgress(),
                    _cancellation!.Token);
            _playlistPlan = null;
            SetStatus(
                WorkbenchStatusTexts.PlaylistWritten(
                    result.PlaylistCount,
                    result.TrackReferenceCount));
            ApplyPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.PlaylistOutputCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.PlaylistOutputFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseExternalToolExecutable))]
    private async Task BrowseExternalToolExecutableAsync()
    {
        string? path = await _files.PickFileAsync(
            L("Workbench.Picker.ExternalTool.Title"));
        if (!string.IsNullOrWhiteSpace(path))
            ExternalToolEditor.Executable = path;
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseExternalToolWorkingDirectory))]
    private async Task BrowseExternalToolWorkingDirectoryAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Workbench.Picker.ExternalWorkingDirectory.Title"));
        if (!string.IsNullOrWhiteSpace(path))
            ExternalToolEditor.WorkingDirectory = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreviewExternalTool))]
    private void PreviewExternalTool()
    {
        if (_externalTools is null)
            return;
        ExternalToolPlan plan = _externalTools.Preview(
            ExternalToolEditor.CreateDefinition(),
            Files.Select(file => file.Path).ToArray());
        _externalToolPlan = plan;
        ExternalToolInvocations.Clear();
        for (int index = 0; index < plan.Invocations.Count; index++)
        {
            ExternalToolInvocation invocation =
                plan.Invocations[index];
            ExternalToolInvocations.Add(new(
                index + 1,
                invocation.Executable,
                string.Join(
                    Environment.NewLine,
                    invocation.Arguments),
                invocation.WorkingDirectory ??
                    L("Workbench.Tools.ApplicationDefault"),
                invocation.SourcePaths.Count));
        }
        int blockers = plan.Issues.Count(issue =>
            issue.Severity == OperationIssueSeverity.Blocker);
        if (blockers > 0)
            SetCountStatus(
                "Workbench.Status.ExternalToolPreviewBlocked",
                blockers);
        else
            SetCountStatus(
                "Workbench.Status.ExternalToolPreviewReady",
                plan.Invocations.Count);
        RunExternalToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    [RelayCommand(CanExecute = nameof(CanRunExternalTool))]
    private async Task RunExternalToolAsync()
    {
        if (_externalTools is null || _externalToolPlan is null)
            return;
        if (!await _dialogs.ConfirmAsync(
                L("Workbench.Dialog.RunTool.Title"),
                LF(
                    "Workbench.Dialog.RunTool.Message",
                    _externalToolPlan.Definition.Name,
                    _externalToolPlan.Invocations.Count),
                L("Common.Run")))
            return;
        BeginOperation(
            LF(
                "Workbench.Activity.RunningTool",
                _externalToolPlan.Definition.Name));
        try
        {
            ExternalToolRunResult result =
                await _externalTools.RunAsync(
                    _externalToolPlan,
                    CreateProgress(),
                    _cancellation!.Token);
            _externalToolPlan = null;
            SetStatus(
                "Workbench.Status.ExternalToolFinished",
                result.SucceededCount,
                result.FailedCount);
            RunExternalToolCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.ExternalToolCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.ExternalToolFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanFindReleaseArtwork))]
    private async Task FindReleaseArtworkAsync()
    {
        if (SelectedRelease is null)
            return;
        BeginOperation(
            L("Workbench.Activity.FindingCoverArt"));
        try
        {
            IProgress<OperationProgress> progress = CreateProgress();
            CoverArtArchiveResult result =
                await _coverArt.GetReleaseArtworkAsync(
                    SelectedRelease.ReleaseId,
                    progress,
                    _cancellation!.Token);
            ArtworkMatches.Clear();
            foreach (CoverArtArchiveCandidate candidate in result.Images)
                ArtworkMatches.Add(new(
                    candidate,
                    _localization));
            for (int index = 0; index < ArtworkMatches.Count; index++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                CoverArtCandidateRow row = ArtworkMatches[index];
                progress.Report(new(
                    OperationPhase.Planning,
                    index,
                    ArtworkMatches.Count,
                    Message: LF(
                        "Workbench.Progress.LoadingArtworkThumbnail",
                        index + 1,
                        ArtworkMatches.Count)));
                try
                {
                    CoverArtDownload download =
                        await _coverArt.DownloadAsync(
                            row.Candidate,
                            thumbnail: true,
                            ct: _cancellation.Token);
                    row.ThumbnailSource =
                        await _thumbnails.CreateImageSourceAsync(
                            download.Data, 180, _cancellation.Token);
                    if (download.FromCache)
                        row.SetThumbnailStatus(
                            "Workbench.Online.Thumbnail.Cached");
                    else
                        row.SetThumbnailStatus(
                            "Workbench.Online.Thumbnail.Bytes",
                            download.Data.Length);
                }
                catch (Exception error) when (
                    error is not OperationCanceledException)
                {
                    row.SetThumbnailStatus(
                        "Workbench.Online.Thumbnail.Failed");
                    row.ThumbnailDiagnosticDetail =
                        error.Message;
                }
            }
            SelectedArtworkMatch = ArtworkMatches.FirstOrDefault(row =>
                row.Candidate.IsFront) ?? ArtworkMatches.FirstOrDefault();
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.Artwork;
            if (ArtworkMatches.Count == 0)
                SetStatus(
                    "Workbench.Status.NoCoverArt");
            else
                SetCountStatus(
                    "Workbench.Status.CoverArtLoaded",
                    ArtworkMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.CoverArtCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.CoverArtFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewReleaseArtwork))]
    private async Task PreviewReleaseArtworkAsync()
    {
        if (SelectedArtworkMatch is null || SelectedRelease is null)
            return;
        string[] paths = ConfirmedReleasePaths();
        CoverArtCandidateRow selected = SelectedArtworkMatch;
        string releaseTitle = SelectedRelease.Title;
        await PreviewAsync(async (progress, ct) =>
        {
            CoverArtDownload download = await _coverArt.DownloadAsync(
                selected.Candidate,
                thumbnail: false,
                progress,
                ct);
            var image = new ArtworkInput(
                ID3v2Util.APICType.FrontCover,
                download.ContentType,
                download.Data,
                string.IsNullOrWhiteSpace(selected.Comment)
                    ? null
                    : selected.Comment);
            var edits = paths.ToDictionary(
                path => path,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    image),
                PathComparer);
            return await _operations.PreviewArtworkEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.CoverArtArchive",
                    releaseTitle),
                progress,
                ct);
        }, ReviewedMediaMutationKind.Artwork);
        if (_plan is not null)
        {
            SetStatus(
                "Workbench.Status.ReleaseArtworkPreviewed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedArtwork))]
    private async Task PreviewLocalArtworkAsync()
    {
        string? artworkPath = await _files.PickFileAsync(
            L("Workbench.Picker.FrontCover.Title"),
            [new(L("Workbench.Picker.ArtworkImages"),
                [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"])]);
        if (artworkPath is null || SelectedFile is null)
            return;
        string targetPath = SelectedFile.Path;
        await PreviewAsync(async (progress, ct) =>
        {
            progress.Report(new(
                OperationPhase.Planning,
                0,
                1,
                artworkPath,
                LF(
                    "Workbench.Progress.ReadingFile",
                    Path.GetFileName(artworkPath))));
            byte[] data = await File.ReadAllBytesAsync(artworkPath, ct);
            var edits = new Dictionary<string, ArtworkValueEdit>(
                PathComparer)
            {
                [targetPath] = new(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    new(
                        ID3v2Util.APICType.FrontCover,
                        MimeTypeFromPath(artworkPath),
                        data,
                        Path.GetFileNameWithoutExtension(artworkPath))),
            };
            return await _operations.PreviewArtworkEditsAsync(
                edits,
                L("Workbench.Operation.ReplaceFrontCover"),
                progress,
                ct);
        }, ReviewedMediaMutationKind.Artwork);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedArtwork))]
    private async Task PreviewRemoveFrontCoverAsync()
    {
        if (SelectedFile is null)
            return;
        var edits = new Dictionary<string, ArtworkValueEdit>(
            PathComparer)
        {
            [SelectedFile.Path] = new(
                ArtworkValueEditMode.RemoveFrontCover),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewArtworkEditsAsync(
                edits,
                L("Workbench.Operation.RemoveFrontCover"),
                progress,
                ct),
            ReviewedMediaMutationKind.Artwork);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedArtwork))]
    private async Task PreviewRemoveAllArtworkAsync()
    {
        if (SelectedFile is null)
            return;
        var edits = new Dictionary<string, ArtworkValueEdit>(
            PathComparer)
        {
            [SelectedFile.Path] = new(ArtworkValueEditMode.RemoveAll),
        };
        await PreviewAsync((progress, ct) =>
            _operations.PreviewArtworkEditsAsync(
                edits,
                L("Workbench.Operation.RemoveAllArtwork"),
                progress,
                ct),
            ReviewedMediaMutationKind.Artwork);
    }

    [RelayCommand(CanExecute = nameof(CanAddStagedArtwork))]
    private async Task AddStagedArtworkAsync()
    {
        string? path = await PickArtworkFileAsync(
            L("Workbench.Picker.AddArtwork.Title"));
        if (path is null)
            return;
        byte[] data = await File.ReadAllBytesAsync(path);
        object? source =
            await _thumbnails.CreateImageSourceAsync(
                data,
                decodePixelWidth: 180);
        ID3v2Util.APICType type =
            StagedArtworkItems.Any(item =>
                item.Type ==
                ID3v2Util.APICType.FrontCover)
                ? ID3v2Util.APICType.Other
                : ID3v2Util.APICType.FrontCover;
        var item = new ArtworkPreviewItem(
            source,
            type,
            MimeTypeFromPath(path),
            data,
            $"{MimeTypeFromPath(path)} · " +
            $"{FormatBytes(data.LongLength)}",
            Path.GetFileNameWithoutExtension(path));
        item.PropertyChanged += OnStagedArtworkChanged;
        StagedArtworkItems.Add(item);
        SelectedStagedArtwork = item;
        MarkStagedArtworkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEditStagedArtwork))]
    private async Task ReplaceStagedArtworkAsync()
    {
        if (SelectedStagedArtwork is not { } item)
            return;
        string? path = await PickArtworkFileAsync(
            L("Workbench.Picker.ReplaceArtwork.Title"));
        if (path is null)
            return;
        byte[] data = await File.ReadAllBytesAsync(path);
        object? source =
            await _thumbnails.CreateImageSourceAsync(
                data,
                decodePixelWidth: 180);
        item.ReplaceContent(
            source,
            MimeTypeFromPath(path),
            data,
            $"{MimeTypeFromPath(path)} · " +
            $"{FormatBytes(data.LongLength)}");
        CancelPlan();
    }

    [RelayCommand(CanExecute = nameof(CanEditStagedArtwork))]
    private void RemoveStagedArtwork()
    {
        if (SelectedStagedArtwork is not { } item)
            return;
        item.PropertyChanged -= OnStagedArtworkChanged;
        StagedArtworkItems.Remove(item);
        SelectedStagedArtwork =
            StagedArtworkItems.FirstOrDefault();
        MarkStagedArtworkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanMoveStagedArtworkUp))]
    private void MoveStagedArtworkUp()
    {
        if (SelectedStagedArtwork is not { } item)
            return;
        int index = StagedArtworkItems.IndexOf(item);
        if (index <= 0)
            return;
        StagedArtworkItems.Move(index, index - 1);
        SelectedStagedArtwork = item;
        MarkStagedArtworkDirty();
        NotifyStagedArtworkMoveCommands();
    }

    [RelayCommand(CanExecute = nameof(CanMoveStagedArtworkDown))]
    private void MoveStagedArtworkDown()
    {
        if (SelectedStagedArtwork is not { } item)
            return;
        int index = StagedArtworkItems.IndexOf(item);
        if (index < 0 ||
            index >= StagedArtworkItems.Count - 1)
            return;
        StagedArtworkItems.Move(index, index + 1);
        SelectedStagedArtwork = item;
        MarkStagedArtworkDirty();
        NotifyStagedArtworkMoveCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEditStagedArtwork))]
    private async Task ExportStagedArtworkAsync()
    {
        if (SelectedStagedArtwork is not { } item)
            return;
        string extension = ArtworkFileExtension(
            item.MimeType);
        string baseName = Path.GetFileNameWithoutExtension(
            SelectedFile?.Path) ??
            L("Inspector.Artwork.DefaultFileName");
        string suggested =
            $"{baseName}-{item.Type}{extension}";
        string? path = await _files.SaveFileAsync(
            L("Workbench.Picker.ExportArtwork.Title"),
            suggested,
            extension);
        if (path is null)
            return;
        await File.WriteAllBytesAsync(path, item.Data);
        SetStatus(
            "Workbench.Status.ArtworkExported",
            item.Label,
            path);
    }

    [RelayCommand(CanExecute = nameof(CanPreviewStagedArtwork))]
    private async Task PreviewStagedArtworkAsync()
    {
        IReadOnlyList<WorkbenchTrackViewModel> targets =
            EditTargets;
        IReadOnlyDictionary<string, ArtworkSetPreviewRequest>
            requests = BuildArtworkSetRequests(
                targets,
                StagedArtworkItems,
                ArtworkMaxDimension);
        await PreviewAsync((progress, ct) =>
            _operations.PreviewArtworkSetsAsync(
                requests,
                LF(
                    "Workbench.Operation.EditEmbeddedArtwork",
                    targets.Count),
                progress,
                ct),
            ReviewedMediaMutationKind.Artwork);
    }

    [RelayCommand(CanExecute = nameof(CanBuildReleaseMapping))]
    private async Task BuildReleaseMappingAsync()
    {
        if (SelectedRelease is null)
            return;
        BeginOperation(
            L("Workbench.Activity.MappingReleaseTracks"));
        try
        {
            MusicBrainzReleaseCandidate release =
                await EnsureSelectedReleaseDetailsAsync(
                    CreateProgress(), _cancellation!.Token);
            MusicBrainzSourceFile[] sources = Files.Select(file =>
                new MusicBrainzSourceFile(
                    file.Path,
                    ConfirmedRecordingIds(file.Path),
                    file.Title,
                    file.Artist,
                    ParsePositive(file.Disc),
                    ParsePositive(file.Track),
                    file.Document.Codec is null
                        ? null
                        : TimeSpan.FromSeconds(
                            file.Document.Codec.DurationInSeconds),
                    ConfirmedRecordingScores(file.Path),
                    file.Album,
                    file.AlbumArtist))
                .ToArray();
            MusicBrainzReleaseMapping mapping =
                await _releaseMapping.MapAsync(
                    release,
                    sources,
                    CreateProgress(),
                    _cancellation!.Token);
            ClearReleaseTrackMappings();
            foreach (MusicBrainzTrackMatch match in mapping.Files)
            {
                var row = new MusicBrainzTrackMappingRow(
                    match,
                    _localization);
                row.PropertyChanged += OnReleaseMappingChanged;
                ReleaseTrackMappings.Add(row);
            }
            SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep.TrackMapping;
            SetStatus(
                WorkbenchStatusTexts.ReleaseMappingReady(
                    mapping.SuggestedCount,
                    mapping.Files.Length,
                    mapping.AmbiguousCount));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.ReleaseMappingCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.ReleaseMappingFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewReleaseMetadata))]
    private async Task PreviewReleaseMetadataAsync()
    {
        if (SelectedRelease is null)
            return;
        MusicBrainzConfirmedTrack[] confirmed = ReleaseTrackMappings
            .Where(row => row.IsIncluded && row.SelectedTrack is not null)
            .Select(row => new MusicBrainzConfirmedTrack(
                row.Path, row.SelectedTrack!.Track))
            .ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> edits =
            _releaseMapping.CreateEdits(
                SelectedRelease.Candidate,
                confirmed,
                ReleaseImport.CreateOptions());
        await PreviewAsync((progress, ct) =>
            _operations.PreviewValueEditsAsync(
                edits,
                LF(
                    "Workbench.Operation.MusicBrainzMetadata",
                    SelectedRelease.Title),
                progress,
                ct));
        SetStatus(
            "Workbench.Status.MusicBrainzMetadataPreviewed");
    }

    private async Task DiscoverAudioAsync(IReadOnlyList<string> paths)
    {
        HasCompletedOnlineDiscovery = false;
        BeginOperation(
            L("Workbench.Activity.PreparingFingerprint"));
        try
        {
            AcoustIdDiscoveryResult result = await _audioDiscovery.DiscoverAsync(
                paths, CreateProgress(), _cancellation!.Token);
            AudioMatches.Clear();
            ReleaseMatches.Clear();
            SelectedRelease = null;
            ClearReleaseTrackMappings();
            foreach (AudioDiscoveryRow row in
                     AudioDiscoveryRows.Create(
                         result,
                         _localization))
                AudioMatches.Add(row);
            SelectedAudioMatch = AudioMatches.FirstOrDefault();
            HasCompletedOnlineDiscovery = true;
            int issues = result.Files.Sum(file => file.Issues.Length);
            SetStatus(
                WorkbenchStatusTexts.FingerprintDiscovery(
                    result.FingerprintedFileCount,
                    result.CandidateCount,
                    issues));
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.FingerprintCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.FingerprintFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    public Task<bool> ConfirmNavigationAsync()
    {
        if (!HasUnsavedChanges)
            return Task.FromResult(true);
        return _dialogs.ConfirmDestructiveAsync(
            L("Workbench.Dialog.Leave.Title"),
            L("Workbench.Dialog.Leave.Message"),
            L("Workbench.Dialog.Leave.Confirm"));
    }

    private async Task ReloadAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress> progress,
        CancellationToken ct,
        List<string> postCommitWarnings)
    {
        if (paths.Count == 0)
            return;
        HashSet<string> selectedPaths = EditTargets
            .Select(file => file.Path)
            .ToHashSet(PathComparer);
        WorkbenchLoadResult loaded = await _workbench.LoadAsync(
            new(paths, Recursive: false), progress, ct);
        AddPostCommitIssues(
            loaded.Issues,
            postCommitWarnings);
        var documents = loaded.Documents.ToDictionary(document => document.Path, PathComparer);
        for (int index = 0; index < Files.Count; index++)
        {
            WorkbenchTrackViewModel previous = Files[index];
            if (!documents.TryGetValue(previous.Path, out MediaDocument? document))
                continue;
            previous.PropertyChanged -= OnTrackChanged;
            WorkbenchTrackViewModel replacement = new(document);
            replacement.PropertyChanged += OnTrackChanged;
            Files[index] = replacement;
            if (ReferenceEquals(SelectedFile, previous))
                SelectedFile = replacement;
        }
        SetSelectedFiles(Files.Where(file =>
            selectedPaths.Contains(file.Path)));
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
    }

    private void AddTrack(WorkbenchTrackViewModel track)
    {
        track.PropertyChanged += OnTrackChanged;
        Files.Add(track);
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        SelectedFile ??= track;
    }

    private void RebuildMetadataFields()
    {
        MetadataFields.Clear();
        IReadOnlyList<WorkbenchTrackViewModel> targets =
            EditTargets;
        if (targets.Count == 0)
        {
            SelectedMetadataField = null;
            return;
        }
        foreach (WorkbenchMetadataFieldRow row in
                 BuildMetadataFieldRows(targets))
            MetadataFields.Add(row);
        SelectedMetadataField = MetadataFields.FirstOrDefault();
    }

    private void ScheduleRepresentativePreview() =>
        RepresentativePreview.Schedule(
            SelectedFile?.Path,
            () => OperationEditor.CreateRecipe(
                L("Workbench.Operation.DraftRepresentativePreview")));

    public static IReadOnlyList<WorkbenchMetadataFieldRow>
        BuildMetadataFieldRows(
            IReadOnlyList<WorkbenchTrackViewModel> files)
    {
        if (files.Count == 0)
            return [];
        Dictionary<MetadataFieldKey, (
            HashSet<string> Layers,
            ImmutableArray<string> Values)>[] byFile = files
            .Select(file => file.Document.TagLayers
                .SelectMany(layer => layer.Fields.Select(field =>
                    (layer.TagType, Field: field)))
                .GroupBy(item => item.Field.Field)
                .ToDictionary(
                    group => group.Key,
                    group => (
                        group.Select(item => item.TagType)
                            .ToHashSet(
                                StringComparer.OrdinalIgnoreCase),
                        group.SelectMany(item => item.Field.Values)
                            .ToImmutableArray())))
            .ToArray();
        MetadataFieldKey[] fields = byFile
            .SelectMany(file => file.Keys)
            .Distinct()
            .OrderBy(
                field => field.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = new List<WorkbenchMetadataFieldRow>(
            fields.Length);
        foreach (MetadataFieldKey field in fields)
        {
            var values =
                new ImmutableArray<string>[files.Count];
            var layers = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            int present = 0;
            for (int index = 0;
                 index < byFile.Length;
                 index++)
            {
                if (byFile[index].TryGetValue(
                        field, out var entry))
                {
                    values[index] = entry.Values;
                    layers.UnionWith(entry.Layers);
                    present++;
                }
                else
                {
                    values[index] = [];
                }
            }
            bool mixed = values.Skip(1).Any(value =>
                !values[0].SequenceEqual(
                    value,
                    StringComparer.Ordinal));
            rows.Add(new(
                field,
                string.Join(
                    ", ",
                    layers.Order(
                        StringComparer.OrdinalIgnoreCase)),
                mixed ? [] : values[0],
                mixed,
                files.Count,
                present));
        }
        return rows;
    }

    public static IReadOnlyDictionary<
        string,
        IReadOnlyList<MetadataValueEdit>> BuildValueEdits(
            IReadOnlyList<WorkbenchTrackViewModel> files,
            MetadataFieldKey field,
            WorkbenchFieldEditMode mode,
            ImmutableArray<string> entered)
    {
        var edits = new Dictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>(PathComparer);
        foreach (WorkbenchTrackViewModel file in files)
        {
            ImmutableArray<string> current =
                file.Document.Values(field);
            ImmutableArray<string> result = mode switch
            {
                WorkbenchFieldEditMode.Replace => entered,
                WorkbenchFieldEditMode.Append =>
                    current.AddRange(entered),
                WorkbenchFieldEditMode.RemoveValues =>
                    current.Where(value =>
                            !entered.Contains(
                                value,
                                StringComparer.Ordinal))
                        .ToImmutableArray(),
                WorkbenchFieldEditMode.RemoveField => [],
                _ => entered,
            };
            edits[file.Path] = [new(field, result)];
        }
        return edits;
    }

    public static IReadOnlyDictionary<
        string,
        ArtworkSetPreviewRequest> BuildArtworkSetRequests(
            IReadOnlyList<WorkbenchTrackViewModel> files,
            IReadOnlyList<ArtworkPreviewItem> images,
            int maxDimension)
    {
        ArtworkSetPreviewRequest request =
            BuildArtworkSetRequest(
                images,
                maxDimension);
        return files.ToDictionary(
            file => file.Path,
            _ => request,
            PathComparer);
    }

    private static ArtworkSetPreviewRequest
        BuildArtworkSetRequest(
            IEnumerable<ArtworkPreviewItem> images,
            int maxDimension) =>
        new(
            [
                .. images.Select(item =>
                    new ArtworkInput(
                        item.Type,
                        item.MimeType,
                        item.Data,
                        item.Description)),
            ],
            maxDimension);

    private IReadOnlyList<WorkbenchTrackViewModel> EditTargets =>
        _selectedFiles.Count > 0
            ? _selectedFiles
            : SelectedFile is null
                ? []
                : [SelectedFile];

    private MetadataFieldKey? ResolveEditedField()
    {
        if (SelectedMetadataField is { } selected)
            return selected.Field;
        if (!string.IsNullOrWhiteSpace(CustomFieldName))
            return MetadataFieldKey.Custom(CustomFieldName);
        return SelectedNewKnownField is { } known
            ? MetadataFieldKey.Known(known.Field)
            : null;
    }

    private async Task RebuildStagedArtworkAsync(
        WorkbenchTrackViewModel? file)
    {
        _artworkHydrationCancellation?.Cancel();
        _artworkHydrationCancellation?.Dispose();
        _artworkHydrationCancellation =
            file is null
                ? null
                : new();
        CancellationToken artworkToken =
            _artworkHydrationCancellation?.Token ??
            new CancellationToken(
                canceled: true);
        int generation = ++_artworkGeneration;
        foreach (ArtworkPreviewItem item in
                 StagedArtworkItems)
            item.PropertyChanged -= OnStagedArtworkChanged;
        StagedArtworkItems.Clear();
        SelectedStagedArtwork = null;
        _stagedArtworkPath = file?.Path;
        _stagedArtworkDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (file is null)
            return;

        if (_artworkDrafts.TryGetValue(
                file.Path,
                out ArtworkSetPreviewRequest? draft))
        {
            _settingArtworkMaxDimension = true;
            ArtworkMaxDimension = draft.MaxDimension;
            _settingArtworkMaxDimension = false;
            foreach (ArtworkInput image in draft.Images)
            {
                object? source = null;
                try
                {
                    source = await _thumbnails
                        .CreateImageSourceAsync(
                            image.Data,
                            decodePixelWidth: 180,
                            artworkToken);
                }
                catch (OperationCanceledException)
                    when (artworkToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException error)
                {
                    if (generation == _artworkGeneration &&
                        ReferenceEquals(
                            file,
                            SelectedFile))
                        SetFailure(
                            "Workbench.Status.LoadingFailed",
                            error.Message);
                    return;
                }
                catch
                {
                    // Invalid artwork remains editable and removable.
                }
                if (generation != _artworkGeneration ||
                    !ReferenceEquals(file, SelectedFile))
                    return;
                var draftItem = new ArtworkPreviewItem(
                    source,
                    image.Type,
                    image.MimeType,
                    image.Data,
                    $"{image.MimeType} · " +
                    $"{FormatBytes(image.Data.LongLength)}",
                    image.Description);
                draftItem.PropertyChanged +=
                    OnStagedArtworkChanged;
                StagedArtworkItems.Add(draftItem);
            }
            _stagedArtworkDirty = true;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SelectedStagedArtwork =
                StagedArtworkItems.FirstOrDefault();
            PreviewStagedArtworkCommand
                .NotifyCanExecuteChanged();
            NotifyStagedArtworkMoveCommands();
            return;
        }

        if (!file.HasAuthoritativeArtwork)
        {
            if (_metadataDocuments is null)
            {
                AppendStatusDiagnostics(
                [
                    L(
                        "Inspector.Error.MetadataServiceUnavailable"),
                ]);
                NotifyArtworkEditCommands();
                return;
            }
            else
            {
                try
                {
                    MediaDocument hydrated =
                        await _metadataDocuments.LoadAsync(
                            file.Path,
                            includeArtwork: true,
                            artworkToken);
                    if (generation != _artworkGeneration ||
                        !ReferenceEquals(
                            file,
                            SelectedFile))
                        return;
                    file.UpdateArtwork(
                        hydrated);
                }
                catch (OperationCanceledException)
                    when (artworkToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException error)
                {
                    if (generation == _artworkGeneration &&
                        ReferenceEquals(
                            file,
                            SelectedFile))
                        SetFailure(
                            "Workbench.Status.LoadingFailed",
                            error.Message);
                    return;
                }
                catch (Exception error)
                {
                    if (generation == _artworkGeneration &&
                        ReferenceEquals(
                            file,
                            SelectedFile))
                        SetFailure(
                            "Workbench.Status.LoadingFailed",
                            error.Message);
                    return;
                }
            }
        }
        if (generation != _artworkGeneration ||
            !ReferenceEquals(file, SelectedFile))
            return;

        _settingArtworkMaxDimension = true;
        ArtworkMaxDimension = 0;
        _settingArtworkMaxDimension = false;
        for (int index = 0;
             index < file.Document.Artwork.Length;
             index++)
        {
            ArtworkModel artwork =
                file.Document.Artwork[index];
            object? source = null;
            try
            {
                source = await _thumbnails
                    .CreateImageSourceAsync(
                        artwork.Data,
                        decodePixelWidth: 180,
                        artworkToken);
            }
            catch (OperationCanceledException)
                when (artworkToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException error)
            {
                if (generation == _artworkGeneration &&
                    ReferenceEquals(
                        file,
                        SelectedFile))
                    SetFailure(
                        "Workbench.Status.LoadingFailed",
                        error.Message);
                return;
            }
            catch
            {
                // Invalid artwork remains editable and removable.
            }
            if (generation != _artworkGeneration ||
                !ReferenceEquals(file, SelectedFile))
                return;
            var item = new ArtworkPreviewItem(
                source,
                ArtworkType(artwork, index),
                ArtworkMimeType(artwork),
                artwork.Data,
                ArtworkDetails(artwork),
                artwork.Description);
            item.PropertyChanged += OnStagedArtworkChanged;
            StagedArtworkItems.Add(item);
        }
        SelectedStagedArtwork =
            StagedArtworkItems.FirstOrDefault();
        NotifyArtworkEditCommands();
        NotifyStagedArtworkMoveCommands();
    }

    private void NotifyArtworkEditCommands()
    {
        PreviewLocalArtworkCommand
            .NotifyCanExecuteChanged();
        PreviewRemoveFrontCoverCommand
            .NotifyCanExecuteChanged();
        PreviewRemoveAllArtworkCommand
            .NotifyCanExecuteChanged();
        AddStagedArtworkCommand
            .NotifyCanExecuteChanged();
        PreviewStagedArtworkCommand
            .NotifyCanExecuteChanged();
    }

    partial void OnSelectedStagedArtworkChanged(
        ArtworkPreviewItem? value) =>
        NotifyStagedArtworkMoveCommands();

    partial void OnArtworkMaxDimensionChanged(int value)
    {
        if (!_settingArtworkMaxDimension &&
            SelectedFile is not null)
            MarkStagedArtworkDirty();
    }

    private void OnStagedArtworkChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
                nameof(ArtworkPreviewItem.Type) or
                nameof(ArtworkPreviewItem.Description) or
                nameof(ArtworkPreviewItem.Data)))
            return;
        MarkStagedArtworkDirty();
    }

    private void MarkStagedArtworkDirty()
    {
        _stagedArtworkDirty = true;
        if (_stagedArtworkPath is not null)
            _artworkDrafts[_stagedArtworkPath] =
                BuildArtworkSetRequest(
                    StagedArtworkItems,
                    ArtworkMaxDimension);
        CancelPlan();
        NotifySessionChanged();
        PreviewStagedArtworkCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStagedArtworkMoveCommands()
    {
        MoveStagedArtworkUpCommand.NotifyCanExecuteChanged();
        MoveStagedArtworkDownCommand.NotifyCanExecuteChanged();
    }

    private async Task<string?> PickArtworkFileAsync(
        string title) =>
        await _files.PickFileAsync(
            title,
            [new(
                L("Workbench.Picker.ArtworkImages"),
                [
                    ".jpg",
                    ".jpeg",
                    ".png",
                    ".gif",
                    ".webp",
                    ".bmp",
                ])]);

    private static string ArtworkFileExtension(
        string? mimeType) =>
        mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };

    private static ID3v2Util.APICType ArtworkType(
        ArtworkModel image,
        int index) =>
        Enum.TryParse(
            image.Category?.Replace(" ", ""),
            ignoreCase: true,
            out ID3v2Util.APICType type)
            ? type
            : index == 0
                ? ID3v2Util.APICType.FrontCover
                : ID3v2Util.APICType.Other;

    private static string ArtworkMimeType(
        ArtworkModel image)
    {
        if (!string.IsNullOrWhiteSpace(image.ImageType))
            return image.ImageType;
        return image.Data.AsSpan().StartsWith(
            new byte[] { 0x89, 0x50, 0x4e, 0x47 })
                ? "image/png"
                : "image/jpeg";
    }

    private static string ArtworkDetails(
        ArtworkModel image) =>
        LocalizedText.Format(
            "Inspector.Artwork.Details",
            image.ImageType ??
                LocalizedText.Get(
                    "Inspector.Artwork.Image"),
            image.Width,
            image.Height,
            FormatBytes(image.Size));

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => LocalizedText.Format(
            "Inspector.Size.Megabytes",
            bytes / 1024d / 1024d),
        >= 1024 => LocalizedText.Format(
            "Inspector.Size.Kilobytes",
            bytes / 1024d),
        _ => LocalizedText.FormatCount(
            "Inspector.Size.Bytes",
            bytes),
    };

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchTrackViewModel.HasChanges))
        {
            RebuildPendingChanges();
            ClearReleaseTrackMappings();
            NotifySessionChanged();
        }
    }

    private void CancelPlan()
    {
        _metadataIntents.Clear();
        RecomposeMetadataPlan();
    }

    private void RecomposeMetadataPlan()
    {
        _plan = ComposeMetadataPlan();
        RefreshMetadataPlanPresentation();
    }

    private MetadataOperationPlan? ComposeMetadataPlan() =>
        ComposeMetadataPlan(
            _metadataIntents);

    private MetadataOperationPlan? ComposeMetadataPlan(
        IReadOnlyList<ReviewedMetadataMutationIntent> intents) =>
        intents.Count switch
        {
            0 => null,
            1 => intents[0].Plan,
            _ => MetadataOperationPlanComposer.Combine(
                L("Workbench.Operation.PendingChanges"),
                [
                    .. intents.Select(intent =>
                        intent.Plan),
                ]),
        };

    private void RefreshMetadataPlanPresentation()
    {
        if (_plan is null)
        {
            PreviewChanges.Clear();
            PendingOperations.Clear();
        }
        else
        {
            MetadataPreviewRowBuilder.Populate(
                PreviewChanges,
                _plan,
                _localization);
            PendingMetadataOperationRowBuilder.Populate(
                PendingOperations,
                _plan,
                _localization);
        }
        HasApplicablePreview =
            _plan?.CanApply == true ||
            _transcodePlans.Count > 0 ||
            _fileOperationPlans.Count > 0;
        RebuildPendingChanges();
    }

    private void RetainMetadataPlansForPaths(
        IEnumerable<string> paths,
        IReadOnlyCollection<ReviewedMetadataMutationIntent>?
            capturedIntents = null,
        bool refreshPresentation = true)
    {
        HashSet<string> retained =
            paths.ToHashSet(PathComparer);
        HashSet<ReviewedMetadataMutationIntent>? captured =
            capturedIntents is null
                ? null
                : new(
                    capturedIntents,
                    ReferenceEqualityComparer.Instance);
        for (int index = _metadataIntents.Count - 1;
             index >= 0;
             index--)
        {
            ReviewedMetadataMutationIntent intent =
                _metadataIntents[index];
            if (captured?.Contains(intent) == false)
                continue;
            ImmutableArray<MetadataFilePlan> files =
            [
                .. intent.Plan.Files.Where(file =>
                    retained.Contains(file.Path)),
            ];
            if (files.IsDefaultOrEmpty)
            {
                _metadataIntents.RemoveAt(index);
                continue;
            }
            MetadataOperationPlan plan =
                intent.Plan with
                {
                    Files = files,
                };
            _metadataIntents[index] =
                intent with
                {
                    Paths =
                    [
                        .. files.Select(file =>
                            file.Path),
                    ],
                    Plan = plan,
                };
        }
        _plan = ComposeMetadataPlan();
        if (refreshPresentation)
            RefreshMetadataPlanPresentation();
    }

    private void RemoveCapturedMetadataIntents(
        IReadOnlyCollection<ReviewedMetadataMutationIntent>
            capturedIntents)
    {
        HashSet<ReviewedMetadataMutationIntent> captured =
            new(
                capturedIntents,
                ReferenceEqualityComparer.Instance);
        _metadataIntents.RemoveAll(
            intent => captured.Contains(intent));
    }

    private static bool MetadataPlansConflict(
        MetadataOperationPlan existing,
        MetadataOperationPlan incoming)
    {
        Dictionary<
            string,
            Dictionary<string, string>> existingMutations =
                BuildMetadataMutationMap(existing);
        foreach ((string path,
                     Dictionary<string, string> incomingSlots)
                 in BuildMetadataMutationMap(incoming))
        {
            if (!existingMutations.TryGetValue(
                    path,
                    out Dictionary<string, string>?
                        existingSlots))
                continue;
            foreach ((string slot, string fingerprint)
                     in incomingSlots)
            {
                if (existingSlots.TryGetValue(
                        slot,
                        out string? existingFingerprint) &&
                    !StringComparer.Ordinal.Equals(
                        existingFingerprint,
                        fingerprint))
                    return true;
            }
        }
        return false;
    }

    private static bool MetadataPlanFullyRepresents(
        MetadataOperationPlan existing,
        MetadataOperationPlan incoming)
    {
        Dictionary<
            string,
            Dictionary<string, string>> existingMutations =
                BuildMetadataMutationMap(existing);
        Dictionary<
            string,
            Dictionary<string, string>> incomingMutations =
                BuildMetadataMutationMap(incoming);
        bool foundMutation = false;
        foreach ((string path,
                     Dictionary<string, string> incomingSlots)
                 in incomingMutations)
        {
            if (!existingMutations.TryGetValue(
                    path,
                    out Dictionary<string, string>?
                        existingSlots))
                return false;
            foreach ((string slot, string fingerprint)
                     in incomingSlots)
            {
                foundMutation = true;
                if (!existingSlots.TryGetValue(
                        slot,
                        out string? existingFingerprint) ||
                    !StringComparer.Ordinal.Equals(
                        existingFingerprint,
                        fingerprint))
                    return false;
            }
        }
        return foundMutation;
    }

    private static Dictionary<
        string,
        Dictionary<string, string>>
        BuildMetadataMutationMap(
            MetadataOperationPlan plan)
    {
        var result = new Dictionary<
            string,
            Dictionary<string, string>>(
            PathComparer);
        foreach (MetadataFilePlan file in
                 plan.Files.Where(file =>
                     file.HasChanges))
        {
            if (!result.TryGetValue(
                    file.Path,
                    out Dictionary<string, string>? slots))
            {
                slots = new(
                    StringComparer.OrdinalIgnoreCase);
                result[file.Path] = slots;
            }

            foreach (MetadataFieldDifference difference in
                     file.Differences)
                slots[
                    "field:" +
                    MetadataGridValueKey.For(
                        difference.Field)] =
                    JsonSerializer.Serialize(
                        difference.After);
            foreach (MetadataValueEdit edit in file.Edits)
                slots[
                    "field:" +
                    MetadataGridValueKey.For(
                        edit.Field)] =
                    JsonSerializer.Serialize(
                        edit.Values);

            if (file.ArtworkEdit is { } artwork)
            {
                slots["artwork"] =
                    JsonSerializer.Serialize(
                        artwork.Images.Select(image =>
                            new
                            {
                                image.Type,
                                image.MimeType,
                                image.Description,
                                Hash =
                                    Convert.ToHexString(
                                        System.Security.Cryptography
                                            .SHA256.HashData(
                                                image.Data)),
                            }));
            }
            else if (file.ArtworkDifference is { } artworkDifference)
            {
                slots["artwork"] =
                    JsonSerializer.Serialize(
                        artworkDifference.After.Select(image =>
                            new
                            {
                                image.Type,
                                image.MimeType,
                                image.Description,
                                image.Size,
                                image.Hash,
                            }));
            }

            if (!file.TagLayerEdits.IsDefaultOrEmpty)
            {
                foreach (TagLayerEdit edit in
                         file.TagLayerEdits)
                    slots[$"tag-layer:{edit.Kind}"] =
                        JsonSerializer.Serialize(
                            new
                            {
                                edit.Mode,
                                edit.CopyMode,
                            });
            }
            if (!file.TagLayerConversions.IsDefaultOrEmpty)
            {
                foreach (TagLayerConversionEdit conversion in
                         file.TagLayerConversions)
                {
                    string fingerprint =
                        JsonSerializer.Serialize(
                            conversion);
                    slots[
                        $"tag-layer:{conversion.Source}"] =
                        fingerprint;
                    slots[
                        $"tag-layer:{conversion.Target}"] =
                        fingerprint;
                }
            }
            if (file.Id3VersionEdit is { } id3)
                slots[
                    $"tag-layer:{TagLayerKind.Id3v2}"] =
                    JsonSerializer.Serialize(id3);
        }
        return result;
    }

    private bool HasDirectPendingChanges =>
        Files.Any(file => file.HasChanges) ||
        _inspector?.HasUnsavedChanges == true;

    private PendingDirectChangesCapture
        CaptureDirectPendingChanges()
    {
        (
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>?
                ValueEdits,
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>?
                ArtworkEdits) inspectorInputs =
            _inspector?.HasUnsavedChanges == true
                ? _inspector
                    .CreatePendingOperationInputs()
                : (null, null);
        return new(
            Files.Where(file =>
                    file.HasChanges)
                .ToImmutableDictionary(
                    file => file.Path,
                    file => file.CreateEdits(),
                    PathComparer),
            inspectorInputs.ValueEdits?
                .ToImmutableDictionary(
                    pair => pair.Key,
                    pair =>
                        (IReadOnlyList<MetadataValueEdit>)
                        pair.Value.ToImmutableArray(),
                    PathComparer),
            inspectorInputs.ArtworkEdits?
                .ToImmutableDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    PathComparer),
            _inspector?.HasUnsavedChanges == true
                ?
                [
                    .. _inspector.Selection.Paths,
                ]
                : []);
    }

    private void RebuildPendingChanges()
    {
        PendingChanges.Clear();
        foreach (MetadataPreviewRow row in PreviewChanges)
            PendingChanges.Add(row);
        foreach (MetadataPreviewRow row in Files
                     .Where(file => file.HasChanges)
                     .SelectMany(file =>
                         file.CreatePendingChangeRows(
                             field =>
                                 MetadataPreviewRowBuilder
                                     .DisplayFieldName(
                                         field,
                                         _localization))))
        {
            if (!PendingChanges.Any(existing =>
                    PendingRowsAreEquivalent(existing, row)))
                PendingChanges.Add(row);
        }
        if (_inspector is not null)
        {
            foreach (MetadataPreviewRow row in
                         _inspector.CreatePendingChangeRows())
                if (!PendingChanges.Any(existing =>
                        PendingRowsAreEquivalent(existing, row)))
                    PendingChanges.Add(row);
        }
        foreach (ReviewedFileOperationItem item in
                 OrderedFileOperationPlans()
                     .SelectMany(plan =>
                         plan.Items)
                     .OrderBy(item =>
                         item.SourcePath,
                         PathComparer)
                     .ThenBy(item =>
                         item.DestinationPath ?? "",
                         PathComparer))
            PendingChanges.Add(new(
                Path.GetFileName(item.SourcePath),
                L("Workbench.PendingChanges.FileOperation"),
                item.SourcePath,
                item.DestinationPath ??
                L("Workbench.PendingChanges.NoDestination"),
                FormatPendingDiagnostics(item.Issues)));
        foreach (AudioTranscodePlanItem item in
                 OrderedTranscodePlans()
                     .SelectMany(plan =>
                         plan.Items)
                     .OrderBy(item =>
                         item.SourcePath,
                         PathComparer)
                     .ThenBy(item =>
                         item.DestinationPath,
                         PathComparer))
            PendingChanges.Add(new(
                Path.GetFileName(item.SourcePath),
                L("Transcode.Pending.Field"),
                item.SourcePath,
                item.DestinationPath,
                FormatTranscodePendingDiagnostics(
                    item)));
        OnPropertyChanged(
            nameof(HasPendingChanges));
        OnPropertyChanged(
            nameof(PendingMutationUnits));
        RevertPendingChangesCommand
            .NotifyCanExecuteChanged();
    }

    private static bool PendingRowsAreEquivalent(
        MetadataPreviewRow left,
        MetadataPreviewRow right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            left.File,
            right.File) &&
        StringComparer.OrdinalIgnoreCase.Equals(
            left.Field,
            right.Field) &&
        StringComparer.Ordinal.Equals(
            left.Before,
            right.Before) &&
        StringComparer.Ordinal.Equals(
            left.After,
            right.After);

    private static string? FormatPendingDiagnostics(
        IEnumerable<OperationIssue> issues)
    {
        string[] messages =
        [
            .. issues
                .Select(issue => issue.Message)
                .Where(message =>
                    !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal),
        ];
        return messages.Length == 0
            ? null
            : string.Join(
                Environment.NewLine,
                messages);
    }

    private string? FormatTranscodePendingDiagnostics(
        AudioTranscodePlanItem item)
    {
        string? previewDiagnostics =
            FormatPendingDiagnostics(
                item.Issues);
        _transcodeStageDiagnostics.TryGetValue(
            item.Id,
            out string? stageDiagnostics);
        string[] messages =
        [
            .. new[]
                {
                    previewDiagnostics,
                    stageDiagnostics,
                }
                .Where(message =>
                    !string.IsNullOrWhiteSpace(
                        message))
                .Select(message =>
                    message!)
                .Distinct(
                    StringComparer.Ordinal),
        ];
        return messages.Length == 0
            ? null
            : string.Join(
                Environment.NewLine,
                messages);
    }

    private async Task<
        ReviewedMetadataMutationIntent?>
        PreviewDirectPendingChangesAsync(
            PendingDirectChangesCapture capture,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
    {
        var editsByPath = new Dictionary<
            string,
            Dictionary<string, MetadataValueEdit>>(
            PathComparer);
        foreach ((string path,
                     ImmutableArray<TagEdit> edits) in
                 capture.TrackEdits)
        {
            Dictionary<string, MetadataValueEdit> fileEdits =
                GetOrCreate(path);
            foreach (TagEdit edit in edits)
            {
                var valueEdit = new MetadataValueEdit(
                    MetadataFieldKey.Known(edit.Field),
                    edit.Value is null ? [] : [edit.Value]);
                fileEdits[
                    MetadataGridValueKey.For(
                        valueEdit.Field)] = valueEdit;
            }
        }

        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>? artworkEdits = null;
        if (capture.InspectorValueEdits is not null ||
            capture.InspectorArtworkEdits is not null)
        {
            if (capture.InspectorValueEdits is not null)
            {
                foreach ((string path,
                             IReadOnlyList<MetadataValueEdit> edits)
                         in capture.InspectorValueEdits)
                {
                    Dictionary<string, MetadataValueEdit> fileEdits =
                        GetOrCreate(path);
                    foreach (MetadataValueEdit edit in edits)
                        fileEdits[
                            MetadataGridValueKey.For(
                                edit.Field)] = edit;
                }
            }
            artworkEdits =
                capture.InspectorArtworkEdits;
        }

        MetadataOperationPlan? valuesPlan = editsByPath.Count == 0
            ? null
            : await _operations.PreviewValueEditsAsync(
                editsByPath.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<
                        MetadataValueEdit>)pair.Value.Values
                        .ToArray(),
                    PathComparer),
                L("Workbench.Operation.GridAndInspectorEdits"),
                progress,
                cancellationToken);
        MetadataOperationPlan? artworkPlan = artworkEdits is null
            ? null
            : await _operations.PreviewArtworkSetsAsync(
                artworkEdits,
                L("Workbench.Operation.InspectorArtwork"),
                progress,
                cancellationToken);
        if (valuesPlan is null && artworkPlan is null)
            return null;
        MetadataOperationPlan combined =
            MetadataOperationPlanComposer.Combine(
                L(
                    "Workbench.Operation.PendingChanges"),
                valuesPlan,
                artworkPlan);
        return ReviewedMetadataMutationIntent.Create(
            combined,
            (valuesPlan is not null,
                artworkPlan is not null) switch
            {
                (true, true) =>
                [
                    ReviewedMediaMutationKind
                        .Metadata,
                    ReviewedMediaMutationKind
                        .Artwork,
                ],
                (false, true) =>
                [
                    ReviewedMediaMutationKind
                        .Artwork,
                ],
                _ =>
                [
                    ReviewedMediaMutationKind
                        .Metadata,
                ],
            });

        Dictionary<string, MetadataValueEdit> GetOrCreate(
            string path)
        {
            if (!editsByPath.TryGetValue(
                    path,
                    out Dictionary<
                        string,
                        MetadataValueEdit>? edits))
            {
                edits = new(
                    StringComparer.OrdinalIgnoreCase);
                editsByPath[path] = edits;
            }
            return edits;
        }
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanUndoLatest));
        OnPropertyChanged(nameof(CanRedoLatest));
        OnPropertyChanged(nameof(CanRepeatLatest));
        PreviewEditsCommand.NotifyCanExecuteChanged();
        PreviewOperationCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RepeatCommand.NotifyCanExecuteChanged();
        DiscoverSelectedAudioCommand.NotifyCanExecuteChanged();
        DiscoverAllAudioCommand.NotifyCanExecuteChanged();
        DiscoverOnlineAudioCommand.NotifyCanExecuteChanged();
        SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
        PreviewAudioIdentifiersCommand.NotifyCanExecuteChanged();
        ResolveSelectedRecordingCommand.NotifyCanExecuteChanged();
        BuildReleaseMappingCommand.NotifyCanExecuteChanged();
        PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        RemoveCurrentCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAfterFileOperationAsync(
        ReviewedFileOperationPlan plan,
        List<string> postCommitWarnings)
    {
        FileMutationAction[] actions =
            plan.MutationPlan.Actions.ToArray();
        string[] destinations = actions
            .Where(action =>
                action.Kind is FileMutationKind.Copy or
                    FileMutationKind.Move)
            .Select(action => action.DestinationPath)
            .ToArray();
        WorkbenchLoadResult loaded =
            destinations.Length == 0
                ? new([], [])
                : await _workbench.LoadAsync(
                    new(
                        destinations,
                        Recursive: false));
        AddPostCommitIssues(
            loaded.Issues,
            postCommitWarnings);
        var documents = loaded.Documents.ToDictionary(
            document => document.Path,
            PathComparer);
        HashSet<string> selected = EditTargets
            .Select(file => file.Path)
            .ToHashSet(PathComparer);
        var resultingSelection =
            new HashSet<string>(PathComparer);

        foreach (FileMutationAction action in actions)
        {
            WorkbenchTrackViewModel? source =
                Files.FirstOrDefault(file =>
                    PathComparer.Equals(
                        file.Path,
                        action.SourcePath));
            if (action.Kind == FileMutationKind.Copy)
            {
                if (documents.TryGetValue(
                        action.DestinationPath,
                        out MediaDocument? copied) &&
                    Files.All(file =>
                        !PathComparer.Equals(
                            file.Path,
                            copied.Path)))
                    AddTrack(new(copied));
                if (selected.Contains(action.SourcePath))
                    resultingSelection.Add(
                        action.SourcePath);
                continue;
            }

            if (source is null)
                continue;
            int index = Files.IndexOf(source);
            source.PropertyChanged -= OnTrackChanged;
            if (action.Kind == FileMutationKind.Move &&
                documents.TryGetValue(
                    action.DestinationPath,
                    out MediaDocument? moved))
            {
                var replacement =
                    new WorkbenchTrackViewModel(moved);
                replacement.PropertyChanged +=
                    OnTrackChanged;
                Files[index] = replacement;
                if (selected.Contains(action.SourcePath))
                    resultingSelection.Add(
                        action.DestinationPath);
                if (ReferenceEquals(
                        SelectedFile,
                        source))
                    SelectedFile = replacement;
            }
            else
            {
                Files.RemoveAt(index);
                _artworkDrafts.Remove(
                    action.SourcePath);
                if (ReferenceEquals(
                        SelectedFile,
                        source))
                    SelectedFile =
                        Files.Count == 0
                            ? null
                            : Files[
                                Math.Min(
                                    index,
                                    Files.Count - 1)];
            }
        }

        AudioMatches.Clear();
        ReleaseMatches.Clear();
        SelectedRelease = null;
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        SetSelectedFiles(Files.Where(file =>
            resultingSelection.Contains(file.Path)));
        NotifySessionChanged();
    }

    private void LoadRecentLocations()
    {
        try
        {
            string? json = _settings.GetPreference(RecentLocationsPreference);
            foreach (string path in string.IsNullOrWhiteSpace(json)
                         ? []
                         : JsonSerializer.Deserialize<string[]>(json) ?? [])
                if (File.Exists(path) || Directory.Exists(path))
                    RecentLocations.Add(path);
        }
        catch { }
    }

    private void LoadUiPreferences()
    {
        _loadingUiPreferences = true;
        try
        {
            string? json = _settings.GetPreference(UiPreference);
            WorkbenchUiPreferences? preferences =
                string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<
                        WorkbenchUiPreferences>(json);
            if (preferences is null)
                return;
            if (Enum.IsDefined(preferences.Section))
                SelectedSection = preferences.Section;
            IsInspectorOpen = preferences.InspectorOpen;
        }
        catch
        {
            SelectedSection = WorkbenchSection.Session;
            IsInspectorOpen = true;
        }
        finally
        {
            _loadingUiPreferences = false;
        }
    }

    private void PersistUiPreferences()
    {
        if (_loadingUiPreferences)
            return;
        try
        {
            _settings.SetPreference(
                UiPreference,
                JsonSerializer.Serialize(
                    new WorkbenchUiPreferences(
                        SelectedSection,
                        IsInspectorOpen)));
        }
        catch { }
    }

    partial void OnSelectedSectionChanged(
        WorkbenchSection value)
    {
        OnPropertyChanged(nameof(SelectedSectionOption));
        PersistUiPreferences();
    }

    partial void OnIsInspectorOpenChanged(bool value) =>
        PersistUiPreferences();

    private void AddRecentLocation(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        string? previous = RecentLocations.FirstOrDefault(item =>
            PathComparer.Equals(item, fullPath));
        if (previous is not null)
            RecentLocations.Remove(previous);
        RecentLocations.Insert(0, fullPath);
        while (RecentLocations.Count > RecentLocationLimit)
            RecentLocations.RemoveAt(RecentLocations.Count - 1);
        try
        {
            _settings.SetPreference(
                RecentLocationsPreference,
                JsonSerializer.Serialize(RecentLocations.ToArray()));
        }
        catch { }
    }

    private void BeginOperation(string message)
    {
        unchecked
        {
            _operationGeneration++;
        }
        _cancellation?.Dispose();
        _cancellation = new();
        ProgressText = message;
        ProgressValue = 0;
        ProgressMaximum = 1;
        IsProgressIndeterminate = true;
        IsBusy = true;
    }

    private void EndOperation()
    {
        InvalidateOperationProgress();
        IsBusy = false;
        _cancellation?.Dispose();
        _cancellation = null;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = "";
    }

    private void EndCancellablePhase()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        InvalidateOperationProgress();
    }

    private void InvalidateOperationProgress()
    {
        unchecked
        {
            _operationGeneration++;
        }
    }

    private IProgress<OperationProgress> CreateProgress()
    {
        long generation = _operationGeneration;
        return new Progress<OperationProgress>(progress =>
        {
            if (!IsBusy ||
                generation != _operationGeneration)
                return;
            ReportProgress(
                progress);
        });
    }

    private IProgress<int> CreateRestoreProgress(
        int total)
    {
        long generation = _operationGeneration;
        return new Progress<int>(completed =>
        {
            if (!IsBusy ||
                generation != _operationGeneration)
                return;
            ReportProgress(new(
                OperationPhase.Applying,
                completed,
                Math.Max(1, total),
                Message: LC(
                    "Workbench.Progress.Restored",
                    completed)));
        });
    }

    private void ReportProgress(OperationProgress progress)
    {
        if (progress.Total is > 0)
        {
            IsProgressIndeterminate = false;
            ProgressMaximum = progress.Total.Value;
            ProgressValue = Math.Clamp(
                progress.Completed, 0, progress.Total.Value);
        }
        else
        {
            IsProgressIndeterminate = true;
        }
        if (!string.IsNullOrWhiteSpace(
                progress.MessageKey))
            ProgressText =
                _localization?.Format(
                    progress.MessageKey,
                    progress.MessageArguments.IsDefault
                        ? []
                        : progress.MessageArguments
                            .ToArray()) ??
                LocalizedText.Format(
                    progress.MessageKey,
                    progress.MessageArguments.IsDefault
                        ? []
                        : progress.MessageArguments
                            .ToArray());
        else if (!string.IsNullOrWhiteSpace(progress.Message))
            ProgressText = progress.Message;
    }

    private bool CanBrowse() => !IsBusy;
    private bool CanRemoveCurrent() =>
        !IsBusy &&
        (SelectedFiles.Count > 0 ||
         SelectedFile is not null);
    private bool CanMoveUp() =>
        !IsBusy && SelectedFile is not null && Files.IndexOf(SelectedFile) > 0;
    private bool CanMoveDown() =>
        !IsBusy && SelectedFile is not null &&
        Files.IndexOf(SelectedFile) is var index && index >= 0 && index < Files.Count - 1;
    private bool CanPreviewEdits() =>
        !IsBusy && Files.Any(file => file.HasChanges);
    private bool CanImportDelimitedMetadata() =>
        !IsBusy && Files.Count > 0 &&
        _delimitedImports is not null;
    private bool CanPreviewOperation() =>
        !IsBusy && Files.Count > 0 && OperationEditor.CanCreate;
    private bool CanPreviewFieldValues() =>
        !IsBusy && EditTargets.Count > 0 &&
        (SelectedMetadataField is not null ||
         !string.IsNullOrWhiteSpace(CustomFieldName) ||
         SelectedNewKnownField is not null);
    private bool CanCopyMetadataField() =>
        !IsBusy && _platform is not null &&
        SelectedMetadataField is not null;
    private bool CanPasteMetadataField() =>
        !IsBusy && _platform is not null &&
        EditTargets.Count > 0;
    private bool CanApply() =>
        !IsBusy &&
        (HasApplicablePreview && _plan is not null ||
         HasDirectPendingChanges ||
         _fileOperationPlans.Count > 0 ||
         _transcodePlans.Count > 0);
    private bool CanUndo() =>
        !IsBusy &&
        (_history.CanUndo ||
         _reviewedHistory?.CanUndo == true);
    private bool CanRedo() =>
        !IsBusy &&
        (_history.CanRedo ||
         _reviewedHistory?.CanRedo == true);
    private bool CanRepeat() =>
        !IsBusy && Files.Count > 0 &&
        _history.Entries.FirstOrDefault()?.Recipe is not null;
    private bool CanDiscoverSelectedAudio() => !IsBusy && SelectedFile is not null;
    private bool CanDiscoverAllAudio() => !IsBusy && Files.Count > 0;
    private bool CanDiscoverOnlineAudio() =>
        SelectedOnlineMetadataScope.Scope ==
            WorkbenchOnlineMetadataScope.SelectedFile
            ? CanDiscoverSelectedAudio()
            : CanDiscoverAllAudio();
    private bool CanPreviewAudioIdentifiers() =>
        !IsBusy && SelectedAudioMatch?.AcoustId is not null &&
        !string.IsNullOrWhiteSpace(SelectedAudioMatch.Fingerprint);
    private bool CanResolveSelectedRecording() =>
        !IsBusy &&
        SelectedAudioMatch?.MusicBrainzRecordingIdValues.Length == 1;
    private bool CanSearchMusicBrainzReleases() =>
        !IsBusy && ReleaseSearch.HasCriteria;
    private bool CanSearchDiscogsReleases() =>
        !IsBusy && _discogs is not null && DiscogsSearch.HasCriteria;
    private bool CanSearchOnlineReleases() =>
        SelectedOnlineMetadataProvider.Provider ==
            WorkbenchOnlineMetadataProvider.MusicBrainz
            ? CanSearchMusicBrainzReleases()
            : CanSearchDiscogsReleases();
    private bool CanLoadDiscogsReleaseDetails() =>
        !IsBusy && _discogs is not null && SelectedDiscogsRelease is not null;
    private bool CanBuildDiscogsReleaseMapping() =>
        !IsBusy && _discogsMapping is not null &&
        SelectedDiscogsRelease is not null && Files.Count > 0;
    private bool CanPreviewDiscogsReleaseMetadata() =>
        !IsBusy && _discogsMapping is not null &&
        SelectedDiscogsRelease is not null &&
        DiscogsImport.HasSelection &&
        DiscogsTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanPreviewDiscogsReleaseArtwork() =>
        !IsBusy && _discogs is not null &&
        SelectedDiscogsRelease?.Candidate.CoverImageUri is not null &&
        DiscogsTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanBrowseReportOutput() =>
        !IsBusy && _reports is not null;
    private bool CanPreviewReport() =>
        !IsBusy && _reports is not null && Files.Count > 0 &&
        ReportEditor.Fields.Count > 0 &&
        !string.IsNullOrWhiteSpace(ReportEditor.OutputPath);
    private bool CanApplyReport() =>
        !IsBusy && _reports is not null &&
        _reportPlan?.CanApply == true;
    private bool CanBrowsePlaylistOutput() =>
        !IsBusy && _playlists is not null;
    private bool CanPreviewPlaylist() =>
        !IsBusy && _playlists is not null && Files.Count > 0 &&
        !string.IsNullOrWhiteSpace(PlaylistEditor.OutputPath);
    private bool CanApplyPlaylist() =>
        !IsBusy && _playlists is not null &&
        _playlistPlan?.CanApply == true;
    private bool CanBrowseExternalToolExecutable() =>
        !IsBusy && _externalTools is not null;
    private bool CanBrowseExternalToolWorkingDirectory() =>
        !IsBusy && _externalTools is not null;
    private bool CanPreviewExternalTool() =>
        !IsBusy && _externalTools is not null &&
        Files.Count > 0 &&
        !string.IsNullOrWhiteSpace(
            ExternalToolEditor.Executable);
    private bool CanRunExternalTool() =>
        !IsBusy && _externalTools is not null &&
        _externalToolPlan?.CanRun == true;
    private bool CanFindReleaseArtwork() =>
        !IsBusy && SelectedRelease is not null;
    private bool CanPreviewReleaseArtwork() =>
        !IsBusy && SelectedRelease is not null &&
        SelectedArtworkMatch is not null &&
        ReleaseTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanPreviewSelectedArtwork() =>
        !IsBusy &&
        SelectedFile?.HasAuthoritativeArtwork == true;
    private bool CanAddStagedArtwork() =>
        !IsBusy &&
        SelectedFile?.HasAuthoritativeArtwork == true;
    private bool CanEditStagedArtwork() =>
        !IsBusy && SelectedStagedArtwork is not null;
    private bool CanMoveStagedArtworkUp() =>
        CanEditStagedArtwork() &&
        StagedArtworkItems.IndexOf(SelectedStagedArtwork!) > 0;
    private bool CanMoveStagedArtworkDown()
    {
        if (!CanEditStagedArtwork())
            return false;
        int index = StagedArtworkItems.IndexOf(
            SelectedStagedArtwork!);
        return index >= 0 &&
            index < StagedArtworkItems.Count - 1;
    }
    private bool CanPreviewStagedArtwork() =>
        !IsBusy && EditTargets.Count > 0 &&
        SelectedFile?.HasAuthoritativeArtwork == true &&
        ArtworkMaxDimension >= 0;
    private bool CanBuildReleaseMapping() =>
        !IsBusy && SelectedRelease is not null && Files.Count > 0;
    private bool CanPreviewReleaseMetadata() =>
        !IsBusy && SelectedRelease is not null &&
        ReleaseImport.HasSelection &&
        ReleaseTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);

    private ImmutableArray<Guid> ConfirmedRecordingIds(string path)
    {
        if (SelectedAudioMatch is not null &&
            PathComparer.Equals(SelectedAudioMatch.Path, path) &&
            SelectedAudioMatch.MusicBrainzRecordingIdValues.Length == 1)
            return SelectedAudioMatch.MusicBrainzRecordingIdValues;
        Guid[] ids = AudioMatches
            .Where(row => PathComparer.Equals(row.Path, path))
            .SelectMany(row => row.MusicBrainzRecordingIdValues)
            .Distinct()
            .ToArray();
        return [.. ids];
    }

    private ImmutableDictionary<Guid, double>
        ConfirmedRecordingScores(string path)
    {
        HashSet<Guid> confirmed =
            ConfirmedRecordingIds(path).ToHashSet();
        return AudioMatches
            .Where(row =>
                PathComparer.Equals(row.Path, path) &&
                row.Score is not null)
            .SelectMany(row =>
                row.MusicBrainzRecordingIdValues.Select(id =>
                    (Id: id, Score: row.Score!.Value)))
            .Where(item => confirmed.Contains(item.Id))
            .GroupBy(item => item.Id)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Max(item => item.Score));
    }

    private void ClearReleaseTrackMappings()
    {
        foreach (MusicBrainzTrackMappingRow row in ReleaseTrackMappings)
            row.PropertyChanged -= OnReleaseMappingChanged;
        ReleaseTrackMappings.Clear();
        PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewReleaseArtworkCommand.NotifyCanExecuteChanged();
    }

    private void ClearDiscogsTrackMappings()
    {
        foreach (DiscogsTrackMappingRow row in DiscogsTrackMappings)
            row.PropertyChanged -= OnDiscogsMappingChanged;
        DiscogsTrackMappings.Clear();
        PreviewDiscogsReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewDiscogsReleaseArtworkCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateReportPlan()
    {
        if (_reportPlan is null && ReportOutputs.Count == 0)
        {
            PreviewReportCommand.NotifyCanExecuteChanged();
            return;
        }
        _reportPlan = null;
        ReportOutputs.Clear();
        PreviewReportCommand.NotifyCanExecuteChanged();
        ApplyReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void InvalidatePlaylistPlan()
    {
        if (_playlistPlan is null && PlaylistOutputs.Count == 0)
        {
            PreviewPlaylistCommand.NotifyCanExecuteChanged();
            return;
        }
        _playlistPlan = null;
        PlaylistOutputs.Clear();
        PreviewPlaylistCommand.NotifyCanExecuteChanged();
        ApplyPlaylistCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void InvalidateExternalToolPlan()
    {
        if (_externalToolPlan is null &&
            ExternalToolInvocations.Count == 0)
        {
            PreviewExternalToolCommand.NotifyCanExecuteChanged();
            return;
        }
        _externalToolPlan = null;
        ExternalToolInvocations.Clear();
        PreviewExternalToolCommand.NotifyCanExecuteChanged();
        RunExternalToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnDiscogsMappingChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        CancelPlan();
        PreviewDiscogsReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewDiscogsReleaseArtworkCommand.NotifyCanExecuteChanged();
        NotifySessionChanged();
    }

    private string[] ConfirmedDiscogsReleasePaths() =>
        DiscogsTrackMappings
            .Where(row =>
                row.IsIncluded && row.SelectedTrack is not null)
            .Select(row => row.Path)
            .Distinct(PathComparer)
            .ToArray();

    private void OnReleaseMappingChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        CancelPlan();
        PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewReleaseArtworkCommand.NotifyCanExecuteChanged();
        NotifySessionChanged();
    }

    private string[] ConfirmedReleasePaths() => ReleaseTrackMappings
        .Where(row => row.IsIncluded && row.SelectedTrack is not null)
        .Select(row => row.Path)
        .Distinct(PathComparer)
        .ToArray();

    private void OnReleaseImportChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        CancelPlan();
        PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        NotifySessionChanged();
    }

    private static int? ParsePositive(string? value) =>
        int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;

    private static string MimeTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private async Task<MusicBrainzReleaseCandidate>
        EnsureSelectedReleaseDetailsAsync(
            IProgress<OperationProgress> progress,
            CancellationToken ct)
    {
        MusicBrainzReleaseRow selected = SelectedRelease ??
            throw new InvalidOperationException(
                L("Workbench.Error.ChooseMusicBrainzRelease"));
        if (selected.Candidate.Tracks.Length > 0)
            return selected.Candidate;
        MusicBrainzReleaseCandidate detailed =
            await _musicBrainz.GetReleaseAsync(
                selected.ReleaseId, progress, ct);
        var row = MusicBrainzReleaseRows.CreateDetailed(
            selected.SourcePath, detailed, selected.RecordingId);
        int index = ReleaseMatches.IndexOf(selected);
        if (index >= 0)
            ReleaseMatches[index] = row;
        SelectedRelease = row;
        return detailed;
    }

    private async Task<DiscogsReleaseCandidate>
        EnsureSelectedDiscogsReleaseDetailsAsync(
            IProgress<OperationProgress> progress,
            CancellationToken ct)
    {
        if (_discogs is null)
            throw new InvalidOperationException(
                L("Workbench.Error.DiscogsUnavailable"));
        DiscogsReleaseRow selected = SelectedDiscogsRelease ??
            throw new InvalidOperationException(
                L("Workbench.Error.ChooseDiscogsRelease"));
        if (selected.Candidate.Tracks.Length > 0)
            return selected.Candidate;
        DiscogsReleaseCandidate release =
            await _discogs.GetReleaseAsync(
                selected.ReleaseId, progress, ct);
        var detailed = DiscogsReleaseRow.Create(
            release, selected.Source);
        int index = DiscogsMatches.IndexOf(selected);
        if (index >= 0)
            DiscogsMatches[index] = detailed;
        SelectedDiscogsRelease = detailed;
        return release;
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

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
        string key,
        params object?[] arguments)
    {
        _statusState.Set(
            WorkbenchStatusText.Format(
                key,
                arguments));
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusState.Set(
            WorkbenchStatusText.FormatCount(
                key,
                count,
                arguments));
        StatusDiagnosticDetail = null;
    }

    private void SetStatus(
        WorkbenchStatusText status)
    {
        _statusState.Set(status);
        StatusDiagnosticDetail = null;
    }

    private void SetFailure(
        string key,
        string? diagnosticDetail,
        params object?[] arguments)
    {
        SetStatus(key, arguments);
        StatusDiagnosticDetail = diagnosticDetail;
    }

    private void AppendStatusDiagnostics(
        IEnumerable<string> diagnostics)
    {
        string[] messages =
        [
            .. new[]
                {
                    StatusDiagnosticDetail,
                }
                .Concat(diagnostics)
                .Where(message =>
                    !string.IsNullOrWhiteSpace(message))
                .Select(message =>
                    message!)
                .Distinct(StringComparer.Ordinal),
        ];
        StatusDiagnosticDetail = messages.Length == 0
            ? null
            : string.Join(
                Environment.NewLine,
                messages);
    }

    private void RefreshLocalizedChoices()
    {
        WorkbenchOnlineMetadataScope selectedScope =
            SelectedOnlineMetadataScope.Scope;
        WorkbenchOnlineMetadataProvider selectedProvider =
            SelectedOnlineMetadataProvider.Provider;
        TagFields? selectedKnown =
            SelectedNewKnownField?.Field;

        SectionOptions.Clear();
        foreach (WorkbenchSection section in
                 Enum.GetValues<WorkbenchSection>())
        {
            string group = section switch
            {
                WorkbenchSection.Session =>
                    L("Workbench.Navigation.Group.Workspace"),
                WorkbenchSection.BulkOperation or
                    WorkbenchSection.AllFields or
                    WorkbenchSection.Files =>
                    L("Workbench.Navigation.Group.Edit"),
                WorkbenchSection.OnlineMetadata =>
                    L("Workbench.Navigation.Group.Enrich"),
                WorkbenchSection.Reports or
                    WorkbenchSection.Playlists =>
                    L("Workbench.Navigation.Group.Output"),
                _ =>
                    L("Workbench.Navigation.Group.Automate"),
            };
            SectionOptions.Add(new(
                section,
                group,
                L($"Workbench.Navigation.Section.{section}")));
        }

        OnlineMetadataScopeOptions.Clear();
        foreach (WorkbenchOnlineMetadataScope scope in
                 Enum.GetValues<WorkbenchOnlineMetadataScope>())
            OnlineMetadataScopeOptions.Add(new(
                scope,
                L($"Workbench.Choice.OnlineScope.{scope}")));
        SelectedOnlineMetadataScope =
            OnlineMetadataScopeOptions.First(option =>
                option.Scope == selectedScope);

        OnlineMetadataProviderOptions.Clear();
        foreach (WorkbenchOnlineMetadataProvider provider in
                 Enum.GetValues<WorkbenchOnlineMetadataProvider>())
            OnlineMetadataProviderOptions.Add(new(
                provider,
                L($"Workbench.Choice.OnlineProvider.{provider}")));
        SelectedOnlineMetadataProvider =
            OnlineMetadataProviderOptions.First(option =>
                option.Provider == selectedProvider);

        KnownFieldChoices.Clear();
        foreach (TagFields field in Enum.GetValues<TagFields>()
                     .Where(field =>
                         field != TagFields.NullField))
            KnownFieldChoices.Add(new(
                field,
                L($"Settings.Choice.TagFields.{field}")));
        if (selectedKnown is { } known)
            SelectedNewKnownField =
                KnownFieldChoices.First(choice =>
                    choice.Field == known);

        RefreshChoices(
            FieldEditModeChoices,
            FieldEditModes,
            "Workbench.Choice.FieldEditMode");
        RefreshChoices(
            ImportEmptyCellModeChoices,
            ImportEmptyCellModes,
            "Workbench.Choice.ImportEmptyCellMode");
        RefreshChoices(
            Id3VersionChoices,
            Id3Versions,
            keyPrefix: null);
        RefreshChoices(
            Id3EncodingPolicyChoices,
            Id3EncodingPolicies,
            "Workbench.Choice.Id3EncodingPolicy");
        RefreshChoices(
            ArtworkTypeChoices,
            ArtworkTypes,
            "Inspector.Artwork.Type",
            ".Label");
        OnPropertyChanged(nameof(SelectedSectionOption));
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        string? keyPrefix,
        string keySuffix = "")
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = L(
                TechnicalLabelResourceKeys.For(value) ??
                $"{keyPrefix!}.{value}{keySuffix}");
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        RebuildMetadataFields();
        foreach (WorkbenchTrackViewModel file in Files)
            file.RefreshLocalizedText();
        foreach (AudioDiscoveryRow row in AudioMatches)
            row.RefreshLocalizedText();
        foreach (MusicBrainzTrackMappingRow row in
                 ReleaseTrackMappings)
            row.RefreshLocalizedText();
        foreach (DiscogsTrackMappingRow row in
                 DiscogsTrackMappings)
            row.RefreshLocalizedText();
        foreach (CoverArtCandidateRow row in
                 ArtworkMatches)
            row.RefreshLocalizedText();
        if (_plan is not null)
        {
            MetadataPreviewRowBuilder.Populate(
                PreviewChanges,
                _plan,
                _localization);
            PendingMetadataOperationRowBuilder.Populate(
                PendingOperations,
                _plan,
                _localization);
        }
        RebuildPendingChanges();
        OnPropertyChanged(nameof(FieldSelectionSummary));
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record WorkbenchUiPreferences(
        WorkbenchSection Section,
        bool InspectorOpen);

    private sealed record PendingTranscodeApplyOutcome(
        int ChangedFiles,
        bool Proceed,
        bool Committed,
        MetadataOperationStageResult? AppliedMetadata,
        ImmutableArray<string> ConsumedMetadataPaths,
        ImmutableArray<string> RetainedSourcePaths);

    private sealed record PendingDirectChangesCapture(
        ImmutableDictionary<
            string,
            ImmutableArray<TagEdit>> TrackEdits,
        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>?
            InspectorValueEdits,
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest>?
            InspectorArtworkEdits,
        ImmutableArray<string> InspectorPaths);

    private sealed class
        PendingFileOperationPreflightException(
            string message) :
            InvalidOperationException(message);
}

public partial class WorkbenchTrackViewModel : ObservableObject
{
    private readonly Dictionary<TagFields, string?> _original;

    public WorkbenchTrackViewModel(MediaDocument document)
    {
        Document = document;
        MetadataValues = document.TagLayers
            .SelectMany(layer => layer.Fields)
            .GroupBy(value =>
                MetadataGridValueKey.For(value.Field),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    "; ",
                    group.SelectMany(value => value.Values)),
                StringComparer.OrdinalIgnoreCase);
        _title = document.FirstValue(TagFields.Title);
        _artist = document.FirstValue(TagFields.Artist);
        _albumArtist = document.FirstValue(TagFields.AlbumArtist);
        _album = document.FirstValue(TagFields.Album);
        _genre = document.FirstValue(TagFields.Genre);
        _composer = document.FirstValue(TagFields.Composer);
        _date = document.FirstValue(TagFields.Date);
        _track = document.FirstValue(TagFields.TrackNumber);
        _trackTotal = document.FirstValue(TagFields.TotalTracks);
        _disc = document.FirstValue(TagFields.DiscNumber);
        _discTotal = document.FirstValue(TagFields.TotalDiscs);
        _comment = document.FirstValue(TagFields.Comment);
        _original = CurrentValues();
    }

    public MediaDocument Document { get; private set; }
    public IReadOnlyDictionary<string, string> MetadataValues
    {
        get;
        private set;
    }
    public bool HasAuthoritativeArtwork { get; private set; }
    public string Path => Document.Path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string? Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Codec => Document.Codec?.CodecName ?? "";
    public string CodecType => Document.Codec?.CodecType.ToString() ?? "";
    public string SampleRate => Document.Codec?.Samplerate is > 0
        ? LocalizedText.Format(
            "Workbench.Value.SampleRate",
            Document.Codec.Samplerate)
        : "";
    public string BitsPerSample => Document.Codec?.BitsPerSample is > 0
        ? Document.Codec.BitsPerSample.ToString()
        : "";
    public string Channels => Document.Codec?.Channels is > 0
        ? Document.Codec.Channels.ToString()
        : "";
    public string Duration => Document.Codec is null
        ? ""
        : TimeSpan.FromSeconds(Document.Codec.DurationInSeconds).ToString(@"h\:mm\:ss");
    public string Bitrate => Document.Codec?.AverageBitrate is > 0
        ? LocalizedText.Format(
            "Workbench.Value.Bitrate",
            Document.Codec.AverageBitrate / 1000)
        : "";
    public string LayerSummary => string.Join(", ",
        Document.TagLayers.Select(layer => layer.TagType));

    public bool HasEditableTagLayers =>
        !Document.EditableTagLayers.IsDefaultOrEmpty;
    public bool HasId3Tag => Document.Id3Version is not null;
    public int ArtworkCount => Document.Artwork.Length;
    public int FieldCount => Document.TagLayers.Sum(layer => layer.Fields.Length);
    public string FileSize => FormatBytes(Document.Snapshot.Length);
    public string Modified => Document.Snapshot.LastWriteTimeUtc == default
        ? ""
        : Document.Snapshot.LastWriteTimeUtc.ToLocalTime()
            .ToString(
                "g",
                System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _artist;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _albumArtist;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _album;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _genre;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _composer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _date;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _track;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _trackTotal;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _disc;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _discTotal;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _comment;

    public bool HasChanges => _original.Any(pair =>
        !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)));

    public ImmutableArray<TagEdit> CreateEdits() => _original
        .Where(pair => !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)))
        .Select(pair => new TagEdit(pair.Key,
            string.IsNullOrWhiteSpace(Value(pair.Key)) ? null : Value(pair.Key)))
        .ToImmutableArray();

    public IEnumerable<MetadataPreviewRow>
        CreatePendingChangeRows(
            Func<MetadataFieldKey, string>?
                fieldLabel = null) =>
        _original
            .Where(pair => !StringComparer.Ordinal.Equals(
                pair.Value,
                Value(pair.Key)))
            .Select(pair => new MetadataPreviewRow(
                FileName,
                fieldLabel?.Invoke(
                    MetadataFieldKey.Known(
                        pair.Key)) ??
                MetadataFieldKey.Known(
                        pair.Key).DisplayName,
                pair.Value ?? "",
                Value(pair.Key) ?? ""));

    public void RevertPendingChanges()
    {
        Title = _original[TagFields.Title];
        Artist = _original[TagFields.Artist];
        AlbumArtist = _original[TagFields.AlbumArtist];
        Album = _original[TagFields.Album];
        Genre = _original[TagFields.Genre];
        Composer = _original[TagFields.Composer];
        Date = _original[TagFields.Date];
        Track = _original[TagFields.TrackNumber];
        TrackTotal = _original[TagFields.TotalTracks];
        Disc = _original[TagFields.DiscNumber];
        DiscTotal = _original[TagFields.TotalDiscs];
        Comment = _original[TagFields.Comment];
    }

    public void AcceptPendingChanges(
        IEnumerable<TagEdit> committedEdits)
    {
        ArgumentNullException.ThrowIfNull(
            committedEdits);
        foreach (TagEdit edit in committedEdits)
            _original[edit.Field] =
                string.IsNullOrWhiteSpace(
                    edit.Value)
                    ? null
                    : edit.Value;
        OnPropertyChanged(nameof(HasChanges));
    }

    public void MarkArtworkAuthoritative()
    {
        if (HasAuthoritativeArtwork)
            return;
        HasAuthoritativeArtwork = true;
        OnPropertyChanged(
            nameof(HasAuthoritativeArtwork));
    }

    public void UpdateArtwork(
        MediaDocument hydratedDocument)
    {
        ArgumentNullException.ThrowIfNull(
            hydratedDocument);
        if (!TrackPathComparer.Equals(
                Path,
                hydratedDocument.Path))
            throw new ArgumentException(
                null,
                nameof(hydratedDocument));

        // Artwork hydration is deliberately narrow. Replacing the whole
        // document here could mix newly-read metadata with the row's older
        // editable values and dirty baseline.
        Document = Document with
        {
            Artwork = hydratedDocument.Artwork,
        };
        HasAuthoritativeArtwork = true;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(HasAuthoritativeArtwork));
        OnPropertyChanged(nameof(ArtworkCount));
    }

    private static readonly StringComparer TrackPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(Bitrate));
        OnPropertyChanged(nameof(FileSize));
        OnPropertyChanged(nameof(Modified));
    }

    private Dictionary<TagFields, string?> CurrentValues() => new()
    {
        [TagFields.Title] = Title,
        [TagFields.Artist] = Artist,
        [TagFields.AlbumArtist] = AlbumArtist,
        [TagFields.Album] = Album,
        [TagFields.Genre] = Genre,
        [TagFields.Composer] = Composer,
        [TagFields.Date] = Date,
        [TagFields.TrackNumber] = Track,
        [TagFields.TotalTracks] = TrackTotal,
        [TagFields.DiscNumber] = Disc,
        [TagFields.TotalDiscs] = DiscTotal,
        [TagFields.Comment] = Comment,
    };

    private string? Value(TagFields field) => field switch
    {
        TagFields.Title => Title,
        TagFields.Artist => Artist,
        TagFields.AlbumArtist => AlbumArtist,
        TagFields.Album => Album,
        TagFields.Genre => Genre,
        TagFields.Composer => Composer,
        TagFields.Date => Date,
        TagFields.TrackNumber => Track,
        TagFields.TotalTracks => TrackTotal,
        TagFields.DiscNumber => Disc,
        TagFields.TotalDiscs => DiscTotal,
        TagFields.Comment => Comment,
        _ => null,
    };

    private static string FormatBytes(long bytes)
    {
        string[] units =
        [
            LocalizedText.Get("Workbench.Size.Bytes"),
            LocalizedText.Get("Workbench.Size.Kilobytes"),
            LocalizedText.Get("Workbench.Size.Megabytes"),
            LocalizedText.Get("Workbench.Size.Gigabytes"),
            LocalizedText.Get("Workbench.Size.Terabytes"),
        ];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }
}
