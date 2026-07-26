using System.Collections.ObjectModel;
using System.ComponentModel;
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
    string Output) : INotifyPropertyChanged
{
    private ILocalizationService? _localization;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string State => Get(Applied
        ? Success
            ? "Operations.History.State.Applied"
            : "Operations.History.State.ApplyFailed"
        : Success
            ? "Operations.History.State.PreviewPassed"
            : "Operations.History.State.PreviewFailed");

    public string Created => CreatedAt.ToLocalTime().ToString(
        "g",
        _localization?.CurrentUICulture);

    public string Elapsed => Format(
        "Operations.History.Elapsed",
        ElapsedSeconds);

    public void RefreshLocalizedText(
        ILocalizationService? localization)
    {
        _localization = localization;
        PropertyChanged?.Invoke(
            this,
            new(nameof(State)));
        PropertyChanged?.Invoke(
            this,
            new(nameof(Created)));
        PropertyChanged?.Invoke(
            this,
            new(nameof(Elapsed)));
    }

    private string Get(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);
}

/// <summary>
/// Localized presentation for a job descriptor. The descriptor and its ID remain the stable
/// semantic identity used by preview/apply logic and configuration persistence.
/// </summary>
public sealed class UnifiedJobChoiceViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localization;
    private string _name;
    private string _description;

    public UnifiedJobDescriptor Value { get; }
    public string Id => Value.Id;

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public UnifiedJobChoiceViewModel(
        UnifiedJobDescriptor value,
        ILocalizationService? localization)
    {
        Value = value;
        _localization = localization;
        _name = "";
        _description = "";
        RefreshLocalizedText();
    }

    public void RefreshLocalizedText()
    {
        Name = ResolveName();
        Description = ResolveDescription();
    }

    private string ResolveName()
    {
        if (Value.Id.StartsWith(
                UnifiedJobService.ConfiguredExportJobPrefix,
                StringComparison.Ordinal))
        {
            string profileName = Value.Name.StartsWith(
                    "Export: ",
                    StringComparison.Ordinal)
                ? Value.Name["Export: ".Length..]
                : Value.Name;
            return Format(
                "Operations.Job.ConfiguredExport.Name",
                profileName);
        }

        string? key = Value.Id switch
        {
            "playlist-sync" => "Operations.Job.PlaylistSync.Name",
            "artwork-normalization" => "Operations.Job.ArtworkNormalization.Name",
            "smart-storage" => "Operations.Job.SmartStorage.Name",
            "car-card" => "Operations.Job.CarCard.Name",
            "cross-library-sync" => "Operations.Job.CrossLibrarySync.Name",
            "redundancies" => "Operations.Job.Redundancies.Name",
            "itunes-validation" => "Operations.Job.ItunesValidation.Name",
            _ => null,
        };
        return key is null ? Value.Name : Get(key);
    }

    private string ResolveDescription()
    {
        string? key = Value.Id.StartsWith(
                UnifiedJobService.ConfiguredExportJobPrefix,
                StringComparison.Ordinal)
            ? "Operations.Job.ConfiguredExport.Description"
            : Value.Id switch
            {
                "playlist-sync" => "Operations.Job.PlaylistSync.Description",
                "artwork-normalization" =>
                    "Operations.Job.ArtworkNormalization.Description",
                "smart-storage" => "Operations.Job.SmartStorage.Description",
                "car-card" => "Operations.Job.CarCard.Description",
                "cross-library-sync" =>
                    "Operations.Job.CrossLibrarySync.Description",
                "redundancies" => "Operations.Job.Redundancies.Description",
                "itunes-validation" =>
                    "Operations.Job.ItunesValidation.Description",
                _ => null,
            };
        return key is null ? Value.Description : Get(key);
    }

    private string Get(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);
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
    private readonly ISmartStorageService? _smartStorage;
    private readonly ICarCardService? _carCard;
    private readonly IActivityService? _activities;
    private readonly IConfiguredExportService? _configuredExport;
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cts;
    private Func<string>? _statusTextRefresh;
    private Func<string>? _jobStatusRefresh;
    private Func<string>? _restorePreviewTextRefresh;
    private Func<string>? _purgePreviewTextRefresh;
    private Func<string>? _jobOutputRefresh;
    private bool _synchronizingSelectedJob;

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
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _retentionDays = 90;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

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
    private SmartStoragePlan? _smartStoragePlan;
    private CarCardPlan? _carCardPlan;
    private ConfiguredExportPlan? _configuredExportPlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJobApply))]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistName))]
    [NotifyPropertyChangedFor(nameof(ShowDestinationPath))]
    [NotifyPropertyChangedFor(nameof(ShowValidationPath))]
    [NotifyPropertyChangedFor(nameof(ShowRemovalLimit))]
    [NotifyPropertyChangedFor(nameof(ShowInitialize))]
    [NotifyPropertyChangedFor(nameof(ShowRebalance))]
    [NotifyPropertyChangedFor(nameof(ShowFixErrors))]
    [NotifyPropertyChangedFor(nameof(UsesActiveLibraryContext))]
    private UnifiedJobDescriptor? _selectedJob;
    [ObservableProperty]
    private string _jobStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJobStatusDiagnosticDetail))]
    private string? _jobStatusDiagnosticDetail;

    [ObservableProperty]
    private UnifiedJobChoiceViewModel? _selectedJobChoice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJobOutput))]
    private string _jobOutput = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJobApply))]
    private bool _hasJobPreview;

    [ObservableProperty] private string _jobPlaylistName = "";
    [ObservableProperty] private string _jobDestinationPath = "";
    [ObservableProperty] private string _jobValidationPath = "";
    [ObservableProperty] private int _jobMaxRemovals;
    [ObservableProperty] private bool _jobInitialize;
    [ObservableProperty] private bool _jobRebalance;
    [ObservableProperty] private bool _jobFixErrors;

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];
    public ObservableCollection<OperationEntryNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<OperationEntryNodeViewModel>
        RecoveryEntryNodes { get; } = [];
    public ObservableCollection<UnifiedJobHistoryItem> JobHistory { get; } = [];
    public ObservableCollection<UnifiedJobChoiceViewModel> JobChoices { get; } = [];
    public IReadOnlyList<UnifiedJobDescriptor> JobCatalog => _jobs?.Catalog ?? [];
    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(StatusDiagnosticDetail);
    public bool HasJobStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(JobStatusDiagnosticDetail);
    public bool HasJobOutput =>
        !string.IsNullOrWhiteSpace(JobOutput);
    public bool HasRuns => Runs.Count > 0;
    public bool IsRunListEmpty => Runs.Count == 0;
    public bool ShowJobApply => HasJobPreview &&
        SelectedJob?.ApplyMode == UnifiedJobApplyMode.ApplyFlag;
    private bool JobIs(params string[] ids) => SelectedJob is not null && ids.Contains(SelectedJob.Id);
    public bool ShowPlaylistName => JobIs("artwork-normalization");
    public bool ShowDestinationPath => JobIs("smart-storage");
    public bool ShowValidationPath => JobIs("itunes-validation");
    public bool ShowRemovalLimit => JobIs("smart-storage", "car-card");
    public bool ShowInitialize => JobIs("smart-storage", "car-card");
    public bool ShowRebalance => JobIs("car-card");
    public bool ShowFixErrors => JobIs("car-card");
    public bool UsesActiveLibraryContext =>
        JobIs("playlist-sync", "cross-library-sync", "car-card") ||
        IsConfiguredExportJob(SelectedJob?.Id);

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
        ISmartStorageService? smartStorage = null,
        ICarCardService? carCard = null,
        IActivityService? activities = null,
        IConfiguredExportService? configuredExport = null,
        ILocalizationService? localization = null)
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
        _smartStorage = smartStorage;
        _carCard = carCard;
        _activities = activities;
        _configuredExport = configuredExport;
        _localization = localization;
        RootNodes.CollectionChanged +=
            (_, _) => RebuildRecoveryEntryNodes();
        SetStatus("Operations.Status.Ready");
        SetJobStatus("Operations.Job.Status.Ready");
        SearchRoot = settings.GetLibraryPreference(SearchRootPreference);
        if (int.TryParse(settings.GetLibraryPreference(RetentionDaysPreference), out int days))
            RetentionDays = Math.Clamp(days, 1, 3650);
        LoadJobHistory();
        RebuildJobChoices();
        SelectedJob = JobCatalog.FirstOrDefault();
        if (_localization is not null)
            _localization.CultureChanged +=
                (_, _) => RefreshLocalizedText();
        _settings.ConfigurationChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(JobCatalog));
            string? selectedJobId = SelectedJob?.Id;
            RebuildJobChoices();
            SelectedJob = JobCatalog.FirstOrDefault(job =>
                              StringComparer.Ordinal.Equals(
                                  job.Id,
                                  selectedJobId)) ??
                          JobCatalog.FirstOrDefault();
            JobHistory.Clear();
            LoadJobHistory();
            SearchRoot = _settings.GetLibraryPreference(SearchRootPreference);
            if (int.TryParse(_settings.GetLibraryPreference(RetentionDaysPreference), out int scopedDays))
                RetentionDays = Math.Clamp(scopedDays, 1, 3650);
            else
                RetentionDays = 90;
            PopulateDefaultJobInputs();
        };
    }

    partial void OnSelectedJobChanged(UnifiedJobDescriptor? value)
    {
        if (!_synchronizingSelectedJob)
        {
            _synchronizingSelectedJob = true;
            SelectedJobChoice = value is null
                ? null
                : JobChoices.FirstOrDefault(choice =>
                    StringComparer.Ordinal.Equals(
                        choice.Id,
                        value.Id));
            _synchronizingSelectedJob = false;
        }
        InvalidateJobPreview();
        PopulateDefaultJobInputs();
    }

    partial void OnSelectedJobChoiceChanged(
        UnifiedJobChoiceViewModel? value)
    {
        if (_synchronizingSelectedJob)
            return;
        _synchronizingSelectedJob = true;
        SelectedJob = value?.Value;
        _synchronizingSelectedJob = false;
    }

    private void RebuildJobChoices()
    {
        string? selectedJobId =
            SelectedJob?.Id ??
            SelectedJobChoice?.Id;
        JobChoices.Clear();
        foreach (UnifiedJobDescriptor job in JobCatalog)
            JobChoices.Add(
                new(job, _localization));

        _synchronizingSelectedJob = true;
        SelectedJobChoice = selectedJobId is null
            ? null
            : JobChoices.FirstOrDefault(choice =>
                StringComparer.Ordinal.Equals(
                    choice.Id,
                    selectedJobId));
        _synchronizingSelectedJob = false;
    }

    private void RefreshLocalizedText()
    {
        foreach (UnifiedJobChoiceViewModel choice in JobChoices)
            choice.RefreshLocalizedText();
        foreach (UnifiedJobHistoryItem item in JobHistory)
            item.RefreshLocalizedText(_localization);
        foreach (OperationRunViewModel run in Runs)
            run.RefreshLocalizedText();
        foreach (OperationEntryNodeViewModel root in RootNodes)
            root.RefreshLocalizedText();

        if (_statusTextRefresh is not null)
            StatusText = _statusTextRefresh();
        if (_jobStatusRefresh is not null)
            JobStatus = _jobStatusRefresh();
        if (_restorePreviewTextRefresh is not null)
            RestorePreviewText =
                _restorePreviewTextRefresh();
        if (_purgePreviewTextRefresh is not null)
            PurgePreviewText =
                _purgePreviewTextRefresh();
        if (_jobOutputRefresh is not null)
            JobOutput = _jobOutputRefresh();
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
        object?[] captured = [.. arguments];
        _statusTextRefresh =
            () => LF(key, captured);
        StatusDiagnosticDetail = null;
        StatusText = _statusTextRefresh();
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        object?[] captured = [.. arguments];
        _statusTextRefresh =
            () => LC(key, count, captured);
        StatusDiagnosticDetail = null;
        StatusText = _statusTextRefresh();
    }

    private void SetStatusFailure(
        string key,
        string diagnosticDetail)
    {
        SetStatus(key);
        StatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void SetJobStatus(
        string key,
        params object?[] arguments)
    {
        object?[] captured = [.. arguments];
        _jobStatusRefresh =
            () => LF(key, captured);
        JobStatusDiagnosticDetail = null;
        JobStatus = _jobStatusRefresh();
    }

    private void SetCountJobStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        object?[] captured = [.. arguments];
        _jobStatusRefresh =
            () => LC(key, count, captured);
        JobStatusDiagnosticDetail = null;
        JobStatus = _jobStatusRefresh();
    }

    private void SetJobStatusFailure(
        string key,
        string diagnosticDetail)
    {
        SetJobStatus(key);
        JobStatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void SetRestorePreview(
        string key,
        params object?[] arguments)
    {
        object?[] captured = [.. arguments];
        _restorePreviewTextRefresh =
            () => LF(key, captured);
        RestorePreviewText =
            _restorePreviewTextRefresh();
    }

    private void SetCountRestorePreview(
        string key,
        long count,
        params object?[] arguments)
    {
        object?[] captured = [.. arguments];
        _restorePreviewTextRefresh =
            () => LC(key, count, captured);
        RestorePreviewText =
            _restorePreviewTextRefresh();
    }

    private void SetPurgePreview(
        Func<string> refresh)
    {
        _purgePreviewTextRefresh = refresh;
        PurgePreviewText = refresh();
    }

    private void SetJobOutput(
        Func<string> refresh)
    {
        _jobOutputRefresh = refresh;
        JobOutput = refresh();
    }

    private string JobDisplayName(
        UnifiedJobDescriptor job) =>
        JobChoices.FirstOrDefault(choice =>
            StringComparer.Ordinal.Equals(
                choice.Id,
                job.Id))?.Name ??
        new UnifiedJobChoiceViewModel(
            job,
            _localization).Name;

    private void PopulateDefaultJobInputs()
    {
        InvalidateJobPreview();
        SetJobStatus(
            "Operations.Job.Status.Ready");
    }

    partial void OnJobPlaylistNameChanged(string value) => InvalidateJobPreview();
    partial void OnJobDestinationPathChanged(string value) => InvalidateJobPreview();
    partial void OnJobValidationPathChanged(string value) => InvalidateJobPreview();
    partial void OnJobMaxRemovalsChanged(int value) => InvalidateJobPreview();
    partial void OnJobInitializeChanged(bool value) => InvalidateJobPreview();
    partial void OnJobRebalanceChanged(bool value) => InvalidateJobPreview();
    partial void OnJobFixErrorsChanged(bool value) => InvalidateJobPreview();

    [RelayCommand]
    private async Task BrowseJobDestinationAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Operations.Dialog.SelectSmartStorageDestination"));
        if (path is not null) JobDestinationPath = path;
    }

    [RelayCommand]
    private async Task BrowseJobValidationAsync()
    {
        string? path = await _files.PickOpenFileAsync(
            L("Operations.Dialog.SelectItunesLibrary"),
            [new(
                L("Operations.FileType.ItunesLibrary"),
                ["*.itl"])]);
        if (path is not null) JobValidationPath = path;
    }
    partial void OnSearchRootChanged(string? value) =>
        _settings.SetLibraryPreference(SearchRootPreference, string.IsNullOrWhiteSpace(value) ? null : value);

    partial void OnRetentionDaysChanged(int value)
    {
        if (value < 1)
            return;
        _settings.SetLibraryPreference(RetentionDaysPreference, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        InvalidatePurgePreview();
        PreviewPurgeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseRootAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Operations.Dialog.SelectRecoveryRoot"));
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
        string jobName =
            JobDisplayName(SelectedJob);
        Guid? activity = _activities?.Start(
            LF(
                "Operations.Activity.Preview.Title",
                jobName),
            L("Operations.Activity.Preview.Starting"),
            ShellDestination.Operations,
            Cancel);
        _jobOutputRefresh = null;
        JobOutput = "";
        SetJobStatus(
            "Operations.Job.Status.Previewing",
            jobName);
        try
        {
            if (TryGetConfiguredExportProfileId(SelectedJob.Id, out string exportProfileId) &&
                _configuredExport is not null)
            {
                IReadOnlyList<string> parsed = [];
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _configuredExportPlan = await Task.Run(() => _configuredExport.PreviewAsync(
                    new(exportProfileId), typedProgress, _cts.Token), _cts.Token);
                int exitCode = _configuredExportPlan.CanApply ? 0 : 4;
                ConfiguredExportPlan renderedPlan =
                    _configuredExportPlan;
                string output = await Task.Run(
                    () => RenderConfiguredExportPlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderConfiguredExportPlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "cross-library-sync" && _crossLibrarySync is not null)
            {
                IReadOnlyList<string> parsed = [];
                CrossLibrarySyncRequest request = new(
                    ConfigurationPath: null,
                    ItunesLibraryPath: null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _crossLibrarySyncPlan = await Task.Run(() => _crossLibrarySync.PreviewAsync(
                    request, typedProgress, _cts.Token), _cts.Token);
                int exitCode = _crossLibrarySyncPlan.CanApply ? 0 : 4;
                CrossLibrarySyncPlan renderedPlan =
                    _crossLibrarySyncPlan;
                string output = await Task.Run(
                    () => RenderCrossLibrarySyncPlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderCrossLibrarySyncPlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "playlist-sync" && _playlistExport is not null)
            {
                IReadOnlyList<string> parsed = [];
                PlaylistExportRequest request = new(
                    ConfigurationPath: null,
                    ItunesLibraryPath: null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _playlistExportPlan = await Task.Run(() => _playlistExport.PreviewAsync(
                    request, typedProgress, _cts.Token), _cts.Token);
                int exitCode = _playlistExportPlan.CanApply ? 0 : 4;
                PlaylistExportPlan renderedPlan =
                    _playlistExportPlan;
                string output = await Task.Run(
                    () => RenderPlaylistExportPlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderPlaylistExportPlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "artwork-normalization" && _artworkNormalization is not null)
            {
                IReadOnlyList<string> parsed = [];
                ArtworkNormalizationRequest request = new(Required(JobPlaylistName,
                    "Operations.Validation.PlaylistNameRequired"),
                    ConfiguredLibraryPath());
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _artworkNormalizationPlan = await Task.Run(() => _artworkNormalization.PreviewAsync(
                    request, typedProgress, _cts.Token), _cts.Token);
                int exitCode = _artworkNormalizationPlan.CanApply ? 0 : 4;
                ArtworkNormalizationPlan renderedPlan =
                    _artworkNormalizationPlan;
                string output = await Task.Run(
                    () => RenderArtworkNormalizationPlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderArtworkNormalizationPlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "smart-storage" && _smartStorage is not null)
            {
                IReadOnlyList<string> parsed = [];
                SmartStorageRequest request = new(Required(JobDestinationPath,
                    "Operations.Validation.SmartStorageDestinationRequired"),
                    JobInitialize,
                    JobMaxRemovals,
                    ConfiguredLibraryPath());
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _smartStoragePlan = await Task.Run(() => _smartStorage.PreviewAsync(
                    request, typedProgress, _cts.Token), _cts.Token);
                int exitCode = _smartStoragePlan.CanApply ? 0 : 4;
                SmartStoragePlan renderedPlan =
                    _smartStoragePlan;
                string output = await Task.Run(
                    () => RenderSmartStoragePlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderSmartStoragePlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "car-card" && _carCard is not null)
            {
                IReadOnlyList<string> parsed = [];
                CarCardRequest request = new(null, JobRebalance, JobFixErrors,
                    JobInitialize, JobMaxRemovals, null);
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                _carCardPlan = await Task.Run(() =>
                    _carCard.PreviewAsync(request, typedProgress, _cts.Token), _cts.Token);
                int exitCode = _carCardPlan.CanApply ? 0 : 4;
                CarCardPlan renderedPlan =
                    _carCardPlan;
                string output = await Task.Run(
                    () => RenderCarCardPlan(renderedPlan), _cts.Token);
                _jobOutputRefresh =
                    () => RenderCarCardPlan(
                        renderedPlan);
                _jobPlan = new(SelectedJob, parsed, exitCode,
                    output, DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "redundancies" && _redundancyAnalysis is not null)
            {
                IReadOnlyList<string> parsed = [];
                string? library = ConfiguredLibraryPath();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                RedundancyAnalysisResult result = await Task.Run(() =>
                    _redundancyAnalysis.AnalyzeAsync(
                        library, typedProgress, _cts.Token), _cts.Token);
                string output = await Task.Run(
                    () => RenderRedundancyResult(result), _cts.Token);
                _jobOutputRefresh =
                    () => RenderRedundancyResult(
                        result);
                _jobPlan = new(SelectedJob, parsed, 0, output,
                    DateTimeOffset.UtcNow);
            }
            else if (SelectedJob.Id == "itunes-validation" && _itunesValidation is not null)
            {
                IReadOnlyList<string> parsed = [];
                string validationPath = Required(JobValidationPath,
                    "Operations.Validation.ItunesLibraryRequired");
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                ItunesValidationResult result = await Task.Run(() =>
                    _itunesValidation.ValidateAsync(
                        validationPath, typedProgress, _cts.Token), _cts.Token);
                string output = await Task.Run(
                    () => RenderValidationResult(result), _cts.Token);
                _jobOutputRefresh =
                    () => RenderValidationResult(
                        result);
                _jobPlan = new(SelectedJob, parsed, result.IsValid ? 0 : 4,
                    output, DateTimeOffset.UtcNow);
            }
            else
            {
                throw new InvalidOperationException(
                    LF(
                        "Operations.Diagnostic.NoPreviewService",
                        SelectedJob.Id));
            }
            HasJobPreview = true;
            JobOutput = _jobOutputRefresh?.Invoke() ??
                _jobPlan.PreviewOutput;
            if (_jobPlan.PreviewExitCode == 0)
                SetJobStatus(
                    "Operations.Job.Status.PreviewCompleted");
            else
                SetJobStatus(
                    "Operations.Job.Status.PreviewExitCode",
                    _jobPlan.PreviewExitCode);
            AddJobHistory(new(SelectedJob.Name, false, _jobPlan.PreviewExitCode == 0,
                _jobPlan.CreatedAtUtc, 0, TrimOutput(JobOutput)));
            FinishActivity(activity, JobStatus,
                _jobPlan.PreviewExitCode == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetJobStatus(
                "Operations.Job.Status.PreviewCancelled");
            FinishActivity(activity, JobStatus, AppActivityState.Cancelled);
        }
        catch (OperationsValidationException ex)
        {
            SetJobStatus(ex.ResourceKey);
            FinishActivity(
                activity,
                JobStatus,
                AppActivityState.Failed);
        }
        catch (Exception ex)
        {
            SetJobStatusFailure(
                "Operations.Job.Status.PreviewFailed",
                ex.Message);
            FinishActivity(activity, JobStatus, AppActivityState.Failed);
        }
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
        string jobName =
            JobDisplayName(plan.Job);
        if (!await _dialogs.ConfirmApplyAsync(
                LF(
                    "Operations.Dialog.ApplyJob.Title",
                    jobName),
                DescribeJobApplyConfirmation(plan),
                L("Common.Apply")))
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        JobOutput = "";
        _jobOutputRefresh = null;
        Guid? activity = _activities?.Start(
            LF(
                "Operations.Activity.Apply.Title",
                jobName),
            L("Operations.Activity.Apply.Starting"),
            ShellDestination.Operations,
            Cancel);
        try
        {
            UnifiedJobResult result;
            if (IsConfiguredExportJob(plan.Job.Id) && _configuredExport is not null &&
                _configuredExportPlan is { CanApply: true } exportPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                ConfiguredExportResult typedResult = await Task.Run(() =>
                    _configuredExport.ApplyAsync(
                        exportPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                _jobOutputRefresh =
                    () => RenderConfiguredExportResult(
                        typedResult);
                result = new(0, RenderConfiguredExportResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "cross-library-sync" && _crossLibrarySync is not null &&
                _crossLibrarySyncPlan is { CanApply: true } typedPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                CrossLibrarySyncResult typedResult = await Task.Run(() =>
                    _crossLibrarySync.ApplyAsync(
                        typedPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                string output = RenderCrossLibrarySyncResult(typedResult);
                _jobOutputRefresh =
                    () => RenderCrossLibrarySyncResult(
                        typedResult);
                result = new(0, output, clock.Elapsed);
            }
            else if (plan.Job.Id == "playlist-sync" && _playlistExport is not null &&
                     _playlistExportPlan is { CanApply: true } playlistPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                PlaylistExportResult typedResult = await Task.Run(() =>
                    _playlistExport.ApplyAsync(
                        playlistPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                _jobOutputRefresh =
                    () => RenderPlaylistExportResult(
                        typedResult);
                result = new(0, RenderPlaylistExportResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "artwork-normalization" && _artworkNormalization is not null &&
                     _artworkNormalizationPlan is { CanApply: true } artworkPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                ArtworkNormalizationResult typedResult = await Task.Run(() =>
                    _artworkNormalization.ApplyAsync(
                        artworkPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                if (typedResult.UpdatedPaths.Count > 0)
                    ArtworkNormalized?.Invoke(typedResult.UpdatedPaths);
                _jobOutputRefresh =
                    () => RenderArtworkNormalizationResult(
                        typedResult);
                result = new(0, RenderArtworkNormalizationResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "smart-storage" && _smartStorage is not null &&
                     _smartStoragePlan is { CanApply: true } smartPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                SmartStorageResult typedResult = await Task.Run(() =>
                    _smartStorage.ApplyAsync(
                        smartPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                _jobOutputRefresh =
                    () => RenderSmartStorageResult(
                        typedResult);
                result = new(0, RenderSmartStorageResult(typedResult), clock.Elapsed);
            }
            else if (plan.Job.Id == "car-card" && _carCard is not null &&
                     _carCardPlan is { CanApply: true } carCardPlan)
            {
                var clock = Stopwatch.StartNew();
                var typedProgress = new Progress<OperationProgress>(value =>
                    ReportJobProgress(activity, value));
                CarCardResult typedResult = await Task.Run(() =>
                    _carCard.ApplyAsync(
                        carCardPlan, typedProgress, _cts.Token), _cts.Token);
                clock.Stop();
                _jobOutputRefresh =
                    () => RenderCarCardResult(
                        typedResult);
                result = new(0, RenderCarCardResult(typedResult), clock.Elapsed);
            }
            else
            {
                throw new InvalidOperationException(
                    LF(
                        "Operations.Diagnostic.NoApplyService",
                        plan.Job.Id));
            }
            JobOutput = result.Output;
            if (result.Success)
                SetJobStatus(
                    "Operations.Job.Status.ApplyCompleted",
                    jobName);
            else
                SetJobStatus(
                    "Operations.Job.Status.ApplyExitCode",
                    result.ExitCode);
            AddJobHistory(new(plan.Job.Name, true, result.Success, DateTimeOffset.UtcNow,
                result.Elapsed.TotalSeconds, TrimOutput(result.Output)));
            InvalidateJobPreview(clearOutput: false);
            FinishActivity(activity, JobStatus,
                result.Success ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetJobStatus(
                "Operations.Job.Status.ApplyCancelled");
            FinishActivity(activity, JobStatus, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetJobStatusFailure(
                "Operations.Job.Status.ApplyFailed",
                ex.Message);
            FinishActivity(activity, JobStatus, AppActivityState.Failed);
        }
        finally { _cts?.Dispose(); _cts = null; IsBusy = false; }
    }

    private string DescribeJobApplyConfirmation(UnifiedJobPlan plan)
    {
        (int mutations, bool recovery, string? recoveryRoot) = plan.Job.Id switch
        {
            "cross-library-sync" when _crossLibrarySyncPlan is { } typed =>
                (typed.MutationPlan.Actions.Count, typed.MutationPlan.RetainRecovery,
                    typed.MutationPlan.RecoveryRoot),
            "playlist-sync" when _playlistExportPlan is { } typed =>
                (typed.MutationPlan.Actions.Count, typed.MutationPlan.RetainRecovery,
                    typed.MutationPlan.RecoveryRoot),
            "artwork-normalization" when _artworkNormalizationPlan is { } typed =>
                (typed.Items.Count, !string.IsNullOrWhiteSpace(typed.RecoveryRoot), typed.RecoveryRoot),
            "smart-storage" when _smartStoragePlan is { } typed =>
                (typed.MutationPlan.Actions.Count, typed.MutationPlan.RetainRecovery,
                    typed.MutationPlan.RecoveryRoot),
            "car-card" when _carCardPlan is { } typed =>
                (typed.MutationPlan.Actions.Count, typed.MutationPlan.RetainRecovery,
                    typed.MutationPlan.RecoveryRoot),
            _ when IsConfiguredExportJob(plan.Job.Id) &&
                   _configuredExportPlan?.TransportPlan?.MutationPlan is { } typed =>
                (typed.Actions.Count, typed.RetainRecovery, typed.RecoveryRoot),
            _ => (0, false, null),
        };
        string recoveryText = recovery
            ? string.IsNullOrWhiteSpace(
                recoveryRoot)
                ? L("Operations.Dialog.ApplyJob.RecoveryAvailable")
                : LF(
                    "Operations.Dialog.ApplyJob.RecoveryAvailableAt",
                    recoveryRoot)
            : L("Operations.Dialog.ApplyJob.RecoveryUnavailable");
        return LC(
            "Operations.Dialog.ApplyJob.Message",
            mutations,
            JobDisplayName(plan.Job),
            recoveryText);
    }

    private void InvalidateJobPreview(bool clearOutput = true)
    {
        _jobPlan = null;
        _crossLibrarySyncPlan = null;
        _playlistExportPlan = null;
        _artworkNormalizationPlan = null;
        _smartStoragePlan = null;
        _carCardPlan = null;
        _configuredExportPlan = null;
        HasJobPreview = false;
        if (clearOutput)
        {
            _jobOutputRefresh = null;
            JobOutput = "";
        }
        ApplyJobCommand.NotifyCanExecuteChanged();
    }

    private void LoadJobHistory()
    {
        try
        {
            foreach (var item in JsonSerializer.Deserialize<List<UnifiedJobHistoryItem>>(
                         _settings.GetLibraryPreference(JobHistoryPreference) ?? "[]") ?? [])
            {
                item.RefreshLocalizedText(
                    _localization);
                JobHistory.Add(item);
            }
        }
        catch { }
    }

    private void AddJobHistory(UnifiedJobHistoryItem item)
    {
        item.RefreshLocalizedText(
            _localization);
        JobHistory.Insert(0, item);
        while (JobHistory.Count > 30) JobHistory.RemoveAt(JobHistory.Count - 1);
        _settings.SetLibraryPreference(JobHistoryPreference, JsonSerializer.Serialize(JobHistory));
    }

    private static string TrimOutput(string output) => output.Length <= 20_000 ? output : output[^20_000..];
    private static string Required(
        string value,
        string resourceKey) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new OperationsValidationException(
                resourceKey)
            : value.Trim();

    private string ConfiguredLibraryPath() =>
        _settings.Configuration?.ItunesLibraryPath
        ?? throw new OperationsValidationException(
            "Operations.Validation.ActiveLibraryPathRequired");

    private string RenderIssue(
        OperationIssue issue) =>
        LF(
            "Operations.Output.Issue",
            L(
                $"Operations.Choice.OperationIssueSeverity.{issue.Severity}"),
            issue.Code,
            issue.Message,
            issue.Path is null
                ? ""
                : LF(
                    "Operations.Output.PathSuffix",
                    issue.Path));

    private string MutationKindLabel(
        FileMutationKind kind) =>
        L(
            $"Operations.Choice.FileMutationKind.{kind}");

    private string RenderMutation(
        FileMutationAction action,
        bool destinationOnly = false) =>
        destinationOnly
            ? LF(
                "Operations.Output.Mutation.Destination",
                MutationKindLabel(action.Kind),
                action.DestinationPath)
            : action.Kind == FileMutationKind.Delete
                ? LF(
                    "Operations.Output.Mutation.Source",
                    MutationKindLabel(action.Kind),
                    action.SourcePath)
                : LF(
                    "Operations.Output.Mutation.SourceDestination",
                    MutationKindLabel(action.Kind),
                    action.SourcePath,
                    action.DestinationPath);

    private string RecoveryJournalLine(
        string? journalPath) =>
        journalPath is null
            ? ""
            : Environment.NewLine +
              LF(
                  "Operations.Output.RecoveryJournal",
                  journalPath);

    private string RenderCrossLibrarySyncPlan(CrossLibrarySyncPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(
                RenderMutation(action));
        output.AppendLine(
            LC(
                "Operations.Output.CrossLibrary.Plan",
                plan.Files.Count,
                plan.UnchangedCount,
                plan.StaleCount,
                plan.MutationPlan.Actions.Count));
        return output.ToString();
    }

    private string RenderCrossLibrarySyncResult(
        CrossLibrarySyncResult result) =>
        LC(
            "Operations.Output.CrossLibrary.Result",
            result.Mutations.Copied,
            result.Mutations.Replaced,
            result.Mutations.Quarantined,
            result.Mutations.Deleted,
            result.UnchangedCount) +
        RecoveryJournalLine(
            result.Mutations.JournalPath);

    private string RenderConfiguredExportPlan(ConfiguredExportPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (ConfiguredExportFile file in plan.Files)
            output.AppendLine(file.Mutation is { } mutation
                ? LF(
                    "Operations.Output.Mutation.SourceDestination",
                    MutationKindLabel(mutation),
                    file.SourcePath,
                    file.DestinationPath)
                : LF(
                    "Operations.Output.Mutation.Destination",
                    L("Operations.Output.Unchanged"),
                    file.DestinationPath));
        output.AppendLine(
            LC(
                "Operations.Output.ConfiguredExport.Plan",
                plan.Files.Count,
                plan.UnchangedCount,
                plan.ExtraFileCount,
                plan.TransportPlan?.MutationPlan.Actions.Count ??
                0));
        return output.ToString();
    }

    private string RenderConfiguredExportResult(
        ConfiguredExportResult result) =>
        LC(
            "Operations.Output.ConfiguredExport.Result",
            result.Mutations.Copied,
            result.ProfileId,
            result.Mutations.Replaced,
            result.Mutations.Quarantined,
            result.Mutations.Deleted,
            result.UnchangedCount) +
        RecoveryJournalLine(
            result.Mutations.JournalPath);

    private static bool IsConfiguredExportJob(string? jobId) =>
        jobId?.StartsWith(UnifiedJobService.ConfiguredExportJobPrefix,
            StringComparison.Ordinal) == true;

    private static bool TryGetConfiguredExportProfileId(
        string? jobId,
        out string profileId)
    {
        if (IsConfiguredExportJob(jobId) &&
            jobId!.Length > UnifiedJobService.ConfiguredExportJobPrefix.Length)
        {
            profileId = jobId[UnifiedJobService.ConfiguredExportJobPrefix.Length..];
            return true;
        }
        profileId = "";
        return false;
    }

    private string RenderArtworkNormalizationPlan(ArtworkNormalizationPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (ArtworkNormalizationItem item in plan.Items)
            output.AppendLine(
                LF(
                    "Operations.Output.ArtworkNormalization.Item",
                    MutationKindLabel(
                        FileMutationKind.Replace),
                    item.Path,
                    item.Current.MimeType,
                    item.Current.Width,
                    item.Current.Height,
                    item.Current.Size,
                    item.Proposed.Width,
                    item.Proposed.Height,
                    item.Proposed.Size));
        output.AppendLine(
            LC(
                "Operations.Output.ArtworkNormalization.Plan",
                plan.ScannedTrackCount,
                plan.UnchangedCount,
                plan.Items.Count));
        return output.ToString();
    }

    private string RenderArtworkNormalizationResult(
        ArtworkNormalizationResult result) =>
        LC(
            "Operations.Output.ArtworkNormalization.Result",
            result.UpdatedFileCount,
            result.UpdatedTrackCount) +
        RecoveryJournalLine(
            result.JournalPath) +
        (result.CacheError is null
            ? ""
            : Environment.NewLine +
              LF(
                  "Operations.Output.CacheWarning",
                  result.CacheError));

    private string RenderSmartStoragePlan(SmartStoragePlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(
                RenderMutation(
                    action,
                    destinationOnly: true));
        output.AppendLine(
            LC(
                "Operations.Output.SmartStorage.Plan",
                plan.LibraryTrackCount,
                plan.InstalledTrackCount,
                plan.UnchangedTrackCount,
                plan.StaleTrackCount,
                plan.PlaylistCount,
                plan.ArtworkCount));
        return output.ToString();
    }

    private string RenderSmartStorageResult(
        SmartStorageResult result) =>
        LC(
            "Operations.Output.SmartStorage.Result",
            result.LibraryTrackCount,
            result.PlaylistCount,
            result.ArtworkCount,
            result.Mutations.Copied,
            result.Mutations.Replaced,
            result.Mutations.Quarantined) +
        RecoveryJournalLine(
            result.Mutations.JournalPath);

    private string RenderCarCardPlan(CarCardPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(
                RenderMutation(
                    action,
                    destinationOnly: true));
        output.AppendLine(
            LC(
                "Operations.Output.CarCard.Plan",
                plan.LibraryTrackCount,
                plan.InstalledTrackCount,
                plan.UnchangedTrackCount,
                plan.RemovedTrackCount,
                plan.PlaylistCount));
        return output.ToString();
    }

    private string RenderCarCardResult(
        CarCardResult result) =>
        LC(
            "Operations.Output.CarCard.Result",
            result.LibraryTrackCount,
            result.PlaylistCount,
            result.Mutations.Copied,
            result.Mutations.Replaced,
            result.Mutations.Quarantined) +
        RecoveryJournalLine(
            result.Mutations.JournalPath);

    private string RenderPlaylistExportPlan(PlaylistExportPlan plan)
    {
        var output = new StringBuilder();
        foreach (OperationIssue issue in plan.Issues)
            output.AppendLine(
                RenderIssue(issue));
        foreach (PlaylistExportTargetPlan target in plan.Targets)
            output.AppendLine(
                LC(
                    "Operations.Output.PlaylistExport.Target",
                    target.Files.Count,
                    target.Target,
                    target.MissingTrackCount));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            output.AppendLine(
                action.Kind ==
                FileMutationKind.Delete
                    ? RenderMutation(action)
                    : RenderMutation(
                        action,
                        destinationOnly: true));
        return output.ToString();
    }

    private string RenderPlaylistExportResult(
        PlaylistExportResult result) =>
        LC(
            "Operations.Output.PlaylistExport.Result",
            result.PlaylistCount,
            result.Mutations.Copied,
            result.Mutations.Replaced,
            result.Mutations.Quarantined,
            result.Mutations.Deleted) +
        RecoveryJournalLine(
            result.Mutations.JournalPath);

    private string RenderRedundancyResult(RedundancyAnalysisResult result)
    {
        var output = new StringBuilder();
        foreach (RedundancyGroup group in result.Groups)
        {
            foreach (RedundancyTrack track in group.Tracks)
                output.AppendLine($"{track.Artist} - {track.Title} ({track.Album}) [{track.Path}]");
            output.AppendLine();
        }
        output.AppendLine(
            LC(
                "Operations.Output.Redundancy.Summary",
                result.Groups.Count,
                result.ScannedTrackCount));
        return output.ToString();
    }

    private string RenderValidationResult(ItunesValidationResult result)
    {
        var output = new StringBuilder();
        foreach (var issue in result.Issues)
            output.AppendLine(
                LF(
                    "Operations.Output.Validation.Issue",
                    L(
                        $"Operations.Choice.ItlValidationSeverity.{issue.Severity}"),
                    issue.Code,
                    issue.Message));
        output.AppendLine(
            LF(
                "Operations.Output.Validation.Summary",
                LC(
                    "Operations.Count.Errors",
                    result.ErrorCount),
                LC(
                    "Operations.Count.Warnings",
                    result.WarningCount)));
        return output.ToString();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var roots = CollectSearchRoots();
        if (roots.Count == 0)
        {
            SetStatus(
                "Operations.Status.NoSearchRoots");
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        Guid? activity = _activities?.Start(
            L("Operations.Activity.Discovery.Title"),
            LC(
                "Operations.Activity.Discovery.Starting",
                roots.Count),
            ShellDestination.Operations,
            Cancel);
        SetCountStatus(
            "Operations.Status.ScanningRoots",
            roots.Count);
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
                Runs.Add(
                    new(
                        run,
                        _localization));
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(IsRunListEmpty));
            int interrupted = result.Runs.Count(run => run.State == OperationJournalState.Interrupted);
            SetCountStatus(
                "Operations.Status.DiscoveryCompleted",
                result.Runs.Count,
                interrupted,
                result.Warnings.Count);
            StatusDiagnosticDetail =
                result.Warnings.Count == 0
                    ? null
                    : string.Join(
                        Environment.NewLine,
                        result.Warnings);
            PreviewPurgeCommand.NotifyCanExecuteChanged();
            FinishActivity(activity, StatusText,
                result.Warnings.Count == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.DiscoveryCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.DiscoveryFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private bool CanCancel() => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void ReportJobProgress(Guid? activity, OperationProgress progress)
    {
        string phase = L(
            $"Operations.Progress.Phase.{progress.Phase}");
        if (progress.Total is > 0)
            SetJobStatus(
                "Operations.Progress.Determinate",
                phase,
                progress.Completed,
                progress.Total.Value);
        else
            SetJobStatus(
                "Operations.Progress.Indeterminate",
                phase);
        JobStatusDiagnosticDetail = string.Join(
            Environment.NewLine,
            new[]
            {
                progress.Message,
                progress.CurrentPath,
            }.Where(value =>
                !string.IsNullOrWhiteSpace(
                    value)));
        if (activity is { } id)
            _activities?.Report(id, JobStatus,
                progress.Total is > 0 ? (double)progress.Completed / progress.Total.Value : null);
    }

    private void FinishActivity(
        Guid? activity,
        string message,
        AppActivityState state = AppActivityState.Completed)
    {
        if (activity is { } id)
            _activities?.Finish(id, message, state);
    }

    private bool CanOpenRun(OperationRunViewModel? run) => !IsBusy && run is not null;

    [RelayCommand(CanExecute = nameof(CanOpenRun))]
    private async Task OpenRunAsync(OperationRunViewModel? run)
    {
        if (run is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Guid? activity = _activities?.Start(
            L("Operations.Activity.OpenRun.Title"),
            LF(
                "Operations.Activity.OpenRun.Starting",
                run.ToolName),
            ShellDestination.Operations,
            Cancel);
        SetStatus(
            "Operations.Status.OpeningRun",
            run.ToolName);
        try
        {
            var browse = await _journals.BrowseAsync(run.Summary, _cts.Token);
            RootNodes.Clear();
            var root = OperationEntryNodeViewModel.Build(
                browse,
                _localization);
            root.SelectionChanged += OnRestoreSelectionChanged;
            RootNodes.Add(root);
            SelectedRun = run;
            ShowBrowser = true;
            InvalidateRestorePreview();
            SetCountStatus(
                "Operations.Status.RunOpened",
                browse.Entries.Count,
                browse.Warnings.Count);
            StatusDiagnosticDetail =
                browse.Warnings.Count == 0
                    ? null
                    : string.Join(
                        Environment.NewLine,
                        browse.Warnings);
            FinishActivity(activity, StatusText,
                browse.Warnings.Count == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.OpenRunCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.OpenRunFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
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
            run = new OperationRunViewModel(
                summary,
                _localization);
            Runs.Insert(0, run);
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(IsRunListEmpty));
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
        SetCountStatus(
            "Operations.Status.ShowingRuns",
            Runs.Count);
    }

    private void OnRestoreSelectionChanged()
    {
        InvalidateRestorePreview();
        PreviewRestoreCommand.NotifyCanExecuteChanged();
    }

    private void RebuildRecoveryEntryNodes()
    {
        RecoveryEntryNodes.Clear();
        foreach (OperationEntryNodeViewModel root in
                 RootNodes)
        {
            AddRecoveryEntries(root);
        }
    }

    private void AddRecoveryEntries(
        OperationEntryNodeViewModel node)
    {
        if (node.HasEntry)
            RecoveryEntryNodes.Add(node);
        foreach (OperationEntryNodeViewModel child in
                 node.Children)
        {
            AddRecoveryEntries(child);
        }
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
        Guid? activity = _activities?.Start(
            L("Operations.Activity.RestorePreview.Title"),
            LC(
                "Operations.Activity.RestorePreview.Starting",
                entries.Count),
            ShellDestination.Operations,
            Cancel);
        try
        {
            _restorePlan = await _journals.PreviewRestoreAsync(SelectedRun.Summary, entries, _cts.Token);
            ShowRestorePreview = _restorePlan.CanApply;
            if (_restorePlan.CanApply)
                SetCountRestorePreview(
                    "Operations.RestorePreview.Ready",
                    _restorePlan.Actions.Count,
                    _restorePlan.CollisionCount,
                    _restorePlan.SkippedCount);
            else
                SetRestorePreview(
                    "Operations.RestorePreview.None");
            _statusTextRefresh =
                _restorePreviewTextRefresh;
            StatusDiagnosticDetail = null;
            StatusText = RestorePreviewText!;
            FinishActivity(activity, StatusText,
                _restorePlan.CanApply ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.RestorePreviewCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.RestorePreviewFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
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
        Guid? activity = _activities?.Start(
            L("Operations.Activity.Restore.Title"),
            LC(
                "Operations.Activity.Restore.Starting",
                plan.Actions.Count),
            ShellDestination.Operations,
            Cancel);
        try
        {
            var progress = new Progress<int>(count =>
            {
                SetStatus(
                    "Operations.Status.Restoring",
                    count,
                    plan.Actions.Count);
                if (activity is { } id)
                    _activities?.Report(id, StatusText,
                        plan.Actions.Count == 0 ? null : (double)count / plan.Actions.Count);
            });
            var result = await _journals.ApplyRestoreAsync(plan, progress, _cts.Token);
            var browse = await _journals.BrowseAsync(SelectedRun.Summary, CancellationToken.None);
            RootNodes.Clear();
            var root = OperationEntryNodeViewModel.Build(
                browse,
                _localization);
            root.SelectionChanged += OnRestoreSelectionChanged;
            RootNodes.Add(root);
            InvalidateRestorePreview();
            SetCountStatus(
                "Operations.Status.RestoreCompleted",
                result.RestoredCount,
                result.CollisionBackupCount);
            FinishActivity(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.RestoreCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.RestoreFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
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
        _restorePreviewTextRefresh = null;
        ApplyRestoreCommand.NotifyCanExecuteChanged();
    }

    private bool CanPreviewPurge() => !IsBusy && Runs.Count > 0 && RetentionDays >= 1;

    [RelayCommand(CanExecute = nameof(CanPreviewPurge))]
    private async Task PreviewPurgeAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Guid? activity = _activities?.Start(
            L("Operations.Activity.PurgePreview.Title"),
            LC(
                "Operations.Activity.PurgePreview.Starting",
                RetentionDays),
            ShellDestination.Operations,
            Cancel);
        SetCountStatus(
            "Operations.Status.InventoryingPurge",
            RetentionDays);
        try
        {
            _purgePlan = await _journals.PreviewPurgeAsync(
                Runs.Select(run => run.Summary).ToList(), RetentionDays, null, _cts.Token);
            OperationPurgePlan renderedPlan =
                _purgePlan;
            ShowPurgePreview = true;
            SetPurgePreview(
                () => DescribePurgePlan(
                    renderedPlan));
            _statusTextRefresh =
                _purgePreviewTextRefresh;
            StatusDiagnosticDetail = null;
            StatusText = PurgePreviewText!;
            FinishActivity(activity, StatusText,
                _purgePlan.CanApply ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.PurgePreviewCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.PurgePreviewFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
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
        Guid? activity = _activities?.Start(
            L("Operations.Activity.Purge.Title"),
            LC(
                "Operations.Activity.Purge.Starting",
                plan.Runs.Count),
            ShellDestination.Operations,
            Cancel);
        try
        {
            var progress = new Progress<int>(count =>
            {
                SetCountStatus(
                    "Operations.Status.Purging",
                    plan.Runs.Count,
                    count);
                if (activity is { } id)
                    _activities?.Report(id, StatusText,
                        plan.Runs.Count == 0 ? null : (double)count / plan.Runs.Count);
            });
            var result = await _journals.ApplyPurgeAsync(plan, progress, _cts.Token);
            var deleted = plan.Runs.Select(run => run.Run.RunPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var run in Runs.Where(run => deleted.Contains(run.RunPath)).ToList())
                Runs.Remove(run);
            InvalidatePurgePreview();
            SetCountStatus(
                "Operations.Status.PurgeCompleted",
                result.RunsDeleted,
                result.FilesDeleted,
                FormatBytes(
                    result.BytesDeleted));
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(IsRunListEmpty));
            FinishActivity(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Operations.Status.PurgeCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetStatusFailure(
                "Operations.Status.PurgeFailed",
                ex.Message);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
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
        _purgePreviewTextRefresh = null;
        ApplyPurgeCommand.NotifyCanExecuteChanged();
    }

    private string DescribePurgePlan(OperationPurgePlan plan)
    {
        string eligible = plan.CanApply
            ? LC(
                "Operations.PurgePreview.Eligible",
                plan.Runs.Count,
                plan.FileCount,
                FormatBytes(
                    plan.TotalBytes))
            : L("Operations.PurgePreview.NoneEligible");
        string backups = plan.RestoreBackupFileCount > 0
            ? LC(
                "Operations.PurgePreview.Backups",
                plan.RestoreBackupFileCount)
            : "";
        string unsafeRoots =
            plan.ProtectedUnsafeCount == 0
                ? ""
                : LC(
                    "Operations.PurgePreview.UnsafeRoots",
                    plan.ProtectedUnsafeCount);
        return LF(
            "Operations.PurgePreview.Summary",
            eligible,
            backups,
            plan.ProtectedInterruptedCount,
            plan.NewerCount,
            unsafeRoots);
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
        Add(_settings.GetLibraryPreference("manager.ingest.source.v1"));
#else
        Add(_settings.GetLibraryPreference("Ingest.SourceDirectory"));
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

internal sealed class OperationsValidationException(
    string resourceKey) : Exception
{
    public string ResourceKey { get; } =
        resourceKey;
}

public sealed class OperationRunViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localization;

    public OperationJournalSummary Summary { get; }
    public string ToolName =>
        Summary.Kind ==
        OperationJournalKind.ReviewedChange
            ? Get(
                "Operations.Choice.OperationJournalKind.ReviewedChange")
            : Summary.ToolName;
    public string Kind => Get(
        Summary.Kind switch
        {
            OperationJournalKind.Ingest =>
                "Operations.Choice.OperationJournalKind.Ingest",
            OperationJournalKind.Organize =>
                "Operations.Choice.OperationJournalKind.Organize",
            OperationJournalKind.Sync =>
                "Operations.Choice.OperationJournalKind.Sync",
            OperationJournalKind.Device =>
                "Operations.Choice.OperationJournalKind.Device",
            OperationJournalKind.ReviewedChange =>
                "Operations.Choice.OperationJournalKind.ReviewedChange",
            _ =>
                "Operations.Choice.OperationJournalKind.OtherKind",
        });
    public string State => Summary.State switch
    {
        OperationJournalState.Completed => Get(
            "Operations.Choice.OperationJournalState.Completed"),
        OperationJournalState.Interrupted => Get(
            "Operations.Choice.OperationJournalState.Interrupted"),
        OperationJournalState.RolledBack => Get(
            "Operations.Choice.OperationJournalState.RolledBack"),
        _ => Get(
            "Operations.Choice.OperationJournalState.Unknown"),
    };
    public bool IsInterrupted =>
        Summary.State ==
        OperationJournalState.Interrupted;
    public string Created =>
        Summary.CreatedAtUtc.ToLocalTime().ToString(
            "g",
            _localization?.CurrentUICulture);
    public string RunPath => Summary.RunPath;
    public string Journal =>
        Summary.JournalPath ??
        Get("Operations.Run.NoJournal");
    public string AffectedItems =>
        Summary.AffectedItemCount is int count
            ? FormatCount(
                "Operations.Run.AffectedItems",
                count)
            : Get(
                "Operations.Run.AffectedItemsDeferred");
    public bool HasReviewedTransaction =>
        Summary.ReviewedChangeTransaction is not null;
    public string ReviewedTransactionDetail =>
        Summary.ReviewedChangeTransaction is
            { } transaction
            ? FormatCount(
                "Operations.Run.ReviewedTransaction",
                transaction.ParticipantCount,
                transaction.AppliedParticipantCount)
            : "";

    public OperationRunViewModel(
        OperationJournalSummary summary,
        ILocalizationService? localization = null)
    {
        Summary = summary;
        _localization = localization;
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ToolName));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Created));
        OnPropertyChanged(nameof(Journal));
        OnPropertyChanged(nameof(AffectedItems));
        OnPropertyChanged(
            nameof(ReviewedTransactionDetail));
    }

    private string Get(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string FormatCount(
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
}

public partial class OperationEntryNodeViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localization;

    public string Name { get; }
    public string OriginalPath { get; }
    public string? CurrentPath { get; private set; }
    public OperationEntryKind? Kind { get; private set; }
    public bool Exists { get; private set; }
    public bool IsDirectory { get; private set; }
    public RecoveryPayloadKind PayloadKind { get; private set; }
    public long RetainedBytes { get; private set; }
    public long OriginalBytes { get; private set; }
    public long PostEditBytes { get; private set; }
    public string? OriginalSha256 { get; private set; }
    public string? PostEditSha256 { get; private set; }
    public string? DeltaPath { get; private set; }
    public DateTime? OriginalLastWriteTimeUtc { get; private set; }
    public FileAttributes? OriginalAttributes { get; private set; }
    public string? PayloadSha256 { get; private set; }
    public List<OperationEntryNodeViewModel> Children { get; } = [];
    public bool HasEntry => Kind is not null;
    public bool CanRestore => HasEntry && Exists && CurrentPath is not null &&
        Kind is OperationEntryKind.Quarantined or
            OperationEntryKind.Moved or
            OperationEntryKind.Created or
            OperationEntryKind.Planned;

    [ObservableProperty]
    private bool _isSelected;

    public event Action? SelectionChanged;
    public string StateText => Kind switch
    {
        OperationEntryKind.Quarantined => Get(
            Exists
                ? "Operations.Entry.State.Quarantined"
                : "Operations.Entry.State.QuarantineMissing"),
        OperationEntryKind.Moved => Get(
            Exists
                ? "Operations.Entry.State.Moved"
                : "Operations.Entry.State.MoveDestinationMissing"),
        OperationEntryKind.Created => Get(
            Exists
                ? "Operations.Entry.State.Created"
                : "Operations.Entry.State.CreatedItemMissing"),
        OperationEntryKind.Deleted => Get(
            "Operations.Entry.State.Deleted"),
        OperationEntryKind.Planned => Get(
            "Operations.Entry.State.Planned"),
        OperationEntryKind.Unknown => Get(
            "Operations.Entry.State.Unknown"),
        _ => "",
    };
    public bool HasRecoveryStorage =>
        Kind == OperationEntryKind.Quarantined && RetainedBytes > 0;
    public string RecoveryStorageText => !HasRecoveryStorage
        ? ""
        : PayloadKind == RecoveryPayloadKind.ReverseDelta
            ? Format(
                "Operations.Entry.Recovery.Compact",
                RetainedBytes,
                Math.Max(0, OriginalBytes - RetainedBytes))
            : Format(
                "Operations.Entry.Recovery.Full",
                RetainedBytes);

    private OperationEntryNodeViewModel(
        string name,
        string originalPath,
        ILocalizationService? localization)
    {
        Name = name;
        OriginalPath = originalPath;
        _localization = localization;
    }

    partial void OnIsSelectedChanged(bool value) =>
        SelectionChanged?.Invoke();

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
                OriginalPath,
                CurrentPath,
                Name,
                Kind!.Value,
                Exists,
                IsDirectory,
                PayloadKind,
                RetainedBytes,
                OriginalBytes,
                PostEditBytes,
                OriginalSha256,
                PostEditSha256,
                DeltaPath,
                OriginalLastWriteTimeUtc,
                OriginalAttributes,
                PayloadSha256);
        foreach (var child in Children)
            foreach (var entry in child.SelectedEntries())
                yield return entry;
    }

    public static OperationEntryNodeViewModel Build(
        OperationBrowseResult browse,
        ILocalizationService? localization = null)
    {
        var root = new OperationEntryNodeViewModel(
            browse.OriginalRoot,
            browse.OriginalRoot,
            localization)
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
                child = new OperationEntryNodeViewModel(
                    part,
                    path,
                    _localization)
                {
                    IsDirectory = true,
                };
                child.SelectionChanged +=
                    () => SelectionChanged?.Invoke();
                parent.Children.Add(child);
            }
            parent = child;
        }
        parent.CurrentPath = entry.CurrentPath;
        parent.Kind = entry.Kind;
        parent.Exists = entry.Exists;
        parent.IsDirectory = entry.IsDirectory;
        parent.PayloadKind = entry.PayloadKind;
        parent.RetainedBytes = entry.RetainedBytes;
        parent.OriginalBytes = entry.OriginalBytes;
        parent.PostEditBytes = entry.PostEditBytes;
        parent.OriginalSha256 = entry.OriginalSha256;
        parent.PostEditSha256 = entry.PostEditSha256;
        parent.DeltaPath = entry.DeltaPath;
        parent.OriginalLastWriteTimeUtc = entry.OriginalLastWriteTimeUtc;
        parent.OriginalAttributes = entry.OriginalAttributes;
        parent.PayloadSha256 = entry.PayloadSha256;
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

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RecoveryStorageText));
        foreach (OperationEntryNodeViewModel child in Children)
            child.RefreshLocalizedText();
    }

    private string Get(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(string key, params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);
}
