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
    private MetadataOperationPlan? _libraryOperationPlan;
    private ReportExportPlan? _reportPlan;
    private PlaylistWorkspacePlan? _playlistPlan;
    private ExternalToolPlan? _externalToolPlan;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly object _thumbnailSync = new();
    private readonly Dictionary<LibraryRow, CancellationTokenSource> _thumbnailLoads = [];
    private readonly Dictionary<string, ThumbnailCacheItem> _thumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailLru = [];
    private CancellationTokenSource _thumbnailLifetime = new();
    private const int ThumbnailCacheLimit = 256;
    private List<LibraryRow> _allRows = [];
    private HashSet<string> _healthFilterPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _selectedPaths = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _loadingWorkspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInWorkbenchCommand))]
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
    [NotifyPropertyChangedFor(nameof(HasVisualFilter))]
    private LibraryVisualFilterNode? _visualFilterExpression;

    [ObservableProperty]
    private string _statusText = "Load a configuration to browse your library.";

    [ObservableProperty]
    private string? _newViewName;

    [ObservableProperty]
    private LibraryViewDefinition? _selectedView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(ResultCountText))]
    private IReadOnlyList<LibraryRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    [NotifyPropertyChangedFor(nameof(EmptyStateActionLabel))]
    private LibraryPageState _pageState = LibraryPageState.NoConfiguration;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

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
        "Choose an operation and scope, then preview authoritative metadata from disk.";

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
        IPlatformService? platform = null)
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
        OperationEditor = new(
            operationCatalog ?? new MetadataOperationCatalog(),
            MetadataOperationSurface.Library,
            recipeStore);
        ColumnEditor = new(
            metadataColumns,
            MetadataGridSurface.Library);
        VisualFilterEditor = new();
        OperationEditor.PropertyChanged += (_, _) =>
        {
            InvalidateLibraryOperationPreview();
            CopyLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
            PasteLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        };
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
        ReportEditor.Changed += InvalidateReportPlan;
        PlaylistEditor.Changed += InvalidatePlaylistPlan;
        ExternalToolEditor = new(externalToolStore);
        ExternalToolEditor.Changed += InvalidateExternalToolPlan;
        Indexing = indexing;
        foreach (DetailsColumn column in DetailsColumns.All)
            Columns.Add(new LibraryColumnChoice(column.Key, column.Header, DetailsColumns.DefaultVisible.Contains(column.Key)));
        LoadViews();
        LoadWorkspace();
        settings.ConfigurationChanged += OnConfigurationChanged;
        indexing.IndexCompleted += () => _ = ReloadAsync();
        inspector.FilesChanged += () => _ = ReloadAsync();
        inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SelectionInspectorViewModel.HasUnsavedChanges))
                OnPropertyChanged(nameof(HasUnsavedSelectionChanges));
        };
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
    public ObservableCollection<MetadataPreviewRow> OperationPreviewChanges { get; } = [];
    public MusicBrainzImportSelectionViewModel ReleaseImport { get; } = new();
    public MusicBrainzReleaseSearchViewModel ReleaseSearch { get; } = new();
    public DiscogsReleaseSearchViewModel DiscogsSearch { get; } = new();
    public DiscogsImportSelectionViewModel DiscogsImport { get; } = new();
    public ReportEditorViewModel ReportEditor { get; } = new();
    public PlaylistEditorViewModel PlaylistEditor { get; } = new();
    public ExternalToolEditorViewModel ExternalToolEditor { get; }
    public MetadataGridColumnEditorViewModel ColumnEditor { get; }
    public VisualFilterEditorViewModel VisualFilterEditor { get; }
    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();
    public IReadOnlyList<LibraryOperationScope> OperationScopes { get; } =
        Enum.GetValues<LibraryOperationScope>();
    public MetadataOperationEditorViewModel OperationEditor { get; }
    public SelectionInspectorViewModel Inspector => _inspector;
    public IndexingViewModel Indexing { get; }
    public int TotalCount => _allRows.Count;
    public int HealthFilterCount => _healthFilterPaths.Count;
    public bool HasHealthFilter => _healthFilterPaths.Count > 0;
    public string HealthFilterSummary => $"Health results: {HealthFilterCount:N0} track(s)";
    public bool HasTextFilter => !string.IsNullOrWhiteSpace(FilterText);
    public bool HasVisualFilter => VisualFilterExpression is not null;
    public bool HasRows => Rows.Count > 0;
    public bool HasEmptyState => Rows.Count == 0 && PageState != LibraryPageState.Loading;
    public bool HasFilterError => !string.IsNullOrWhiteSpace(FilterError);
    public string ResultCountText => Rows.Count == TotalCount
        ? $"{Rows.Count:N0} tracks"
        : $"{Rows.Count:N0} of {TotalCount:N0}";
    public IReadOnlyList<string> SelectedPaths => _selectedPaths;
    public bool HasUnsavedSelectionChanges => Inspector.HasUnsavedChanges;
    public bool HasUnsavedChanges =>
        HasUnsavedSelectionChanges ||
        _libraryOperationPlan is not null ||
        _reportPlan is not null ||
        _playlistPlan is not null ||
        _externalToolPlan is not null;
    public event Action? HealthFilterClearRequested;

    partial void OnSelectedReleaseChanged(MusicBrainzReleaseRow? value)
    {
        ClearReleaseTrackMappings();
        ArtworkMatches.Clear();
        SelectedArtworkMatch = null;
        BuildLibraryReleaseMappingCommand.NotifyCanExecuteChanged();
    }
    public string EmptyStateTitle => PageState switch
    {
        LibraryPageState.NoConfiguration => "Choose a library configuration",
        LibraryPageState.NotIndexed => "This library has not been indexed",
        LibraryPageState.FilteredToZero => "No tracks match this filter",
        LibraryPageState.NoResults => "No tracks match the Health results",
        LibraryPageState.Error => "The library could not be loaded",
        _ => "No tracks to show",
    };
    public string EmptyStateMessage => PageState switch
    {
        LibraryPageState.NoConfiguration => "Open Settings to choose or create a configuration before browsing.",
        LibraryPageState.NotIndexed => "Index the configured music roots to populate the cached library.",
        LibraryPageState.FilteredToZero => "Clear or revise the filter to show tracks again.",
        LibraryPageState.NoResults => "Clear the Health filter to return to the full library.",
        LibraryPageState.Error => StatusText,
        _ => "Adjust this view or reload the library.",
    };
    public string EmptyStateActionLabel => PageState switch
    {
        LibraryPageState.NoConfiguration => "Open Settings",
        LibraryPageState.NotIndexed => "Index library",
        LibraryPageState.FilteredToZero => "Clear filter",
        LibraryPageState.NoResults => "Clear Health filter",
        LibraryPageState.Error => "Try again",
        _ => "Reload",
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            VisualFilterEditor.Status = error;
            return;
        }
        var compiled = new LibraryVisualFilter(expression);
        if (!compiled.IsValid)
        {
            VisualFilterEditor.Status =
                compiled.Error ?? "Invalid visual filter.";
            return;
        }
        VisualFilterExpression = expression;
        VisualFilterEditor.Status =
            expression is null
                ? "No visual filter is active."
                : "Visual filter applied.";
        SaveWorkspace();
        await ApplyFilterAsync(immediate: true);
    }

    [RelayCommand]
    private async Task ClearVisualFilterAsync()
    {
        VisualFilterExpression = null;
        VisualFilterEditor.Load(null);
        VisualFilterEditor.Status = "Visual filter cleared.";
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
            StatusText = "Choose a library configuration in Settings.";
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(ResultCountText));
            return;
        }
        IsBusy = true;
        PageState = LibraryPageState.Loading;
        StatusText = "Loading the cached library…";
        try
        {
            var records = await _library.GetAllRecordsAsync(cancellation.Token);
            var rows = await Task.Run(() => records.Select(record => new LibraryRow(record)).ToList(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
            {
                var loadedPaths = rows.Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.AddRange(_allRows.Where(row =>
                    _selectedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase) &&
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
            StatusText = $"Could not load the cached library: {error.Message}";
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
        if ((_libraryOperationPlan is null && _reportPlan is null) ||
            _dialogs is null)
            return true;
        return await _dialogs.ConfirmAsync(
            "Leave the Library operation?",
            "The reviewed operation or report remains available in this Library session, but has not been applied.",
            "Leave");
    }

    public Task<bool> ConfirmNavigationAsync() => ConfirmCanNavigateAwayAsync();

    public IReadOnlyList<LibraryRow> GetVisibleSelectedRows()
    {
        if (_selectedPaths.Count == 0)
            return [];
        var selected = _selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Rows.Where(row => selected.Contains(row.Path)).ToArray();
    }

    private void SetSelectedPaths(IReadOnlyList<string> paths)
    {
        string[] distinct = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_selectedPaths.SequenceEqual(distinct, StringComparer.OrdinalIgnoreCase))
            return;
        _selectedPaths = distinct;
        OnPropertyChanged(nameof(SelectedPaths));
        InvalidateLibraryOperationPreview();
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        OpenInWorkbenchCommand.NotifyCanExecuteChanged();
        PreviewLibraryOperationCommand.NotifyCanExecuteChanged();
        ImportLibraryDelimitedMetadataCommand.NotifyCanExecuteChanged();
        CopyLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        PasteLibraryMetadataFieldCommand.NotifyCanExecuteChanged();
        DiscoverLibraryAudioCommand.NotifyCanExecuteChanged();
        PreviewLocalLibraryArtworkCommand.NotifyCanExecuteChanged();
        PreviewRemoveLibraryFrontCoverCommand.NotifyCanExecuteChanged();
        PreviewRemoveAllLibraryArtworkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenInWorkbench))]
    private async Task OpenInWorkbenchAsync()
    {
        if (_workbench is null || _selectedPaths.Count == 0)
            return;
        await _workbench.AddSourcesAsync(_selectedPaths);
        _navigation.Navigate(ShellDestination.Workbench);
    }

    private bool CanOpenInWorkbench() =>
        !IsBusy && _workbench is not null && _selectedPaths.Count > 0;

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
            OperationStatus = "The selected Library scope contains no files.";
            return;
        }

        BeginLibraryOperation("Building metadata preview");
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
            MetadataPreviewRowBuilder.Populate(OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            OperationStatus = blockers > 0
                ? $"Previewed {paths.Length:N0} file(s) with {blockers:N0} blocker(s). " +
                  "No files were changed."
                : $"Previewed {plan.ChangeCount:N0} change(s) in " +
                  $"{plan.ChangedFileCount:N0} of {paths.Length:N0} file(s).";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "Preview cancelled. No files were changed.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = $"Preview failed: {error.Message}";
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
            OperationStatus =
                "The selected Library scope contains no cached metadata to copy.";
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
        OperationStatus =
            $"Copied {values.Length:N0} ordered {selected.Label} " +
            $"value(s) from {Path.GetFileName(row.Path)} with tag identity.";
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
            OperationStatus =
                "The clipboard does not contain text metadata.";
            return;
        }

        BeginLibraryOperation("Building clipboard metadata preview");
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
                    $"Paste {payload.Field.DisplayName} values for " +
                    $"{paths.Length:N0} file(s)",
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files
                .SelectMany(file => file.Issues)
                .Count(issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
            OperationStatus = blockers > 0
                ? $"Clipboard preview has {blockers:N0} blocker(s). " +
                  "No files were changed."
                : $"Previewed {plan.ChangeCount:N0} pasted " +
                  $"change(s) in {plan.ChangedFileCount:N0} file(s).";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                "Clipboard preview cancelled. No files were changed.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                $"Clipboard preview failed: {error.Message}";
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
            OperationStatus =
                "The selected Library scope contains no files.";
            return;
        }
        string? source = await _files.PickFileAsync(
            "Import metadata from CSV or delimited text",
            [new(
                "Delimited metadata",
                [".csv", ".tsv", ".txt"])]);
        if (source is null)
            return;

        BeginLibraryOperation("Mapping metadata import");
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
                    "No import rows matched the selected Library scope.";
                throw new InvalidDataException(reason);
            }
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    imported.EditsByPath,
                    $"Import metadata from " +
                    $"{Path.GetFileName(source)}",
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges,
                plan);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files
                .SelectMany(file => file.Issues)
                .Count(issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
            int warnings = imported.Issues.Count(issue =>
                issue.Severity ==
                    DelimitedMetadataImportIssueSeverity.Warning);
            OperationStatus = blockers > 0
                ? $"Import preview has {blockers:N0} blocker(s). " +
                  "No files were changed."
                : $"Previewed {plan.ChangeCount:N0} imported " +
                  $"change(s) in {plan.ChangedFileCount:N0} file(s). " +
                  $"Mapped {imported.MatchedRows:N0} of " +
                  $"{imported.DataRows:N0} row(s)" +
                  (warnings == 0
                      ? "."
                      : $" with {warnings:N0} warning(s).");
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                "Import preview cancelled. No files were changed.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                $"Import preview failed: {error.Message}";
        }
        finally
        {
            EndLibraryOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyLibraryOperation))]
    private async Task ApplyLibraryOperationAsync()
    {
        if (_metadataOperations is null || _libraryOperationPlan is null)
            return;
        BeginLibraryOperation("Applying reviewed metadata changes");
        try
        {
            MetadataApplyResult result =
                await _metadataOperations.ApplyAsync(
                    _libraryOperationPlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = null;
            OperationPreviewChanges.Clear();
            HasApplicableOperationPreview = false;
            OperationStatus = $"Applied {result.ChangedFiles:N0} file(s). Originals are " +
                "available through Operations recovery.";
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            OperationStatus =
                "Apply cancelled. Completed mutations remain available through Operations recovery.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Apply stopped safely: {error.Message}";
        }
        finally
        {
            EndLibraryOperation();
            OnPropertyChanged(nameof(HasUnsavedChanges));
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
            OperationStatus = "The selected Library scope contains no files.";
            return;
        }

        BeginLibraryOperation("Preparing audio fingerprint discovery");
        try
        {
            AcoustIdDiscoveryResult result = await _audioDiscovery.DiscoverAsync(
                paths, CreateOperationProgress(), _operationCancellation!.Token);
            AudioMatches.Clear();
            ReleaseMatches.Clear();
            SelectedRelease = null;
            ClearReleaseTrackMappings();
            foreach (AudioDiscoveryRow row in AudioDiscoveryRows.Create(result))
                AudioMatches.Add(row);
            SelectedAudioMatch = AudioMatches.FirstOrDefault();
            int issues = result.Files.Sum(file => file.Issues.Length);
            OperationStatus =
                $"Fingerprint discovery: {result.FingerprintedFileCount:N0} file(s), " +
                $"{result.CandidateCount:N0} candidate(s), {issues:N0} warning(s).";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Audio fingerprint discovery cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Audio fingerprint discovery failed: {error.Message}";
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
        BeginLibraryOperation("Building audio identifier preview");
        try
        {
            OperationRecipe recipe =
                AudioDiscoveryRows.CreateTagRecipe(SelectedAudioMatch);
            MetadataOperationPlan plan = await _metadataOperations.PreviewAsync(
                [SelectedAudioMatch.Path],
                recipe,
                CreateOperationProgress(),
                _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            OperationStatus =
                "Audio identifiers were added to the normal metadata preview. Review before applying.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Audio identifier preview cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Audio identifier preview failed: {error.Message}";
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
        BeginLibraryOperation("Resolving MusicBrainz release editions");
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
            OperationStatus =
                $"MusicBrainz returned {ReleaseMatches.Count:N0} release edition(s). " +
                "No metadata was selected or changed.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "MusicBrainz release lookup cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"MusicBrainz release lookup failed: {error.Message}";
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
        BeginLibraryOperation("Searching MusicBrainz releases");
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
            OperationStatus =
                $"MusicBrainz found {ReleaseMatches.Count:N0} release edition(s). " +
                "Choose one and build a file-to-track mapping.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "MusicBrainz release search cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"MusicBrainz release search failed: {error.Message}";
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
        BeginLibraryOperation("Searching Discogs releases");
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
                ? "Offline cache"
                : result.FromCache ? "Cache" : "Discogs";
            foreach (DiscogsReleaseCandidate candidate in result.Releases)
                DiscogsMatches.Add(
                    DiscogsReleaseRow.Create(candidate, source));
            SelectedDiscogsRelease = DiscogsMatches.FirstOrDefault();
            OperationStatus =
                $"Discogs found {DiscogsMatches.Count:N0} release edition(s). " +
                "Select one to load its complete track and edition details.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Discogs release search cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus =
                $"Discogs release search failed: {error.Message}";
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
        BeginLibraryOperation("Loading Discogs release details");
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
            OperationStatus =
                $"Loaded Discogs release {release.ReleaseId} with " +
                $"{release.Tracks.Length:N0} track(s). No metadata was changed.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Discogs release detail lookup cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus =
                $"Discogs release detail lookup failed: {error.Message}";
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
        BeginLibraryOperation("Matching Library files to Discogs tracks");
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
                var row = new DiscogsTrackMappingRow(match);
                row.PropertyChanged += OnDiscogsMappingChanged;
                DiscogsTrackMappings.Add(row);
            }
            OperationStatus =
                $"Suggested {mapping.SuggestedCount:N0} of {mapping.Files.Length:N0} " +
                $"Discogs file-to-track mappings; " +
                $"{mapping.AmbiguousCount:N0} need review.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Discogs track mapping cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus =
                $"Discogs track mapping failed: {error.Message}";
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
        BeginLibraryOperation("Building Discogs metadata preview");
        try
        {
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    edits,
                    $"Discogs: {SelectedDiscogsRelease.Title}",
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            OperationStatus =
                "Mapped Discogs fields were added to the normal metadata preview. " +
                "Review every change before applying.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "Discogs metadata preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                $"Discogs metadata preview failed: {error.Message}";
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
        BeginLibraryOperation("Building Discogs artwork preview");
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
                    $"Discogs artwork: {releaseTitle}",
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            OperationStatus =
                "The selected Discogs cover was added to the normal artwork preview. " +
                "Review every change before applying.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "Discogs artwork preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus =
                $"Discogs artwork preview failed: {error.Message}";
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
                "Choose report output folder")
            : await _files.SaveFileAsync(
                "Choose report output",
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
        BeginLibraryOperation("Building report preview");
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
                        ? "All"
                        : file.Group,
                    file.DestinationPath,
                    file.RowCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            OperationStatus = blockers > 0
                ? $"Report preview has {blockers:N0} blocker(s). No output was written."
                : $"Previewed {plan.Files.Count:N0} report file(s). " +
                  "Review the destinations before applying.";
            ApplyLibraryReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateReportPlan();
            OperationStatus = "Report preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateReportPlan();
            OperationStatus = $"Report preview failed: {error.Message}";
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
        BeginLibraryOperation("Writing reviewed report");
        try
        {
            ReportExportResult result = await _reports.ApplyAsync(
                _reportPlan,
                CreateOperationProgress(),
                _operationCancellation!.Token);
            _reportPlan = null;
            OperationStatus =
                $"Wrote {result.FileCount:N0} report file(s) with " +
                $"{result.RowCount:N0} row(s).";
            ApplyLibraryReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Report output cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Report output failed: {error.Message}";
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
                "Choose playlist output folder")
            : await _files.SaveFileAsync(
                "Choose playlist output",
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
        BeginLibraryOperation("Building playlist preview");
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
                        ? "All"
                        : file.Group,
                    file.DestinationPath,
                    file.TrackCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            OperationStatus = blockers > 0
                ? $"Playlist preview has {blockers:N0} blocker(s). No output was written."
                : $"Previewed {plan.Files.Count:N0} playlist file(s). " +
                  "Review the destinations before applying.";
            ApplyLibraryPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidatePlaylistPlan();
            OperationStatus = "Playlist preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidatePlaylistPlan();
            OperationStatus =
                $"Playlist preview failed: {error.Message}";
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
        BeginLibraryOperation("Writing reviewed playlist");
        try
        {
            PlaylistWorkspaceResult result =
                await _playlists.ApplyAsync(
                    _playlistPlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _playlistPlan = null;
            OperationStatus =
                $"Wrote {result.PlaylistCount:N0} playlist file(s) with " +
                $"{result.TrackReferenceCount:N0} track reference(s).";
            ApplyLibraryPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Playlist output cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus =
                $"Playlist output failed: {error.Message}";
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
            "Choose external tool executable");
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
            "Choose external tool working directory");
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
                invocation.WorkingDirectory ?? "(application default)",
                invocation.SourcePaths.Count));
        }
        int blockers = plan.Issues.Count(issue =>
            issue.Severity == OperationIssueSeverity.Blocker);
        OperationStatus = blockers > 0
            ? $"External-tool preview has {blockers:N0} blocker(s). Nothing can run."
            : $"Previewed {plan.Invocations.Count:N0} process invocation(s). " +
              "External tools run outside MusicLibraryManager recovery.";
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
                "Run external tool?",
                $"Run '{_externalToolPlan.Definition.Name}' " +
                $"{_externalToolPlan.Invocations.Count:N0} time(s)? " +
                "External tools can change files and are outside " +
                "MusicLibraryManager recovery.",
                "Run"))
            return;
        BeginLibraryOperation(
            $"Running {_externalToolPlan.Definition.Name}");
        try
        {
            ExternalToolRunResult result =
                await _externalTools.RunAsync(
                    _externalToolPlan,
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _externalToolPlan = null;
            OperationStatus =
                $"External tool finished: {result.SucceededCount:N0} " +
                $"succeeded, {result.FailedCount:N0} failed.";
            RunLibraryExternalToolCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            OperationStatus =
                "External tool cancelled. The active process was stopped.";
        }
        catch (Exception error)
        {
            OperationStatus =
                $"External tool stopped: {error.Message}";
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
        BeginLibraryOperation("Finding Cover Art Archive images");
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
                ArtworkMatches.Add(new(candidate));
            for (int index = 0; index < ArtworkMatches.Count; index++)
            {
                _operationCancellation.Token.ThrowIfCancellationRequested();
                CoverArtCandidateRow row = ArtworkMatches[index];
                progress.Report(new(
                    OperationPhase.Planning,
                    index,
                    ArtworkMatches.Count,
                    Message: $"Loading artwork thumbnail {index + 1:N0} " +
                        $"of {ArtworkMatches.Count:N0}"));
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
                    row.ThumbnailStatus = download.FromCache
                        ? "Cached"
                        : $"{download.Data.Length:N0} bytes";
                }
                catch (Exception error) when (
                    error is not OperationCanceledException)
                {
                    row.ThumbnailStatus = error.Message;
                }
            }
            SelectedArtworkMatch = ArtworkMatches.FirstOrDefault(row =>
                row.Candidate.IsFront) ?? ArtworkMatches.FirstOrDefault();
            OperationStatus = ArtworkMatches.Count == 0
                ? "This release has no Cover Art Archive images."
                : $"Loaded {ArtworkMatches.Count:N0} artwork candidate(s). " +
                  "No files were changed.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Cover Art Archive lookup cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Cover Art Archive lookup failed: {error.Message}";
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
        BeginLibraryOperation("Building artwork preview");
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
                    $"Cover Art Archive: {releaseTitle}",
                    progress,
                    _operationCancellation.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(
                OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            OperationStatus =
                "The selected front cover was added to the normal metadata preview. " +
                "Review every artwork change before applying.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "Artwork preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = $"Artwork preview failed: {error.Message}";
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
            "Choose front-cover artwork",
            [new("Artwork images",
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
                    $"Reading {Path.GetFileName(artworkPath)}"));
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
            "Replace front cover");
    }

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryArtwork))]
    private async Task PreviewRemoveLibraryFrontCoverAsync() =>
        await PreviewLibraryArtworkEditAsync(
            (_, _) => Task.FromResult(new ArtworkValueEdit(
                ArtworkValueEditMode.RemoveFrontCover)),
            "Remove front cover");

    [RelayCommand(CanExecute = nameof(CanPreviewLibraryArtwork))]
    private async Task PreviewRemoveAllLibraryArtworkAsync() =>
        await PreviewLibraryArtworkEditAsync(
            (_, _) => Task.FromResult(new ArtworkValueEdit(
                ArtworkValueEditMode.RemoveAll)),
            "Remove all artwork");

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
        BeginLibraryOperation("Building artwork preview");
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
                OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue =>
                    issue.Severity == OperationIssueSeverity.Blocker);
            OperationStatus = blockers > 0
                ? $"Artwork preview has {blockers:N0} blocker(s). No files were changed."
                : $"Previewed artwork changes for {plan.ChangedFileCount:N0} " +
                  $"of {paths.Length:N0} file(s). No files were changed.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "Artwork preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = $"Artwork preview failed: {error.Message}";
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
        BeginLibraryOperation("Matching Library files to release tracks");
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
                var mappingRow = new MusicBrainzTrackMappingRow(match);
                mappingRow.PropertyChanged += OnReleaseMappingChanged;
                ReleaseTrackMappings.Add(mappingRow);
            }
            OperationStatus =
                $"Suggested {mapping.SuggestedCount:N0} of {mapping.Files.Length:N0} " +
                $"file-to-track mappings; {mapping.AmbiguousCount:N0} need review.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Release track mapping cancelled.";
        }
        catch (Exception error)
        {
            OperationStatus = $"Release track mapping failed: {error.Message}";
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
        BeginLibraryOperation("Building MusicBrainz metadata preview");
        try
        {
            MetadataOperationPlan plan =
                await _metadataOperations.PreviewValueEditsAsync(
                    edits,
                    $"MusicBrainz: {SelectedRelease.Title}",
                    CreateOperationProgress(),
                    _operationCancellation!.Token);
            _libraryOperationPlan = plan;
            MetadataPreviewRowBuilder.Populate(OperationPreviewChanges, plan);
            HasApplicableOperationPreview = plan.CanApply;
            OperationStatus =
                "Mapped MusicBrainz fields were added to the normal metadata preview. " +
                "Review every change before applying.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = "MusicBrainz metadata preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateLibraryOperationPreview();
            OperationStatus = $"MusicBrainz metadata preview failed: {error.Message}";
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

    private void BeginLibraryOperation(string message)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new();
        OperationProgressText = message;
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
        if (_libraryOperationPlan is null && OperationPreviewChanges.Count == 0)
            return;
        _libraryOperationPlan = null;
        OperationPreviewChanges.Clear();
        HasApplicableOperationPreview = false;
        OperationStatus =
            "Operation or scope changed. Preview authoritative metadata again.";
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnSelectedOperationScopeChanged(LibraryOperationScope value)
    {
        InvalidateLibraryOperationPreview();
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
    }

    partial void OnRowsChanged(IReadOnlyList<LibraryRow> value)
    {
        ClearReleaseTrackMappings();
        ClearDiscogsTrackMappings();
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        PreviewLibraryOperationCommand.NotifyCanExecuteChanged();
        ImportLibraryDelimitedMetadataCommand.NotifyCanExecuteChanged();
        OpenOperationsCommand.NotifyCanExecuteChanged();
        DiscoverLibraryAudioCommand.NotifyCanExecuteChanged();
        PreviewLocalLibraryArtworkCommand.NotifyCanExecuteChanged();
        PreviewRemoveLibraryFrontCoverCommand.NotifyCanExecuteChanged();
        PreviewRemoveAllLibraryArtworkCommand.NotifyCanExecuteChanged();
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
        !IsOperationBusy && _libraryOperationPlan is not null &&
        HasApplicableOperationPreview;

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
            throw new InvalidOperationException("Choose a MusicBrainz release.");
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
                "Discogs is unavailable.");
        DiscogsReleaseRow selected = SelectedDiscogsRelease ??
            throw new InvalidOperationException(
                "Choose a Discogs release.");
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
        FilterError = query.Error ?? visual.Error;
        if (!query.IsValid || !visual.IsValid)
        {
            StatusText = FilterError ?? "Invalid filter.";
            return;
        }
        List<LibraryRow> source = _allRows;
        HashSet<string>? healthPaths = _healthFilterPaths.Count == 0
            ? null
            : new HashSet<string>(_healthFilterPaths, StringComparer.OrdinalIgnoreCase);
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
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            LibraryRow[] preserved = source.Where(row =>
                _selectedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase) &&
                includedPaths.Add(row.Path)).ToArray();
            preservedSelectionCount = preserved.Length;
            filtered.AddRange(preserved);
        }

        SelectionContext? updatedSelection = null;
        if (!_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
        {
            var selectedPaths = _selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        StatusText = healthPaths is not null
            ? $"{filtered.Count:N0} Health-filtered track(s) of {source.Count:N0} total"
            : filtered.Count == source.Count
                ? $"{source.Count:N0} tracks"
                : $"{filtered.Count:N0} of {source.Count:N0} tracks";
        if (preservedSelectionCount > 0)
            StatusText += $" · {preservedSelectionCount:N0} selected with unsaved changes kept visible";
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
                IsInspectorOpen = state.InspectorOpen ?? true;
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
                VisualFilterExpression)));

    private sealed record LibraryWorkspaceSnapshot(
        string? Filter,
        FilterMode Mode,
        bool? InspectorOpen = null,
        LibraryVisualFilterNode? VisualFilter = null);
    private sealed record ThumbnailCacheItem(object? Image, LinkedListNode<string> Node);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
