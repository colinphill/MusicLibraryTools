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

public sealed record WorkbenchMetadataFieldRow(
    MetadataFieldKey Field,
    string Layers,
    ImmutableArray<string> Values,
    bool IsMixed = false,
    int SelectedFileCount = 1,
    int PresentFileCount = 1)
{
    public string Name => Field.DisplayName;
    public string Kind => Field.IsKnown ? "Known" : "Custom";
    public string DisplayValue => IsMixed
        ? $"Mixed across {SelectedFileCount:N0} selected files"
        : string.Join("; ", Values);
    public string Coverage => SelectedFileCount <= 1
        ? ""
        : $"{PresentFileCount:N0}/{SelectedFileCount:N0} files";
}

public partial class WorkbenchViewModel : ObservableObject, INavigationGuard
{
    private const string RecentLocationsPreference = "manager.workbench.recentLocations.v1";
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
    private MetadataOperationPlan? _plan;
    private ReportExportPlan? _reportPlan;
    private PlaylistWorkspacePlan? _playlistPlan;
    private ExternalToolPlan? _externalToolPlan;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<WorkbenchTrackViewModel> _selectedFiles = [];

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
    [NotifyCanExecuteChangedFor(nameof(PreviewAudioIdentifiersCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveSelectedRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildReleaseMappingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewReleaseMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchMusicBrainzReleasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchDiscogsReleasesCommand))]
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
    private WorkbenchTrackViewModel? _selectedFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewFieldValuesCommand))]
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
    private string _statusText =
        "Add files, folders, playlists, or cuesheets. No library configuration is required.";

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
        IDelimitedMetadataImportService? delimitedImports = null)
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
        OperationEditor = new(
            operationCatalog, MetadataOperationSurface.Workbench, recipeStore);
        ShortcutEditor = new(shortcutStore, recipeStore);
        ColumnEditor = new(
            metadataColumns,
            MetadataGridSurface.Workbench);
        ReleaseImport.PropertyChanged += OnReleaseImportChanged;
        ReleaseSearch.PropertyChanged += (_, _) =>
            SearchMusicBrainzReleasesCommand.NotifyCanExecuteChanged();
        DiscogsSearch.PropertyChanged += (_, _) =>
            SearchDiscogsReleasesCommand.NotifyCanExecuteChanged();
        DiscogsImport.PropertyChanged += (_, _) =>
        {
            CancelPlan();
            PreviewDiscogsReleaseMetadataCommand.NotifyCanExecuteChanged();
        };
        ReportEditor.Changed += InvalidateReportPlan;
        PlaylistEditor.Changed += InvalidatePlaylistPlan;
        ExternalToolEditor = new(externalToolStore);
        ExternalToolEditor.Changed += InvalidateExternalToolPlan;
        KnownFieldChoices = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new MetadataFieldChoice(field, field.ToString()))
            .ToArray();
        SelectedNewKnownField = KnownFieldChoices[0];
        LoadRecentLocations();
    }

    public ObservableCollection<WorkbenchTrackViewModel> Files { get; } = [];
    public ObservableCollection<MetadataPreviewRow> PreviewChanges { get; } = [];
    public ObservableCollection<WorkbenchMetadataFieldRow> MetadataFields { get; } = [];
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
    public ObservableCollection<string> RecentLocations { get; } = [];
    public MusicBrainzImportSelectionViewModel ReleaseImport { get; } = new();
    public MusicBrainzReleaseSearchViewModel ReleaseSearch { get; } = new();
    public DiscogsReleaseSearchViewModel DiscogsSearch { get; } = new();
    public DiscogsImportSelectionViewModel DiscogsImport { get; } = new();
    public ReportEditorViewModel ReportEditor { get; } = new();
    public PlaylistEditorViewModel PlaylistEditor { get; } = new();
    public ExternalToolEditorViewModel ExternalToolEditor { get; }
    public WorkbenchShortcutEditorViewModel ShortcutEditor { get; }
    public MetadataGridColumnEditorViewModel ColumnEditor { get; }
    public MetadataOperationEditorViewModel OperationEditor { get; }
    public IReadOnlyList<MetadataFieldChoice> KnownFieldChoices { get; }
    public IReadOnlyList<WorkbenchFieldEditMode> FieldEditModes { get; } =
        Enum.GetValues<WorkbenchFieldEditMode>();
    public IReadOnlyList<DelimitedMetadataEmptyCellMode>
        ImportEmptyCellModes { get; } =
            Enum.GetValues<DelimitedMetadataEmptyCellMode>();
    public IReadOnlyList<ID3v2Version> Id3Versions { get; } =
        Enum.GetValues<ID3v2Version>();
    public IReadOnlyList<ID3TextEncodingPolicy> Id3EncodingPolicies { get; } =
        Enum.GetValues<ID3TextEncodingPolicy>();
    public bool HasFiles => Files.Count > 0;
    public IReadOnlyList<WorkbenchTrackViewModel> SelectedFiles =>
        _selectedFiles;
    public int SelectedFileCount => EditTargets.Count;
    public string FieldSelectionSummary => SelectedFileCount switch
    {
        0 => "",
        1 => EditTargets[0].Path,
        _ => $"{SelectedFileCount:N0} files selected",
    };
    public bool HasPreview => PreviewChanges.Count > 0;
    public bool HasUnsavedChanges =>
        _plan is not null ||
        _reportPlan is not null ||
        _playlistPlan is not null ||
        _externalToolPlan is not null ||
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
                StatusText =
                    $"Shortcut recipe '{binding.TargetLabel}' no longer exists.";
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
            StatusText =
                $"Shortcut regenerated '{recipe.Name}' for the current " +
                "Workbench files. Review before applying.";
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
        if (value is null)
            SetSelectedFiles([]);
        else if (!_selectedFiles.Contains(value))
            SetSelectedFiles([value]);
        RebuildMetadataFields();
        DiscoverSelectedAudioCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

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
        _selectedFiles = selected;
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(FieldSelectionSummary));
        RebuildMetadataFields();
        PreviewFieldValuesCommand.NotifyCanExecuteChanged();
    }

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
            "Add media, playlists, or cuesheets",
            [new("Supported sources",
                [".mp3", ".flac", ".ogg", ".wv", ".m4a", ".mp4", ".m4p", ".m4r",
                 ".m4b", ".m4v", ".wma", ".asf", ".wmv",
                 ".dsf", ".m3u", ".m3u8", ".cue"])]);
        if (paths.Count > 0)
            await AddSourcesAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFolderAsync()
    {
        string? path = await _files.PickFolderAsync("Add a media folder");
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
        BeginOperation("Scanning Workbench sources");
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
            StatusText = loaded.Issues.Length == 0
                ? $"Added {added:N0} file(s); {Files.Count:N0} in this Workbench session."
                : $"Added {added:N0} file(s) with {loaded.Issues.Length:N0} warning(s): " +
                  loaded.Issues[0].Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Loading cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Could not load the selected sources: {error.Message}";
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
        StatusText = $"{Files.Count:N0} file(s) in this Workbench session.";
        NotifySessionChanged();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Files.Count == 0)
            return;
        if (HasUnsavedChanges && !await _dialogs.ConfirmAsync(
                "Clear Workbench?",
                "This removes the current session and its uncommitted edits. Files on disk are not changed.",
                "Clear"))
            return;
        foreach (WorkbenchTrackViewModel file in Files)
            file.PropertyChanged -= OnTrackChanged;
        Files.Clear();
        AudioMatches.Clear();
        ReleaseMatches.Clear();
        SelectedRelease = null;
        ClearReleaseTrackMappings();
        PreviewChanges.Clear();
        _plan = null;
        InvalidateReportPlan();
        InvalidatePlaylistPlan();
        InvalidateExternalToolPlan();
        SelectedFile = null;
        StatusText = "Workbench cleared. Files on disk were not changed.";
        NotifySessionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        int index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        if (index <= 0)
            return;
        Files.Move(index, index - 1);
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
            edits, "Workbench field edits", progress, ct));
    }

    [RelayCommand(CanExecute = nameof(CanImportDelimitedMetadata))]
    private async Task ImportDelimitedMetadataAsync()
    {
        if (_delimitedImports is null)
            return;
        string? path = await _files.PickFileAsync(
            "Import metadata from CSV or delimited text",
            [new(
                "Delimited metadata",
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
                    "No import rows matched this Workbench session.";
                throw new InvalidDataException(reason);
            }
            return await _operations.PreviewValueEditsAsync(
                imported.EditsByPath,
                $"Import metadata from {Path.GetFileName(path)}",
                progress,
                ct);
        });
        if (_plan is not null && imported is not null)
        {
            int warnings = imported.Issues.Count(issue =>
                issue.Severity ==
                    DelimitedMetadataImportIssueSeverity.Warning);
            StatusText +=
                $" Mapped {imported.MatchedRows:N0} of " +
                $"{imported.DataRows:N0} row(s)" +
                (warnings == 0
                    ? "."
                    : $" with {warnings:N0} warning(s).");
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
        MetadataFieldKey field;
        if (SelectedMetadataField is { } selected)
        {
            field = selected.Field;
        }
        else if (!string.IsNullOrWhiteSpace(CustomFieldName))
        {
            field = MetadataFieldKey.Custom(CustomFieldName);
        }
        else
        {
            field = MetadataFieldKey.Known(SelectedNewKnownField!.Field);
        }

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
            $"Edit {field.DisplayName} values for " +
            $"{targets.Count:N0} file(s)",
            progress,
            ct));
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
        string action = mode == TagLayerEditMode.Add ? "Add" : "Remove";
        string layerName = kind == TagLayerKind.Id3v2
            ? "ID3v2"
            : "APEv2";
        await PreviewAsync((progress, ct) =>
            _operations.PreviewTagLayerEditsAsync(
                edits,
                $"{action} {layerName} tag layer",
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
                $"Convert to ID3v2.{(int)TargetId3Version}",
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
                $"Convert {source} to {target}",
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
                $"Re-encode ID3 text as {SelectedId3EncodingPolicy}",
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
        BeginOperation("Building metadata preview");
        try
        {
            MetadataOperationPlan plan = await action(
                CreateProgress(), _cancellation!.Token);
            _plan = plan;
            MetadataPreviewRowBuilder.Populate(PreviewChanges, plan);
            HasApplicablePreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            StatusText = blockers > 0
                ? $"Preview has {blockers:N0} blocker(s). No files have been changed."
                : $"Preview: {plan.ChangeCount:N0} metadata change(s) in " +
                  $"{plan.ChangedFileCount:N0} file(s). No files have been changed.";
        }
        catch (OperationCanceledException)
        {
            CancelPlan();
            StatusText = "Preview cancelled. No files were changed.";
        }
        catch (Exception error)
        {
            CancelPlan();
            StatusText = $"Preview failed: {error.Message}";
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
        if (_plan is null)
            return;
        BeginOperation("Applying reviewed metadata changes");
        try
        {
            IProgress<OperationProgress> progress = CreateProgress();
            MetadataApplyResult result = await _operations.ApplyAsync(
                _plan, progress, _cancellation!.Token);
            string[] paths = _plan.Files.Where(file => file.HasChanges)
                .Select(file => file.Path).ToArray();
            await ReloadAsync(paths, progress, _cancellation.Token);
            _plan = null;
            PreviewChanges.Clear();
            HasApplicablePreview = false;
            StatusText = $"Applied {result.ChangedFiles:N0} file(s). Originals are retained " +
                "in Workbench recovery and can be restored from Operations.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Apply cancelled. Completed mutations remain recoverable.";
        }
        catch (Exception error)
        {
            StatusText = $"Apply stopped safely: {error.Message}";
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Restore the last Workbench operation?",
                "Current files will be replaced by the retained originals. A collision backup is created when required.",
                "Restore"))
            return;
        BeginOperation("Restoring the latest Workbench operation");
        try
        {
            var restoreProgress = new Progress<int>(completed =>
                ReportProgress(new(
                    OperationPhase.Applying,
                    completed,
                    Math.Max(1, Files.Count),
                    Message: $"Restored {completed:N0} file(s)")));
            int restored = await _history.UndoLatestAsync(
                restoreProgress, _cancellation!.Token);
            await ReloadAsync(
                Files.Select(file => file.Path).ToArray(),
                CreateProgress(),
                _cancellation.Token);
            StatusText = $"Restored {restored:N0} file(s) from the latest Workbench operation.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Restore cancelled. Any completed restores remain recoverable.";
        }
        catch (Exception error)
        {
            StatusText = $"Restore failed: {error.Message}";
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
        StatusText = "Redo was regenerated against the current files. Review the preview before applying.";
    }

    [RelayCommand(CanExecute = nameof(CanRepeat))]
    private async Task RepeatAsync()
    {
        OperationRecipe? recipe = _history.Entries.FirstOrDefault()?.Recipe;
        if (recipe is null)
            return;
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            Files.Select(file => file.Path).ToArray(), recipe, progress, ct));
        StatusText = "The latest recipe was regenerated for the current Workbench files. Review before applying.";
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanDiscoverSelectedAudio))]
    private async Task DiscoverSelectedAudioAsync()
    {
        if (SelectedFile is not null)
            await DiscoverAudioAsync([SelectedFile.Path]);
    }

    [RelayCommand(CanExecute = nameof(CanDiscoverAllAudio))]
    private async Task DiscoverAllAudioAsync() =>
        await DiscoverAudioAsync(Files.Select(file => file.Path).ToArray());

    [RelayCommand(CanExecute = nameof(CanPreviewAudioIdentifiers))]
    private async Task PreviewAudioIdentifiersAsync()
    {
        if (SelectedAudioMatch is null)
            return;
        OperationRecipe recipe =
            AudioDiscoveryRows.CreateTagRecipe(SelectedAudioMatch);
        await PreviewAsync((progress, ct) => _operations.PreviewAsync(
            [SelectedAudioMatch.Path], recipe, progress, ct));
        StatusText =
            "Audio identifiers were added to the normal metadata preview. Review before applying.";
    }

    [RelayCommand(CanExecute = nameof(CanResolveSelectedRecording))]
    private async Task ResolveSelectedRecordingAsync()
    {
        if (SelectedAudioMatch is null ||
            SelectedAudioMatch.MusicBrainzRecordingIdValues.Length != 1)
            return;
        BeginOperation("Resolving MusicBrainz release editions");
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
            StatusText =
                $"MusicBrainz returned {ReleaseMatches.Count:N0} release edition(s). " +
                "No metadata was selected or changed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "MusicBrainz release lookup cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"MusicBrainz release lookup failed: {error.Message}";
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
        BeginOperation("Searching MusicBrainz releases");
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
            StatusText =
                $"MusicBrainz found {ReleaseMatches.Count:N0} release edition(s). " +
                "Choose one and build a file-to-track mapping.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "MusicBrainz release search cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"MusicBrainz release search failed: {error.Message}";
        }
        finally
        {
            EndOperation();
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchDiscogsReleases))]
    private async Task SearchDiscogsReleasesAsync()
    {
        if (_discogs is null)
            return;
        BeginOperation("Searching Discogs releases");
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
                ? "Offline cache"
                : result.FromCache ? "Cache" : "Discogs";
            foreach (DiscogsReleaseCandidate candidate in result.Releases)
                DiscogsMatches.Add(
                    DiscogsReleaseRow.Create(candidate, source));
            SelectedDiscogsRelease = DiscogsMatches.FirstOrDefault();
            StatusText =
                $"Discogs found {DiscogsMatches.Count:N0} release edition(s). " +
                "Select one to load its complete track and edition details.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Discogs release search cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Discogs release search failed: {error.Message}";
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
        BeginOperation("Loading Discogs release details");
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
            StatusText =
                $"Loaded Discogs release {release.ReleaseId} with " +
                $"{release.Tracks.Length:N0} track(s). No metadata was changed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Discogs release detail lookup cancelled.";
        }
        catch (Exception error)
        {
            StatusText =
                $"Discogs release detail lookup failed: {error.Message}";
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
        BeginOperation("Matching Workbench files to Discogs tracks");
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
                var row = new DiscogsTrackMappingRow(match);
                row.PropertyChanged += OnDiscogsMappingChanged;
                DiscogsTrackMappings.Add(row);
            }
            StatusText =
                $"Suggested {mapping.SuggestedCount:N0} of {mapping.Files.Length:N0} " +
                $"Discogs file-to-track mappings; " +
                $"{mapping.AmbiguousCount:N0} need review.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Discogs track mapping cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Discogs track mapping failed: {error.Message}";
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
                $"Discogs: {SelectedDiscogsRelease.Title}",
                progress,
                ct));
        StatusText =
            "Mapped Discogs fields were added to the normal metadata preview. " +
            "Review every change before applying.";
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
                $"Discogs release {selected.ReleaseId}");
            var edits = paths.ToDictionary(
                path => path,
                _ => new ArtworkValueEdit(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    image),
                PathComparer);
            return await _operations.PreviewArtworkEditsAsync(
                edits,
                $"Discogs artwork: {releaseTitle}",
                progress,
                ct);
        });
        if (_plan is not null)
        {
            StatusText =
                "The selected Discogs cover was added to the normal artwork preview. " +
                "Review every change before applying.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseReportOutput))]
    private async Task BrowseReportOutputAsync()
    {
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

    [RelayCommand(CanExecute = nameof(CanPreviewReport))]
    private async Task PreviewReportAsync()
    {
        if (_reports is null)
            return;
        BeginOperation("Building report preview");
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
                        ? "All"
                        : file.Group,
                    file.DestinationPath,
                    file.RowCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            StatusText = blockers > 0
                ? $"Report preview has {blockers:N0} blocker(s). No output was written."
                : $"Previewed {plan.Files.Count:N0} report file(s). " +
                  "Review the destinations before applying.";
            ApplyReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidateReportPlan();
            StatusText = "Report preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidateReportPlan();
            StatusText = $"Report preview failed: {error.Message}";
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
        BeginOperation("Writing reviewed report");
        try
        {
            ReportExportResult result = await _reports.ApplyAsync(
                _reportPlan,
                CreateProgress(),
                _cancellation!.Token);
            _reportPlan = null;
            StatusText =
                $"Wrote {result.FileCount:N0} report file(s) with " +
                $"{result.RowCount:N0} row(s).";
            ApplyReportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Report output cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Report output failed: {error.Message}";
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
                "Choose playlist output folder")
            : await _files.SaveFileAsync(
                "Choose playlist output",
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
        BeginOperation("Building playlist preview");
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
                        ? "All"
                        : file.Group,
                    file.DestinationPath,
                    file.TrackCount,
                    file.ByteCount));
            int blockers = plan.Issues.Count(issue =>
                issue.Severity == OperationIssueSeverity.Blocker);
            StatusText = blockers > 0
                ? $"Playlist preview has {blockers:N0} blocker(s). No output was written."
                : $"Previewed {plan.Files.Count:N0} playlist file(s). " +
                  "Review the destinations before applying.";
            ApplyPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            InvalidatePlaylistPlan();
            StatusText = "Playlist preview cancelled.";
        }
        catch (Exception error)
        {
            InvalidatePlaylistPlan();
            StatusText =
                $"Playlist preview failed: {error.Message}";
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
        BeginOperation("Writing reviewed playlist");
        try
        {
            PlaylistWorkspaceResult result =
                await _playlists.ApplyAsync(
                    _playlistPlan,
                    CreateProgress(),
                    _cancellation!.Token);
            _playlistPlan = null;
            StatusText =
                $"Wrote {result.PlaylistCount:N0} playlist file(s) with " +
                $"{result.TrackReferenceCount:N0} track reference(s).";
            ApplyPlaylistCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Playlist output cancelled.";
        }
        catch (Exception error)
        {
            StatusText =
                $"Playlist output failed: {error.Message}";
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
            "Choose external tool executable");
        if (!string.IsNullOrWhiteSpace(path))
            ExternalToolEditor.Executable = path;
    }

    [RelayCommand(CanExecute =
        nameof(CanBrowseExternalToolWorkingDirectory))]
    private async Task BrowseExternalToolWorkingDirectoryAsync()
    {
        string? path = await _files.PickFolderAsync(
            "Choose external tool working directory");
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
                invocation.WorkingDirectory ?? "(application default)",
                invocation.SourcePaths.Count));
        }
        int blockers = plan.Issues.Count(issue =>
            issue.Severity == OperationIssueSeverity.Blocker);
        StatusText = blockers > 0
            ? $"External-tool preview has {blockers:N0} blocker(s). Nothing can run."
            : $"Previewed {plan.Invocations.Count:N0} process invocation(s). " +
              "External tools run outside MusicLibraryManager recovery.";
        RunExternalToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    [RelayCommand(CanExecute = nameof(CanRunExternalTool))]
    private async Task RunExternalToolAsync()
    {
        if (_externalTools is null || _externalToolPlan is null)
            return;
        if (!await _dialogs.ConfirmAsync(
                "Run external tool?",
                $"Run '{_externalToolPlan.Definition.Name}' " +
                $"{_externalToolPlan.Invocations.Count:N0} time(s)? " +
                "External tools can change files and are outside " +
                "MusicLibraryManager recovery.",
                "Run"))
            return;
        BeginOperation(
            $"Running {_externalToolPlan.Definition.Name}");
        try
        {
            ExternalToolRunResult result =
                await _externalTools.RunAsync(
                    _externalToolPlan,
                    CreateProgress(),
                    _cancellation!.Token);
            _externalToolPlan = null;
            StatusText =
                $"External tool finished: {result.SucceededCount:N0} " +
                $"succeeded, {result.FailedCount:N0} failed.";
            RunExternalToolCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            StatusText =
                "External tool cancelled. The active process was stopped.";
        }
        catch (Exception error)
        {
            StatusText =
                $"External tool stopped: {error.Message}";
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
        BeginOperation("Finding Cover Art Archive images");
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
                ArtworkMatches.Add(new(candidate));
            for (int index = 0; index < ArtworkMatches.Count; index++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
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
                            ct: _cancellation.Token);
                    row.ThumbnailSource =
                        await _thumbnails.CreateImageSourceAsync(
                            download.Data, 180, _cancellation.Token);
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
            StatusText = ArtworkMatches.Count == 0
                ? "This release has no Cover Art Archive images."
                : $"Loaded {ArtworkMatches.Count:N0} artwork candidate(s). " +
                  "No files were changed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cover Art Archive lookup cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Cover Art Archive lookup failed: {error.Message}";
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
                $"Cover Art Archive: {releaseTitle}",
                progress,
                ct);
        });
        if (_plan is not null)
        {
            StatusText =
                "The selected front cover was added to the normal metadata preview. " +
                "Review every artwork change before applying.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviewSelectedArtwork))]
    private async Task PreviewLocalArtworkAsync()
    {
        string? artworkPath = await _files.PickFileAsync(
            "Choose front-cover artwork",
            [new("Artwork images",
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
                $"Reading {Path.GetFileName(artworkPath)}"));
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
                edits, "Replace front cover", progress, ct);
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
                edits, "Remove front cover", progress, ct));
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
                edits, "Remove all artwork", progress, ct));
    }

    [RelayCommand(CanExecute = nameof(CanBuildReleaseMapping))]
    private async Task BuildReleaseMappingAsync()
    {
        if (SelectedRelease is null)
            return;
        BeginOperation("Matching Workbench files to release tracks");
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
                            file.Document.Codec.DurationInSeconds)))
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
                var row = new MusicBrainzTrackMappingRow(match);
                row.PropertyChanged += OnReleaseMappingChanged;
                ReleaseTrackMappings.Add(row);
            }
            StatusText =
                $"Suggested {mapping.SuggestedCount:N0} of {mapping.Files.Length:N0} " +
                $"file-to-track mappings; {mapping.AmbiguousCount:N0} need review.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Release track mapping cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Release track mapping failed: {error.Message}";
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
                $"MusicBrainz: {SelectedRelease.Title}",
                progress,
                ct));
        StatusText =
            "Mapped MusicBrainz fields were added to the normal metadata preview. " +
            "Review every change before applying.";
    }

    private async Task DiscoverAudioAsync(IReadOnlyList<string> paths)
    {
        BeginOperation("Preparing audio fingerprint discovery");
        try
        {
            AcoustIdDiscoveryResult result = await _audioDiscovery.DiscoverAsync(
                paths, CreateProgress(), _cancellation!.Token);
            AudioMatches.Clear();
            ReleaseMatches.Clear();
            SelectedRelease = null;
            ClearReleaseTrackMappings();
            foreach (AudioDiscoveryRow row in AudioDiscoveryRows.Create(result))
                AudioMatches.Add(row);
            SelectedAudioMatch = AudioMatches.FirstOrDefault();
            int issues = result.Files.Sum(file => file.Issues.Length);
            StatusText =
                $"Fingerprint discovery: {result.FingerprintedFileCount:N0} file(s), " +
                $"{result.CandidateCount:N0} candidate(s), {issues:N0} warning(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Audio fingerprint discovery cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Audio fingerprint discovery failed: {error.Message}";
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
            "Leave the Workbench?",
            "The current preview and inline edits remain in this session, but have not been applied to disk.",
            "Leave");
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

    private IReadOnlyList<WorkbenchTrackViewModel> EditTargets =>
        _selectedFiles.Count > 0
            ? _selectedFiles
            : SelectedFile is null
                ? []
                : [SelectedFile];

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
        HasApplicablePreview = false;
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
        PreviewAudioIdentifiersCommand.NotifyCanExecuteChanged();
        ResolveSelectedRecordingCommand.NotifyCanExecuteChanged();
        BuildReleaseMappingCommand.NotifyCanExecuteChanged();
        PreviewReleaseMetadataCommand.NotifyCanExecuteChanged();
        RemoveCurrentCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
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
    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;
    private bool CanUndo() => !IsBusy && _history.CanUndo;
    private bool CanRedo() => !IsBusy && _history.CanRedo;
    private bool CanRepeat() =>
        !IsBusy && Files.Count > 0 &&
        _history.Entries.FirstOrDefault()?.Recipe is not null;
    private bool CanDiscoverSelectedAudio() => !IsBusy && SelectedFile is not null;
    private bool CanDiscoverAllAudio() => !IsBusy && Files.Count > 0;
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
            throw new InvalidOperationException("Choose a MusicBrainz release.");
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

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
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
        _disc = document.FirstValue(TagFields.DiscNumber);
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
        ? $"{Document.Codec.Samplerate:N0} Hz"
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
        ? $"{Document.Codec.AverageBitrate / 1000:N0} kbps"
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
            .ToString("yyyy-MM-dd HH:mm");

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
    private string? _disc;

    public bool HasChanges => _original.Any(pair =>
        !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)));

    public ImmutableArray<TagEdit> CreateEdits() => _original
        .Where(pair => !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)))
        .Select(pair => new TagEdit(pair.Key,
            string.IsNullOrWhiteSpace(Value(pair.Key)) ? null : Value(pair.Key)))
        .ToImmutableArray();

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
        [TagFields.DiscNumber] = Disc,
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
        TagFields.DiscNumber => Disc,
        _ => null,
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
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
