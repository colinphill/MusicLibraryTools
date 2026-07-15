using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

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
    private const string SearchRootPreference = "Operations.SearchRoot";
    private const string RetentionDaysPreference = "Operations.RetentionDays";
    private const string JobDirectoryPreference = "Operations.JobDirectory";
    private const string JobHistoryPreference = "Operations.JobHistory";
    private readonly IOperationJournalService _journals;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private readonly IUnifiedJobService? _jobs;
    private CancellationTokenSource? _cts;

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

    [ObservableProperty]
    private UnifiedJobDescriptor? _selectedJob;
    [ObservableProperty]
    private string _jobExecutableDirectory = AppContext.BaseDirectory;
    [ObservableProperty]
    private string _jobArguments = "";
    [ObservableProperty]
    private string _jobStatus = "Choose a job and enter its arguments, then Preview.";
    [ObservableProperty]
    private string _jobOutput = "";
    [ObservableProperty]
    private bool _hasJobPreview;

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];
    public ObservableCollection<OperationEntryNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<UnifiedJobHistoryItem> JobHistory { get; } = [];
    public IReadOnlyList<UnifiedJobDescriptor> JobCatalog => _jobs?.Catalog ?? [];

    public OperationsViewModel(
        IOperationJournalService journals,
        IFileDialogService files,
        IDialogService dialogs,
        IAppSettings settings,
        IUnifiedJobService? jobs = null)
    {
        _journals = journals;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _jobs = jobs;
        SearchRoot = settings.GetPreference(SearchRootPreference);
        if (int.TryParse(settings.GetPreference(RetentionDaysPreference), out int days))
            RetentionDays = Math.Clamp(days, 1, 3650);
        JobExecutableDirectory = settings.GetPreference(JobDirectoryPreference) ?? AppContext.BaseDirectory;
        LoadJobHistory();
        SelectedJob = JobCatalog.FirstOrDefault();
    }

    partial void OnSelectedJobChanged(UnifiedJobDescriptor? value)
    {
        InvalidateJobPreview();
        if (value is not null && string.IsNullOrWhiteSpace(JobArguments) &&
            value.Id is "playlist-sync" or "cross-library-sync" or "car-card" &&
            !string.IsNullOrWhiteSpace(_settings.ConfigPath))
            JobArguments = Quote(_settings.ConfigPath!);
    }

    partial void OnJobArgumentsChanged(string value) => InvalidateJobPreview();
    partial void OnJobExecutableDirectoryChanged(string value)
    {
        _settings.SetPreference(JobDirectoryPreference, string.IsNullOrWhiteSpace(value) ? null : value);
        InvalidateJobPreview();
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

    [RelayCommand]
    private async Task BrowseJobDirectoryAsync()
    {
        string? path = await _files.PickFolderAsync("Select directory containing MusicLibraryTools executables");
        if (path is not null) JobExecutableDirectory = path;
    }

    private bool CanPreviewJob() => !IsBusy && _jobs is not null && SelectedJob is not null &&
        !string.IsNullOrWhiteSpace(JobExecutableDirectory);

    [RelayCommand(CanExecute = nameof(CanPreviewJob))]
    private async Task PreviewJobAsync()
    {
        if (_jobs is null || SelectedJob is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        JobOutput = "";
        JobStatus = $"Previewing {SelectedJob.Name}â€¦";
        try
        {
            var progress = new Progress<string>(line => JobStatus = line);
            _jobPlan = await _jobs.PreviewAsync(SelectedJob, JobExecutableDirectory,
                JobArguments, progress, _cts.Token);
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
            var progress = new Progress<string>(line => JobStatus = line);
            var result = await _jobs.ApplyAsync(plan, progress, _cts.Token);
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
    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

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
        Add(_settings.GetPreference("Ingest.SourceDirectory"));
        var snapshot = _settings.GetSnapshot();
        if (snapshot.ConfigPath is not null)
            Add(Path.GetDirectoryName(snapshot.ConfigPath));
        if (snapshot.Configuration is { } configuration)
        {
            foreach (var location in configuration.IndexLocations)
                Add(location.Target);
            foreach (string key in new[] { "SyncTarget", "PlaylistTarget" })
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
