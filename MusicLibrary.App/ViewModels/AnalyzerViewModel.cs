using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>Which result section the Analyze tab is currently showing.</summary>
public enum AnalysisResultView { Findings, Duplicates, Artists }

/// <summary>
/// Library-wide analysis. Each analysis type is run by its own button (inconsistencies, lossy files,
/// duplicates, similar artists, cross-set check); results replace the previous run. Selecting a
/// finding/track opens that file; similar-artist clusters can be merged in place.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Choose an analysis to run.";

    [ObservableProperty]
    private AnalysisResultView _activeView = AnalysisResultView.Findings;

    /// <summary>Fuzzy-distance threshold for the similar-artist check (AnalyzeMetadata's checkartists thresh).</summary>
    [ObservableProperty]
    private double _artistThreshold = 0.2;

    public ObservableCollection<AnalysisReport> Reports { get; } = [];
    public ObservableCollection<DuplicateGroup> Duplicates { get; } = [];
    public ObservableCollection<ArtistGroupViewModel> ArtistGroups { get; } = [];

    // Section visibility (bound in XAML; ActiveView drives which one shows).
    public bool ShowFindings => ActiveView == AnalysisResultView.Findings;
    public bool ShowDuplicates => ActiveView == AnalysisResultView.Duplicates;
    public bool ShowArtists => ActiveView == AnalysisResultView.Artists;

    /// <summary>Raised with a file path when the user opens a finding/track.</summary>
    public event Action<string>? OpenRequested;

    public AnalyzerViewModel(ILibraryService library, IArtistReconciler reconciler, IAppSettings settings)
    {
        _library = library;
        _reconciler = reconciler;
        settings.ConfigurationChanged += (_, _) => NotifyCommands();
    }

    partial void OnActiveViewChanged(AnalysisResultView value)
    {
        OnPropertyChanged(nameof(ShowFindings));
        OnPropertyChanged(nameof(ShowDuplicates));
        OnPropertyChanged(nameof(ShowArtists));
    }

    private bool CanRun() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunInconsistencies() => RunOverRecords("Inconsistencies", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Inconsistencies(records);
        return (report.Count == 0 ? "Inconsistencies: none found." : $"Inconsistencies: {report.Count:N0} finding(s).",
                () => ShowReport(report));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunLossy() => RunOverRecords("Lossy files", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Lossless(records);
        return (report.Count == 0 ? "No lossy files." : $"Lossy files: {report.Count:N0}.",
                () => ShowReport(report));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunDuplicates() => RunOverRecords("Duplicates", AnalysisResultView.Duplicates, (records, ct) =>
    {
        var dupes = DuplicateFinder.Find(records, ct);
        return (dupes.Count == 0 ? "No duplicates found." : $"{dupes.Count:N0} duplicate group(s).",
                () => { Duplicates.Clear(); foreach (var d in dupes) Duplicates.Add(d); });
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunSimilarArtists() => RunOverRecords("Similar artists", AnalysisResultView.Artists, (records, ct) =>
    {
        var groups = _reconciler.FindSimilarArtists(records, ArtistThreshold, ct);
        return (groups.Count == 0 ? "No similar artist names found." : $"{groups.Count:N0} cluster(s) of similar artist names.",
                () => { ArtistGroups.Clear(); foreach (var g in groups) ArtistGroups.Add(new ArtistGroupViewModel(_reconciler, g)); });
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCheckSets()
    {
        using var scope = BeginRun("Cross-set check", AnalysisResultView.Findings);
        try
        {
            var report = await _library.CheckSetsAsync(scope.Token);
            ShowReport(report);
            StatusText = report.Count == 0
                ? "Cross-set check: no differences (needs 2+ configured sets to compare)."
                : $"Cross-set check: {report.Count:N0} finding(s).";
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

    private void ShowReport(AnalysisReport report)
    {
        Reports.Clear();
        Reports.Add(report);
    }

    private void NotifyCommands()
    {
        RunInconsistenciesCommand.NotifyCanExecuteChanged();
        RunLossyCommand.NotifyCanExecuteChanged();
        RunDuplicatesCommand.NotifyCanExecuteChanged();
        RunSimilarArtistsCommand.NotifyCanExecuteChanged();
        RunCheckSetsCommand.NotifyCanExecuteChanged();
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
