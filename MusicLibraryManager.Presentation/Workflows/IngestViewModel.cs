using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public enum IngestPreviewFilter { All, Albums, Outputs, Conflicts, Cleanup }

public partial class IngestViewModel :
    ViewModelBase,
    IIngestSourceHandoff
{
#if MUSIC_LIBRARY_MANAGER
    private const string SourcePreference = "manager.ingest.source.v1";
    private const string RecentSourcesPreference = "manager.ingest.recentSources.v1";
#else
    private const string SourcePreference = "Ingest.SourceDirectory";
    private const string RecentSourcesPreference = "Ingest.RecentSources";
#endif
    private const int RecentSourceLimit = 12;
    private readonly IIngestMusicService _service;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private readonly ILibraryService _library;
    private readonly IIngestPreflightService? _preflight;
    private readonly IOperationJournalService? _journals;
    private readonly IActivityService? _activities;
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cts;
    private IngestPlan? _plan;
    private readonly List<IngestFileItemViewModel> _allFiles = [];
    private IReadOnlyList<string> _sourceFiles = [];
    private bool _settingExplicitSourceFiles;
    private string? _statusKey = "Ingest.Status.ChooseSource";
    private object?[] _statusArguments = [];
    private long? _statusCount;
    private string? _historyStatusKey = "Ingest.History.Status.Ready";
    private object?[] _historyStatusArguments = [];
    private long? _historyStatusCount;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)),
     NotifyCanExecuteChangedFor(nameof(PreflightCommand))]
    private string? _sourceDirectory;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)),
     NotifyCanExecuteChangedFor(nameof(PreflightCommand)), NotifyCanExecuteChangedFor(nameof(ApplyCommand)),
     NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyCanExecuteChangedFor(nameof(OpenHistoryCommand)),
     NotifyCanExecuteChangedFor(nameof(ClearExplicitSourcesCommand))]
    private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyPropertyChangedFor(nameof(IsPreviewPrimary))]
    private bool _hasApplicablePreview;
    [ObservableProperty]
    private string _statusText =
        LocalizedText.Get("Ingest.Status.ChooseSource");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticDetail))]
    private string? _diagnosticDetail;
    [ObservableProperty]
    private bool _isPreviewing;
    [ObservableProperty]
    private bool _isApplying;
    [ObservableProperty]
    private int _applyProgress;
    [ObservableProperty]
    private int _applyProgressMaximum = 1;
    [ObservableProperty]
    private int _previewProgress;
    [ObservableProperty]
    private int _previewProgressMaximum = 1;
    [ObservableProperty]
    private string? _selectedRecentSource;
    [ObservableProperty]
    private IngestPreviewFilter _selectedPreviewFilter;
    [ObservableProperty]
    private bool _hasPreviewSummary;
    [ObservableProperty]
    private int _albumCount;
    [ObservableProperty]
    private int _outputCount;
    [ObservableProperty]
    private int _conflictCount;
    [ObservableProperty]
    private int _cleanupCount;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenHistoryCommand))]
    private bool _isHistoryBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenHistoryCommand))]
    private IngestHistoryItemViewModel? _selectedHistory;
    [ObservableProperty]
    private string _historyStatus =
        LocalizedText.Get("Ingest.History.Status.Ready");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryDiagnosticDetail))]
    private string? _historyDiagnosticDetail;

    public ObservableCollection<IngestFileItemViewModel> Files { get; } = [];
    public ObservableCollection<IngestConflict> Conflicts { get; } = [];
    public ObservableCollection<string> RecentSources { get; } = [];
    public ObservableCollection<IngestPreflightCheckViewModel> PreflightChecks { get; } = [];
    public ObservableCollection<IngestHistoryItemViewModel> History { get; } = [];
    public IReadOnlyList<IngestPreviewFilter> PreviewFilters { get; } = Enum.GetValues<IngestPreviewFilter>();
    public ObservableCollection<LocalizedChoice<IngestPreviewFilter>>
        PreviewFilterChoices { get; } = [];
    public bool HasPreflightChecks => PreflightChecks.Count > 0;
    public bool IsPreviewEmpty => Files.Count == 0;
    public bool HasHistory => History.Count > 0;
    public bool IsHistoryEmpty => History.Count == 0;
    public bool HasExplicitSourceFiles =>
        _sourceFiles.Count > 0;
    public int ExplicitSourceFileCount =>
        _sourceFiles.Count;
    public string ExplicitSourceSummary =>
        LFC(
            "Ingest.SelectedSources.Summary",
            _sourceFiles.Count);
    public int InterruptedHistoryCount => History.Count(item => item.IsInterrupted);
    public bool IsConfigurationReady => GetConfigurationIssues().Count == 0;
    public bool IsPreviewPrimary =>
        IsConfigurationReady &&
        !HasApplicablePreview;
    public double PreviewActionOpacity =>
        IsConfigurationReady
            ? 1
            : 0.52;
    public string ConfigurationReadinessIcon => IsConfigurationReady ? "i" : "⚠";
    public string ConfigurationReadinessText
    {
        get
        {
            IReadOnlyList<string> issues = GetConfigurationIssues();
            return issues.Count == 0
                ? L("Ingest.Configuration.Ready")
                : LFC(
                    "Ingest.Configuration.Incomplete",
                    issues.Count);
        }
    }
    public string? ConfigurationDiagnosticDetail =>
        GetConfigurationIssues() is { Count: > 0 } issues
            ? string.Join(
                Environment.NewLine,
                issues)
            : null;
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(DiagnosticDetail);
    public bool HasHistoryDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(HistoryDiagnosticDetail);
    public event Action? IngestCompleted;
    public event Action<OperationJournalSummary>? RecoveryRequested;

    public IngestViewModel(IIngestMusicService service, IFileDialogService files, IDialogService dialogs,
        IAppSettings settings, ILibraryService library, IIngestPreflightService? preflight = null,
        IOperationJournalService? journals = null, IActivityService? activities = null,
        ILocalizationService? localization = null)
    {
        _service = service; _files = files; _dialogs = dialogs; _settings = settings; _library = library;
        _preflight = preflight;
        _journals = journals;
        _activities = activities;
        _localization = localization;
        SetStatus("Ingest.Status.ChooseSource");
        SetHistoryStatus("Ingest.History.Status.Ready");
        RefreshLocalizedChoices();
        if (_localization is not null)
            _localization.CultureChanged += OnLocalizationCultureChanged;
        LoadRecentSources();
        SourceDirectory = settings.GetLibraryPreference(SourcePreference);
        settings.ConfigurationChanged += (_, _) =>
        {
            LoadRecentSources();
            SourceDirectory = settings.GetLibraryPreference(SourcePreference);
            OnPropertyChanged(nameof(IsConfigurationReady));
            OnPropertyChanged(nameof(IsPreviewPrimary));
            OnPropertyChanged(nameof(PreviewActionOpacity));
            OnPropertyChanged(nameof(ConfigurationReadinessText));
            OnPropertyChanged(nameof(ConfigurationReadinessIcon));
            OnPropertyChanged(nameof(ConfigurationDiagnosticDetail));
            InvalidatePreview();
            NotifyCommands();
        };
    }

    partial void OnSourceDirectoryChanged(string? value)
    {
        if (!_settingExplicitSourceFiles &&
            _sourceFiles.Count > 0)
            SetExplicitSourceFiles([]);
        _settings.SetLibraryPreference(SourcePreference, string.IsNullOrWhiteSpace(value) ? null : value);
        InvalidatePreview();
    }

    partial void OnSelectedRecentSourceChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            SourceDirectory = value;
    }

    partial void OnSelectedPreviewFilterChanged(IngestPreviewFilter value) => RefilterFiles();

    private void InvalidatePreview()
    {
        if (PreflightChecks.Count > 0)
        {
            PreflightChecks.Clear();
            OnPropertyChanged(nameof(HasPreflightChecks));
        }
        if (_plan is null) return;
        _plan = null;
        HasApplicablePreview = false;
        SetStatus("Ingest.Status.InputsChanged");
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Ingest.Dialog.SelectSource"));
        if (path is not null)
        {
            SourceDirectory = path;
            AddRecentSource(path);
        }
    }

    private bool CanPreview() => !IsBusy && !string.IsNullOrWhiteSpace(SourceDirectory) &&
        IsConfigurationReady;

    public void SetDroppedSource(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        SourceDirectory = Path.GetFullPath(path);
        AddRecentSource(SourceDirectory);
        SetStatus("Ingest.Status.SourceDropped");
    }

    public IngestSourceHandoffResult SetSourceFiles(
        IReadOnlyList<string> paths)
    {
        if (IsBusy)
            return new(
                false,
                L("Ingest.Handoff.Busy"));
        string[] selected;
        try
        {
            selected = paths
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToArray();
        }
        catch (Exception error)
        {
            DiagnosticDetail = error.Message;
            return new(
                false,
                L("Ingest.Handoff.InvalidPaths"));
        }
        if (selected.Length == 0)
            return new(
                false,
                L("Ingest.Handoff.NoneSelected"));
        string? unavailable =
            selected.FirstOrDefault(path =>
                !File.Exists(path));
        if (unavailable is not null)
            return new(
                false,
                LF(
                    "Ingest.Handoff.Unavailable",
                    unavailable));
        string? commonRoot =
            CommonSourceRoot(selected);
        if (commonRoot is null)
            return new(
                false,
                L("Ingest.Handoff.CommonTreeRequired"));

        _settingExplicitSourceFiles = true;
        try
        {
            SetExplicitSourceFiles(selected);
            SourceDirectory = commonRoot;
        }
        finally
        {
            _settingExplicitSourceFiles = false;
        }
        SetCountStatus(
            "Ingest.Handoff.Ready",
            selected.Length);
        NotifyCommands();
        return new(true);
    }

    [RelayCommand(CanExecute = nameof(CanClearExplicitSources))]
    private void ClearExplicitSources()
    {
        SetExplicitSourceFiles([]);
        SourceDirectory = null;
        SetStatus("Ingest.Status.SelectionCleared");
        NotifyCommands();
    }

    private bool CanClearExplicitSources() =>
        !IsBusy &&
        HasExplicitSourceFiles;

    private bool CanPreflight() => _preflight is not null && CanPreview();

    [RelayCommand(CanExecute = nameof(CanPreflight))]
    private async Task PreflightAsync()
    {
        if (_preflight is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        DiagnosticDetail = null;
        Guid? activity = _activities?.Start(
            L("Ingest.Activity.Preflight.Title"),
            L("Ingest.Activity.Preflight.Starting"),
            ShellDestination.Ingest,
            Cancel);
        PreflightChecks.Clear();
        OnPropertyChanged(nameof(HasPreflightChecks));
        try
        {
            SetStatus("Ingest.Status.PreflightChecking");
            var result = await _preflight.CheckAsync(
                CreateRequest(), _cts.Token);
            foreach (var check in result.Checks)
                PreflightChecks.Add(
                    new IngestPreflightCheckViewModel(
                        check,
                        _localization));
            OnPropertyChanged(nameof(HasPreflightChecks));
            if (!result.CanProceed)
                SetCountStatus(
                    "Ingest.Status.PreflightErrors",
                    result.ErrorCount);
            else if (result.WarningCount > 0)
                SetCountStatus(
                    "Ingest.Status.PreflightWarnings",
                    result.WarningCount);
            else
                SetStatus("Ingest.Status.PreflightPassed");
            FinishActivity(activity, StatusText,
                result.CanProceed ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Ingest.Status.PreflightCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetFailure(
                "Ingest.Status.PreflightFailed",
                ex);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally { FinishBusy(); }
    }

    private bool CanRefreshHistory() => _journals is not null && !IsHistoryBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private async Task RefreshHistoryAsync()
    {
        if (_journals is null)
            return;
        var roots = RecentSources.Append(SourceDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            SetHistoryStatus("Ingest.History.Status.ChooseSource");
            return;
        }
        IsHistoryBusy = true;
        HistoryDiagnosticDetail = null;
        Guid? activity = _activities?.Start(
            L("Ingest.Activity.History.Title"),
            L("Ingest.Activity.History.Starting"),
            ShellDestination.Ingest);
        try
        {
            SetHistoryCountStatus(
                "Ingest.History.Status.SearchingRoots",
                roots.Count);
            var result = await _journals.DiscoverAsync(roots);
            SelectedHistory = null;
            History.Clear();
            foreach (var run in result.Runs.Where(run => run.Kind == OperationJournalKind.Ingest).Take(50))
                History.Add(
                    new IngestHistoryItemViewModel(
                        run,
                        _localization));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(IsHistoryEmpty));
            OnPropertyChanged(nameof(InterruptedHistoryCount));
            SetHistoryStatus(
                result.Warnings.Count == 0
                    ? "Ingest.History.Status.Summary"
                    : "Ingest.History.Status.SummaryWithWarnings",
                History.Count,
                InterruptedHistoryCount,
                result.Warnings.Count);
            FinishActivity(activity, HistoryStatus,
                result.Warnings.Count == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (Exception ex)
        {
            SetHistoryFailure(
                "Ingest.History.Status.Failed",
                ex);
            FinishActivity(activity, HistoryStatus, AppActivityState.Failed);
        }
        finally { IsHistoryBusy = false; }
    }

    private bool CanOpenHistory() => !IsBusy && !IsHistoryBusy && SelectedHistory is not null;

    [RelayCommand(CanExecute = nameof(CanOpenHistory))]
    private void OpenHistory()
    {
        if (SelectedHistory is not null)
            RecoveryRequested?.Invoke(SelectedHistory.Summary);
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true; IsPreviewing = true; HasApplicablePreview = false; _plan = null;
        PreviewProgress = 0; PreviewProgressMaximum = 1;
        _allFiles.Clear(); Files.Clear(); Conflicts.Clear(); HasPreviewSummary = false;
        OnPropertyChanged(nameof(IsPreviewEmpty));
        _cts = new CancellationTokenSource();
        DiagnosticDetail = null;
        Guid? activity = _activities?.Start(
            L("Ingest.Activity.Preview.Title"),
            L("Ingest.Activity.Preview.Starting"),
            ShellDestination.Ingest,
            Cancel);
        try
        {
            SetStatus("Ingest.Status.PreviewScanning");
            var progress = new DispatchingProgress<IngestProgress>(p =>
            {
                PreviewProgressMaximum = Math.Max(1, p.TotalItems);
                PreviewProgress = Math.Min(
                    PreviewProgressMaximum,
                    p.CompletedItems);
                SetStatus(
                    p.TotalItems > 0
                        ? "Ingest.Status.PreviewProgress"
                        : "Ingest.Status.PreviewFound",
                    LocalizeOperation(p.Operation),
                    p.CompletedItems,
                    p.TotalItems);
                if (activity is { } id)
                    _activities?.Report(
                        id,
                        StatusText,
                        p.TotalItems <= 0
                            ? null
                            : (double)p.CompletedItems / p.TotalItems);
            });
            var plan = await _service.PreviewAsync(
                CreateRequest(),
                progress,
                _cts.Token);
            await progress.DrainAsync();
            _plan = plan;
            foreach (var file in plan.Files)
            {
                bool cleanup = file.SourceType.Equals("Unsupported/non-audio", StringComparison.OrdinalIgnoreCase);
                _allFiles.Add(
                    new IngestFileItemViewModel(
                        file,
                        isAlbum: !cleanup,
                        isCleanup: cleanup,
                        localization: _localization));
            }
            foreach (var output in plan.Albums.SelectMany(album => album.Outputs))
                _allFiles.Add(
                    IngestFileItemViewModel.ForOutput(
                        output,
                        _localization));
            foreach (var conflict in plan.Conflicts)
            {
                Conflicts.Add(conflict);
                _allFiles.Add(
                    IngestFileItemViewModel.ForConflict(
                        conflict,
                        _localization));
            }
            AlbumCount = plan.Albums.Count;
            OutputCount = plan.Albums.Sum(album => album.Outputs.Count);
            ConflictCount = plan.Conflicts.Count;
            CleanupCount = plan.IgnoredFileSnapshots.Count + plan.SourceDirectories.Count;
            HasPreviewSummary = true;
            RefilterFiles();
            HasApplicablePreview = plan.CanApply;
            _settings.SetLibraryPreference(SourcePreference, plan.Request.SourceDirectory);
            AddRecentSource(plan.Request.SourceDirectory);
            if (plan.CanApply && plan.Albums.Count == 0)
                SetStatus(
                    "Ingest.Status.PreviewCleanupReady",
                    plan.IgnoredFileSnapshots.Count,
                    plan.SourceDirectories.Count);
            else if (plan.CanApply)
                SetStatus(
                    "Ingest.Status.PreviewReady",
                    plan.Albums.Count,
                    plan.Files.Count,
                    plan.RequiredApprovals.Count);
            else if (plan.Conflicts.Count > 0)
                SetCountStatus(
                    "Ingest.Status.PreviewConflicts",
                    plan.Conflicts.Count);
            else
                SetStatus("Ingest.Status.PreviewEmpty");
            FinishActivity(activity, StatusText,
                plan.CanApply ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Ingest.Status.PreviewCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetFailure(
                "Ingest.Status.PreviewFailed",
                ex);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally { FinishBusy(); }
    }

    private IngestRequest CreateRequest() =>
        new(
            SourceDirectory!,
            SourceFiles:
                HasExplicitSourceFiles
                    ? _sourceFiles
                    : null);

    private void SetExplicitSourceFiles(
        IReadOnlyList<string> paths)
    {
        _sourceFiles = paths;
        OnPropertyChanged(
            nameof(HasExplicitSourceFiles));
        OnPropertyChanged(
            nameof(ExplicitSourceFileCount));
        OnPropertyChanged(
            nameof(ExplicitSourceSummary));
        ClearExplicitSourcesCommand
            .NotifyCanExecuteChanged();
        InvalidatePreview();
    }

    private static string? CommonSourceRoot(
        IReadOnlyList<string> paths)
    {
        string? root =
            Path.GetDirectoryName(paths[0]);
        while (root is not null &&
               paths.Any(path =>
                   !IsWithin(path, root)))
            root = Directory.GetParent(root)
                ?.FullName;
        if (root is null)
            return null;
        string volumeRoot =
            Path.GetPathRoot(root) ?? "";
        return PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(root),
            Path.TrimEndingDirectorySeparator(
                volumeRoot))
            ? null
            : root;
    }

    private static bool IsWithin(
        string path,
        string root)
    {
        string parent =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        return Path.GetFullPath(path)
            .StartsWith(
                parent,
                PathComparison);
    }

    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null) return;
        IngestPlan plan = _plan;
        IsBusy = true; IsApplying = true; ApplyProgress = 0; ApplyProgressMaximum = 1; _cts = new CancellationTokenSource();
        foreach (var file in _allFiles) file.ResetProgress();
        DiagnosticDetail = null;
        Guid? activity = null;
        try
        {
            var decisions = new List<IngestApprovalDecision>();
            foreach (var item in _plan.RequiredApprovals)
            {
                bool approved = await _dialogs.ConfirmCdDerivationAsync(item);
                decisions.Add(new IngestApprovalDecision(item.AlbumKey, approved));
                if (!approved)
                {
                    SetStatus("Ingest.Status.DerivationDeclined");
                    return;
                }
            }
            int outputs = plan.Albums.Sum(album => album.Outputs.Count);
            int cleanup = plan.IgnoredFileSnapshots.Count + plan.SourceDirectories.Count;
            if (!await _dialogs.ConfirmApplyAsync(
                    L("Ingest.Dialog.Apply.Title"),
                    LF(
                        "Ingest.Dialog.Apply.Message",
                        plan.Albums.Count,
                        outputs,
                        cleanup),
                    L("Ingest.Dialog.Apply.Primary")))
            {
                SetStatus("Ingest.Status.ApplyDeclined");
                return;
            }
            activity = _activities?.Start(
                L("Ingest.Activity.Apply.Title"),
                L("Ingest.Activity.Apply.Starting"),
                ShellDestination.Ingest,
                Cancel);
            var progress = new DispatchingProgress<IngestProgress>(p =>
            {
                ApplyProgressMaximum = Math.Max(1, p.TotalItems);
                ApplyProgress = p.CompletedItems;
                SetStatus(
                    "Ingest.Status.ApplyProgress",
                    LocalizeOperation(p.Operation),
                    LocalizeAlbum(p.Album),
                    p.CompletedItems,
                    p.TotalItems);
                if (p.FileState ==
                        IngestFileProgressState.Failed &&
                    !p.Operation.Equals(
                        "Cancelled",
                        StringComparison.Ordinal))
                    DiagnosticDetail =
                        p.Operation;
                if (p.SourcePath is not null && p.FileState is { } state)
                {
                    // A source row can have several output rows. Updating only the first match
                    // leaves the Progress column blank whenever the Outputs filter is selected.
                    foreach (IngestFileItemViewModel file in _allFiles.Where(file =>
                                 !file.IsConflict && string.Equals(file.Source, p.SourcePath,
                                     StringComparison.OrdinalIgnoreCase)))
                        file.SetProgress(state, p.Operation);
                }
                if (activity is { } id)
                    _activities?.Report(id, StatusText,
                        p.TotalItems <= 0 ? null : (double)p.CompletedItems / p.TotalItems);
            });
            var result = await Task.Run(() =>
                _service.ApplyAsync(plan, decisions, progress, _cts.Token), _cts.Token);
            await progress.DrainAsync();
            if (!result.Cancelled && result.Albums.Any(a => a.Success) && _library.IsReady)
            {
                // Once ingestion commits files, finish the cache refresh even if the user presses
                // Cancel; otherwise disk and the library view would knowingly diverge.
                SetStatus("Ingest.Status.Reindexing");
                var indexed = await _library.IndexAsync(ct: CancellationToken.None);
                SetStatus(
                    "Ingest.Status.ApplyIndexed",
                    result.Installed,
                    result.Failed,
                    indexed.Added,
                    indexed.Modified,
                    indexed.Removed);
                IngestCompleted?.Invoke();
            }
            else
            {
                if (result.Cancelled)
                {
                    SetStatus("Ingest.Status.ApplyCancelledByResult");
                    DiagnosticDetail = result.Message;
                }
                else if (plan.Albums.Count == 0)
                    SetStatus("Ingest.Status.CleanupComplete");
                else
                    SetStatus(
                        !_library.IsReady &&
                        result.Albums.Any(album => album.Success)
                            ? "Ingest.Status.ApplyResultNoLibrary"
                            : "Ingest.Status.ApplyResult",
                        result.Installed,
                        result.Failed);
                if (!result.Cancelled) IngestCompleted?.Invoke();
            }
            HasApplicablePreview = false; _plan = null;
            if (!result.Cancelled && _journals is not null)
                await RefreshHistoryAsync();
            FinishActivity(activity, StatusText,
                result.Cancelled ? AppActivityState.Cancelled :
                result.Failed == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Ingest.Status.ApplyCancelled");
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            SetFailure(
                "Ingest.Status.ApplyFailed",
                ex);
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally { FinishBusy(); }
    }

    private bool CanCancel() => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void FinishActivity(
        Guid? activity,
        string message,
        AppActivityState state = AppActivityState.Completed)
    {
        if (activity is { } id)
            _activities?.Finish(id, message, state);
    }

    private void FinishBusy()
    {
        _cts?.Dispose(); _cts = null; IsBusy = false; IsPreviewing = false; IsApplying = false;
        PreviewCommand.NotifyCanExecuteChanged(); PreflightCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void RefilterFiles()
    {
        Files.Clear();
        foreach (var item in _allFiles.Where(item => SelectedPreviewFilter switch
                 {
                     IngestPreviewFilter.Albums => item.IsAlbum,
                     IngestPreviewFilter.Outputs => item.IsOutput,
                     IngestPreviewFilter.Conflicts => item.IsConflict,
                     IngestPreviewFilter.Cleanup => item.IsCleanup,
                     _ => true,
                 }))
            Files.Add(item);
        OnPropertyChanged(nameof(IsPreviewEmpty));
    }

    private void LoadRecentSources()
    {
        RecentSources.Clear();
        try
        {
            var sources = JsonSerializer.Deserialize<List<string>>(
                _settings.GetLibraryPreference(RecentSourcesPreference) ?? "[]") ?? [];
            foreach (string source in sources.Where(source => !string.IsNullOrWhiteSpace(source))
                         .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentSourceLimit))
                RecentSources.Add(source);
        }
        catch { }
    }

    private void AddRecentSource(string source)
    {
        string fullPath = Path.GetFullPath(source);
        var existing = RecentSources.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item, fullPath));
        if (existing is not null)
            RecentSources.Remove(existing);
        RecentSources.Insert(0, fullPath);
        while (RecentSources.Count > RecentSourceLimit)
            RecentSources.RemoveAt(RecentSources.Count - 1);
        _settings.SetLibraryPreference(RecentSourcesPreference, JsonSerializer.Serialize(RecentSources));
        SelectedRecentSource = fullPath;
    }

    private IReadOnlyList<string> GetConfigurationIssues()
    {
        if (_settings.Configuration is not { } configuration)
            return [
                L(
                    "Ingest.Configuration.Diagnostic.NotLoaded"),
            ];
        return IngestMusicConfiguration.MissingLibrarySettings(configuration);
    }

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

    private string LFC(
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
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = null;
        StatusText = LF(
            key,
            arguments);
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = count;
        StatusText = LFC(
            key,
            count,
            arguments);
    }

    private void SetHistoryStatus(
        string key,
        params object?[] arguments)
    {
        _historyStatusKey = key;
        _historyStatusArguments = arguments;
        _historyStatusCount = null;
        HistoryStatus = LF(
            key,
            arguments);
    }

    private void SetHistoryCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _historyStatusKey = key;
        _historyStatusArguments = arguments;
        _historyStatusCount = count;
        HistoryStatus = LFC(
            key,
            count,
            arguments);
    }

    private void SetFailure(
        string key,
        Exception error)
    {
        SetStatus(key);
        DiagnosticDetail = error.Message;
    }

    private void SetHistoryFailure(
        string key,
        Exception error)
    {
        SetHistoryStatus(key);
        HistoryDiagnosticDetail = error.Message;
    }

    private string LocalizeOperation(
        string operation)
    {
        string? key = operation switch
        {
            "Staging outputs" =>
                "Ingest.Operation.StagingOutputs",
            "Source complete" =>
                "Ingest.Operation.SourceComplete",
            "Cancelled" =>
                "Ingest.Operation.Cancelled",
            "Complete" =>
                "Ingest.Operation.Complete",
            "Preparing" =>
                "Ingest.Operation.Preparing",
            "Encoding" =>
                "Ingest.Operation.Encoding",
            "Empty folders removed" =>
                "Ingest.Operation.EmptyFoldersRemoved",
            _ => null,
        };
        if (key is not null)
            return L(key);
        if (operation.StartsWith(
                "Staged ",
                StringComparison.Ordinal))
            return LF(
                "Ingest.Operation.Staged",
                operation["Staged ".Length..]);
        if (operation.StartsWith(
                "Processing ",
                StringComparison.Ordinal))
            return LF(
                "Ingest.Operation.Processing",
                operation["Processing ".Length..]);
        return L("Ingest.Operation.Working");
    }

    private string LocalizeAlbum(
        string album) =>
        album.Equals(
            "Non-music cleanup",
            StringComparison.Ordinal)
            ? L("Ingest.Operation.NonMusicCleanup")
            : album;

    private void RefreshLocalizedChoices()
    {
        IngestPreviewFilter[] filters =
            Enum.GetValues<IngestPreviewFilter>();
        if (PreviewFilterChoices.Count == 0)
        {
            foreach (IngestPreviewFilter filter in filters)
                PreviewFilterChoices.Add(
                    new LocalizedChoice<IngestPreviewFilter>(
                        filter,
                        L(
                            $"Ingest.PreviewFilter.{filter}")));
            return;
        }

        foreach (LocalizedChoice<IngestPreviewFilter> choice in
                 PreviewFilterChoices)
            choice.Label = L(
                $"Ingest.PreviewFilter.{choice.Value}");
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_statusKey is { } statusKey)
            StatusText = _statusCount is { } statusCount
                ? LFC(
                    statusKey,
                    statusCount,
                    _statusArguments)
                : LF(
                    statusKey,
                    _statusArguments);
        if (_historyStatusKey is { } historyKey)
            HistoryStatus =
                _historyStatusCount is { } historyCount
                    ? LFC(
                        historyKey,
                        historyCount,
                        _historyStatusArguments)
                    : LF(
                        historyKey,
                        _historyStatusArguments);

        RefreshLocalizedChoices();
        foreach (IngestPreflightCheckViewModel check in
                 PreflightChecks)
            check.RefreshLocalization();
        foreach (IngestHistoryItemViewModel item in History)
            item.RefreshLocalization();
        foreach (IngestFileItemViewModel item in _allFiles)
            item.RefreshLocalization();
        OnPropertyChanged(nameof(ExplicitSourceSummary));
        OnPropertyChanged(nameof(ConfigurationReadinessText));
    }

    private void NotifyCommands()
    {
        PreviewCommand.NotifyCanExecuteChanged();
        PreflightCommand.NotifyCanExecuteChanged();
        ClearExplicitSourcesCommand
            .NotifyCanExecuteChanged();
    }

    private static readonly StringComparer
        PathComparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
    private static readonly StringComparison
        PathComparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
}

public sealed class IngestPreflightCheckViewModel
    : ViewModelBase
{
    private readonly ILocalizationService? _localization;

    public IngestPreflightCheck Check { get; }
    public IngestPreflightSeverity Severity =>
        Check.Severity;
    public string Name => L(
        Check.Name switch
        {
            "Source" =>
                "Ingest.Preflight.Check.Source",
            "Selected files" =>
                "Ingest.Preflight.Check.SelectedFiles",
            "Configuration" =>
                "Ingest.Preflight.Check.Configuration",
            "Path isolation" =>
                "Ingest.Preflight.Check.PathIsolation",
            "Destinations" =>
                "Ingest.Preflight.Check.Destinations",
            "iTunes library" =>
                "Ingest.Preflight.Check.ITunesLibrary",
            "ffmpeg" =>
                "Ingest.Preflight.Check.Ffmpeg",
            "WavPack" =>
                "Ingest.Preflight.Check.WavPack",
            _ =>
                "Ingest.Preflight.Check.Generic",
        });
    public string Message => L(
        Severity switch
        {
            IngestPreflightSeverity.Pass =>
                "Ingest.Preflight.Result.Pass",
            IngestPreflightSeverity.Warning =>
                "Ingest.Preflight.Result.Warning",
            _ =>
                "Ingest.Preflight.Result.Error",
        });
    public string DiagnosticDetail =>
        Check.Message;

    public IngestPreflightCheckViewModel(
        IngestPreflightCheck check,
        ILocalizationService? localization = null)
    {
        Check = check;
        _localization = localization;
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Message));
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
}

public sealed class IngestHistoryItemViewModel
    : ViewModelBase
{
    private readonly ILocalizationService? _localization;

    public OperationJournalSummary Summary { get; }
    public string Created =>
        Summary.CreatedAtUtc
            .ToLocalTime()
            .ToString(
                "g",
                _localization?.CurrentUICulture ??
                System.Globalization.CultureInfo
                    .CurrentCulture);
    public string State => L(
        Summary.State switch
        {
            OperationJournalState.Completed =>
                "Ingest.History.State.Completed",
            OperationJournalState.Interrupted =>
                "Ingest.History.State.Interrupted",
            OperationJournalState.RolledBack =>
                "Ingest.History.State.RolledBack",
            _ =>
                "Ingest.History.State.Quarantine",
        });
    public OperationJournalState StateValue =>
        Summary.State;
    public bool IsInterrupted =>
        Summary.State ==
        OperationJournalState.Interrupted;
    public string AffectedItems =>
        Summary.AffectedItemCount is int count
            ? LFC(
                "Ingest.History.AffectedItems",
                count)
            : L("Ingest.History.OpenForDetails");
    public string RunPath => Summary.RunPath;

    public IngestHistoryItemViewModel(
        OperationJournalSummary summary,
        ILocalizationService? localization = null)
    {
        Summary = summary;
        _localization = localization;
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Created));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(AffectedItems));
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LFC(
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

public partial class IngestFileItemViewModel : ViewModelBase
{
    private readonly IngestFileSummary _file;
    private readonly ILocalizationService? _localization;
    private readonly IngestConflict? _conflict;
    private readonly IngestOutputPlan? _output;
    private IngestFileProgressState? _progressState;
    private string? _progressOperation;

    public string Source => _file.Source;
    public string SourceType =>
        _conflict is not null
            ? L("Ingest.File.Type.Conflict")
            : _output is not null
                ? LF(
                    "Ingest.File.Type.Output",
                    LocalizeOutputKind(
                        _output.Kind))
                : _file.SourceType switch
                {
                    "Hi-Res ALAC" =>
                        L("Ingest.File.Type.HiResAlac"),
                    "Hi-Res FLAC" =>
                        L("Ingest.File.Type.HiResFlac"),
                    "CD-quality ALAC" =>
                        L("Ingest.File.Type.CdAlac"),
                    "CD FLAC" =>
                        L("Ingest.File.Type.CdFlac"),
                    "Unsupported/non-audio" =>
                        L("Ingest.File.Type.Unsupported"),
                    _ =>
                        _file.SourceType,
                };
    public string Summary =>
        _conflict is not null
            ? LF(
                "Ingest.File.ConflictSummary",
                _conflict.Message)
            : _output is not null
                ? LF(
                    "Ingest.File.OutputDestination",
                    _output.DestinationPath)
                : _file.Summary;
    public bool IsAlbum { get; }
    public bool IsOutput { get; }
    public bool IsCleanup { get; }
    public bool IsConflict { get; }
    public IngestFileProgressState? ProgressState =>
        _progressState;
    public string? ProgressText =>
        _progressState switch
        {
            IngestFileProgressState.InProgress =>
                LF(
                    "Ingest.File.Progress.InProgress",
                    LocalizeOperation(
                        _progressOperation)),
            IngestFileProgressState.Completed =>
                L("Ingest.File.Progress.Complete"),
            IngestFileProgressState.Failed =>
                L("Ingest.File.Progress.Failed"),
            _ =>
                null,
        };
    public string? DiagnosticDetail { get; private set; }
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DiagnosticDetail);

    [ObservableProperty]
    private bool _isComplete;

    public IngestFileItemViewModel(
        IngestFileSummary file,
        bool isAlbum = false,
        bool isOutput = false,
        bool isCleanup = false,
        bool isConflict = false,
        ILocalizationService? localization = null)
        : this(
            file,
            isAlbum,
            isOutput,
            isCleanup,
            isConflict,
            localization,
            null,
            null)
    {
    }

    private IngestFileItemViewModel(
        IngestFileSummary file,
        bool isAlbum,
        bool isOutput,
        bool isCleanup,
        bool isConflict,
        ILocalizationService? localization,
        IngestConflict? conflict,
        IngestOutputPlan? output)
    {
        _file = file;
        _localization = localization;
        _conflict = conflict;
        _output = output;
        IsAlbum = isAlbum;
        IsOutput = isOutput;
        IsCleanup = isCleanup;
        IsConflict = isConflict;
        DiagnosticDetail = conflict?.Message;
    }

    public static IngestFileItemViewModel ForConflict(
        IngestConflict conflict,
        ILocalizationService? localization = null) =>
        new(
            new IngestFileSummary(
                conflict.Path,
                "",
                ""),
            false,
            false,
            false,
            true,
            localization,
            conflict,
            null);

    public static IngestFileItemViewModel ForOutput(
        IngestOutputPlan output,
        ILocalizationService? localization = null) =>
        new(
            new IngestFileSummary(
                output.SourcePath,
                "",
                ""),
            false,
            true,
            false,
            false,
            localization,
            null,
            output);

    public void ResetProgress()
    {
        IsComplete = false;
        _progressState = null;
        _progressOperation = null;
        if (_conflict is null)
            DiagnosticDetail = null;
        OnPropertyChanged(nameof(ProgressState));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DiagnosticDetail));
        OnPropertyChanged(nameof(HasDiagnosticDetail));
    }

    public void SetProgress(
        IngestFileProgressState state,
        string operation)
    {
        if (IsComplete &&
            state ==
            IngestFileProgressState.InProgress)
            return;
        IsComplete =
            state ==
            IngestFileProgressState.Completed;
        _progressState = state;
        _progressOperation = operation;
        if (state ==
            IngestFileProgressState.Failed &&
            !operation.Equals(
                "Cancelled",
                StringComparison.Ordinal))
            DiagnosticDetail = operation;
        OnPropertyChanged(nameof(ProgressState));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DiagnosticDetail));
        OnPropertyChanged(nameof(HasDiagnosticDetail));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(SourceType));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ProgressText));
    }

    private string LocalizeOutputKind(
        IngestOutputKind kind) =>
        L(
            $"Ingest.OutputKind.{kind}");

    private string LocalizeOperation(
        string? operation)
    {
        if (operation is null)
            return L("Ingest.Operation.Working");
        string? key = operation switch
        {
            "Staging outputs" =>
                "Ingest.Operation.StagingOutputs",
            "Source complete" =>
                "Ingest.Operation.SourceComplete",
            "Cancelled" =>
                "Ingest.Operation.Cancelled",
            "Complete" =>
                "Ingest.Operation.Complete",
            "Preparing" =>
                "Ingest.Operation.Preparing",
            "Encoding" =>
                "Ingest.Operation.Encoding",
            "Empty folders removed" =>
                "Ingest.Operation.EmptyFoldersRemoved",
            _ => null,
        };
        if (key is not null)
            return L(key);
        if (operation.StartsWith(
                "Staged ",
                StringComparison.Ordinal))
            return LF(
                "Ingest.Operation.Staged",
                operation["Staged ".Length..]);
        if (operation.StartsWith(
                "Processing ",
                StringComparison.Ordinal))
            return LF(
                "Ingest.Operation.Processing",
                operation["Processing ".Length..]);
        return L("Ingest.Operation.Working");
    }

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
}
