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
    string Label);

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

public partial class WorkbenchViewModel : ObservableObject, INavigationGuard
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
    private string? _statusTextKey;
    private object?[] _statusTextArguments = [];
    private long? _statusTextCount;
    private MetadataOperationPlan? _plan;
    private ReportExportPlan? _reportPlan;
    private PlaylistWorkspacePlan? _playlistPlan;
    private ExternalToolPlan? _externalToolPlan;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<WorkbenchTrackViewModel> _selectedFiles = [];
    private int _artworkGeneration;
    private bool _stagedArtworkDirty;
    private string? _stagedArtworkPath;
    private bool _settingArtworkMaxDimension;
    private bool _loadingUiPreferences;
    private readonly Dictionary<
        string,
        ArtworkSetPreviewRequest> _artworkDrafts =
            new(PathComparer);

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
        ILocalizationService? localization = null)
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
        ReleaseSearch = new(localization);
        DiscogsSearch = new(localization);
        if (_inspector is not null)
        {
            _inspector.FilesChanged += OnInspectorFilesChanged;
            _inspector.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is
                    nameof(SelectionInspectorViewModel.HasUnsavedChanges) or
                    nameof(SelectionInspectorViewModel.PendingChangesVersion))
                {
                    if (_inspector.HasUnsavedChanges &&
                        _plan is not null)
                        CancelPlan();
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
                dialogs,
                () => EditTargets
                    .Select(file => file.Path)
                    .ToArray(),
                FileOperationPreflightMessage,
                RefreshAfterFileOperationAsync,
                localization);
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
            SearchMusicBrainzReleasesCommand.NotifyCanExecuteChanged();
            SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
        };
        DiscogsSearch.PropertyChanged += (_, _) =>
        {
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
        Files.Any(file => file.HasChanges);
    public bool CanUndoLatest => _history.CanUndo && !IsBusy;
    public bool CanRedoLatest => _history.CanRedo && !IsBusy;
    public bool CanRepeatLatest =>
        _history.Entries.FirstOrDefault()?.Recipe is not null &&
        Files.Count > 0 && !IsBusy;

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
                if (ApplyCommand.CanExecute(null))
                    await ApplyCommand.ExecuteAsync(null);
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
        OnPropertyChanged(
            nameof(IsMusicBrainzOnlineMetadataProvider));
        OnPropertyChanged(
            nameof(IsDiscogsOnlineMetadataProvider));
        SearchOnlineReleasesCommand.NotifyCanExecuteChanged();
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

    private void ApplySelectedFiles(
        WorkbenchTrackViewModel[] selected)
    {
        _selectedFiles = selected;
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(FieldSelectionSummary));
        SendSelectedToIngestCommand
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
                SetCountStatus(
                    "Workbench.Status.SourcesAddedWithWarnings",
                    added,
                    Files.Count,
                    loaded.Issues.Length);
                StatusDiagnosticDetail =
                    loaded.Issues[0].Message;
            }
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

    [RelayCommand(CanExecute = nameof(CanRemoveCurrent))]
    private void RemoveCurrent()
    {
        if (SelectedFile is not { } current)
            return;
        int index = Files.IndexOf(current);
        current.PropertyChanged -= OnTrackChanged;
        Files.Remove(current);
        _artworkDrafts.Remove(current.Path);
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        foreach (AudioDiscoveryRow row in AudioMatches
                     .Where(row => PathComparer.Equals(row.Path, current.Path))
                     .ToArray())
            AudioMatches.Remove(row);
        foreach (MusicBrainzReleaseRow row in ReleaseMatches
                     .Where(row => PathComparer.Equals(row.SourcePath, current.Path))
                     .ToArray())
            ReleaseMatches.Remove(row);
        if (SelectedRelease is not null &&
            PathComparer.Equals(SelectedRelease.SourcePath, current.Path))
            SelectedRelease = null;
        ClearReleaseTrackMappings();
        SelectedFile = Files.Count == 0
            ? null
            : Files[Math.Min(index, Files.Count - 1)];
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
        if (HasUnsavedChanges && !await _dialogs.ConfirmAsync(
                L("Workbench.Dialog.Clear.Title"),
                L("Workbench.Dialog.Clear.Message"),
                L("Common.Clear")))
            return;
        foreach (WorkbenchTrackViewModel file in Files)
            file.PropertyChanged -= OnTrackChanged;
        Files.Clear();
        _artworkDrafts.Clear();
        AudioMatches.Clear();
        ReleaseMatches.Clear();
        SelectedRelease = null;
        ClearReleaseTrackMappings();
        PreviewChanges.Clear();
        PendingChanges.Clear();
        PendingOperations.Clear();
        _plan = null;
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
                    ? "Workbench.Status.ImportMapped"
                    : "Workbench.Status.ImportMappedWithWarnings",
                imported.MatchedRows,
                imported.DataRows,
                warnings);
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
        string action = mode == TagLayerEditMode.Add
            ? L("Workbench.Action.Add")
            : L("Common.Remove");
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
                    "Workbench.Operation.TagLayer",
                    action,
                    layerName),
                progress,
                ct));
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
                ct));
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
                ct));
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
                        $"Workbench.Choice.Id3EncodingPolicy.{SelectedId3EncodingPolicy}")),
                progress,
                ct));
    }

    private bool CanPreviewId3Encoding() =>
        !IsBusy &&
        SelectedFile?.Document.Id3Version is { } version &&
        (SelectedId3EncodingPolicy != ID3TextEncodingPolicy.Utf8 ||
         version == ID3v2Version.V24);

    private async Task PreviewAsync(
        Func<IProgress<OperationProgress>, CancellationToken,
            Task<MetadataOperationPlan>> action)
    {
        BeginOperation(L("Workbench.Activity.BuildingPreview"));
        try
        {
            MetadataOperationPlan plan = await action(
                CreateProgress(), _cancellation!.Token);
            _plan = plan;
            MetadataPreviewRowBuilder.Populate(
                PreviewChanges,
                plan,
                _localization);
            PendingMetadataOperationRowBuilder.Populate(
                PendingOperations,
                plan,
                _localization);
            HasApplicablePreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountStatus(
                    "Workbench.Status.PreviewBlocked",
                    blockers);
            else
                SetCountStatus(
                    "Workbench.Status.PreviewReady",
                    plan.ChangeCount,
                    plan.ChangedFileCount);
        }
        catch (OperationCanceledException)
        {
            CancelPlan();
            SetStatus("Workbench.Status.PreviewCancelled");
        }
        catch (Exception error)
        {
            CancelPlan();
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
        if (_plan is null &&
            !HasDirectPendingChanges)
            return;
        BeginOperation(
            L("Workbench.Activity.ApplyingReviewedChanges"));
        try
        {
            IProgress<OperationProgress> progress = CreateProgress();
            _plan ??= await PreviewDirectPendingChangesAsync(
                progress,
                _cancellation!.Token);
            if (_plan is null)
            {
                SetStatus(
                    "Workbench.Status.NoPendingChanges");
                return;
            }
            MetadataPreviewRowBuilder.Populate(
                PreviewChanges,
                _plan,
                _localization);
            PendingMetadataOperationRowBuilder.Populate(
                PendingOperations,
                _plan,
                _localization);
            HasApplicablePreview = _plan.CanApply;
            if (!_plan.CanApply)
            {
                OperationIssue? blocker = _plan.Files
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
            MetadataApplyResult result = await _operations.ApplyAsync(
                _plan, progress, _cancellation!.Token);
            string[] paths = _plan.Files.Where(file => file.HasChanges)
                .Select(file => file.Path).ToArray();
            foreach (MetadataFilePlan file in _plan.Files.Where(
                         file => file.ArtworkEdit is not null))
                _artworkDrafts.Remove(file.Path);
            await ReloadAsync(paths, progress, _cancellation.Token);
            if (_inspector?.HasUnsavedChanges == true)
                await _inspector.LoadAsync(
                    _inspector.Selection);
            _plan = null;
            PreviewChanges.Clear();
            PendingOperations.Clear();
            RebuildPendingChanges();
            HasApplicablePreview = false;
            SetCountStatus(
                "Workbench.Status.Applied",
                result.ChangedFiles);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Workbench.Status.ApplyCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.ApplyFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertPendingChanges))]
    private async Task RevertPendingChangesAsync()
    {
        if (_plan is null &&
            PendingChanges.Count == 0)
            return;

        foreach (WorkbenchTrackViewModel file in
                 Files.Where(file => file.HasChanges).ToArray())
            file.RevertPendingChanges();
        if (_inspector?.HasUnsavedChanges == true)
            await _inspector.DiscardPendingChangesAsync();
        CancelPlan();
        SetStatus("Workbench.Status.PendingReverted");
        NotifySessionChanged();
    }

    private bool CanRevertPendingChanges() => !IsBusy;

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
        try
        {
            var restoreProgress = new Progress<int>(completed =>
                ReportProgress(new(
                    OperationPhase.Applying,
                    completed,
                    Math.Max(1, Files.Count),
                    Message: LC(
                        "Workbench.Progress.Restored",
                        completed))));
            int restored = await _history.UndoLatestAsync(
                restoreProgress, _cancellation!.Token);
            await ReloadAsync(
                Files.Select(file => file.Path).ToArray(),
                CreateProgress(),
                _cancellation.Token);
            SetCountStatus(
                "Workbench.Status.Restored",
                restored);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Workbench.Status.RestoreCancelled");
        }
        catch (Exception error)
        {
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
        EditHistoryEntry? candidate = _history.RedoEntries
            .FirstOrDefault(entry => entry.Recipe is not null);
        if (candidate?.Recipe is null)
            return;
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            candidate.Paths, candidate.Recipe, progress, ct));
        SetStatus("Workbench.Status.RedoRegenerated");
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
                "Workbench.Status.DiscogsMappingReady",
                mapping.SuggestedCount,
                mapping.Files.Length,
                mapping.AmbiguousCount);
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
        });
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
            SetCountStatus(
                "Workbench.Status.ReportWritten",
                result.FileCount,
                result.RowCount);
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
            SetCountStatus(
                "Workbench.Status.PlaylistWritten",
                result.PlaylistCount,
                result.TrackReferenceCount);
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
        });
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
        });
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
                ct));
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
                ct));
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
                ct));
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
                "Workbench.Status.ReleaseMappingReady",
                mapping.SuggestedCount,
                mapping.Files.Length,
                mapping.AmbiguousCount);
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
            int issues = result.Files.Sum(file => file.Issues.Length);
            SetStatus(
                "Workbench.Status.FingerprintDiscovery",
                result.FingerprintedFileCount,
                result.CandidateCount,
                issues);
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
        return _dialogs.ConfirmAsync(
            L("Workbench.Dialog.Leave.Title"),
            L("Workbench.Dialog.Leave.Message"),
            L("Workbench.Dialog.Leave.Confirm"));
    }

    private async Task ReloadAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress> progress,
        CancellationToken ct)
    {
        if (paths.Count == 0)
            return;
        HashSet<string> selectedPaths = EditTargets
            .Select(file => file.Path)
            .ToHashSet(PathComparer);
        WorkbenchLoadResult loaded = await _workbench.LoadAsync(
            new(paths, Recursive: false), progress, ct);
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

    private void OnInspectorFilesChanged()
    {
        if (_inspector is null || _inspector.Selection.Paths.Count == 0 ||
            IsBusy)
            return;
        _ = ReloadAfterInspectorSaveAsync(
            _inspector.Selection.Paths.ToArray());
    }

    private async Task ReloadAfterInspectorSaveAsync(
        IReadOnlyList<string> paths)
    {
        BeginOperation(
            L("Workbench.Activity.ReloadingInspectorChanges"));
        try
        {
            await ReloadAsync(paths, CreateProgress(), _cancellation!.Token);
            SetCountStatus(
                "Workbench.Status.InspectorChangesReloaded",
                paths.Count);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Workbench.Status.InspectorReloadCancelled");
        }
        catch (Exception error)
        {
            SetFailure(
                "Workbench.Status.InspectorReloadFailed",
                error.Message);
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
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
                            decodePixelWidth: 180);
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
                        decodePixelWidth: 180);
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
        PreviewStagedArtworkCommand.NotifyCanExecuteChanged();
        NotifyStagedArtworkMoveCommands();
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
            CancelPlan();
            ClearReleaseTrackMappings();
            NotifySessionChanged();
        }
    }

    private void CancelPlan()
    {
        _plan = null;
        PreviewChanges.Clear();
        PendingOperations.Clear();
        HasApplicablePreview = false;
        RebuildPendingChanges();
    }

    private bool HasDirectPendingChanges =>
        Files.Any(file => file.HasChanges) ||
        _inspector?.HasUnsavedChanges == true;

    private void RebuildPendingChanges()
    {
        PendingChanges.Clear();
        if (PreviewChanges.Count > 0)
        {
            foreach (MetadataPreviewRow row in PreviewChanges)
                PendingChanges.Add(row);
            return;
        }

        foreach (MetadataPreviewRow row in Files
                     .Where(file => file.HasChanges)
                     .SelectMany(file =>
                         file.CreatePendingChangeRows()))
            PendingChanges.Add(row);
        if (_inspector is not null)
        {
            foreach (MetadataPreviewRow row in
                     _inspector.CreatePendingChangeRows())
                PendingChanges.Add(row);
        }
    }

    private async Task<MetadataOperationPlan?>
        PreviewDirectPendingChangesAsync(
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
    {
        var editsByPath = new Dictionary<
            string,
            Dictionary<string, MetadataValueEdit>>(
            PathComparer);
        foreach (WorkbenchTrackViewModel file in
                 Files.Where(file => file.HasChanges))
        {
            Dictionary<string, MetadataValueEdit> fileEdits =
                GetOrCreate(file.Path);
            foreach (TagEdit edit in file.CreateEdits())
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
        if (_inspector?.HasUnsavedChanges == true)
        {
            var inspectorInputs =
                _inspector.CreatePendingOperationInputs();
            if (inspectorInputs.ValueEdits is not null)
            {
                foreach ((string path,
                             IReadOnlyList<MetadataValueEdit> edits)
                         in inspectorInputs.ValueEdits)
                {
                    Dictionary<string, MetadataValueEdit> fileEdits =
                        GetOrCreate(path);
                    foreach (MetadataValueEdit edit in edits)
                        fileEdits[
                            MetadataGridValueKey.For(
                                edit.Field)] = edit;
                }
            }
            artworkEdits = inspectorInputs.ArtworkEdits;
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
        return MetadataOperationPlanComposer.Combine(
            L("Workbench.Operation.PendingChanges"),
            valuesPlan,
            artworkPlan);

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

    private string? FileOperationPreflightMessage()
    {
        if (_plan is not null ||
            _stagedArtworkDirty ||
            EditTargets.Any(file => file.HasChanges))
            return L(
                "Workbench.FileOperations.PendingMetadataPreflight");
        return null;
    }

    private async Task RefreshAfterFileOperationAsync(
        ReviewedFileOperationPlan plan)
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
        CancelPlan();
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
        IsBusy = false;
        _cancellation?.Dispose();
        _cancellation = null;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = "";
    }

    private IProgress<OperationProgress> CreateProgress() =>
        new Progress<OperationProgress>(ReportProgress);

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
        if (!string.IsNullOrWhiteSpace(progress.Message))
            ProgressText = progress.Message;
    }

    private bool CanBrowse() => !IsBusy;
    private bool CanRemoveCurrent() => !IsBusy && SelectedFile is not null;
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
         HasDirectPendingChanges);
    private bool CanUndo() => !IsBusy && _history.CanUndo;
    private bool CanRedo() => !IsBusy && _history.CanRedo;
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
        !IsBusy && SelectedFile is not null;
    private bool CanAddStagedArtwork() =>
        !IsBusy && SelectedFile is not null;
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
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = null;
        StatusText = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = count;
        StatusText = LC(key, count, arguments);
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
            "Workbench.Choice.Id3Version");
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
        string keyPrefix,
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
                $"{keyPrefix}.{value}{keySuffix}");
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
        if (_statusTextKey is not null)
            StatusText = _statusTextCount is { } count
                ? LC(
                    _statusTextKey,
                    count,
                    _statusTextArguments)
                : LF(
                    _statusTextKey,
                    _statusTextArguments);
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
        OnPropertyChanged(nameof(FieldSelectionSummary));
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record WorkbenchUiPreferences(
        WorkbenchSection Section,
        bool InspectorOpen);
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

    public MediaDocument Document { get; }
    public IReadOnlyDictionary<string, string> MetadataValues { get; }
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

    public IEnumerable<MetadataPreviewRow> CreatePendingChangeRows() =>
        _original
            .Where(pair => !StringComparer.Ordinal.Equals(
                pair.Value,
                Value(pair.Key)))
            .Select(pair => new MetadataPreviewRow(
                FileName,
                MetadataFieldKey.Known(pair.Key).DisplayName,
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
