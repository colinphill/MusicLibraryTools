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

public enum LibraryOperationScope
{
    SelectedTracks,
    SelectedAlbums,
    VisibleFilteredResults,
    CompleteLibrary,
}

public enum LibraryInspectorPreference
{
    Auto = 0,
    Pinned = 1,
    Closed = 2,
}

public partial class LibraryViewModel : ObservableObject, INavigationGuard
{
    private const string ViewsPreference = "manager.library.views.v1";
    private const string WorkspacePreference = "manager.library.workspace.v1";
    private readonly ILibraryService _library;
    private readonly IReindexService _reindex;
    private readonly IAppSettings _settings;
    private readonly SelectionInspectorViewModel _inspector;
    private readonly INavigationService _navigation;
    private readonly IThumbnailService _thumbnails;
    private readonly WorkbenchViewModel? _workbench;
    private readonly IMetadataOperationService? _metadataOperations;
    private readonly IAcoustIdDiscoveryService? _audioDiscovery;
    private readonly IMusicBrainzMetadataProvider? _musicBrainz;
    private readonly IMusicBrainzReleaseMappingService? _releaseMapping;
    private readonly ICoverArtArchiveProvider? _coverArt;
    private readonly IDiscogsMetadataProvider? _discogs;
    private readonly IDiscogsReleaseMappingService? _discogsMapping;
    private readonly IReportExportService? _reports;
    private readonly IPlaylistWorkspaceService? _playlists;
    private readonly IExternalToolService? _externalTools;
    private readonly IDelimitedMetadataImportService? _delimitedImports;
    private readonly IFilePickerService? _files;
    private readonly IDialogCoordinator? _dialogs;
    private readonly IPlatformService? _platform;
    private readonly IEditHistoryService? _history;
    private readonly ILocalizationService? _localization;
    private MetadataOperationPlan? _libraryOperationPlan;
    private ReportExportPlan? _reportPlan;
    private PlaylistWorkspacePlan? _playlistPlan;
    private ExternalToolPlan? _externalToolPlan;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly object _thumbnailSync = new();
    private readonly Dictionary<LibraryRow, CancellationTokenSource> _thumbnailLoads = [];
    private readonly Dictionary<string, ThumbnailCacheItem> _thumbnailCache =
        new(PathComparer);
    private readonly LinkedList<string> _thumbnailLru = [];
    private CancellationTokenSource _thumbnailLifetime = new();
    private const int ThumbnailCacheLimit = 256;
    private List<LibraryRow> _allRows = [];
    private HashSet<string> _healthFilterPaths = new(PathComparer);
    private IReadOnlyList<string> _selectedPaths = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _loadingWorkspace;
    private string? _statusTextKey;
    private object?[] _statusTextArguments = [];
    private long? _statusTextCount;
    private string? _operationStatusKey;
    private object?[] _operationStatusArguments = [];
    private long? _operationStatusCount;
    private string? _visualFilterStatusKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInWorkbenchCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedInWorkbenchCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditVisibleInWorkbenchCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditAllInWorkbenchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLibraryDelimitedMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverLibraryAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryAudioIdentifiersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveLibraryRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchLibraryMusicBrainzReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchLibraryDiscogsReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLibraryDiscogsReleaseDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryDiscogsReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryDiscogsReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryDiscogsReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryReportOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLibraryReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryPlaylistOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLibraryPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryExternalToolExecutableCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryExternalToolWorkingDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunLibraryExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindLibraryReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLocalLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveLibraryFrontCoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveAllLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLibraryMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteLibraryMetadataFieldCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextFilter))]
    private string? _filterText;

    [ObservableProperty]
    private FilterMode _filterMode = FilterMode.Substring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    private string? _filterError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasFilterDiagnosticDetail))]
    private string? _filterDiagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisualFilter))]
    private LibraryVisualFilterNode? _visualFilterExpression;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

    [ObservableProperty]
    private string? _newViewName;

    [ObservableProperty]
    private LibraryViewDefinition? _selectedView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowFooterGuidance))]
    [NotifyPropertyChangedFor(nameof(ResultCountText))]
    private IReadOnlyList<LibraryRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowFooterGuidance))]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    [NotifyPropertyChangedFor(nameof(EmptyStateActionLabel))]
    private LibraryPageState _pageState = LibraryPageState.NoConfiguration;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    public LibraryInspectorPreference InspectorPreference
    {
        get;
        private set;
    } = LibraryInspectorPreference.Auto;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLibraryDelimitedMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverLibraryAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryAudioIdentifiersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveLibraryRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchLibraryMusicBrainzReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchLibraryDiscogsReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLibraryDiscogsReleaseDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryDiscogsReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryDiscogsReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryDiscogsReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryPlaylistOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLibraryPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryExternalToolExecutableCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseLibraryExternalToolWorkingDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunLibraryExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindLibraryReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLocalLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveLibraryFrontCoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveAllLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLibraryMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteLibraryMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedoLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepeatLibraryRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertPendingChangesCommand))]
    private bool _isOperationBusy;

    [ObservableProperty]
    private bool _isOperationsOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportLibraryDelimitedMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverLibraryAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLocalLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveLibraryFrontCoverCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRemoveAllLibraryArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryExternalToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLibraryMetadataFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteLibraryMetadataFieldCommand))]
    private LibraryOperationScope _selectedOperationScope =
        LibraryOperationScope.SelectedTracks;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyLibraryOperationCommand))]
    private bool _hasApplicableOperationPreview;

    [ObservableProperty]
    private string _operationStatus =
        "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasOperationDiagnosticDetail))]
    private string? _operationDiagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasVisualFilterDiagnosticDetail))]
    private string? _visualFilterDiagnosticDetail;

    [ObservableProperty]
    private DelimitedMetadataEmptyCellMode _importEmptyCellMode =
        DelimitedMetadataEmptyCellMode.Ignore;

    [ObservableProperty]
    private bool _isOperationProgressIndeterminate = true;

    [ObservableProperty]
    private double _operationProgressValue;

    [ObservableProperty]
    private double _operationProgressMaximum = 1;

    [ObservableProperty]
    private string _operationProgressText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryAudioIdentifiersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveLibraryRecordingCommand))]
    private AudioDiscoveryRow? _selectedAudioMatch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindLibraryReleaseArtworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseArtworkCommand))]
    private MusicBrainzReleaseRow? _selectedRelease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLibraryDiscogsReleaseDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildLibraryDiscogsReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryDiscogsReleaseArtworkCommand))]
    private DiscogsReleaseRow? _selectedDiscogsRelease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewLibraryReleaseArtworkCommand))]
    private CoverArtCandidateRow? _selectedArtworkMatch;

    partial void OnSelectedDiscogsReleaseChanged(
        DiscogsReleaseRow? value)
    {
        ClearDiscogsTrackMappings();
        BuildLibraryDiscogsReleaseMappingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedArtworkMatchChanged(CoverArtCandidateRow? value) =>
        InvalidateLibraryOperationPreview();

    public LibraryViewModel(
        ILibraryService library,
        IReindexService reindex,
        IAppSettings settings,
        SelectionInspectorViewModel inspector,
        INavigationService navigation,
        IndexingViewModel indexing,
        IThumbnailService thumbnails,
        WorkbenchViewModel? workbench = null,
        IMetadataOperationService? metadataOperations = null,
        IMetadataOperationCatalog? operationCatalog = null,
        IOperationRecipeStore? recipeStore = null,
        IDialogCoordinator? dialogs = null,
        IAcoustIdDiscoveryService? audioDiscovery = null,
        IMusicBrainzMetadataProvider? musicBrainz = null,
        IMusicBrainzReleaseMappingService? releaseMapping = null,
        ICoverArtArchiveProvider? coverArt = null,
        IFilePickerService? files = null,
        IDiscogsMetadataProvider? discogs = null,
        IDiscogsReleaseMappingService? discogsMapping = null,
        IReportExportService? reports = null,
        IPlaylistWorkspaceService? playlists = null,
        IExternalToolService? externalTools = null,
        IExternalToolStore? externalToolStore = null,
        IMetadataGridColumnStore? metadataColumns = null,
        IDelimitedMetadataImportService? delimitedImports = null,
        IPlatformService? platform = null,
        IReviewedFileOperationService? fileOperations = null,
        IEditHistoryService? history = null,
        ILocalizationService? localization = null)
    {
        _library = library;
        _reindex = reindex;
        _settings = settings;
        _inspector = inspector;
        _navigation = navigation;
        _thumbnails = thumbnails;
        _workbench = workbench;
        _metadataOperations = metadataOperations;
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
        _files = files;
        _dialogs = dialogs;
        _platform = platform;
        _history = history;
        _localization = localization;
        ReleaseSearch = new(localization);
        DiscogsSearch = new(localization);
        SetStatusText(
            "Library.Status.LoadConfiguration");
        SetOperationStatus(
            "Library.Operation.Choose");
        OperationEditor = new(
            operationCatalog ?? new MetadataOperationCatalog(),
            MetadataOperationSurface.Library,
            recipeStore,
            localization);
        RepresentativePreview = metadataOperations is null
            ? null
            : new(metadataOperations);
        FileOperations =
            fileOperations is null ||
            files is null ||
            workbench is null
                ? null
                : new(
                    fileOperations,
                    files,
                    () => ResolveOperationPaths(),
                    plan => workbench
                        .AddPendingMutationAsync(
                            ReviewedFileOperationMutationIntent
                                .Create(plan)),
                    FileOperationPreflightMessage,
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
        ColumnEditor = new(
            metadataColumns,
            MetadataGridSurface.Library);
        VisualFilterEditor = new(localization);
        OperationEditor.PropertyChanged += (_, _) =>
        {
            CopyLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
            PasteLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        };
        OperationEditor.Changed +=
            InvalidateLibraryOperationPreview;
        ReleaseImport.PropertyChanged += OnReleaseImportChanged;
        ReleaseSearch.PropertyChanged += (_, _) =>
            SearchLibraryMusicBrainzReleasesCommand.NotifyCanExecuteChanged();
        DiscogsSearch.PropertyChanged += (_, _) =>
            SearchLibraryDiscogsReleasesCommand.NotifyCanExecuteChanged();
        DiscogsImport.PropertyChanged += (_, _) =>
        {
            InvalidateLibraryOperationPreview();
            PreviewLibraryDiscogsReleaseMetadataCommand
                .NotifyCanExecuteChanged();
        };
        ReportEditor = new(localization);
        PlaylistEditor = new(localization);
        ExternalToolEditor = new(
            externalToolStore,
            localization);
        ReportEditor.Changed += InvalidateReportPlan;
        PlaylistEditor.Changed += InvalidatePlaylistPlan;
        ExternalToolEditor.Changed += InvalidateExternalToolPlan;
        Indexing = indexing;
        foreach (DetailsColumn column in DetailsColumns.All)
        {
            string resourceKey =
                ColumnResourceKey(column.Key);
            Columns.Add(new LibraryColumnChoice(
                column.Key,
                L(resourceKey),
                DetailsColumns.DefaultVisible.Contains(
                    column.Key),
                resourceKey));
        }
        RefreshLocalizedChoices();
        LoadViews();
        LoadWorkspace();
        settings.ConfigurationChanged += OnConfigurationChanged;
        indexing.IndexCompleted += () => _ = ReloadAsync();
        inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is
                nameof(SelectionInspectorViewModel.HasUnsavedChanges) or
                nameof(SelectionInspectorViewModel.PendingChangesVersion))
            {
                RebuildPendingChanges();
                OnPropertyChanged(nameof(HasUnsavedSelectionChanges));
                OnPropertyChanged(nameof(HasUnsavedChanges));
                ApplyLibraryOperationCommand
                    .NotifyCanExecuteChanged();
            }
        };
        OperationPreviewChanges.CollectionChanged +=
            (_, _) => RebuildPendingChanges();
        _localization?.CultureChanged +=
            OnLocalizationCultureChanged;
    }

    public ObservableCollection<LibraryViewDefinition> SavedViews { get; } = [];
    public ObservableCollection<LibraryColumnChoice> Columns { get; } = [];
    public ObservableCollection<AudioDiscoveryRow> AudioMatches { get; } = [];
    public ObservableCollection<MusicBrainzReleaseRow> ReleaseMatches { get; } = [];
    public ObservableCollection<MusicBrainzTrackMappingRow> ReleaseTrackMappings { get; } = [];
    public ObservableCollection<CoverArtCandidateRow> ArtworkMatches { get; } = [];
    public ObservableCollection<DiscogsReleaseRow> DiscogsMatches { get; } = [];
    public ObservableCollection<DiscogsTrackMappingRow>
        DiscogsTrackMappings { get; } = [];
    public ObservableCollection<ReportOutputRow> ReportOutputs { get; } = [];
    public ObservableCollection<PlaylistOutputRow>
        PlaylistOutputs { get; } = [];
    public ObservableCollection<ExternalToolInvocationRow>
        ExternalToolInvocations { get; } = [];
    public IReadOnlyList<DelimitedMetadataEmptyCellMode>
        ImportEmptyCellModes { get; } =
            Enum.GetValues<DelimitedMetadataEmptyCellMode>();
    public ObservableCollection<
        LocalizedChoice<DelimitedMetadataEmptyCellMode>>
        ImportEmptyCellModeChoices { get; } = [];
    public ObservableCollection<MetadataPreviewRow> OperationPreviewChanges { get; } = [];
    public ObservableCollection<MetadataPreviewRow> PendingChanges { get; } = [];
    public bool HasPendingChanges => PendingChanges.Count > 0;
    public bool HasNoPendingChanges => PendingChanges.Count == 0;
    public MusicBrainzImportSelectionViewModel ReleaseImport { get; } = new();
    public MusicBrainzReleaseSearchViewModel ReleaseSearch { get; }
    public DiscogsReleaseSearchViewModel DiscogsSearch { get; }
    public DiscogsImportSelectionViewModel DiscogsImport { get; } = new();
    public ReportEditorViewModel ReportEditor { get; }
    public PlaylistEditorViewModel PlaylistEditor { get; }
    public ExternalToolEditorViewModel ExternalToolEditor { get; }
    public MetadataGridColumnEditorViewModel ColumnEditor { get; }
    public VisualFilterEditorViewModel VisualFilterEditor { get; }
    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();
    public ObservableCollection<
        LocalizedChoice<FilterMode>>
        FilterModeChoices { get; } = [];
    public IReadOnlyList<LibraryOperationScope> OperationScopes { get; } =
        Enum.GetValues<LibraryOperationScope>();
    public ObservableCollection<
        LocalizedChoice<LibraryOperationScope>>
        OperationScopeChoices { get; } = [];
    public MetadataOperationEditorViewModel OperationEditor { get; }
    public RepresentativeMetadataPreviewViewModel?
        RepresentativePreview { get; }
    public ReviewedFileOperationEditorViewModel?
        FileOperations { get; }
    public SelectionInspectorViewModel Inspector => _inspector;
    public IndexingViewModel Indexing { get; }
    public int TotalCount => _allRows.Count;
    public int HealthFilterCount => _healthFilterPaths.Count;
    public bool HasHealthFilter => _healthFilterPaths.Count > 0;
    public string HealthFilterSummary => LC(
        "Library.HealthFilter.Tracks",
        HealthFilterCount);
    public bool HasTextFilter => !string.IsNullOrWhiteSpace(FilterText);
    public bool HasVisualFilter => VisualFilterExpression is not null;
    public bool HasRows => Rows.Count > 0;
    public bool HasEmptyState => Rows.Count == 0 && PageState != LibraryPageState.Loading;
    public bool ShowFooterGuidance =>
        !(HasEmptyState &&
          PageState == LibraryPageState.NoConfiguration);
    public bool HasFilterError => !string.IsNullOrWhiteSpace(FilterError);
    public bool HasFilterDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            FilterDiagnosticDetail);
    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            StatusDiagnosticDetail);
    public bool HasOperationDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            OperationDiagnosticDetail);
    public bool HasVisualFilterDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            VisualFilterDiagnosticDetail);
    public string ResultCountText => Rows.Count == TotalCount
        ? LC(
            "Library.Results.Tracks",
            Rows.Count)
        : LF(
            "Library.Results.OfTotal",
            Rows.Count,
            TotalCount);
    public IReadOnlyList<string> SelectedPaths => _selectedPaths;
    public int SelectedPathCount => _selectedPaths.Count;
    public bool HasSelection => _selectedPaths.Count > 0;
    public bool HasUnsavedSelectionChanges => Inspector.HasUnsavedChanges;
    public bool HasUnsavedChanges =>
        HasUnsavedSelectionChanges ||
        _libraryOperationPlan is not null ||
        _reportPlan is not null ||
        _playlistPlan is not null ||
        _externalToolPlan is not null ||
        FileOperations?.HasUnsavedChanges == true;
    public event Action? HealthFilterClearRequested;

    public bool CanUndoLatestOperation =>
        _history?.CanUndo == true &&
        !IsOperationBusy;

    public bool CanRedoLatestOperation =>
        _history?.CanRedo == true &&
        !IsOperationBusy;

    public bool CanRepeatLatestRecipe =>
        _history?.Entries.FirstOrDefault()?.Recipe is not null &&
        ResolveOperationPaths().Length > 0 &&
        !IsOperationBusy;

    partial void OnSelectedReleaseChanged(MusicBrainzReleaseRow? value)
    {
        ClearReleaseTrackMappings();
        ArtworkMatches.Clear();
        SelectedArtworkMatch = null;
        BuildLibraryReleaseMappingCommand.NotifyCanExecuteChanged();
    }
    public string EmptyStateTitle => PageState switch
    {
        LibraryPageState.NoConfiguration => L(
            "Library.Empty.NoConfiguration.Title"),
        LibraryPageState.NotIndexed => L(
            "Library.Empty.NotIndexed.Title"),
        LibraryPageState.FilteredToZero => L(
            "Library.Empty.Filtered.Title"),
        LibraryPageState.NoResults => L(
            "Library.Empty.Health.Title"),
        LibraryPageState.Error => L(
            "Library.Empty.Error.Title"),
        _ => L("Library.Empty.Default.Title"),
    };
    public string EmptyStateMessage => PageState switch
    {
        LibraryPageState.NoConfiguration => L(
            "Library.Empty.NoConfiguration.Message"),
        LibraryPageState.NotIndexed => L(
            "Library.Empty.NotIndexed.Message"),
        LibraryPageState.FilteredToZero => L(
            "Library.Empty.Filtered.Message"),
        LibraryPageState.NoResults => L(
            "Library.Empty.Health.Message"),
        LibraryPageState.Error => StatusText,
        _ => L("Library.Empty.Default.Message"),
    };
    public string EmptyStateActionLabel => PageState switch
    {
        LibraryPageState.NoConfiguration => L(
            "Library.Empty.NoConfiguration.Action"),
        LibraryPageState.NotIndexed => L(
            "Library.Empty.NotIndexed.Action"),
        LibraryPageState.FilteredToZero => L(
            "Library.Empty.Filtered.Action"),
        LibraryPageState.NoResults => L(
            "Library.Empty.Health.Action"),
        LibraryPageState.Error => L(
            "Library.Empty.Error.Action"),
        _ => L("Common.Reload"),
    };

    private async void OnConfigurationChanged(object? sender, EventArgs args)
    {
        SavedViews.Clear();
        LoadViews();
        LoadWorkspace();
        // Restore cached browsing first, then return to the UI loop before starting the root scan.
        // This mirrors the portable app and ensures Home is painted before progress begins.
        await ReloadAsync();
        await Task.Yield();
        await Indexing.StartAutomaticIndexAsync();
    }

    partial void OnFilterTextChanged(string? value)
    {
        if (!_loadingWorkspace)
            SaveWorkspace();
        QueueFilter();
    }

    partial void OnFilterModeChanged(FilterMode value)
    {
        if (!_loadingWorkspace)
            SaveWorkspace();
        QueueFilter();
    }

    partial void OnIsInspectorOpenChanged(bool value)
    {
        if (!_loadingWorkspace)
        {
            InspectorPreference = value
                ? LibraryInspectorPreference.Pinned
                : LibraryInspectorPreference.Closed;
            OnPropertyChanged(
                nameof(InspectorPreference));
            SaveWorkspace();
        }
    }

    public void SetInspectorPreference(
        LibraryInspectorPreference preference)
    {
        if (!Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(
                nameof(preference));
        InspectorPreference = preference;
        OnPropertyChanged(nameof(InspectorPreference));
        bool shouldBeOpen =
            preference != LibraryInspectorPreference.Closed;
        if (IsInspectorOpen != shouldBeOpen)
        {
            _loadingWorkspace = true;
            try
            {
                IsInspectorOpen = shouldBeOpen;
            }
            finally
            {
                _loadingWorkspace = false;
            }
        }
        SaveWorkspace();
    }

    partial void OnSelectedViewChanged(LibraryViewDefinition? value)
    {
        if (value is null)
            return;
        FilterMode = value.FilterMode;
        VisualFilterExpression = value.VisualFilter;
        VisualFilterEditor.Load(value.VisualFilter);
        FilterText = value.Filter;
    }

    public void SetGlobalFilter(string? text)
    {
        FilterText = text;
        _navigation.Navigate(ShellDestination.Library);
    }

    public void SetHealthFilter(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var next = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(PathComparer);
        if (_healthFilterPaths.SetEquals(next))
            return;
        _healthFilterPaths = next;
        OnPropertyChanged(nameof(HealthFilterCount));
        OnPropertyChanged(nameof(HasHealthFilter));
        OnPropertyChanged(nameof(HealthFilterSummary));
        QueueFilter();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = null;

    [RelayCommand]
    private async Task ApplyVisualFilterAsync()
    {
        LibraryVisualFilterNode? expression =
            VisualFilterEditor.Build(out string? error);
        if (error is not null)
        {
            SetVisualFilterStatus(
                "Library.VisualFilter.Status.Invalid",
                error);
            return;
        }
        var compiled = new LibraryVisualFilter(expression);
        if (!compiled.IsValid)
        {
            SetVisualFilterStatus(
                "Library.VisualFilter.Status.Invalid",
                compiled.Error);
            return;
        }
        VisualFilterExpression = expression;
        SetVisualFilterStatus(
            expression is null
                ? "Library.VisualFilter.Status.None"
                : "Library.VisualFilter.Status.Applied");
        SaveWorkspace();
        await ApplyFilterAsync(immediate: true);
    }

    [RelayCommand]
    private async Task ClearVisualFilterAsync()
    {
        VisualFilterExpression = null;
        VisualFilterEditor.Load(null);
        SetVisualFilterStatus(
            "Library.VisualFilter.Status.Cleared");
        SaveWorkspace();
        await ApplyFilterAsync(immediate: true);
    }

    [RelayCommand]
    private void ClearHealthFilter()
    {
        if (!HasHealthFilter)
            return;
        SetHealthFilter([]);
        HealthFilterClearRequested?.Invoke();
    }

    [RelayCommand]
    private async Task EmptyStateActionAsync()
    {
        switch (PageState)
        {
            case LibraryPageState.NoConfiguration:
                _navigation.Navigate(ShellDestination.Settings);
                break;
            case LibraryPageState.NotIndexed:
                if (Indexing.IndexCommand.CanExecute(null))
                    await Indexing.IndexCommand.ExecuteAsync(null);
                break;
            case LibraryPageState.FilteredToZero:
                ClearFilter();
                break;
            case LibraryPageState.NoResults:
                ClearHealthFilter();
                break;
            default:
                await ReloadAsync();
                break;
        }
    }

    private bool CanReload() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        ResetThumbnails();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        if (!_library.IsReady)
        {
            _allRows = [];
            Rows = [];
            PageState = LibraryPageState.NoConfiguration;
            SetStatusText(
                "Library.Status.ChooseConfiguration");
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(ResultCountText));
            return;
        }
        IsBusy = true;
        PageState = LibraryPageState.Loading;
        SetStatusText(
            "Library.Status.LoadingCache");
        try
        {
            var records = await _library.GetAllRecordsAsync(cancellation.Token);
            var rows = await Task.Run(() => records.Select(record => new LibraryRow(record)).ToList(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
            {
                var loadedPaths = rows.Select(row => row.Path).ToHashSet(PathComparer);
                rows.AddRange(_allRows.Where(row =>
                    _selectedPaths.Contains(row.Path, PathComparer) &&
                    loadedPaths.Add(row.Path)));
            }
            _allRows = rows;
            await ApplyFilterAsync(immediate: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatusFailure(
                "Library.Status.LoadFailed",
                error.Message);
            PageState = LibraryPageState.Error;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void Cancel() => _loadCancellation?.Cancel();

    public async Task<bool> SelectAsync(IReadOnlyList<LibraryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selection = new SelectionContext(
            rows.Select(row => row.Path).ToArray(),
            rows.Select(row => row.Record).ToArray());
        if (!await _inspector.TryLoadAsync(selection))
            return false;
        SetSelectedPaths(selection.Paths);
        return true;
    }

    /// <summary>Navigation hosts call this before replacing the Library view.</summary>
    public async Task<bool> ConfirmCanNavigateAwayAsync()
    {
        if (!await _inspector.ConfirmDiscardChangesAsync())
            return false;
        if ((_libraryOperationPlan is null &&
             _reportPlan is null &&
             _playlistPlan is null &&
             _externalToolPlan is null &&
             FileOperations?.HasUnsavedChanges != true) ||
            _dialogs is null)
            return true;
        return await _dialogs.ConfirmDestructiveAsync(
            L("Library.Dialog.Leave.Title"),
            L("Library.Dialog.Leave.Message"),
            L("Library.Dialog.Leave.Confirm"));
    }

    public Task<bool> ConfirmNavigationAsync() => ConfirmCanNavigateAwayAsync();

    public IReadOnlyList<LibraryRow> GetVisibleSelectedRows()
    {
        if (_selectedPaths.Count == 0)
            return [];
        var selected = _selectedPaths.ToHashSet(PathComparer);
        return Rows.Where(row => selected.Contains(row.Path)).ToArray();
    }

    private void SetSelectedPaths(IReadOnlyList<string> paths)
    {
        string[] distinct = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
        if (_selectedPaths.SequenceEqual(distinct, PathComparer))
            return;
        _selectedPaths = distinct;
        OnPropertyChanged(nameof(SelectedPaths));
        OnPropertyChanged(nameof(SelectedPathCount));
        OnPropertyChanged(nameof(HasSelection));
        FileOperations?.InvalidateTargets();
        InvalidateLibraryOperationPreview();
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        OpenInWorkbenchCommand.NotifyCanExecuteChanged();
        EditSelectedInWorkbenchCommand.NotifyCanExecuteChanged();
        PreviewLibraryOperationCommand.NotifyCanExecuteChanged();
        ImportLibraryDelimitedMetadataCommand.NotifyCanExecuteChanged();
        CopyLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        PasteLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        NotifyHistoryCommands();
        DiscoverLibraryAudioCommand.NotifyCanExecuteChanged();
        PreviewLocalLibraryArtworkCommand.NotifyCanExecuteChanged();
        PreviewRemoveLibraryFrontCoverCommand.NotifyCanExecuteChanged();
        PreviewRemoveAllLibraryArtworkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenInWorkbench))]
    private async Task OpenInWorkbenchAsync()
    {
        await HandoffToWorkbenchAsync(
            WorkbenchSection.Session,
            WorkbenchHandoffScopeKind.Selected);
    }

    private bool CanOpenInWorkbench() =>
        !IsBusy && _workbench is not null && _selectedPaths.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditSelectedInWorkbench))]
    private Task EditSelectedInWorkbenchAsync(string? sectionId) =>
        TryParseHandoffSection(sectionId, out WorkbenchSection section)
            ? HandoffToWorkbenchAsync(
                section,
                WorkbenchHandoffScopeKind.Selected)
            : Task.CompletedTask;

    private bool CanEditSelectedInWorkbench(string? sectionId) =>
        CanHandoffToWorkbench(
            sectionId,
            WorkbenchHandoffScopeKind.Selected);

    [RelayCommand(CanExecute = nameof(CanEditVisibleInWorkbench))]
    private Task EditVisibleInWorkbenchAsync(string? sectionId) =>
        TryParseHandoffSection(sectionId, out WorkbenchSection section)
            ? HandoffToWorkbenchAsync(
                section,
                WorkbenchHandoffScopeKind.VisibleResults)
            : Task.CompletedTask;

    private bool CanEditVisibleInWorkbench(string? sectionId) =>
        CanHandoffToWorkbench(
            sectionId,
            WorkbenchHandoffScopeKind.VisibleResults);

    [RelayCommand(CanExecute = nameof(CanEditAllInWorkbench))]
    private Task EditAllInWorkbenchAsync(string? sectionId) =>
        TryParseHandoffSection(sectionId, out WorkbenchSection section)
            ? HandoffToWorkbenchAsync(
                section,
                WorkbenchHandoffScopeKind.AllResults)
            : Task.CompletedTask;

    private bool CanEditAllInWorkbench(string? sectionId) =>
        CanHandoffToWorkbench(
            sectionId,
            WorkbenchHandoffScopeKind.AllResults);

    public async Task HandoffToWorkbenchAsync(
        WorkbenchSection destinationSection,
        WorkbenchHandoffScopeKind scopeKind)
    {
        if (_workbench is null || IsBusy)
            return;
        string[] paths = ResolveHandoffPaths(scopeKind);
        if (paths.Length == 0)
            return;
        var request = WorkbenchHandoffRequest.Create(
            destinationSection,
            scopeKind,
            paths);
        if (await _workbench.AcceptHandoffAsync(request))
            _navigation.Navigate(ShellDestination.Workbench);
    }

    private bool CanHandoffToWorkbench(
        string? sectionId,
        WorkbenchHandoffScopeKind scopeKind) =>
        !IsBusy &&
        _workbench is not null &&
        TryParseHandoffSection(sectionId, out _) &&
        ResolveHandoffPaths(scopeKind).Length > 0;

    private static bool TryParseHandoffSection(
        string? sectionId,
        out WorkbenchSection section) =>
        Enum.TryParse(sectionId, ignoreCase: false, out section) &&
        Enum.IsDefined(section);

    private string[] ResolveHandoffPaths(
        WorkbenchHandoffScopeKind scopeKind)
    {
        IEnumerable<string> paths = scopeKind switch
        {
            WorkbenchHandoffScopeKind.Selected =>
                _selectedPaths,
            WorkbenchHandoffScopeKind.VisibleResults =>
                Rows.Select(row => row.Path),
            WorkbenchHandoffScopeKind.AllResults =>
                _allRows.Select(row => row.Path),
            _ => [],
        };
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
    }

    [RelayCommand]
    private void OpenOperations()
    {
        IsOperationsOpen = true;
        PreviewLibraryOperationCommand.NotifyCanExecuteChanged();
        ImportLibraryDelimitedMetadataCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CloseOperations() => IsOperationsOpen = false;

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryOperation))]
    private async Task PreviewLibraryOperationAsync()
    {
        if (_metadataOperations is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
        {
            SetOperationStatus(
                "Library.Operation.NoFiles");
            return;
        }

        BeginLibraryOperation(
            "Library.Progress.BuildingMetadataPreview");
        try
        {
            OperationRecipe recipe = OperationEditor.CreateRecipe();
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewAsync(
                    paths,
                    recipe,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetOperationStatus(
                    "Library.Operation.PreviewBlockers",
                    paths.Length,
                    blockers);
            else
                SetOperationStatus(
                    "Library.Operation.PreviewComplete",
                    plan.ChangeCount,
                    plan.ChangedFileCount,
                    paths.Length);
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.PreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyLibraryMetadataField))]
    private async Task CopyLibraryMetadataFieldAsync()
    {
        if (_platform is null ||
            OperationEditor.SelectedField is not { } selected)
            return;
        string? path = ResolveOperationPaths().FirstOrDefault();
        LibraryRow? row = _allRows.FirstOrDefault(
            candidate => PathComparer.Equals(candidate.Path, path));
        if (row is null)
        {
            SetOperationStatus(
                "Library.Operation.Copy.NoMetadata");
            return;
        }

        MetadataFieldKey field =
            MetadataFieldKey.Known(selected.Field);
        string[] values = CachedMetadataValues(
            row.Record,
            selected.Field);
        await _platform.CopyTextAsync(
            MetadataClipboardCodec.Encode(
                new(field, values.ToImmutableArray())));
        SetOperationStatus(
            "Library.Operation.Copy.Complete",
            values.Length,
            selected.Label,
            Path.GetFileName(row.Path));
    }

    [RelayCommand(CanExecute = nameof(CanPasteLibraryMetadataField))]
    private async Task PasteLibraryMetadataFieldAsync()
    {
        if (_platform is null ||
            _metadataOperations is null ||
            OperationEditor.SelectedField is not { } selected)
            return;
        string[] paths = ResolveOperationPaths();
        string? text = await _platform.ReadTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            SetOperationStatus(
                "Library.Operation.Clipboard.NoText");
            return;
        }

        BeginLibraryOperation(
            "Library.Progress.BuildingClipboardPreview");
        try
        {
            MetadataClipboardPayload payload =
                MetadataClipboardCodec.DecodeOrPlainText(
                    text,
                    MetadataFieldKey.Known(selected.Field));
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>> edits =
                    paths.ToDictionary(
                        path => path,
                        _ => (IReadOnlyList<MetadataValueEdit>)
                        [
                            new(payload.Field, payload.Values),
                        ],
                        PathComparer);
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    edits,
                    LF(
                        "Library.OperationName.PasteValues",
                        payload.Field.DisplayName,
                        paths.Length),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files
                .SelectMany(file => file.Issues)
                .Count(issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountOperationStatus(
                    "Library.Operation.Clipboard.Blockers",
                    blockers);
            else
                SetOperationStatus(
                    "Library.Operation.Clipboard.Complete",
                    plan.ChangeCount,
                    plan.ChangedFileCount);
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Clipboard.Cancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Clipboard.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportLibraryDelimitedMetadata))]
    private async Task ImportLibraryDelimitedMetadataAsync()
    {
        if (_metadataOperations is null ||
            _delimitedImports is null ||
            _files is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
        {
            SetOperationStatus(
                "Library.Operation.NoFiles");
            return;
        }
        string? source = await _files.PickFileAsync(
            L("Library.Picker.ImportMetadata.Title"),
            [new(
                L("Library.Picker.DelimitedMetadata"),
                [".csv", ".tsv", ".txt"])]);
        if (source is null)
            return;

        BeginLibraryOperation(
            "Library.Progress.MappingMetadataImport");
        try
        {
            IProgress<OperationProgress> progress =
                CreateOperationProgress();
            DelimitedMetadataImportResult imported =
                await _delimitedImports.ImportAsync(
                    source,
                    paths,
                    new(EmptyCellMode: ImportEmptyCellMode),
                    progress: progress,
                    ct: _operationCancellation!.Token);
            if (!imported.CanPreview)
            {
                string reason = imported.Issues
                    .FirstOrDefault(issue =>
                        issue.Severity ==
                            DelimitedMetadataImportIssueSeverity.Blocker)
                    ?.Message ??
                    L(
                        "Library.Operation.Import.NoRowsMatched");
                throw new InvalidDataException(reason);
            }
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    imported.EditsByPath,
                    LF(
                        "Library.OperationName.ImportMetadata",
                        Path.GetFileName(source)),
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files
                .SelectMany(file => file.Issues)
                .Count(issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
            int warnings = imported.Issues.Count(issue =>
                issue.Severity ==
                    DelimitedMetadataImportIssueSeverity.Warning);
            if (blockers > 0)
                SetCountOperationStatus(
                    "Library.Operation.Import.Blockers",
                    blockers);
            else if (warnings == 0)
                SetOperationStatus(
                    "Library.Operation.Import.Complete",
                    plan.ChangeCount,
                    plan.ChangedFileCount,
                    imported.MatchedRows,
                    imported.DataRows);
            else
                SetOperationStatus(
                    "Library.Operation.Import.CompleteWithWarnings",
                    plan.ChangeCount,
                    plan.ChangedFileCount,
                    imported.MatchedRows,
                    imported.DataRows,
                    warnings);
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Import.Cancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Import.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyLibraryOperation))]
    private async Task ApplyLibraryOperationAsync()
    {
        if (_metadataOperations is null ||
            _libraryOperationPlan is null &&
            !_inspector.HasUnsavedChanges)
            return;
        BeginLibraryOperation(
            "Library.Progress.ApplyingMetadataChanges");
        try
        {
            MetadataOperationPlan? inspectorPlan =
                _inspector.HasUnsavedChanges
                    ? await _inspector.PreviewPendingChangesAsync(
                        CreateOperationProgress(),
                        _operationCancellation!.Token)
                    : null;
            MetadataOperationPlan? effectivePlan =
                _libraryOperationPlan is null
                    ? inspectorPlan
                    : inspectorPlan is null
                        ? _libraryOperationPlan
                        : MetadataOperationPlanComposer.Combine(
                            L(
                                "Library.PendingChanges.Title"),
                            _libraryOperationPlan,
                            inspectorPlan);
            if (effectivePlan is null)
            {
                SetOperationStatus(
                    "Library.Operation.Apply.NoPending");
                return;
            }
            HasApplicableOperationPreview =
                effectivePlan.CanApply;
            if (!effectivePlan.CanApply)
            {
                OperationIssue? blocker = effectivePlan
                    .Files
                    .SelectMany(file => file.Issues)
                    .FirstOrDefault(issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker);
                if (blocker is null)
                    SetOperationStatus(
                        "Library.Operation.Apply.NoApplicable");
                else
                    SetOperationFailure(
                        "Library.Operation.Apply.Blocked",
                        blocker.Message);
                return;
            }
            MetadataApplyResult result =
                await _metadataOperations.ApplyAsync(
                    effectivePlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            bool appliedInspectorDraft =
                inspectorPlan?.ChangedFileCount > 0;
            _libraryOperationPlan = null;
            OperationPreviewChanges.Clear();
            if (appliedInspectorDraft &&
                _inspector.HasUnsavedChanges)
                await _inspector.LoadAsync(
                    _inspector.Selection);
            RebuildPendingChanges();
            HasApplicableOperationPreview = false;
            SetCountOperationStatus(
                "Library.Operation.Apply.Complete",
                result.ChangedFiles);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Apply.Cancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Apply.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
            NotifyHistoryCommands();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevertPendingChanges))]
    private async Task RevertPendingChangesAsync()
    {
        if (_libraryOperationPlan is null &&
            PendingChanges.Count == 0)
            return;

        _libraryOperationPlan = null;
        OperationPreviewChanges.Clear();
        if (_inspector.HasUnsavedChanges)
            await _inspector.DiscardPendingChangesAsync();
        RebuildPendingChanges();
        HasApplicableOperationPreview = false;
        SetOperationStatus(
            "Library.Operation.PendingReverted");
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private bool CanRevertPendingChanges() =>
        !IsOperationBusy &&
        HasPendingChanges;

    [RelayCommand(CanExecute = nameof(CanUndoLibraryOperation))]
    private async Task UndoLibraryOperationAsync()
    {
        if (_history is null ||
            _dialogs is null ||
            _history.Entries.FirstOrDefault() is not { } candidate)
            return;
        if (!await _dialogs.ConfirmAsync(
                L("Library.Dialog.Restore.Title"),
                L("Library.Dialog.Restore.Message"),
                L("Common.Restore")))
            return;

        BeginLibraryOperation(
            "Library.Progress.RestoringMetadataOperation");
        try
        {
            var progress = new Progress<int>(completed =>
            {
                IsOperationProgressIndeterminate = false;
                OperationProgressMaximum =
                    Math.Max(1, candidate.Paths.Length);
                OperationProgressValue =
                    Math.Clamp(
                        completed,
                        0,
                        OperationProgressMaximum);
                OperationProgressText = LC(
                    "Library.Progress.RestoredFiles",
                    completed);
            });
            int restored =
                await _history.UndoLatestAsync(
                    progress,
                    _operationCancellation!.Token);
            foreach (string path in candidate.Paths)
                await _reindex.ReindexFileAsync(
                    path,
                    _operationCancellation.Token);
            await ReloadAsync();
            SetCountOperationStatus(
                "Library.Operation.Restore.Complete",
                restored);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Restore.Cancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Restore.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
            NotifyHistoryCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedoLibraryOperation))]
    private async Task RedoLibraryOperationAsync()
    {
        EditHistoryEntry? candidate =
            _history?.RedoEntries.FirstOrDefault(
                entry => entry.Recipe is not null);
        if (candidate?.Recipe is null)
            return;
        await PreviewHistoryRecipeAsync(
            candidate.Paths,
            candidate.Recipe,
            "Library.Operation.History.RedoReady");
    }

    [RelayCommand(CanExecute = nameof(CanRepeatLibraryRecipe))]
    private async Task RepeatLibraryRecipeAsync()
    {
        OperationRecipe? recipe =
            _history?.Entries.FirstOrDefault()?.Recipe;
        if (recipe is null)
            return;
        await PreviewHistoryRecipeAsync(
            ResolveOperationPaths(),
            recipe,
            "Library.Operation.History.RepeatReady");
    }

    private async Task PreviewHistoryRecipeAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        string successMessageKey)
    {
        if (_metadataOperations is null ||
            paths.Count == 0)
            return;
        BeginLibraryOperation(
            "Library.Progress.RegeneratingMetadataPreview");
        try
        {
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewAsync(
                    paths,
                    recipe,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview =
                plan.CanApply;
            SetOperationStatus(
                successMessageKey);
            OnPropertyChanged(
                nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.History.Cancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.History.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
            NotifyHistoryCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverLibraryAudio))]
    private async Task DiscoverLibraryAudioAsync()
    {
        if (_audioDiscovery is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
        {
            SetOperationStatus(
                "Library.Operation.NoFiles");
            return;
        }

        BeginLibraryOperation(
            "Library.Progress.PreparingAudioDiscovery");
        try
        {
            AcoustIdDiscoveryResult result = await _audioDiscovery.DiscoverAsync(
                paths, CreateOperationProgress(), _operationCancellation!.Token);
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
            SetOperationStatus(
                "Library.Operation.Audio.DiscoveryComplete",
                result.FingerprintedFileCount,
                result.CandidateCount,
                issues);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Audio.DiscoveryCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Audio.DiscoveryFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryAudioIdentifiers))]
    private async Task PreviewLibraryAudioIdentifiersAsync()
    {
        if (_metadataOperations is null || SelectedAudioMatch is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.BuildingAudioIdentifierPreview");
        try
        {
            OperationRecipe recipe =
                AudioDiscoveryRows.CreateTagRecipe(
                    SelectedAudioMatch,
                    _localization);
            MetadataOperationPlan plan = await _metadataOperations.PreviewAsync(
                [SelectedAudioMatch.Path],
                recipe,
                CreateOperationProgress(),
                _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            SetOperationStatus(
                "Library.Operation.Audio.IdentifiersReady");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Audio.IdentifierPreviewCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Audio.IdentifierPreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanResolveLibraryRecording))]
    private async Task ResolveLibraryRecordingAsync()
    {
        if (_musicBrainz is null || SelectedAudioMatch is null ||
            SelectedAudioMatch.MusicBrainzRecordingIdValues.Length != 1)
            return;
        BeginLibraryOperation(
            "Library.Progress.ResolvingMusicBrainzReleases");
        try
        {
            MusicBrainzReleaseResult result =
                await _musicBrainz.ResolveRecordingAsync(
                    SelectedAudioMatch.MusicBrainzRecordingIdValues[0],
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            ReleaseMatches.Clear();
            foreach (MusicBrainzReleaseRow row in MusicBrainzReleaseRows.Create(
                         SelectedAudioMatch.Path, result))
                ReleaseMatches.Add(row);
            SelectedRelease = ReleaseMatches.FirstOrDefault();
            SetCountOperationStatus(
                "Library.Operation.MusicBrainz.LookupComplete",
                ReleaseMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.MusicBrainz.LookupCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.MusicBrainz.LookupFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchLibraryMusicBrainzReleases))]
    private async Task SearchLibraryMusicBrainzReleasesAsync()
    {
        if (_musicBrainz is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.SearchingMusicBrainzReleases");
        try
        {
            MusicBrainzReleaseSearchResult result =
                await _musicBrainz.SearchReleasesAsync(
                    ReleaseSearch.CreateQuery(),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            SelectedRelease = null;
            ReleaseMatches.Clear();
            ClearReleaseTrackMappings();
            string sourcePath =
                ResolveOperationPaths().FirstOrDefault() ?? "";
            foreach (MusicBrainzReleaseRow row in
                     MusicBrainzReleaseRows.CreateSearch(sourcePath, result))
                ReleaseMatches.Add(row);
            SelectedRelease = ReleaseMatches.FirstOrDefault();
            SetCountOperationStatus(
                "Library.Operation.MusicBrainz.SearchComplete",
                ReleaseMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.MusicBrainz.SearchCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.MusicBrainz.SearchFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchLibraryDiscogsReleases))]
    private async Task SearchLibraryDiscogsReleasesAsync()
    {
        if (_discogs is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.SearchingDiscogsReleases");
        try
        {
            DiscogsReleaseSearchResult result =
                await _discogs.SearchReleasesAsync(
                    DiscogsSearch.CreateQuery(),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            SelectedDiscogsRelease = null;
            DiscogsMatches.Clear();
            string source = result.OfflineFallback
                ? L("Library.ProviderSource.OfflineCache")
                : result.FromCache
                    ? L("Library.ProviderSource.Cache")
                    : L("Library.ProviderSource.Discogs");
            foreach (DiscogsReleaseCandidate candidate in result.Releases)
                DiscogsMatches.Add(
                    DiscogsReleaseRow.Create(candidate, source));
            SelectedDiscogsRelease = DiscogsMatches.FirstOrDefault();
            SetCountOperationStatus(
                "Library.Operation.Discogs.SearchComplete",
                DiscogsMatches.Count);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Discogs.SearchCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Discogs.SearchFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadLibraryDiscogsReleaseDetails))]
    private async Task LoadLibraryDiscogsReleaseDetailsAsync()
    {
        if (_discogs is null || SelectedDiscogsRelease is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.LoadingDiscogsDetails");
        try
        {
            DiscogsReleaseRow selected = SelectedDiscogsRelease;
            DiscogsReleaseCandidate release =
                await _discogs.GetReleaseAsync(
                    selected.ReleaseId,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            var detailed = DiscogsReleaseRow.Create(
                release, selected.Source);
            int index = DiscogsMatches.IndexOf(selected);
            if (index >= 0)
                DiscogsMatches[index] = detailed;
            SelectedDiscogsRelease = detailed;
            SetOperationStatus(
                "Library.Operation.Discogs.DetailsComplete",
                release.ReleaseId,
                release.Tracks.Length);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Discogs.DetailsCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Discogs.DetailsFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanBuildLibraryDiscogsReleaseMapping))]
    private async Task BuildLibraryDiscogsReleaseMappingAsync()
    {
        if (_discogsMapping is null || SelectedDiscogsRelease is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
            return;
        BeginLibraryOperation(
            "Library.Progress.MatchingDiscogsTracks");
        try
        {
            DiscogsReleaseCandidate release =
                await EnsureSelectedDiscogsReleaseDetailsAsync(
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            var rows = _allRows.ToDictionary(
                row => row.Path, PathComparer);
            DiscogsSourceFile[] sources = paths.Select(path =>
            {
                rows.TryGetValue(path, out LibraryRow? row);
                double? discoveredDuration = AudioMatches
                    .Where(match =>
                        PathComparer.Equals(match.Path, path))
                    .Select(match => match.DurationSeconds)
                    .FirstOrDefault(value => value is not null);
                return new DiscogsSourceFile(
                    path,
                    row?.Record.Title,
                    row?.Record.Artist,
                    row?.Record.DiscNumber,
                    row?.Record.TrackNumber,
                    discoveredDuration is not null
                        ? TimeSpan.FromSeconds(discoveredDuration.Value)
                        : row?.Record.DurationInSeconds is > 0
                            ? TimeSpan.FromSeconds(
                                row.Record.DurationInSeconds)
                            : null);
            }).ToArray();
            DiscogsReleaseMapping mapping =
                await _discogsMapping.MapAsync(
                    release,
                    sources,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            ClearDiscogsTrackMappings();
            foreach (DiscogsTrackMatch match in mapping.Files)
            {
                var row = new DiscogsTrackMappingRow(
                    match,
                    _localization);
                row.PropertyChanged += OnDiscogsMappingChanged;
                DiscogsTrackMappings.Add(row);
            }
            SetOperationStatus(
                "Library.Operation.Discogs.MappingComplete",
                mapping.SuggestedCount,
                mapping.Files.Length,
                mapping.AmbiguousCount);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Discogs.MappingCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Discogs.MappingFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
            PreviewLibraryDiscogsReleaseMetadataCommand
                .NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanPreviewLibraryDiscogsReleaseMetadata))]
    private async Task PreviewLibraryDiscogsReleaseMetadataAsync()
    {
        if (_metadataOperations is null ||
            _discogsMapping is null ||
            SelectedDiscogsRelease is null)
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
        BeginLibraryOperation(
            "Library.Progress.BuildingDiscogsMetadataPreview");
        try
        {
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    edits,
                    LF(
                        "Library.OperationName.DiscogsMetadata",
                        SelectedDiscogsRelease.Title),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            SetOperationStatus(
                "Library.Operation.Discogs.MetadataReady");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Discogs.MetadataCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Discogs.MetadataFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanPreviewLibraryDiscogsReleaseArtwork))]
    private async Task PreviewLibraryDiscogsReleaseArtworkAsync()
    {
        if (_metadataOperations is null ||
            _discogs is null ||
            SelectedDiscogsRelease is null)
            return;
        DiscogsReleaseRow selected = SelectedDiscogsRelease;
        string[] paths = ConfirmedDiscogsReleasePaths();
        string releaseTitle = selected.Title;
        BeginLibraryOperation(
            "Library.Progress.BuildingDiscogsArtworkPreview");
        try
        {
            IProgress<OperationProgress> progress =
                CreateOperationProgress();
            CoverArtDownload download =
                await _discogs.DownloadPrimaryArtworkAsync(
                    selected.Candidate,
                    progress,
                    _operationCancellation!.Token);
            var image = new ArtworkInput(
                MusicFileUtilities.ID3v2Util.APICType.FrontCover,
                download.ContentType,
                download.Data,
                $"Discogs release {selected.ReleaseId}");
            var edits = paths.ToDictionary(
                path => path,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    image),
                PathComparer);
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewArtworkEditsAsync(
                    edits,
                    LF(
                        "Library.OperationName.DiscogsArtwork",
                        releaseTitle),
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            SetOperationStatus(
                "Library.Operation.Discogs.ArtworkReady");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Discogs.ArtworkCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Discogs.ArtworkFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseLibraryReportOutput))]
    private async Task BrowseLibraryReportOutputAsync()
    {
        if (_files is null)
            return;
        string? path = ReportEditor.OneFilePerGroup
            ? await _files.PickFolderAsync(
                L("Library.Picker.ReportFolder"))
            : await _files.SaveFileAsync(
                L("Library.Picker.ReportOutput"),
                "music-library-report." +
                ReportEditor.SuggestedExtension,
                ReportEditor.SuggestedExtension);
        if (!string.IsNullOrWhiteSpace(path))
            ReportEditor.OutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryReport))]
    private async Task PreviewLibraryReportAsync()
    {
        if (_reports is null)
            return;
        string[] paths = ResolveOperationPaths();
        BeginLibraryOperation(
            "Library.Progress.BuildingReportPreview");
        try
        {
            ReportExportPlan plan = await _reports.PreviewAsync(
                new(paths, ReportEditor.CreateConfiguration()),
                CreateOperationProgress(),
                _operationCancellation!.Token);
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
                SetCountOperationStatus(
                    "Library.Operation.Report.Blockers",
                    blockers);
            else
                SetCountOperationStatus(
                    "Library.Operation.Report.Ready",
                    plan.Files.Count);
            ApplyLibraryReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateReportPlan();
            SetOperationStatus(
                "Library.Operation.Report.PreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidateReportPlan();
            SetOperationFailure(
                "Library.Operation.Report.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyLibraryReport))]
    private async Task ApplyLibraryReportAsync()
    {
        if (_reports is null || _reportPlan is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.WritingReport");
        try
        {
            ReportExportResult result = await _reports.ApplyAsync(
                _reportPlan,
                CreateOperationProgress(),
                _operationCancellation!.Token);
            _reportPlan = null;
            SetOperationStatus(
                "Library.Operation.Report.Written",
                result.FileCount,
                result.RowCount);
            ApplyLibraryReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Report.OutputCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Report.OutputFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseLibraryPlaylistOutput))]
    private async Task BrowseLibraryPlaylistOutputAsync()
    {
        if (_files is null)
            return;
        string? path = PlaylistEditor.OnePlaylistPerGroup
            ? await _files.PickFolderAsync(
                L("Library.Picker.PlaylistFolder"))
            : await _files.SaveFileAsync(
                L("Library.Picker.PlaylistOutput"),
                "music-playlist." +
                PlaylistEditor.SuggestedExtension,
                PlaylistEditor.SuggestedExtension);
        if (!string.IsNullOrWhiteSpace(path))
            PlaylistEditor.OutputPath = path;
    }

    [RelayCommand(CanExecute =
        nameof(CanPreviewLibraryPlaylist))]
    private async Task PreviewLibraryPlaylistAsync()
    {
        if (_playlists is null)
            return;
        string[] paths = ResolveOperationPaths();
        BeginLibraryOperation(
            "Library.Progress.BuildingPlaylistPreview");
        try
        {
            PlaylistWorkspacePlan plan =
                await _playlists.PreviewAsync(
                    new(
                        paths,
                        PlaylistEditor.CreateConfiguration()),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
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
                SetCountOperationStatus(
                    "Library.Operation.Playlist.Blockers",
                    blockers);
            else
                SetCountOperationStatus(
                    "Library.Operation.Playlist.Ready",
                    plan.Files.Count);
            ApplyLibraryPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidatePlaylistPlan();
            SetOperationStatus(
                "Library.Operation.Playlist.PreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidatePlaylistPlan();
            SetOperationFailure(
                "Library.Operation.Playlist.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanApplyLibraryPlaylist))]
    private async Task ApplyLibraryPlaylistAsync()
    {
        if (_playlists is null || _playlistPlan is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.WritingPlaylist");
        try
        {
            PlaylistWorkspaceResult result =
                await _playlists.ApplyAsync(
                    _playlistPlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _playlistPlan = null;
            SetOperationStatus(
                "Library.Operation.Playlist.Written",
                result.PlaylistCount,
                result.TrackReferenceCount);
            ApplyLibraryPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.Playlist.OutputCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.Playlist.OutputFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseLibraryExternalToolExecutable))]
    private async Task BrowseLibraryExternalToolExecutableAsync()
    {
        if (_files is null)
            return;
        string? path = await _files.PickFileAsync(
            L("Library.Picker.ExternalToolExecutable"));
        if (!string.IsNullOrWhiteSpace(path))
            ExternalToolEditor.Executable = path;
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseLibraryExternalToolWorkingDirectory))]
    private async Task BrowseLibraryExternalToolWorkingDirectoryAsync()
    {
        if (_files is null)
            return;
        string? path = await _files.PickFolderAsync(
            L("Library.Picker.ExternalToolDirectory"));
        if (!string.IsNullOrWhiteSpace(path))
            ExternalToolEditor.WorkingDirectory = path;
    }

    [RelayCommand(CanExecute =
        nameof(CanPreviewLibraryExternalTool))]
    private void PreviewLibraryExternalTool()
    {
        if (_externalTools is null)
            return;
        ExternalToolPlan plan = _externalTools.Preview(
            ExternalToolEditor.CreateDefinition(),
            ResolveOperationPaths());
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
                L("Library.Tools.ApplicationDefault"),
                invocation.SourcePaths.Count));
        }
        int blockers = plan.Issues.Count(issue =>
            issue.Severity == OperationIssueSeverity.Blocker);
        if (blockers > 0)
            SetCountOperationStatus(
                "Library.Operation.ExternalTool.Blockers",
                blockers);
        else
            SetCountOperationStatus(
                "Library.Operation.ExternalTool.Ready",
                plan.Invocations.Count);
        RunLibraryExternalToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    [RelayCommand(CanExecute =
        nameof(CanRunLibraryExternalTool))]
    private async Task RunLibraryExternalToolAsync()
    {
        if (_externalTools is null || _externalToolPlan is null ||
            _dialogs is null)
            return;
        if (!await _dialogs.ConfirmAsync(
                L("Library.Dialog.ExternalTool.Title"),
                LF(
                    "Library.Dialog.ExternalTool.Message",
                    _externalToolPlan.Definition.Name,
                    _externalToolPlan.Invocations.Count),
                L("Common.Run")))
            return;
        BeginLibraryOperation(
            "Library.Progress.RunningExternalTool",
            _externalToolPlan.Definition.Name);
        try
        {
            ExternalToolRunResult result =
                await _externalTools.RunAsync(
                    _externalToolPlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _externalToolPlan = null;
            SetOperationStatus(
                "Library.Operation.ExternalTool.Complete",
                result.SucceededCount,
                result.FailedCount);
            RunLibraryExternalToolCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.ExternalTool.Cancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.ExternalTool.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanFindLibraryReleaseArtwork))]
    private async Task FindLibraryReleaseArtworkAsync()
    {
        if (_coverArt is null || SelectedRelease is null)
            return;
        BeginLibraryOperation(
            "Library.Progress.FindingCoverArt");
        try
        {
            IProgress<OperationProgress> progress = CreateOperationProgress();
            CoverArtArchiveResult result =
                await _coverArt.GetReleaseArtworkAsync(
                    SelectedRelease.ReleaseId,
                    progress,
                    _operationCancellation!.Token);
            ArtworkMatches.Clear();
            foreach (CoverArtArchiveCandidate candidate in result.Images)
                ArtworkMatches.Add(new(
                    candidate,
                    _localization));
            var thumbnailDiagnostics =
                new List<string>();
            for (int index = 0; index < ArtworkMatches.Count; index++)
            {
                _operationCancellation.Token.ThrowIfCancellationRequested();
                CoverArtCandidateRow row = ArtworkMatches[index];
                progress.Report(new(
                    OperationPhase.Planning,
                    index,
                    ArtworkMatches.Count,
                    Message: LF(
                        "Library.Progress.LoadingArtworkThumbnail",
                        index + 1,
                        ArtworkMatches.Count)));
                try
                {
                    CoverArtDownload download =
                        await _coverArt.DownloadAsync(
                            row.Candidate,
                            thumbnail: true,
                            ct: _operationCancellation.Token);
                    row.ThumbnailSource =
                        await _thumbnails.CreateImageSourceAsync(
                            download.Data, 180, _operationCancellation.Token);
                    if (download.FromCache)
                        row.SetThumbnailStatus(
                            "Library.Artwork.Cached");
                    else
                        row.SetCountStatus(
                            "Library.Artwork.Bytes",
                            download.Data.Length);
                }
                catch (Exception error) when (
                    error is not OperationCanceledException)
                {
                    row.SetThumbnailStatus(
                        "Library.Artwork.ThumbnailFailed");
                    row.ThumbnailDiagnosticDetail =
                        error.Message;
                    thumbnailDiagnostics.Add(
                        error.Message);
                }
            }
            SelectedArtworkMatch = ArtworkMatches.FirstOrDefault(row =>
                row.Candidate.IsFront) ?? ArtworkMatches.FirstOrDefault();
            if (ArtworkMatches.Count == 0)
                SetOperationStatus(
                    "Library.Operation.CoverArt.None");
            else
                SetCountOperationStatus(
                    "Library.Operation.CoverArt.Loaded",
                    ArtworkMatches.Count);
            if (thumbnailDiagnostics.Count > 0)
                OperationDiagnosticDetail =
                    string.Join(
                        Environment.NewLine,
                        thumbnailDiagnostics);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.CoverArt.Cancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.CoverArt.Failed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryReleaseArtwork))]
    private async Task PreviewLibraryReleaseArtworkAsync()
    {
        if (_metadataOperations is null ||
            _coverArt is null ||
            SelectedArtworkMatch is null ||
            SelectedRelease is null)
            return;
        string[] paths = ConfirmedReleasePaths();
        CoverArtCandidateRow selected = SelectedArtworkMatch;
        string releaseTitle = SelectedRelease.Title;
        BeginLibraryOperation(
            "Library.Progress.BuildingArtworkPreview");
        try
        {
            IProgress<OperationProgress> progress = CreateOperationProgress();
            CoverArtDownload download = await _coverArt.DownloadAsync(
                selected.Candidate,
                thumbnail: false,
                progress,
                _operationCancellation!.Token);
            var image = new ArtworkInput(
                MusicFileUtilities.ID3v2Util.APICType.FrontCover,
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
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewArtworkEditsAsync(
                    edits,
                    LF(
                        "Library.OperationName.CoverArt",
                        releaseTitle),
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            SetOperationStatus(
                "Library.Operation.Artwork.ReleaseReady");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Artwork.PreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Artwork.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLocalLibraryArtwork))]
    private async Task PreviewLocalLibraryArtworkAsync()
    {
        if (_files is null)
            return;
        string? artworkPath = await _files.PickFileAsync(
            L("Library.Picker.FrontCover"),
            [new(L("Library.Picker.ArtworkImages"),
                [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"])]);
        if (artworkPath is null)
            return;
        await PreviewLibraryArtworkEditAsync(
            async (progress, ct) =>
            {
                progress.Report(new(
                    OperationPhase.Planning,
                    0,
                    1,
                    artworkPath,
                    LF(
                        "Library.Progress.ReadingFile",
                        Path.GetFileName(artworkPath))));
                byte[] data =
                    await File.ReadAllBytesAsync(artworkPath, ct);
                return new(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    new(
                        MusicFileUtilities.ID3v2Util.APICType.FrontCover,
                        MimeTypeFromPath(artworkPath),
                        data,
                        Path.GetFileNameWithoutExtension(artworkPath)));
            },
            L("Library.OperationName.ReplaceFrontCover"));
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryArtwork))]
    private async Task PreviewRemoveLibraryFrontCoverAsync() =>
        await PreviewLibraryArtworkEditAsync(
            (_, _) => Task.FromResult(new ArtworkValueEdit(
                ArtworkValueEditMode.RemoveFrontCover)),
            L("Library.OperationName.RemoveFrontCover"));

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryArtwork))]
    private async Task PreviewRemoveAllLibraryArtworkAsync() =>
        await PreviewLibraryArtworkEditAsync(
            (_, _) => Task.FromResult(new ArtworkValueEdit(
                ArtworkValueEditMode.RemoveAll)),
            L("Library.OperationName.RemoveAllArtwork"));

    private async Task PreviewLibraryArtworkEditAsync(
        Func<IProgress<OperationProgress>, CancellationToken,
            Task<ArtworkValueEdit>> createEdit,
        string name)
    {
        if (_metadataOperations is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
            return;
        BeginLibraryOperation(
            "Library.Progress.BuildingArtworkPreview");
        try
        {
            IProgress<OperationProgress> progress =
                CreateOperationProgress();
            ArtworkValueEdit edit = await createEdit(
                progress, _operationCancellation!.Token);
            var edits = paths.ToDictionary(
                path => path,
                _ => edit,
                PathComparer);
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewArtworkEditsAsync(
                    edits,
                    name,
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue =>
                    issue.Severity == OperationIssueSeverity.Blocker);
            if (blockers > 0)
                SetCountOperationStatus(
                    "Library.Operation.Artwork.Blockers",
                    blockers);
            else
                SetOperationStatus(
                    "Library.Operation.Artwork.Ready",
                    plan.ChangedFileCount,
                    paths.Length);
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.Artwork.PreviewCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.Artwork.PreviewFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuildLibraryReleaseMapping))]
    private async Task BuildLibraryReleaseMappingAsync()
    {
        if (_releaseMapping is null || SelectedRelease is null)
            return;
        string[] paths = ResolveOperationPaths();
        if (paths.Length == 0)
            return;
        BeginLibraryOperation(
            "Library.Progress.MatchingReleaseTracks");
        try
        {
            MusicBrainzReleaseCandidate release =
                await EnsureSelectedReleaseDetailsAsync(
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            var rows = _allRows.ToDictionary(row => row.Path, PathComparer);
            MusicBrainzSourceFile[] sources = paths.Select(path =>
            {
                rows.TryGetValue(path, out LibraryRow? row);
                double? discoveredDuration = AudioMatches
                    .Where(match => PathComparer.Equals(match.Path, path))
                    .Select(match => match.DurationSeconds)
                    .FirstOrDefault(value => value is not null);
                return new MusicBrainzSourceFile(
                    path,
                    ConfirmedRecordingIds(path),
                    row?.Record.Title,
                    row?.Record.Artist,
                    row?.Record.DiscNumber,
                    row?.Record.TrackNumber,
                    discoveredDuration is not null
                        ? TimeSpan.FromSeconds(discoveredDuration.Value)
                        : row?.Record.DurationInSeconds is > 0
                            ? TimeSpan.FromSeconds(row.Record.DurationInSeconds)
                            : null,
                    ConfirmedRecordingScores(path),
                    row?.Record.Album,
                    row?.Record.AlbumArtist);
            }).ToArray();
            MusicBrainzReleaseMapping mapping =
                await _releaseMapping.MapAsync(
                    release,
                    sources,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            ClearReleaseTrackMappings();
            foreach (MusicBrainzTrackMatch match in mapping.Files)
            {
                var mappingRow = new MusicBrainzTrackMappingRow(
                    match,
                    _localization);
                mappingRow.PropertyChanged += OnReleaseMappingChanged;
                ReleaseTrackMappings.Add(mappingRow);
            }
            SetOperationStatus(
                "Library.Operation.MusicBrainz.MappingComplete",
                mapping.SuggestedCount,
                mapping.Files.Length,
                mapping.AmbiguousCount);
        }
        catch (OperationCanceledException)
        {
            SetOperationStatus(
                "Library.Operation.MusicBrainz.MappingCancelled");
        }
        catch (Exception error)
        {
            SetOperationFailure(
                "Library.Operation.MusicBrainz.MappingFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
            PreviewLibraryReleaseMetadataCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryReleaseMetadata))]
    private async Task PreviewLibraryReleaseMetadataAsync()
    {
        if (_metadataOperations is null ||
            _releaseMapping is null ||
            SelectedRelease is null)
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
        BeginLibraryOperation(
            "Library.Progress.BuildingMusicBrainzMetadataPreview");
        try
        {
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    edits,
                    LF(
                        "Library.OperationName.MusicBrainzMetadata",
                        SelectedRelease.Title),
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan,
                _localization);
            HasApplicableOperationPreview = plan.CanApply;
            SetOperationStatus(
                "Library.Operation.MusicBrainz.MetadataReady");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            SetOperationStatus(
                "Library.Operation.MusicBrainz.MetadataCancelled");
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            SetOperationFailure(
                "Library.Operation.MusicBrainz.MetadataFailed",
                error.Message);
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand]
    private void CancelLibraryOperation()
    {
        _operationCancellation?.Cancel();
        _loadCancellation?.Cancel();
    }

    private void BeginLibraryOperation(
        string messageKey,
        params object?[] arguments)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new();
        OperationProgressText = LF(
            messageKey,
            arguments);
        OperationProgressValue = 0;
        OperationProgressMaximum = 1;
        IsOperationProgressIndeterminate = true;
        IsOperationBusy = true;
    }

    private void EndLibraryOperation()
    {
        IsOperationBusy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        IsOperationProgressIndeterminate = true;
        OperationProgressValue = 0;
        OperationProgressMaximum = 1;
        OperationProgressText = "";
    }

    private IProgress<OperationProgress> CreateOperationProgress() =>
        new Progress<OperationProgress>(progress =>
        {
            if (progress.Total is > 0)
            {
                IsOperationProgressIndeterminate = false;
                OperationProgressMaximum = progress.Total.Value;
                OperationProgressValue = Math.Clamp(
                    progress.Completed, 0, progress.Total.Value);
            }
            else
            {
                IsOperationProgressIndeterminate = true;
            }
            if (!string.IsNullOrWhiteSpace(progress.Message))
                OperationProgressText = progress.Message;
        });

    private string[] ResolveOperationPaths()
    {
        IEnumerable<string> paths = SelectedOperationScope switch
        {
            LibraryOperationScope.SelectedTracks => _selectedPaths,
            LibraryOperationScope.SelectedAlbums => ResolveSelectedAlbumPaths(),
            LibraryOperationScope.VisibleFilteredResults =>
                Rows.Select(row => row.Path),
            LibraryOperationScope.CompleteLibrary =>
                _allRows.Select(row => row.Path),
            _ => [],
        };
        return paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
    }

    private IEnumerable<string> ResolveSelectedAlbumPaths()
    {
        if (_selectedPaths.Count == 0)
            return [];
        var selected = _selectedPaths.ToHashSet(PathComparer);
        var albums = _allRows.Where(row => selected.Contains(row.Path))
            .Select(row => (row.AlbumArtist, row.Album))
            .ToHashSet();
        return _allRows.Where(row => albums.Contains((row.AlbumArtist, row.Album)))
            .Select(row => row.Path);
    }

    private static string[] CachedMetadataValues(
        TrackRecord record,
        TagFields field)
    {
        string[]? values = record.Metadata
            .FirstOrDefault(pair =>
                pair.Key.Equals(
                    field.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            .Value;
        if (values is not null)
            return values;
        string? fallback = field switch
        {
            TagFields.Title => record.Title,
            TagFields.Artist => record.Artist,
            TagFields.AlbumArtist => record.AlbumArtist,
            TagFields.Album => record.Album,
            TagFields.Genre => record.Genre,
            TagFields.Composer => record.Composer,
            TagFields.Grouping => record.Grouping,
            TagFields.Date => record.ReleaseDate,
            TagFields.TrackNumber =>
                record.TrackNumber?.ToString(),
            TagFields.TotalTracks =>
                record.TrackTotal?.ToString(),
            TagFields.DiscNumber =>
                record.DiscNumber?.ToString(),
            TagFields.TotalDiscs =>
                record.DiscTotal?.ToString(),
            _ => null,
        };
        return string.IsNullOrEmpty(fallback)
            ? []
            : [fallback];
    }

    private void InvalidateLibraryOperationPreview()
    {
        ScheduleRepresentativePreview();
        if (_libraryOperationPlan is null && OperationPreviewChanges.Count == 0)
            return;
        _libraryOperationPlan = null;
        OperationPreviewChanges.Clear();
        HasApplicableOperationPreview = false;
        SetOperationStatus(
            "Library.Operation.PreviewInvalidated");
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void RebuildPendingChanges()
    {
        PendingChanges.Clear();
        foreach (MetadataPreviewRow row in
                 OperationPreviewChanges)
            PendingChanges.Add(row);
        foreach (MetadataPreviewRow row in
                 _inspector.CreatePendingChangeRows())
            PendingChanges.Add(row);
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(HasNoPendingChanges));
        RevertPendingChangesCommand
            .NotifyCanExecuteChanged();
    }

    private void ScheduleRepresentativePreview()
    {
        if (RepresentativePreview is null)
            return;
        string? path = ResolveOperationPaths()
            .FirstOrDefault();
        RepresentativePreview.Schedule(
            path,
            () => OperationEditor.CreateRecipe(
                L(
                    "Library.OperationName.RepresentativePreview")));
    }

    private string? FileOperationPreflightMessage()
    {
        if (_inspector.HasUnsavedChanges ||
            _libraryOperationPlan is not null)
            return L(
                "Library.FileOperation.MetadataEditsPending");
        return null;
    }

    partial void OnSelectedOperationScopeChanged(LibraryOperationScope value)
    {
        FileOperations?.InvalidateTargets();
        InvalidateLibraryOperationPreview();
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        NotifyHistoryCommands();
    }

    partial void OnRowsChanged(IReadOnlyList<LibraryRow> value)
    {
        FileOperations?.InvalidateTargets();
        ScheduleRepresentativePreview();
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        PreviewLibraryOperationCommand.NotifyCanExecuteChanged();
        ImportLibraryDelimitedMetadataCommand.NotifyCanExecuteChanged();
        OpenOperationsCommand.NotifyCanExecuteChanged();
        EditVisibleInWorkbenchCommand.NotifyCanExecuteChanged();
        EditAllInWorkbenchCommand.NotifyCanExecuteChanged();
        DiscoverLibraryAudioCommand.NotifyCanExecuteChanged();
        PreviewLocalLibraryArtworkCommand.NotifyCanExecuteChanged();
        PreviewRemoveLibraryFrontCoverCommand.NotifyCanExecuteChanged();
        PreviewRemoveAllLibraryArtworkCommand.NotifyCanExecuteChanged();
        NotifyHistoryCommands();
    }

    private bool CanPreviewLibraryOperation() =>
        !IsBusy && !IsOperationBusy && _metadataOperations is not null &&
        OperationEditor.CanCreate && ResolveOperationPaths().Length > 0;

    private bool CanImportLibraryDelimitedMetadata() =>
        !IsBusy && !IsOperationBusy &&
        _metadataOperations is not null &&
        _delimitedImports is not null &&
        _files is not null &&
        ResolveOperationPaths().Length > 0;

    private bool CanCopyLibraryMetadataField() =>
        !IsBusy && !IsOperationBusy &&
        _platform is not null &&
        OperationEditor.SelectedField is not null &&
        ResolveOperationPaths().Length > 0;

    private bool CanPasteLibraryMetadataField() =>
        !IsBusy && !IsOperationBusy &&
        _platform is not null &&
        _metadataOperations is not null &&
        OperationEditor.SelectedField is not null &&
        ResolveOperationPaths().Length > 0;

    private bool CanApplyLibraryOperation() =>
        !IsOperationBusy &&
        (_libraryOperationPlan is not null &&
             HasApplicableOperationPreview ||
         _inspector.HasUnsavedChanges);

    private bool CanUndoLibraryOperation() =>
        _history?.CanUndo == true &&
        _dialogs is not null &&
        !IsOperationBusy;

    private bool CanRedoLibraryOperation() =>
        _history?.CanRedo == true &&
        _metadataOperations is not null &&
        !IsOperationBusy;

    private bool CanRepeatLibraryRecipe() =>
        _history?.Entries.FirstOrDefault()?.Recipe is not null &&
        _metadataOperations is not null &&
        ResolveOperationPaths().Length > 0 &&
        !IsOperationBusy;

    private void NotifyHistoryCommands()
    {
        OnPropertyChanged(
            nameof(CanUndoLatestOperation));
        OnPropertyChanged(
            nameof(CanRedoLatestOperation));
        OnPropertyChanged(
            nameof(CanRepeatLatestRecipe));
        UndoLibraryOperationCommand
            .NotifyCanExecuteChanged();
        RedoLibraryOperationCommand
            .NotifyCanExecuteChanged();
        RepeatLibraryRecipeCommand
            .NotifyCanExecuteChanged();
    }

    private bool CanDiscoverLibraryAudio() =>
        !IsBusy && !IsOperationBusy && _audioDiscovery is not null &&
        ResolveOperationPaths().Length > 0;

    private bool CanPreviewLibraryAudioIdentifiers() =>
        !IsBusy && !IsOperationBusy && _metadataOperations is not null &&
        SelectedAudioMatch?.AcoustId is not null &&
        !string.IsNullOrWhiteSpace(SelectedAudioMatch.Fingerprint);

    private bool CanResolveLibraryRecording() =>
        !IsBusy && !IsOperationBusy && _musicBrainz is not null &&
        SelectedAudioMatch?.MusicBrainzRecordingIdValues.Length == 1;
    private bool CanSearchLibraryMusicBrainzReleases() =>
        !IsBusy && !IsOperationBusy && _musicBrainz is not null &&
        ReleaseSearch.HasCriteria;
    private bool CanSearchLibraryDiscogsReleases() =>
        !IsBusy && !IsOperationBusy && _discogs is not null &&
        DiscogsSearch.HasCriteria;
    private bool CanLoadLibraryDiscogsReleaseDetails() =>
        !IsBusy && !IsOperationBusy && _discogs is not null &&
        SelectedDiscogsRelease is not null;
    private bool CanBuildLibraryDiscogsReleaseMapping() =>
        !IsBusy && !IsOperationBusy && _discogsMapping is not null &&
        SelectedDiscogsRelease is not null &&
        ResolveOperationPaths().Length > 0;
    private bool CanPreviewLibraryDiscogsReleaseMetadata() =>
        !IsBusy && !IsOperationBusy &&
        _metadataOperations is not null &&
        _discogsMapping is not null &&
        SelectedDiscogsRelease is not null &&
        DiscogsImport.HasSelection &&
        DiscogsTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanPreviewLibraryDiscogsReleaseArtwork() =>
        !IsBusy && !IsOperationBusy &&
        _metadataOperations is not null &&
        _discogs is not null &&
        SelectedDiscogsRelease?.Candidate.CoverImageUri is not null &&
        DiscogsTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanBrowseLibraryReportOutput() =>
        !IsBusy && !IsOperationBusy &&
        _reports is not null && _files is not null;
    private bool CanPreviewLibraryReport() =>
        !IsBusy && !IsOperationBusy && _reports is not null &&
        ResolveOperationPaths().Length > 0 &&
        ReportEditor.Fields.Count > 0 &&
        !string.IsNullOrWhiteSpace(ReportEditor.OutputPath);
    private bool CanApplyLibraryReport() =>
        !IsBusy && !IsOperationBusy && _reports is not null &&
        _reportPlan?.CanApply == true;
    private bool CanBrowseLibraryPlaylistOutput() =>
        !IsBusy && !IsOperationBusy &&
        _playlists is not null && _files is not null;
    private bool CanPreviewLibraryPlaylist() =>
        !IsBusy && !IsOperationBusy && _playlists is not null &&
        ResolveOperationPaths().Length > 0 &&
        !string.IsNullOrWhiteSpace(PlaylistEditor.OutputPath);
    private bool CanApplyLibraryPlaylist() =>
        !IsBusy && !IsOperationBusy && _playlists is not null &&
        _playlistPlan?.CanApply == true;
    private bool CanBrowseLibraryExternalToolExecutable() =>
        !IsBusy && !IsOperationBusy &&
        _externalTools is not null && _files is not null;
    private bool CanBrowseLibraryExternalToolWorkingDirectory() =>
        !IsBusy && !IsOperationBusy &&
        _externalTools is not null && _files is not null;
    private bool CanPreviewLibraryExternalTool() =>
        !IsBusy && !IsOperationBusy &&
        _externalTools is not null &&
        ResolveOperationPaths().Length > 0 &&
        !string.IsNullOrWhiteSpace(
            ExternalToolEditor.Executable);
    private bool CanRunLibraryExternalTool() =>
        !IsBusy && !IsOperationBusy &&
        _externalTools is not null && _dialogs is not null &&
        _externalToolPlan?.CanRun == true;
    private bool CanFindLibraryReleaseArtwork() =>
        !IsBusy && !IsOperationBusy && _coverArt is not null &&
        SelectedRelease is not null;
    private bool CanPreviewLibraryReleaseArtwork() =>
        !IsBusy && !IsOperationBusy &&
        _metadataOperations is not null &&
        _coverArt is not null &&
        SelectedRelease is not null &&
        SelectedArtworkMatch is not null &&
        ReleaseTrackMappings.Any(row =>
            row.IsIncluded && row.SelectedTrack is not null);
    private bool CanPreviewLocalLibraryArtwork() =>
        CanPreviewLibraryArtwork() && _files is not null;
    private bool CanPreviewLibraryArtwork() =>
        !IsBusy && !IsOperationBusy &&
        _metadataOperations is not null &&
        ResolveOperationPaths().Length > 0;
    private bool CanBuildLibraryReleaseMapping() =>
        !IsBusy && !IsOperationBusy && _releaseMapping is not null &&
        SelectedRelease is not null && ResolveOperationPaths().Length > 0;
    private bool CanPreviewLibraryReleaseMetadata() =>
        !IsBusy && !IsOperationBusy && _metadataOperations is not null &&
        _releaseMapping is not null && SelectedRelease is not null &&
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
        PreviewLibraryReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewLibraryReleaseArtworkCommand.NotifyCanExecuteChanged();
    }

    private void ClearDiscogsTrackMappings()
    {
        foreach (DiscogsTrackMappingRow row in DiscogsTrackMappings)
            row.PropertyChanged -= OnDiscogsMappingChanged;
        DiscogsTrackMappings.Clear();
        PreviewLibraryDiscogsReleaseMetadataCommand
            .NotifyCanExecuteChanged();
        PreviewLibraryDiscogsReleaseArtworkCommand
            .NotifyCanExecuteChanged();
    }

    private void InvalidateReportPlan()
    {
        if (_reportPlan is null && ReportOutputs.Count == 0)
        {
            PreviewLibraryReportCommand.NotifyCanExecuteChanged();
            return;
        }
        _reportPlan = null;
        ReportOutputs.Clear();
        PreviewLibraryReportCommand.NotifyCanExecuteChanged();
        ApplyLibraryReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void InvalidatePlaylistPlan()
    {
        if (_playlistPlan is null && PlaylistOutputs.Count == 0)
        {
            PreviewLibraryPlaylistCommand.NotifyCanExecuteChanged();
            return;
        }
        _playlistPlan = null;
        PlaylistOutputs.Clear();
        PreviewLibraryPlaylistCommand.NotifyCanExecuteChanged();
        ApplyLibraryPlaylistCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void InvalidateExternalToolPlan()
    {
        if (_externalToolPlan is null &&
            ExternalToolInvocations.Count == 0)
        {
            PreviewLibraryExternalToolCommand
                .NotifyCanExecuteChanged();
            return;
        }
        _externalToolPlan = null;
        ExternalToolInvocations.Clear();
        PreviewLibraryExternalToolCommand.NotifyCanExecuteChanged();
        RunLibraryExternalToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnDiscogsMappingChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        InvalidateLibraryOperationPreview();
        PreviewLibraryDiscogsReleaseMetadataCommand
            .NotifyCanExecuteChanged();
        PreviewLibraryDiscogsReleaseArtworkCommand
            .NotifyCanExecuteChanged();
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
        InvalidateLibraryOperationPreview();
        PreviewLibraryReleaseMetadataCommand.NotifyCanExecuteChanged();
        PreviewLibraryReleaseArtworkCommand.NotifyCanExecuteChanged();
    }

    private string[] ConfirmedReleasePaths() => ReleaseTrackMappings
        .Where(row => row.IsIncluded && row.SelectedTrack is not null)
        .Select(row => row.Path)
        .Distinct(PathComparer)
        .ToArray();

    private static string MimeTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private void OnReleaseImportChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        InvalidateLibraryOperationPreview();
        PreviewLibraryReleaseMetadataCommand.NotifyCanExecuteChanged();
    }

    private async Task<MusicBrainzReleaseCandidate>
        EnsureSelectedReleaseDetailsAsync(
            IProgress<OperationProgress> progress,
            CancellationToken ct)
    {
        MusicBrainzReleaseRow selected = SelectedRelease ??
            throw new InvalidOperationException(
                L(
                    "Library.Error.ChooseMusicBrainzRelease"));
        if (selected.Candidate.Tracks.Length > 0)
            return selected.Candidate;
        MusicBrainzReleaseCandidate detailed =
            await _musicBrainz!.GetReleaseAsync(
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
                L("Library.Error.DiscogsUnavailable"));
        DiscogsReleaseRow selected = SelectedDiscogsRelease ??
            throw new InvalidOperationException(
                L(
                    "Library.Error.ChooseDiscogsRelease"));
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

    public Task ApplyFilterNowAsync(CancellationToken cancellationToken = default)
        => ApplyFilterAsync(immediate: true, cancellationToken);

    public async Task ReindexAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
            await _reindex.ReindexFileAsync(path);
        await ReloadAsync();
    }

    /// <summary>Loads artwork only for a row that the virtualized table has realized.</summary>
    public async Task LoadThumbnailAsync(LibraryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        CancellationTokenSource cancellation;
        object? cached = null;
        bool hasCachedValue;
        lock (_thumbnailSync)
        {
            if (row.ThumbnailLoaded || _thumbnailLoads.ContainsKey(row))
                return;
            hasCachedValue = TryGetCachedThumbnail(row.Path, out cached);
            if (!hasCachedValue)
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailLifetime.Token);
                _thumbnailLoads[row] = cancellation;
            }
            else
            {
                cancellation = null!;
            }
        }

        if (hasCachedValue)
        {
            row.ThumbnailSource = cached;
            row.ThumbnailLoaded = true;
            return;
        }

        bool enteredGate = false;
        try
        {
            await _thumbnailGate.WaitAsync(cancellation.Token);
            enteredGate = true;
            byte[]? bytes = await _library.GetFirstImageAsync(row.Path, cancellation.Token);
            object? image = bytes is { Length: > 0 }
                ? await _thumbnails.CreateImageSourceAsync(bytes, 56, cancellation.Token)
                : null;
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_thumbnailSync)
            {
                if (!_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? active) ||
                    !ReferenceEquals(active, cancellation))
                    return;
                AddCachedThumbnail(row.Path, image);
            }
            row.ThumbnailSource = image;
            row.ThumbnailLoaded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // A malformed image should leave a blank thumbnail without affecting the library grid.
            if (!cancellation.IsCancellationRequested)
                row.ThumbnailLoaded = true;
        }
        finally
        {
            if (enteredGate)
                _thumbnailGate.Release();
            lock (_thumbnailSync)
            {
                if (_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? active) &&
                    ReferenceEquals(active, cancellation))
                    _thumbnailLoads.Remove(row);
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Stops work for a recycled row and releases its image reference.</summary>
    public void ReleaseThumbnail(LibraryRow row)
    {
        lock (_thumbnailSync)
        {
            if (_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? cancellation))
                cancellation.Cancel();
        }
        row.ThumbnailSource = null;
        row.ThumbnailLoaded = false;
    }

    private bool TryGetCachedThumbnail(string path, out object? image)
    {
        if (!_thumbnailCache.TryGetValue(path, out ThumbnailCacheItem? item))
        {
            image = null;
            return false;
        }
        _thumbnailLru.Remove(item.Node);
        _thumbnailLru.AddFirst(item.Node);
        image = item.Image;
        return true;
    }

    private void AddCachedThumbnail(string path, object? image)
    {
        if (_thumbnailCache.Remove(path, out ThumbnailCacheItem? old))
            _thumbnailLru.Remove(old.Node);
        var node = new LinkedListNode<string>(path);
        _thumbnailLru.AddFirst(node);
        _thumbnailCache[path] = new ThumbnailCacheItem(image, node);
        while (_thumbnailCache.Count > ThumbnailCacheLimit && _thumbnailLru.Last is { } last)
        {
            _thumbnailCache.Remove(last.Value);
            _thumbnailLru.RemoveLast();
        }
    }

    private void ResetThumbnails()
    {
        lock (_thumbnailSync)
        {
            _thumbnailLifetime.Cancel();
            _thumbnailLifetime.Dispose();
            _thumbnailLifetime = new CancellationTokenSource();
            foreach (CancellationTokenSource cancellation in _thumbnailLoads.Values)
                cancellation.Cancel();
            _thumbnailCache.Clear();
            _thumbnailLru.Clear();
        }
    }

    [RelayCommand]
    private void SaveView()
    {
        string name = NewViewName?.Trim() ?? "";
        if (name.Length == 0)
            return;
        var columns = Columns.Select((column, index) =>
            new LibraryColumnState(column.Key, null, index, column.IsVisible)).ToArray();
        SaveNamedView(name, columns, null);
    }

    /// <summary>
    /// Saves a named view using layout details supplied by a platform-specific grid. The original
    /// parameterless command remains available to XAML shells that only expose visibility choices.
    /// </summary>
    public void SaveNamedView(
        string name,
        IReadOnlyList<LibraryColumnState> columns,
        LibrarySortState? sort)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        var view = new LibraryViewDefinition(
            name,
            FilterText,
            FilterMode,
            columns,
            sort,
            VisualFilterExpression);
        LibraryViewDefinition? existing = SavedViews.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            SavedViews.Remove(existing);
        SavedViews.Add(view);
        SelectedView = view;
        NewViewName = null;
        PersistViews();
    }

    [RelayCommand]
    private void DeleteView()
    {
        if (SelectedView is null)
            return;
        SavedViews.Remove(SelectedView);
        SelectedView = null;
        PersistViews();
    }

    private void QueueFilter()
    {
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        _ = ApplyFilterAfterDelayAsync(cancellation);
    }

    private async Task ApplyFilterAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await ApplyFilterAsync(immediate: false, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
                _filterCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ApplyFilterAsync(bool immediate, CancellationToken cancellationToken = default)
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(FilterText, FilterMode);
        var visual = new LibraryVisualFilter(
            VisualFilterExpression);
        string? filterDiagnostic =
            query.Error ?? visual.Error;
        if (!query.IsValid || !visual.IsValid)
        {
            FilterError = L(
                "Library.Filter.Invalid");
            FilterDiagnosticDetail =
                filterDiagnostic;
            SetStatusFailure(
                "Library.Status.InvalidFilter",
                filterDiagnostic);
            return;
        }
        FilterError = null;
        FilterDiagnosticDetail = null;
        List<LibraryRow> source = _allRows;
        HashSet<string>? healthPaths = _healthFilterPaths.Count == 0
            ? null
            : new HashSet<string>(_healthFilterPaths, PathComparer);
        List<LibraryRow> filtered = await Task.Run(() => source
            .Where(row => (healthPaths is null || healthPaths.Contains(row.Path)) &&
                query.IsMatch(row.Details, row.SearchText) &&
                visual.IsMatch(row.Record))
            .ToList(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        int preservedSelectionCount = 0;
        if (_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
        {
            var includedPaths = filtered.Select(row => row.Path)
                .ToHashSet(PathComparer);
            LibraryRow[] preserved = source.Where(row =>
                _selectedPaths.Contains(row.Path, PathComparer) &&
                includedPaths.Add(row.Path)).ToArray();
            preservedSelectionCount = preserved.Length;
            filtered.AddRange(preserved);
        }

        SelectionContext? updatedSelection = null;
        if (!_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
        {
            var selectedPaths = _selectedPaths.ToHashSet(PathComparer);
            LibraryRow[] visibleSelection = filtered.Where(row => selectedPaths.Contains(row.Path)).ToArray();
            if (visibleSelection.Length != _selectedPaths.Count)
            {
                SetSelectedPaths(visibleSelection.Select(row => row.Path).ToArray());
                updatedSelection = new SelectionContext(
                    visibleSelection.Select(row => row.Path).ToArray(),
                    visibleSelection.Select(row => row.Record).ToArray());
            }
        }

        // Replace the view once. Raising one collection notification per cached track makes a
        // virtualized table spend seconds processing changes on the UI thread and also starves
        // live window layout while a large library is loading or being filtered.
        Rows = filtered;
        if (healthPaths is not null)
            SetStatusText(
                preservedSelectionCount > 0
                    ? "Library.Status.HealthFilteredPreserved"
                    : "Library.Status.HealthFiltered",
                filtered.Count,
                source.Count,
                preservedSelectionCount);
        else if (filtered.Count == source.Count)
            SetCountStatusText(
                preservedSelectionCount > 0
                    ? "Library.Status.TracksPreserved"
                    : "Library.Status.Tracks",
                source.Count,
                preservedSelectionCount);
        else
            SetStatusText(
                preservedSelectionCount > 0
                    ? "Library.Status.FilteredPreserved"
                    : "Library.Status.Filtered",
                filtered.Count,
                source.Count,
                preservedSelectionCount);
        PageState = source.Count == 0
            ? LibraryPageState.NotIndexed
            : filtered.Count > 0
                ? LibraryPageState.Ready
                : HasTextFilter || HasVisualFilter
                    ? LibraryPageState.FilteredToZero
                    : healthPaths is not null
                        ? LibraryPageState.NoResults
                        : LibraryPageState.NotIndexed;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultCountText));
        if (updatedSelection is not null)
            await _inspector.LoadAsync(updatedSelection);
    }

    private void LoadViews()
    {
        try
        {
            string? json = _settings.GetLibraryPreference(ViewsPreference);
            foreach (LibraryViewDefinition view in string.IsNullOrWhiteSpace(json)
                         ? []
                         : JsonSerializer.Deserialize<List<LibraryViewDefinition>>(json) ?? [])
                SavedViews.Add(view);
        }
        catch
        {
        }
    }

    private void PersistViews()
        => _settings.SetLibraryPreference(ViewsPreference, JsonSerializer.Serialize(SavedViews));

    private void LoadWorkspace()
    {
        _loadingWorkspace = true;
        try
        {
            FilterText = null;
            FilterMode = FilterMode.Substring;
            VisualFilterExpression = null;
            VisualFilterEditor.Load(null);
            InspectorPreference =
                LibraryInspectorPreference.Auto;
            IsInspectorOpen = true;
            string? json = _settings.GetLibraryPreference(WorkspacePreference);
            if (string.IsNullOrWhiteSpace(json))
                return;
            var state = JsonSerializer.Deserialize<LibraryWorkspaceSnapshot>(json);
            if (state is not null)
            {
                FilterText = state.Filter;
                FilterMode = state.Mode;
                VisualFilterExpression = state.VisualFilter;
                VisualFilterEditor.Load(state.VisualFilter);
                InspectorPreference =
                    state.InspectorPreference ??
                    (state.InspectorOpen switch
                    {
                        true =>
                            LibraryInspectorPreference.Pinned,
                        false =>
                            LibraryInspectorPreference.Closed,
                        _ =>
                            LibraryInspectorPreference.Auto,
                    });
                IsInspectorOpen =
                    InspectorPreference !=
                    LibraryInspectorPreference.Closed;
                OnPropertyChanged(
                    nameof(InspectorPreference));
            }
        }
        catch
        {
        }
        finally
        {
            _loadingWorkspace = false;
        }
    }

    private void SaveWorkspace()
        => _settings.SetLibraryPreference(WorkspacePreference,
            JsonSerializer.Serialize(new LibraryWorkspaceSnapshot(
                FilterText,
                FilterMode,
                IsInspectorOpen,
                VisualFilterExpression,
                InspectorPreference)));

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

    private void SetStatusText(
        string key,
        params object?[] arguments)
    {
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = null;
        StatusText = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatusText(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = count;
        StatusText = LC(
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
        SetStatusText(key, arguments);
        StatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void SetOperationStatus(
        string key,
        params object?[] arguments)
    {
        _operationStatusKey = key;
        _operationStatusArguments = arguments;
        _operationStatusCount = null;
        OperationStatus = LF(key, arguments);
        OperationDiagnosticDetail = null;
    }

    private void SetCountOperationStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _operationStatusKey = key;
        _operationStatusArguments = arguments;
        _operationStatusCount = count;
        OperationStatus = LC(
            key,
            count,
            arguments);
        OperationDiagnosticDetail = null;
    }

    private void SetOperationFailure(
        string key,
        string? diagnosticDetail,
        params object?[] arguments)
    {
        SetOperationStatus(key, arguments);
        OperationDiagnosticDetail =
            diagnosticDetail;
    }

    private void SetVisualFilterStatus(
        string key,
        string? diagnosticDetail = null)
    {
        _visualFilterStatusKey = key;
        VisualFilterEditor.Status = L(key);
        VisualFilterDiagnosticDetail =
            diagnosticDetail;
    }

    private void RefreshLocalizedChoices()
    {
        RefreshChoices(
            FilterModeChoices,
            FilterModes,
            "Library.Choice.FilterMode");
        RefreshChoices(
            OperationScopeChoices,
            OperationScopes,
            "Library.Choice.OperationScope");
        RefreshChoices(
            ImportEmptyCellModeChoices,
            ImportEmptyCellModes,
            "Library.Choice.ImportEmptyCellMode");
        foreach (LibraryColumnChoice column in Columns)
            column.RefreshLocalizedText(L);
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>>
            target,
        IEnumerable<T> values,
        string keyPrefix)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default
                        .Equals(
                            item.Value,
                            value));
            string label = L(
                $"{keyPrefix}.{value}");
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
            StatusText =
                _statusTextCount is { } statusCount
                    ? LC(
                        _statusTextKey,
                        statusCount,
                        _statusTextArguments)
                    : LF(
                        _statusTextKey,
                        _statusTextArguments);
        if (_operationStatusKey is not null)
            OperationStatus =
                _operationStatusCount is { } operationCount
                    ? LC(
                        _operationStatusKey,
                        operationCount,
                        _operationStatusArguments)
                    : LF(
                        _operationStatusKey,
                        _operationStatusArguments);
        if (_visualFilterStatusKey is not null)
            VisualFilterEditor.Status = L(
                _visualFilterStatusKey);
        if (FilterError is not null)
            FilterError = L(
                "Library.Filter.Invalid");
        OnPropertyChanged(
            nameof(HealthFilterSummary));
        OnPropertyChanged(
            nameof(ResultCountText));
        OnPropertyChanged(
            nameof(EmptyStateTitle));
        OnPropertyChanged(
            nameof(EmptyStateMessage));
        OnPropertyChanged(
            nameof(EmptyStateActionLabel));
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
        if (_libraryOperationPlan is not null)
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                _libraryOperationPlan,
                _localization);
    }

    private static string ColumnResourceKey(
        string key) =>
        key switch
        {
            "Type" => "Column.CodecType",
            _ => $"Column.{key}",
        };

    private sealed record LibraryWorkspaceSnapshot(
        string? Filter,
        FilterMode Mode,
        bool? InspectorOpen = null,
        LibraryVisualFilterNode? VisualFilter = null,
        LibraryInspectorPreference? InspectorPreference = null);
    private sealed record ThumbnailCacheItem(object? Image, LinkedListNode<string> Node);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
