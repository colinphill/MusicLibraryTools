using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>Which result section the Analyze tab is currently showing.</summary>
public enum AnalysisResultView { Findings, Duplicates, Artists, Conflicts, Repairs, Matrix }

/// <summary>
/// Library-wide analysis. Each analysis type is run by its own button (inconsistencies, lossy files,
/// duplicates, similar artists, cross-set check); typed results are retained for the session.
/// Selecting a finding/track opens that file; similar-artist clusters can be merged in place.
/// Conservative and user-directed tag repairs share a preview/select/apply surface and reject
/// sources changed since preview.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private readonly IAnalysisRepairService _repairs;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Choose an analysis to run.";

    [ObservableProperty]
    private AnalysisResultView _activeView = AnalysisResultView.Findings;

    [ObservableProperty]
    private AnalysisRunViewModel? _selectedRun;

    /// <summary>Fuzzy-distance threshold for the similar-artist check (AnalyzeMetadata's checkartists thresh).</summary>
    [ObservableProperty]
    private double _artistThreshold = 0.2;

    public ObservableCollection<AnalysisRunViewModel> Runs { get; } = [];
    public ObservableCollection<AnalysisProblemGroupViewModel> FindingGroups { get; } = [];
    public ObservableCollection<DuplicateGroup> Duplicates { get; } = [];
    public ObservableCollection<ArtistGroupViewModel> ArtistGroups { get; } = [];
    public ObservableCollection<AnalysisConflictGroupViewModel> ConflictGroups { get; } = [];
    public ObservableCollection<AnalysisRepairItemViewModel> RepairItems { get; } = [];
    public ObservableCollection<AlbumMetadataMatrix> Matrices { get; } = [];
    public bool HasRuns => Runs.Count > 0;

    // Section visibility (bound in XAML; ActiveView drives which one shows).
    public bool ShowFindings => ActiveView == AnalysisResultView.Findings;
    public bool ShowDuplicates => ActiveView == AnalysisResultView.Duplicates;
    public bool ShowArtists => ActiveView == AnalysisResultView.Artists;
    public bool ShowConflicts => ActiveView == AnalysisResultView.Conflicts;
    public bool ShowRepairs => ActiveView == AnalysisResultView.Repairs;
    public bool ShowMatrix => ActiveView == AnalysisResultView.Matrix;

    /// <summary>Raised with a file path when the user opens a finding/track.</summary>
    public event Action<string>? OpenRequested;
    public event Action<IReadOnlyList<string>>? RepairsApplied;

    public AnalyzerViewModel(ILibraryService library, IArtistReconciler reconciler,
        IAnalysisRepairService repairs, IAppSettings settings)
    {
        _library = library;
        _reconciler = reconciler;
        _repairs = repairs;
        settings.ConfigurationChanged += (_, _) =>
        {
            ClearRuns();
        };
    }

    partial void OnActiveViewChanged(AnalysisResultView value)
    {
        OnPropertyChanged(nameof(ShowFindings));
        OnPropertyChanged(nameof(ShowDuplicates));
        OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowConflicts));
        OnPropertyChanged(nameof(ShowRepairs));
        OnPropertyChanged(nameof(ShowMatrix));
    }

    partial void OnSelectedRunChanged(AnalysisRunViewModel? value)
    {
        FindingGroups.Clear();
        Duplicates.Clear();
        ArtistGroups.Clear();
        ConflictGroups.Clear();
        RepairItems.Clear();
        Matrices.Clear();

        if (value is not null)
        {
            ActiveView = value.View;
            foreach (var group in value.FindingGroups) FindingGroups.Add(group);
            foreach (var group in value.Duplicates) Duplicates.Add(group);
            foreach (var group in value.ArtistGroups) ArtistGroups.Add(group);
            foreach (var group in value.ConflictGroups) ConflictGroups.Add(group);
            foreach (var item in value.RepairItems) RepairItems.Add(item);
            foreach (var matrix in value.Matrices) Matrices.Add(matrix);
            StatusText = value.Summary;
        }
        else
        {
            StatusText = "Choose an analysis to run.";
        }

        NotifyCommands();
    }

    private bool CanRun() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunInconsistencies() => RunOverRecords("Inconsistencies", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Inconsistencies(records);
        string status = report.Count == 0 ? "Inconsistencies: none found." : $"Inconsistencies: {report.Count:N0} finding(s).";
        return (status, () => AddRun(AnalysisRunViewModel.ForFindings(report, records, status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunLossy() => RunOverRecords("Lossy files", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Lossless(records);
        string status = report.Count == 0 ? "No lossy files." : $"Lossy files: {report.Count:N0}.";
        return (status, () => AddRun(AnalysisRunViewModel.ForFindings(report, records, status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunDuplicates() => RunOverRecords("Duplicates", AnalysisResultView.Duplicates, (records, ct) =>
    {
        var dupes = DuplicateFinder.Find(records, ct);
        string status = dupes.Count == 0 ? "No duplicates found." : $"{dupes.Count:N0} duplicate group(s).";
        return (status, () => AddRun(AnalysisRunViewModel.ForDuplicates("Duplicates", dupes, status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunSimilarArtists() => RunOverRecords("Similar artists", AnalysisResultView.Artists, (records, ct) =>
    {
        var groups = _reconciler.FindSimilarArtists(records, ArtistThreshold, ct);
        string status = groups.Count == 0 ? "No similar artist names found." : $"{groups.Count:N0} cluster(s) of similar artist names.";
        return (status, () => AddRun(AnalysisRunViewModel.ForArtists(
            "Similar artists",
            groups.Select(group => new ArtistGroupViewModel(_reconciler, group)).ToList(),
            status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAlbumMatrix() => RunOverRecords("Album metadata matrix", AnalysisResultView.Matrix, (records, ct) =>
    {
        var matrices = AlbumMetadataMatrixBuilder.Build(records);
        string status = matrices.Count == 0
            ? "Album metadata matrix: no inconsistent albums found."
            : $"Album metadata matrix: {matrices.Count:N0} album(s), " +
              $"{matrices.Sum(matrix => matrix.InconsistentCellCount):N0} inconsistent cell(s).";
        return (status, () => AddRun(AnalysisRunViewModel.ForMatrices(matrices, status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunRepresentations() => RunOverRecords("Album representations", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = RepresentationAnalyzer.Compare(records, ct);
        string status = report.Count == 0
            ? "Album representations: no missing or ambiguous counterparts found."
            : $"Album representations: {report.Count:N0} missing/ambiguous counterpart finding(s).";
        return (status, () => AddRun(AnalysisRunViewModel.ForFindings(report, records, status)));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewMetadataRepairs()
    {
        using var scope = BeginRun("Metadata repair preview", AnalysisResultView.Repairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var plan = await Task.Run(() => _repairs.PreviewSafeRepairs(records), scope.Token);
            var repairItems = new List<AnalysisRepairItemViewModel>(plan.Items.Count);
            foreach (var item in plan.Items)
            {
                var viewModel = new AnalysisRepairItemViewModel(item);
                viewModel.SelectionChanged += () => ApplyRepairsCommand.NotifyCanExecuteChanged();
                repairItems.Add(viewModel);
            }
            string status = plan.Items.Count == 0
                ? "No safely inferable metadata repairs were found."
                : $"Previewed {plan.Items.Count:N0} metadata repair(s). Review, then apply selected.";
            AddRun(AnalysisRunViewModel.ForRepairs(plan, repairItems, status));
        }
        catch (OperationCanceledException) { StatusText = "Metadata repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"Metadata repair preview failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FindAlbumArtistConflicts()
    {
        using var scope = BeginRun("Album artist conflicts", AnalysisResultView.Conflicts);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var conflicts = await Task.Run(() => _repairs.FindAlbumArtistConflicts(records), scope.Token);
            var groups = conflicts.Select(conflict =>
            {
                var group = new AnalysisConflictGroupViewModel(conflict);
                group.SelectionChanged += () => PreviewConflictRepairsCommand.NotifyCanExecuteChanged();
                return group;
            }).ToList();
            string status = groups.Count == 0
                ? "No conflicting album artists were found."
                : $"Found {groups.Count:N0} album(s) with conflicting album artists. Choose canonical values to continue.";
            AddRun(AnalysisRunViewModel.ForConflicts(groups, status));
        }
        catch (OperationCanceledException) { StatusText = "Album artist conflict search cancelled."; }
        catch (Exception ex) { StatusText = $"Album artist conflict search failed: {ex.Message}"; }
        finally { PreviewConflictRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanPreviewConflictRepairs() => !IsBusy &&
        ConflictGroups.Any(group => group.SelectedOption is not null);

    [RelayCommand(CanExecute = nameof(CanPreviewConflictRepairs))]
    private void PreviewConflictRepairs()
    {
        var resolutions = ConflictGroups
            .Where(group => group.SelectedOption is not null)
            .Select(group => new AnalysisConflictResolution(group.Conflict, group.SelectedOption!.Value))
            .ToList();
        if (resolutions.Count == 0)
            return;

        var plan = _repairs.PreviewConflictRepairs(resolutions);
        var items = plan.Items.Select(item =>
        {
            var viewModel = new AnalysisRepairItemViewModel(item);
            viewModel.SelectionChanged += () => ApplyRepairsCommand.NotifyCanExecuteChanged();
            return viewModel;
        }).ToList();
        string status = plan.Items.Count == 0
            ? "The selected canonical values already match every file."
            : $"Previewed {plan.Items.Count:N0} user-directed repair(s). Review, then apply selected.";
        AddRun(AnalysisRunViewModel.ForRepairs(plan, items, status));
    }

    private bool CanApplyRepairs() => !IsBusy && SelectedRun?.RepairPlan is not null &&
        RepairItems.Any(item => item.IsSelected && !item.IsApplied);

    [RelayCommand(CanExecute = nameof(CanApplyRepairs))]
    private async Task ApplyRepairs()
    {
        if (SelectedRun?.RepairPlan is not { } repairPlan)
            return;
        var selected = RepairItems.Where(item => item.IsSelected && !item.IsApplied).ToList();
        if (selected.Count == 0)
            return;
        int selectedFiles = selected.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        using var scope = BeginRun("Apply metadata repairs", AnalysisResultView.Repairs);
        try
        {
            var selectedPlan = repairPlan with { Items = selected.Select(item => item.Repair).ToList() };
            var progress = new Progress<int>(done =>
                StatusText = $"Applying metadata repairs… {done:N0}/{selectedFiles:N0} file(s)");
            var result = await _repairs.ApplyAsync(selectedPlan, progress, scope.Token);
            var byPath = result.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var item in selected)
            {
                if (!byPath.TryGetValue(item.Path, out var file))
                    continue;
                item.ResultText = file.Outcome switch
                {
                    WriteOutcome.Saved => "Applied",
                    WriteOutcome.Skipped => "Already correct",
                    _ => file.Error ?? "Failed",
                };
                item.IsApplied = file.Outcome is WriteOutcome.Saved or WriteOutcome.Skipped;
                if (item.IsApplied)
                    item.IsSelected = false;
            }
            StatusText = $"Metadata repairs: {result.Summary}.";
            var changed = result.Files
                .Where(file => file.Outcome == WriteOutcome.Saved)
                .Select(file => file.Path)
                .ToList();
            if (changed.Count > 0)
                RepairsApplied?.Invoke(changed);
        }
        catch (OperationCanceledException) { StatusText = "Metadata repair apply cancelled."; }
        catch (Exception ex) { StatusText = $"Metadata repair apply failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCheckSets()
    {
        using var scope = BeginRun("Cross-set check", AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var report = await _library.CheckSetsAsync(scope.Token);
            string status = report.Count == 0
                ? "Cross-set check: no differences (needs 2+ configured sets to compare)."
                : $"Cross-set check: {report.Count:N0} finding(s).";
            AddRun(AnalysisRunViewModel.ForFindings(report, records, status));
        }
        catch (OperationCanceledException) { StatusText = "Cross-set check cancelled."; }
        catch (Exception ex) { StatusText = $"Cross-set check failed: {ex.Message}"; }
    }

    // Shared runner for the analyses that operate on the flat record list: fetch records, run `body`
    // off the UI thread, then apply its result on the UI thread and set the status.
    private async Task RunOverRecords(string label, AnalysisResultView view,
        Func<IReadOnlyList<TrackRecord>, CancellationToken, (string Status, Action Apply)> body)
    {
        using var scope = BeginRun(label, view);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var (status, apply) = await Task.Run(() => body(records, scope.Token), scope.Token);
            apply();
            StatusText = status;
        }
        catch (OperationCanceledException) { StatusText = $"{label} cancelled."; }
        catch (Exception ex) { StatusText = $"{label} failed: {ex.Message}"; }
    }

    // Sets up busy state / cancellation / active view; disposing restores idle state.
    private RunScope BeginRun(string label, AnalysisResultView view)
    {
        IsBusy = true;
        // Keep the selected retained result visible while another analysis runs. With no history
        // yet, show the destination surface so the first run still has a natural empty state.
        if (SelectedRun is null)
            ActiveView = view;
        StatusText = $"Running {label}…";
        _cts = new CancellationTokenSource();
        NotifyCommands();
        return new RunScope(this);
    }

    private sealed class RunScope(AnalyzerViewModel vm) : IDisposable
    {
        public CancellationToken Token => vm._cts!.Token;
        public void Dispose()
        {
            vm.IsBusy = false;
            vm._cts?.Dispose();
            vm._cts = null;
            vm.NotifyCommands();
        }
    }

    private void AddRun(AnalysisRunViewModel run)
    {
        Runs.Insert(0, run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveRun() => !IsBusy && SelectedRun is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveRun))]
    private void RemoveRun()
    {
        if (SelectedRun is null)
            return;
        int index = Runs.IndexOf(SelectedRun);
        Runs.Remove(SelectedRun);
        SelectedRun = Runs.Count == 0 ? null : Runs[Math.Min(index, Runs.Count - 1)];
        OnPropertyChanged(nameof(HasRuns));
        NotifyCommands();
    }

    private bool CanClearRuns() => !IsBusy && Runs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRuns))]
    private void ClearRuns()
    {
        Runs.Clear();
        SelectedRun = null;
        OnPropertyChanged(nameof(HasRuns));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RunInconsistenciesCommand.NotifyCanExecuteChanged();
        RunLossyCommand.NotifyCanExecuteChanged();
        RunDuplicatesCommand.NotifyCanExecuteChanged();
        RunSimilarArtistsCommand.NotifyCanExecuteChanged();
        FindAlbumArtistConflictsCommand.NotifyCanExecuteChanged();
        PreviewConflictRepairsCommand.NotifyCanExecuteChanged();
        RunAlbumMatrixCommand.NotifyCanExecuteChanged();
        RunRepresentationsCommand.NotifyCanExecuteChanged();
        RunCheckSetsCommand.NotifyCanExecuteChanged();
        PreviewMetadataRepairsCommand.NotifyCanExecuteChanged();
        ApplyRepairsCommand.NotifyCanExecuteChanged();
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Open(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            OpenRequested?.Invoke(path);
    }
}

public partial class AnalysisRepairItemViewModel : ViewModelBase
{
    public AnalysisTagRepair Repair { get; }
    public string Path => Repair.Path;
    public string Field => Repair.Field.ToString();
    public string Before => string.IsNullOrWhiteSpace(Repair.Before) ? "(missing)" : Repair.Before;
    public string After => Repair.After;
    public string Reason => Repair.Reason;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string? _resultText;

    public event Action? SelectionChanged;

    public AnalysisRepairItemViewModel(AnalysisTagRepair repair) => Repair = repair;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

public partial class AnalysisConflictGroupViewModel : ViewModelBase
{
    public AnalysisTagConflict Conflict { get; }
    public string Album => Conflict.Album;
    public string Directory => Conflict.Directory;
    public string Field => Conflict.Field.ToString();
    public int FileCount => Conflict.Targets.Count;
    public int MissingCount => Conflict.Targets.Count(target => string.IsNullOrWhiteSpace(target.Before));
    public IReadOnlyList<AnalysisConflictOptionViewModel> Options { get; }

    [ObservableProperty]
    private AnalysisConflictOptionViewModel? _selectedOption;

    public event Action? SelectionChanged;

    public AnalysisConflictGroupViewModel(AnalysisTagConflict conflict)
    {
        Conflict = conflict;
        Options = conflict.Options.Select(option => new AnalysisConflictOptionViewModel(
            option,
            conflict.Targets
                .Where(target => !string.IsNullOrWhiteSpace(target.Before) &&
                    StringComparer.CurrentCultureIgnoreCase.Equals(target.Before.Trim(), option.Value))
                .Select(target => target.Path)
                .ToList()))
            .ToList();
    }

    partial void OnSelectedOptionChanged(AnalysisConflictOptionViewModel? value) => SelectionChanged?.Invoke();
}

public sealed class AnalysisConflictOptionViewModel(
    AnalysisConflictOption option,
    IReadOnlyList<string> paths)
{
    public string Value => option.Value;
    public int FileCount => option.FileCount;
    public IReadOnlyList<string> Paths => paths;
}
