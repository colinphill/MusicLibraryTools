using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record UnifiedJobHistoryItem(
    string JobName,
    bool Applied,
    bool Success,
    DateTimeOffset CreatedAt,
    double ElapsedSeconds,
    string Output)
{
    public string State => Success ? (Applied ? "Applied" : "Preview passed") : (Applied ? "Apply failed" : "Preview failed");
    public string Created => CreatedAt.ToLocalTime().ToString("g");
    public string Elapsed => $"{ElapsedSeconds:0.##}s";
}

/// <summary>Discovers, browses, restores, and retention-purges mutation journals and quarantine runs.</summary>
public partial class OperationsViewModel : ViewModelBase
{
#if MUSIC_LIBRARY_MANAGER
    private const string SearchRootPreference = "manager.operations.searchRoot.v1";
    private const string RetentionDaysPreference = "manager.operations.retentionDays.v1";
    private const string JobHistoryPreference = "manager.operations.jobHistory.v1";
#else
    private const string SearchRootPreference = "Operations.SearchRoot";
    private const string RetentionDaysPreference = "Operations.RetentionDays";
    private const string JobHistoryPreference = "Operations.JobHistory";
#endif
    private readonly IOperationJournalService _journals;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private readonly IUnifiedJobService? _jobs;
    private readonly ICrossLibrarySyncService? _crossLibrarySync;
    private readonly IPlaylistExportService? _playlistExport;
    private readonly IRedundancyAnalysisService? _redundancyAnalysis;
    private readonly IItunesValidationService? _itunesValidation;
    private readonly IArtworkNormalizationService? _artworkNormalization;
    private readonly IDeviceSyncService? _deviceSync;
    private readonly ISmartStorageService? _smartStorage;
    private readonly ICarCardService? _carCard;
    private CancellationTokenSource? _cts;

    public event Action<IReadOnlyList<string>>? ArtworkNormalized;

    [ObservableProperty]
    private string? _searchRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewPurgeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPurgeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewJobCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyJobCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _retentionDays = 90;

    [ObservableProperty]
    private string _statusText = "Scan configured roots or choose a device/folder to discover recovery operations.";

    [ObservableProperty]
    private bool _showBrowser;

    [ObservableProperty]
    private OperationRunViewModel? _selectedRun;

    [ObservableProperty]
    private bool _showRestorePreview;

    [ObservableProperty]
    private string? _restorePreviewText;

    private OperationRestorePlan? _restorePlan;

    [ObservableProperty]
    private bool _showPurgePreview;

    [ObservableProperty]
    private string? _purgePreviewText;

    private OperationPurgePlan? _purgePlan;
    private UnifiedJobPlan? _jobPlan;
    private CrossLibrarySyncPlan? _crossLibrarySyncPlan;
    private PlaylistExportPlan? _playlistExportPlan;
    private ArtworkNormalizationPlan? _artworkNormalizationPlan;
    private DeviceSyncPlan? _deviceSyncPlan;
    private SmartStoragePlan? _smartStoragePlan;
    private CarCardPlan? _carCardPlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJobApply))]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistName))]
    [NotifyPropertyChangedFor(nameof(ShowDevicePaths))]
    [NotifyPropertyChangedFor(nameof(ShowDestinationPath))]
    [NotifyPropertyChangedFor(nameof(ShowValidationPath))]
    [NotifyPropertyChangedFor(nameof(ShowRemovalLimit))]
    [NotifyPropertyChangedFor(nameof(ShowInitialize))]
    [NotifyPropertyChangedFor(nameof(ShowRebalance))]
    [NotifyPropertyChangedFor(nameof(ShowFixErrors))]
    [NotifyPropertyChangedFor(nameof(ShowRemap))]
    [NotifyPropertyChangedFor(nameof(UsesActiveLibraryContext))]
    private UnifiedJobDescriptor? _selectedJob;
    [ObservableProperty]
    private string _jobStatus = "Choose a job, supply any required arguments, then Preview.";
    [ObservableProperty]
    private string _jobOutput = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJobApply))]
    private bool _hasJobPreview;

    [ObservableProperty] private string _jobPlaylistName = "";
    [ObservableProperty] private string _jobSourcePath = "";
    [ObservableProperty] private string _jobDestinationPath = "";
    [ObservableProperty] private string _jobValidationPath = "";
    [ObservableProperty] private int _jobMaxRemovals;
    [ObservableProperty] private bool _jobInitialize;
    [ObservableProperty] private bool _jobRebalance;
    [ObservableProperty] private bool _jobFixErrors;
    [ObservableProperty] private bool _jobRemap;

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];
    public ObservableCollection<OperationEntryNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<UnifiedJobHistoryItem> JobHistory { get; } = [];
#if MUSIC_LIBRARY_MANAGER
    public IReadOnlyList<UnifiedJobDescriptor> JobCatalog => _jobs?.Catalog
        .Where(job => job.Id != "device-sync")
        .ToArray() ?? [];
#else
    public IReadOnlyList<UnifiedJobDescriptor> JobCatalog => _jobs?.Catalog ?? [];
#endif
    public bool ShowJobApply => HasJobPreview &&
        SelectedJob?.ApplyMode == UnifiedJobApplyMode.ApplyFlag;
    private bool JobIs(params string[] ids) => SelectedJob is not null && ids.Contains(SelectedJob.Id);
    public bool ShowPlaylistName => JobIs("artwork-normalization");
    public bool ShowDevicePaths => JobIs("device-sync");
    public bool ShowDestinationPath => JobIs("smart-storage");
    public bool ShowValidationPath => JobIs("itunes-validation");
    public bool ShowRemovalLimit => JobIs("device-sync", "smart-storage", "car-card");
    public bool ShowInitialize => JobIs("smart-storage", "car-card");
    public bool ShowRebalance => JobIs("car-card");
    public bool ShowFixErrors => JobIs("car-card");
    public bool ShowRemap => JobIs("device-sync");
    public bool UsesActiveLibraryContext =>
        JobIs("playlist-sync", "cross-library-sync", "car-card");

    public OperationsViewModel(
        IOperationJournalService journals,
        IFileDialogService files,
        IDialogService dialogs,
        IAppSettings settings,
        IUnifiedJobService? jobs = null,
        ICrossLibrarySyncService? crossLibrarySync = null,
        IPlaylistExportService? playlistExport = null,
        IRedundancyAnalysisService? redundancyAnalysis = null,
        IItunesValidationService? itunesValidation = null,
        IArtworkNormalizationService? artworkNormalization = null,
        IDeviceSyncService? deviceSync = null,
        ISmartStorageService? smartStorage = null,
        ICarCardService? carCard = null)
    {
        _journals = journals;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _jobs = jobs;
        _crossLibrarySync = crossLibrarySync;
        _playlistExport = playlistExport;
        _redundancyAnalysis = redundancyAnalysis;
        _itunesValidation = itunesValidation;
        _artworkNormalization = artworkNormalization;
        _deviceSync = deviceSync;
        _smartStorage = smartStorage;
        _carCard = carCard;
        SearchRoot = settings.GetPreference(SearchRootPreference);
        if (int.TryParse(settings.GetPreference(RetentionDaysPreference), out int days))
            RetentionDays = Math.Clamp(days, 1, 3650);
        LoadJobHistory();
        SelectedJob = JobCatalog.FirstOrDefault();
        _settings.ConfigurationChanged += (_, _) => PopulateDefaultJobInputs();
    }

    partial void OnSelectedJobChanged(UnifiedJobDescriptor? value)
    {
        InvalidateJobPreview();
        PopulateDefaultJobInputs();
    }

    private void PopulateDefaultJobInputs()
    {
        InvalidateJobPreview();
    }

    partial void OnJobPlaylistNameChanged(string value) => InvalidateJobPreview();
    partial void OnJobSourcePathChanged(string value) => InvalidateJobPreview();
    partial void OnJobDestinationPathChanged(string value) => InvalidateJobPreview();
    partial void OnJobValidationPathChanged(string value) => InvalidateJobPreview();
    partial void OnJobMaxRemovalsChanged(int value) => InvalidateJobPreview();
    partial void OnJobInitializeChanged(bool value) => InvalidateJobPreview();
    partial void OnJobRebalanceChanged(bool value) => InvalidateJobPreview();
    partial void OnJobFixErrorsChanged(bool value) => InvalidateJobPreview();
    partial void OnJobRemapChanged(bool value) => InvalidateJobPreview();

    [RelayCommand]
    private async Task BrowseJobSourceAsync()
    {
        string? path = await _files.PickFolderAsync("Select device-sync source");
        if (path is not null) JobSourcePath = path;
    }

    [RelayCommand]
    private async Task BrowseJobDestinationAsync()
    {
        string? path = await _files.PickFolderAsync(SelectedJob?.Id == "device-sync"
            ? "Select device-sync destination" : "Select smart-storage destination");
        if (path is not null) JobDestinationPath = path;
    }

    [RelayCommand]
    private async Task BrowseJobValidationAsync()
    {
        string? path = await _files.PickOpenFileAsync("Select iTunes library to validate",
            [new("iTunes library", ["*.itl"])]);
        if (path is not null) JobValidationPath = path;
    }
    partial void OnSearchRootChanged(string? value) =>
        _settings.SetPreference(SearchRootPreference, string.IsNullOrWhiteSpace(value) ? null : value);

    partial void OnRetentionDaysChanged(int value)
    {
        if (value < 1)
            return;
        _settings.SetPreference(RetentionDaysPreference, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        InvalidatePurgePreview();
        PreviewPurgeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseRootAsync()
    {
        string? path = await _files.PickFolderAsync("Select the source, sync, or device root to scan");
        if (path is not null)
            SearchRoot = path;
    }

    private bool CanPreviewJob() => !IsBusy && _jobs is not null && SelectedJob is not null;

    [RelayCommand(CanExecute = nameof(CanPreviewJob))]
    private async Task PreviewJobAsync()
    {
        if (_jobs is null || SelectedJob is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        JobOutput = "";
        JobStatus = $"Previewing {SelectedJob.Name}…";
        try
        {
            if (SelectedJob.Id == "cross-library-sync" && _crossLibrarySync is not null)
            {
                IReadOnlyList<string> parsed = [];
                CrossLibrarySyncRequest request = new(
                    ConfigurationPath: null,
                    ItunesLibraryPath: null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _crossLibrarySyncPlan = await _crossLibrarySync.PreviewAsync(
                    request, typedProgress, _cts.Token);
                int exitCode = _crossLibrarySyncPlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderCrossLibrarySyncPlan(_crossLibrarySyncPlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "playlist-sync" && _playlistExport is not null)
            {
                IReadOnlyList<string> parsed = [];
                PlaylistExportRequest request = new(
                    ConfigurationPath: null,
                    ItunesLibraryPath: null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _playlistExportPlan = await _playlistExport.PreviewAsync(
                    request, typedProgress, _cts.Token);
                int exitCode = _playlistExportPlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderPlaylistExportPlan(_playlistExportPlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "artwork-normalization" && _artworkNormalization is not null)
            {
                IReadOnlyList<string> parsed = [];
                ArtworkNormalizationRequest request = new(Required(JobPlaylistName,
                    "An iTunes playlist name is required."), ConfiguredLibraryPath());
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _artworkNormalizationPlan = await _artworkNormalization.PreviewAsync(
                    request, typedProgress, _cts.Token);
                int exitCode = _artworkNormalizationPlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderArtworkNormalizationPlan(_artworkNormalizationPlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "device-sync" && _deviceSync is not null)
            {
                IReadOnlyList<string> parsed = [];
                DeviceSyncRequest request = new(Required(JobSourcePath, "A source path is required."),
                    Required(JobDestinationPath, "A destination path is required."), JobRemap,
                    JobMaxRemovals);
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _deviceSyncPlan = await _deviceSync.PreviewAsync(request, typedProgress, _cts.Token);
                int exitCode = _deviceSyncPlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderDeviceSyncPlan(_deviceSyncPlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "smart-storage" && _smartStorage is not null)
            {
                IReadOnlyList<string> parsed = [];
                SmartStorageRequest request = new(Required(JobDestinationPath,
                    "A smart-storage destination is required."), JobInitialize, JobMaxRemovals,
                    ConfiguredLibraryPath());
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _smartStoragePlan = await _smartStorage.PreviewAsync(
                    request, typedProgress, _cts.Token);
                int exitCode = _smartStoragePlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderSmartStoragePlan(_smartStoragePlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "car-card" && _carCard is not null)
            {
                IReadOnlyList<string> parsed = [];
                CarCardRequest request = new(null, JobRebalance, JobFixErrors,
                    JobInitialize, JobMaxRemovals, null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                _carCardPlan = await _carCard.PreviewAsync(request, typedProgress, _cts.Token);
                int exitCode = _carCardPlan.CanApply ? 0 : 4;
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    RenderCarCardPlan(_carCardPlan), DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "redundancies" && _redundancyAnalysis is not null)
            {
                IReadOnlyList<string> parsed = [];
                string? library = ConfiguredLibraryPath();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                RedundancyAnalysisResult result = await _redundancyAnalysis.AnalyzeAsync(
                    library, typedProgress, _cts.Token);
                _jobPlan = new(SelectedJob, parsed, 0, RenderRedundancyResult(result),
                    DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "itunes-validation" && _itunesValidation is not null)
            {
                IReadOnlyList<string> parsed = [];
                string validationPath = Required(JobValidationPath,
                    "An iTunes Library.itl path is required.");
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                ItunesValidationResult result = await _itunesValidation.ValidateAsync(
                    validationPath, typedProgress, _cts.Token);
                _jobPlan = new(SelectedJob, parsed, result.IsValid ? 0 : 4,
                    RenderValidationResult(result), DateTimeOffset.UtcNow);
            }
            else
            {
                throw new InvalidOperationException(
                    $"No typed service is available for '{SelectedJob.Name}'.");
            }
            HasJobPreview = true;
            JobOutput = _jobPlan.PreviewOutput;
            JobStatus = _jobPlan.PreviewExitCode == 0
                ? $"Preview completed. Review output before applying."
                : $"Preview exited with code {_jobPlan.PreviewExitCode}.";
            AddJobHistory(new(SelectedJob.Name, false, _jobPlan.PreviewExitCode == 0,
                _jobPlan.CreatedAtUtc, 0, TrimOutput(JobOutput)));
        }
        catch (OperationCanceledException) { JobStatus = "Job preview cancelled."; }
        catch (Exception ex) { JobStatus = $"Job preview failed: {ex.Message}"; }
        finally
        {
            _cts?.Dispose(); _cts = null; IsBusy = false;
            ApplyJobCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApplyJob() => !IsBusy && _jobPlan?.CanApply == true && HasJobPreview;

    [RelayCommand(CanExecute = nameof(CanApplyJob))]
    private async Task ApplyJobAsync()
    {
        if (_jobs is null || _jobPlan is not { CanApply: true } plan)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        JobOutput = "";
        try
        {
            UnifiedJobResult result;
            if (plan.Job.Id == "cross-library-sync" && _crossLibrarySync is not null &&
                _crossLibrarySyncPlan is { CanApply: true } typedPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                CrossLibrarySyncResult typedResult = await _crossLibrarySync.ApplyAsync(
                    typedPlan, typedProgress, _cts.Token);
                clock.Stop();
                string output = RenderCrossLibrarySyncResult(typedResult);
                result = new(0, output, clock.Elapsed);
            }
            else if (plan.Job.Id == "playlist-sync" && _playlistExport is not null &&
                     _playlistExportPlan is { CanApply: true } playlistPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                PlaylistExportResult typedResult = await _playlistExport.ApplyAsync(
                    playlistPlan, typedProgress, _cts.Token);
                clock.Stop();
                result = new(0, RenderPlaylistExportResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "artwork-normalization" && _artworkNormalization is not null &&
                     _artworkNormalizationPlan is { CanApply: true } artworkPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                ArtworkNormalizationResult typedResult = await _artworkNormalization.ApplyAsync(
                    artworkPlan, typedProgress, _cts.Token);
                clock.Stop();
                if (typedResult.UpdatedPaths.Count > 0)
                    ArtworkNormalized?.Invoke(typedResult.UpdatedPaths);
                result = new(0, RenderArtworkNormalizationResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "device-sync" && _deviceSync is not null &&
                     _deviceSyncPlan is { CanApply: true } devicePlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                DeviceSyncResult typedResult = await _deviceSync.ApplyAsync(
                    devicePlan, typedProgress, _cts.Token);
                clock.Stop();
                result = new(0, RenderDeviceSyncResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "smart-storage" && _smartStorage is not null &&
                     _smartStoragePlan is { CanApply: true } smartPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                SmartStorageResult typedResult = await _smartStorage.ApplyAsync(
                    smartPlan, typedProgress, _cts.Token);
                clock.Stop();
                result = new(0, RenderSmartStorageResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "car-card" && _carCard is not null &&
                     _carCardPlan is { CanApply: true } carCardPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    JobStatus = value.Message ?? value.Phase.ToString());
                CarCardResult typedResult = await _carCard.ApplyAsync(
                    carCardPlan, typedProgress, _cts.Token);
                clock.Stop();
                result = new(0, RenderCarCardResult(typedResult), clock.Elapsed);
            }
            else
            {
                throw new InvalidOperationException(
                    $"No typed apply service is available for '{plan.Job.Name}'.");
            }
            JobOutput = result.Output;
            JobStatus = result.Success ? $"{plan.Job.Name} applied successfully."
                : $"Apply exited with code {result.ExitCode}. Review the output and operation journal.";
            AddJobHistory(new(plan.Job.Name, true, result.Success, DateTimeOffset.UtcNow,
                result.Elapsed.TotalSeconds, TrimOutput(result.Output)));
            InvalidateJobPreview(clearOutput: false);
        }
        catch (OperationCanceledException) { JobStatus = "Job apply cancelled; inspect Operations for a recovery journal."; }
        catch (Exception ex) { JobStatus = $"Job apply failed: {ex.Message}"; }
        finally { _cts?.Dispose(); _cts = null; IsBusy = false; }
    }

    private void InvalidateJobPreview(bool clearOutput = true)
    {
        _jobPlan = null;
        _crossLibrarySyncPlan = null;
        _playlistExportPlan = null;
        _artworkNormalizationPlan = null;
        _deviceSyncPlan = null;
        _smartStoragePlan = null;
        _carCardPlan = null;
        HasJobPreview = false;
        if (clearOutput) JobOutput = "";
        ApplyJobCommand.NotifyCanExecuteChanged();
    }

    private void LoadJobHistory()
    {
        try
        {
            foreach (var item in JsonSerializer.Deserialize<List<UnifiedJobHistoryItem>>(
                         _settings.GetPreference(JobHistoryPreference) ?? "[]") ?? [])
                JobHistory.Add(item);
        }
        catch { }
    }

    private void AddJobHistory(UnifiedJobHistoryItem item)
    {
        JobHistory.Insert(0, item);
        while (JobHistory.Count > 30) JobHistory.RemoveAt(JobHistory.Count - 1);
        _settings.SetPreference(JobHistoryPreference, JsonSerializer.Serialize(JobHistory));
    }

    private static string TrimOutput(string output) => output.Length <= 20_000 ? output : output[^20_000..];
    private static string Required(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();
    private string ConfiguredLibraryPath() =>
        _settings.Configuration?.ItunesLibraryPath
        ?? throw new ArgumentException(
            "Set the iTunes library path in the active library configuration.");

    private static string RenderCrossLibrarySyncPlan(CrossLibrarySyncPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(action.Kind == FileMutationKind.Delete
                ? $"{action.Kind,-10} {action.SourcePath}"
                : $"{action.Kind,-10} {action.SourcePath} -> {action.DestinationPath}");
        output.AppendLine($"Plan: {plan.Files.Count:N0} desired, {plan.UnchangedCount:N0} unchanged, " +
            $"{plan.StaleCount:N0} stale, {plan.MutationPlan.Actions.Count:N0} mutations.");
        return output.ToString();
    }

    private static string RenderCrossLibrarySyncResult(CrossLibrarySyncResult result) =>
        $"Applied: {result.Mutations.Copied:N0} copied, {result.Mutations.Replaced:N0} replaced, " +
        $"{result.Mutations.Quarantined:N0} quarantined, {result.Mutations.Deleted:N0} deleted, " +
        $"{result.UnchangedCount:N0} unchanged." +
        (result.Mutations.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.Mutations.JournalPath);

    private static string RenderArtworkNormalizationPlan(ArtworkNormalizationPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (ArtworkNormalizationItem item in plan.Items)
            output.AppendLine($"REPLACE    {item.Path}: {item.Current.MimeType}, " +
                $"{item.Current.Width}x{item.Current.Height}, {item.Current.Size:N0} bytes -> " +
                $"image/jpeg, {item.Proposed.Width}x{item.Proposed.Height}, " +
                $"{item.Proposed.Size:N0} bytes");
        output.AppendLine($"Plan: {plan.ScannedTrackCount:N0} tracks inspected, " +
            $"{plan.UnchangedCount:N0} already valid, {plan.Items.Count:N0} media files to replace.");
        return output.ToString();
    }

    private static string RenderArtworkNormalizationResult(ArtworkNormalizationResult result) =>
        $"Applied: {result.UpdatedFileCount:N0} media files and " +
        $"{result.UpdatedTrackCount:N0} ITL track caches updated." +
        (result.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.JournalPath) +
        (result.CacheError is null ? "" :
            Environment.NewLine + "Cache warning: " + result.CacheError);

    private static string RenderDeviceSyncPlan(DeviceSyncPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (DeviceSyncAction action in plan.Actions)
            output.AppendLine($"{action.Kind,-20} {action.RelativePath}");
        output.AppendLine($"Plan: {plan.Actions.Count:N0} action(s), " +
            $"{plan.UnchangedFileCount:N0} unchanged file(s), {plan.RemovalCount:N0} removal(s).");
        return output.ToString();
    }

    private static string RenderDeviceSyncResult(DeviceSyncResult result) =>
        $"Applied: {result.CreatedDirectoryCount:N0} directories created, " +
        $"{result.CopiedFileCount:N0} files copied, {result.ReplacedFileCount:N0} replaced, " +
        $"{result.QuarantinedCount:N0} quarantined." +
        (result.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.JournalPath);

    private static string RenderSmartStoragePlan(SmartStoragePlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine($"{action.Kind,-16} {action.DestinationPath}");
        output.AppendLine($"Plan: {plan.LibraryTrackCount:N0} tracks, " +
            $"{plan.InstalledTrackCount:N0} installs, {plan.UnchangedTrackCount:N0} unchanged, " +
            $"{plan.StaleTrackCount:N0} stale, {plan.PlaylistCount:N0} playlists, " +
            $"{plan.ArtworkCount:N0} artwork items.");
        return output.ToString();
    }

    private static string RenderSmartStorageResult(SmartStorageResult result) =>
        $"Applied {result.LibraryTrackCount:N0} tracks, {result.PlaylistCount:N0} playlists, " +
        $"and {result.ArtworkCount:N0} artwork items: {result.Mutations.Copied:N0} created, " +
        $"{result.Mutations.Replaced:N0} replaced, {result.Mutations.Quarantined:N0} quarantined." +
        (result.Mutations.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.Mutations.JournalPath);

    private static string RenderCarCardPlan(CarCardPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine($"{action.Kind,-16} {action.DestinationPath}");
        output.AppendLine($"Plan: {plan.LibraryTrackCount:N0} tracks, " +
            $"{plan.InstalledTrackCount:N0} installs, {plan.UnchangedTrackCount:N0} unchanged, " +
            $"{plan.RemovedTrackCount:N0} removals, {plan.PlaylistCount:N0} playlists.");
        return output.ToString();
    }

    private static string RenderCarCardResult(CarCardResult result) =>
        $"Applied {result.LibraryTrackCount:N0} tracks and {result.PlaylistCount:N0} playlists: " +
        $"{result.Mutations.Copied:N0} created, {result.Mutations.Replaced:N0} replaced, " +
        $"{result.Mutations.Quarantined:N0} quarantined." +
        (result.Mutations.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.Mutations.JournalPath);

    private static string RenderPlaylistExportPlan(PlaylistExportPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (PlaylistExportTargetPlan target in plan.Targets)
            output.AppendLine($"Target {target.Target}: {target.Files.Count:N0} playlist(s), " +
                $"{target.MissingTrackCount:N0} missing mapping(s).");
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(action.Kind == FileMutationKind.Delete
                ? $"{action.Kind,-16} {action.SourcePath}"
                : $"{action.Kind,-16} {action.DestinationPath}");
        return output.ToString();
    }

    private static string RenderPlaylistExportResult(PlaylistExportResult result) =>
        $"Applied {result.PlaylistCount:N0} playlist(s): {result.Mutations.Copied:N0} created, " +
        $"{result.Mutations.Replaced:N0} replaced, " +
        $"{result.Mutations.Quarantined:N0} quarantined, " +
        $"{result.Mutations.Deleted:N0} deleted." +
        (result.Mutations.JournalPath is null ? "" :
            Environment.NewLine + "Recovery journal: " + result.Mutations.JournalPath);

    private static string RenderRedundancyResult(RedundancyAnalysisResult result)
    {
        var output = new StringBuilder();
        foreach (RedundancyGroup group in result.Groups)
        {
            foreach (RedundancyTrack track in group.Tracks)
                output.AppendLine($"{track.Artist} - {track.Title} ({track.Album}) [{track.Path}]");
            output.AppendLine();
        }
        output.AppendLine($"{result.Groups.Count:N0} redundancy group(s) among " +
            $"{result.ScannedTrackCount:N0} local tracks.");
        return output.ToString();
    }

    private static string RenderValidationResult(ItunesValidationResult result)
    {
        var output = new StringBuilder();
        foreach (var issue in result.Issues)
            output.AppendLine($"{issue.Severity,-7} {issue.Code,-30} {issue.Message}");
        output.AppendLine($"validation: {result.ErrorCount} error(s), " +
            $"{result.WarningCount} warning(s)");
        return output.ToString();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var roots = CollectSearchRoots();
        if (roots.Count == 0)
        {
            StatusText = "No configuration or search root is available. Choose a folder to scan.";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Scanning {roots.Count:N0} root(s) for operation journals…";
        try
        {
            var result = await _journals.DiscoverAsync(roots, _cts.Token);
            Runs.Clear();
            RootNodes.Clear();
            SelectedRun = null;
            ShowBrowser = false;
            InvalidateRestorePreview();
            InvalidatePurgePreview();
            foreach (var run in result.Runs)
                Runs.Add(new OperationRunViewModel(run));
            int interrupted = result.Runs.Count(run => run.State == OperationJournalState.Interrupted);
            StatusText = $"Found {result.Runs.Count:N0} operation run(s); {interrupted:N0} interrupted"
                + (result.Warnings.Count == 0 ? "." : $"; {result.Warnings.Count:N0} root(s) could not be scanned.");
            PreviewPurgeCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation discovery cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Operation discovery failed: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanOpenRun(OperationRunViewModel? run) => !IsBusy && run is not null;

    [RelayCommand(CanExecute = nameof(CanOpenRun))]
    private async Task OpenRunAsync(OperationRunViewModel? run)
    {
        if (run is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Opening {run.ToolName} operation…";
        try
        {
            var browse = await _journals.BrowseAsync(run.Summary, _cts.Token);
            RootNodes.Clear();
            var root = OperationEntryNodeViewModel.Build(browse);
            root.SelectionChanged += OnRestoreSelectionChanged;
            RootNodes.Add(root);
            SelectedRun = run;
            ShowBrowser = true;
            InvalidateRestorePreview();
            StatusText = $"{browse.Entries.Count:N0} operation item(s) in their original hierarchy"
                + (browse.Warnings.Count == 0 ? "." : $"; {browse.Warnings.Count:N0} item(s) could not be read.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Opening operation cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open operation: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    public async Task OpenRunFromHistoryAsync(OperationJournalSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var run = Runs.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.RunPath, summary.RunPath));
        if (run is null)
        {
            run = new OperationRunViewModel(summary);
            Runs.Insert(0, run);
        }
        await OpenRunAsync(run);
    }

    [RelayCommand]
    private void CloseBrowser()
    {
        ShowBrowser = false;
        SelectedRun = null;
        RootNodes.Clear();
        InvalidateRestorePreview();
        StatusText = $"Showing {Runs.Count:N0} discovered operation run(s).";
    }

    private void OnRestoreSelectionChanged()
    {
        InvalidateRestorePreview();
        PreviewRestoreCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAllRestorable()
    {
        foreach (var root in RootNodes)
            root.SetSelection(true);
        PreviewRestoreCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearRestoreSelection()
    {
        foreach (var root in RootNodes)
            root.SetSelection(false);
        PreviewRestoreCommand.NotifyCanExecuteChanged();
    }

    private bool CanPreviewRestore() => !IsBusy && SelectedRun is not null &&
        RootNodes.SelectMany(root => root.SelectedEntries()).Any();

    [RelayCommand(CanExecute = nameof(CanPreviewRestore))]
    private async Task PreviewRestoreAsync()
    {
        if (SelectedRun is null)
            return;
        var entries = RootNodes.SelectMany(root => root.SelectedEntries()).ToList();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            _restorePlan = await _journals.PreviewRestoreAsync(SelectedRun.Summary, entries, _cts.Token);
            ShowRestorePreview = _restorePlan.CanApply;
            RestorePreviewText = _restorePlan.CanApply
                ? $"Restore preview: {_restorePlan.Actions.Count:N0} item(s), " +
                  $"{_restorePlan.CollisionCount:N0} destination collision(s), " +
                  $"{_restorePlan.SkippedCount:N0} skipped."
                : "No selected entries are currently recoverable.";
            StatusText = RestorePreviewText;
        }
        catch (OperationCanceledException) { StatusText = "Restore preview cancelled."; }
        catch (Exception ex) { StatusText = $"Restore preview failed: {ex.Message}"; }
        finally
        {
            _cts?.Dispose(); _cts = null; IsBusy = false;
            ApplyRestoreCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApplyRestore() => !IsBusy && _restorePlan?.CanApply == true;

    [RelayCommand(CanExecute = nameof(CanApplyRestore))]
    private async Task ApplyRestoreAsync()
    {
        if (_restorePlan is not { } plan || SelectedRun is null ||
            !await _dialogs.ConfirmRestoreAsync(plan))
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<int>(count =>
                StatusText = $"Restoring… {count:N0}/{plan.Actions.Count:N0}");
            var result = await _journals.ApplyRestoreAsync(plan, progress, _cts.Token);
            var browse = await _journals.BrowseAsync(SelectedRun.Summary, CancellationToken.None);
            RootNodes.Clear();
            var root = OperationEntryNodeViewModel.Build(browse);
            root.SelectionChanged += OnRestoreSelectionChanged;
            RootNodes.Add(root);
            InvalidateRestorePreview();
            StatusText = $"Restored {result.RestoredCount:N0} item(s); " +
                $"preserved {result.CollisionBackupCount:N0} collision(s).";
        }
        catch (OperationCanceledException) { StatusText = "Restore cancelled and completed actions were rolled back."; }
        catch (Exception ex) { StatusText = $"Restore failed and completed actions were rolled back: {ex.Message}"; }
        finally
        {
            _cts?.Dispose(); _cts = null; IsBusy = false;
            PreviewRestoreCommand.NotifyCanExecuteChanged();
            ApplyRestoreCommand.NotifyCanExecuteChanged();
        }
    }

    private void InvalidateRestorePreview()
    {
        _restorePlan = null;
        ShowRestorePreview = false;
        RestorePreviewText = null;
        ApplyRestoreCommand.NotifyCanExecuteChanged();
    }

    private bool CanPreviewPurge() => !IsBusy && Runs.Count > 0 && RetentionDays >= 1;

    [RelayCommand(CanExecute = nameof(CanPreviewPurge))]
    private async Task PreviewPurgeAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Inventorying operation runs older than {RetentionDays:N0} day(s)…";
        try
        {
            _purgePlan = await _journals.PreviewPurgeAsync(
                Runs.Select(run => run.Summary).ToList(), RetentionDays, null, _cts.Token);
            ShowPurgePreview = true;
            PurgePreviewText = DescribePurgePlan(_purgePlan);
            StatusText = PurgePreviewText;
        }
        catch (OperationCanceledException) { StatusText = "Purge preview cancelled."; }
        catch (Exception ex) { StatusText = $"Purge preview failed: {ex.Message}"; }
        finally
        {
            _cts?.Dispose(); _cts = null; IsBusy = false;
            ApplyPurgeCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApplyPurge() => !IsBusy && _purgePlan?.CanApply == true;

    [RelayCommand(CanExecute = nameof(CanApplyPurge))]
    private async Task ApplyPurgeAsync()
    {
        if (_purgePlan is not { CanApply: true } plan ||
            !await _dialogs.ConfirmPurgeAsync(plan))
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<int>(count =>
                StatusText = $"Purging… {count:N0}/{plan.Runs.Count:N0} run(s)");
            var result = await _journals.ApplyPurgeAsync(plan, progress, _cts.Token);
            var deleted = plan.Runs.Select(run => run.Run.RunPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var run in Runs.Where(run => deleted.Contains(run.RunPath)).ToList())
                Runs.Remove(run);
            InvalidatePurgePreview();
            StatusText = $"Purged {result.RunsDeleted:N0} run(s), {result.FilesDeleted:N0} file(s), " +
                $"and {FormatBytes(result.BytesDeleted)}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Purge cancelled. Runs not yet irreversibly deleted may remain in purge staging.";
        }
        catch (Exception ex)
        {
            StatusText = $"Purge stopped: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose(); _cts = null; IsBusy = false;
            PreviewPurgeCommand.NotifyCanExecuteChanged();
            ApplyPurgeCommand.NotifyCanExecuteChanged();
        }
    }

    private void InvalidatePurgePreview()
    {
        _purgePlan = null;
        ShowPurgePreview = false;
        PurgePreviewText = null;
        ApplyPurgeCommand.NotifyCanExecuteChanged();
    }

    private static string DescribePurgePlan(OperationPurgePlan plan)
    {
        string eligible = plan.CanApply
            ? $"Purge preview: {plan.Runs.Count:N0} run(s), {plan.FileCount:N0} file(s), {FormatBytes(plan.TotalBytes)}"
            : "Purge preview: no runs are old enough to purge";
        string backups = plan.RestoreBackupFileCount > 0
            ? $", including {plan.RestoreBackupFileCount:N0} restore-collision backup file(s)"
            : "";
        return eligible + backups + $". Protected {plan.ProtectedInterruptedCount:N0} interrupted run(s); " +
            $"{plan.NewerCount:N0} run(s) remain within retention" +
            (plan.ProtectedUnsafeCount == 0 ? "." : $"; {plan.ProtectedUnsafeCount:N0} unsafe run root(s) were excluded.");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    internal IReadOnlyList<string> CollectSearchRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                roots.Add(path);
        }

        Add(SearchRoot);
#if MUSIC_LIBRARY_MANAGER
        Add(_settings.GetPreference("manager.ingest.source.v1"));
#else
        Add(_settings.GetPreference("Ingest.SourceDirectory"));
#endif
        var snapshot = _settings.GetSnapshot();
        if (snapshot.ConfigPath is not null)
            Add(Path.GetDirectoryName(snapshot.ConfigPath));
        if (snapshot.Configuration is { } configuration)
        {
            foreach (var location in configuration.IndexLocations)
                Add(location.Target);
            foreach (string key in new[] { "PlaylistTarget" })
                foreach (string value in configuration[key])
                    Add(value);
        }
        return roots.ToList();
    }
}

public sealed class OperationRunViewModel
{
    public OperationJournalSummary Summary { get; }
    public string ToolName => Summary.ToolName;
    public string Kind => Summary.Kind.ToString();
    public string State => Summary.State switch
    {
        OperationJournalState.Completed => "Completed",
        OperationJournalState.Interrupted => "Interrupted — recovery may be required",
        OperationJournalState.RolledBack => "Rolled back",
        _ => "Quarantine present — terminal state not recorded",
    };
    public bool IsInterrupted => Summary.State == OperationJournalState.Interrupted;
    public string Created => Summary.CreatedAtUtc.ToLocalTime().ToString("g");
    public string RunPath => Summary.RunPath;
    public string Journal => Summary.JournalPath ?? "No journal (folder-only quarantine)";
    public string AffectedItems => Summary.AffectedItemCount is int count
        ? $"{count:N0} recorded item(s)"
        : "Item count available when the run is opened";

    public OperationRunViewModel(OperationJournalSummary summary) => Summary = summary;
}

public partial class OperationEntryNodeViewModel : ViewModelBase
{
    public string Name { get; }
    public string OriginalPath { get; }
    public string? CurrentPath { get; private set; }
    public OperationEntryKind? Kind { get; private set; }
    public bool Exists { get; private set; }
    public bool IsDirectory { get; private set; }
    public List<OperationEntryNodeViewModel> Children { get; } = [];
    public bool HasEntry => Kind is not null;
    public bool CanRestore => HasEntry && Exists && CurrentPath is not null &&
        Kind is OperationEntryKind.Quarantined or OperationEntryKind.Moved or OperationEntryKind.Planned;

    [ObservableProperty]
    private bool _isSelected;

    public event Action? SelectionChanged;
    public string StateText => Kind switch
    {
        OperationEntryKind.Quarantined => Exists ? "Quarantined" : "Quarantine copy missing",
        OperationEntryKind.Moved => Exists ? "Moved" : "Move destination missing",
        OperationEntryKind.Created => Exists ? "Created" : "Created item missing",
        OperationEntryKind.Deleted => "Deleted",
        OperationEntryKind.Planned => "Planned; completion not recorded",
        OperationEntryKind.Unknown => "Unknown operation",
        _ => "",
    };

    private OperationEntryNodeViewModel(string name, string originalPath)
    {
        Name = name;
        OriginalPath = originalPath;
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public void SetSelection(bool selected)
    {
        if (CanRestore)
            IsSelected = selected;
        foreach (var child in Children)
            child.SetSelection(selected);
    }

    public IEnumerable<OperationFileEntry> SelectedEntries()
    {
        if (IsSelected && CanRestore)
            yield return new OperationFileEntry(
                OriginalPath, CurrentPath, Name, Kind!.Value, Exists, IsDirectory);
        foreach (var child in Children)
            foreach (var entry in child.SelectedEntries())
                yield return entry;
    }

    public static OperationEntryNodeViewModel Build(OperationBrowseResult browse)
    {
        var root = new OperationEntryNodeViewModel(browse.OriginalRoot, browse.OriginalRoot)
        {
            IsDirectory = true,
        };
        foreach (var entry in browse.Entries)
            root.Add(entry);
        root.SortChildren();
        return root;
    }

    private void Add(OperationFileEntry entry)
    {
        string relative = entry.RelativePath;
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            relative = Path.GetFileName(entry.OriginalPath);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            parts = [Path.GetFileName(entry.OriginalPath)];

        var parent = this;
        string path = OriginalPath;
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
            var child = parent.Children.FirstOrDefault(candidate =>
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, part));
            if (child is null)
            {
                child = new OperationEntryNodeViewModel(part, path) { IsDirectory = true };
                child.SelectionChanged += () => SelectionChanged?.Invoke();
                parent.Children.Add(child);
            }
            parent = child;
        }
        parent.CurrentPath = entry.CurrentPath;
        parent.Kind = entry.Kind;
        parent.Exists = entry.Exists;
        parent.IsDirectory = entry.IsDirectory;
    }

    private void SortChildren()
    {
        Children.Sort((left, right) =>
        {
            int directories = right.IsDirectory.CompareTo(left.IsDirectory);
            return directories != 0 ? directories :
                StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        foreach (var child in Children)
            child.SortChildren();
    }
}
