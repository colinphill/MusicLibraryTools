using System.ComponentModel;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Services;

/// <summary>Connects Core workflow view models to manager-wide navigation, refresh, and activity UI.</summary>
public sealed class WorkflowIntegrationService : IDisposable
{
    private readonly AnalyzerViewModel _health;
    private readonly IngestViewModel _ingest;
    private readonly OrganizeViewModel _organize;
    private readonly OperationsViewModel _operations;
    private readonly LibraryViewModel _library;
    private readonly INavigationService _navigation;
    private readonly IPlatformService _platform;
    private readonly IActivityService _activities;
    private readonly ILocalizationService? _localization;
    private readonly Dictionary<INotifyPropertyChanged, WorkflowState> _workflows = [];
    private bool _started;

    public WorkflowIntegrationService(
        AnalyzerViewModel health,
        IngestViewModel ingest,
        OrganizeViewModel organize,
        OperationsViewModel operations,
        LibraryViewModel library,
        INavigationService navigation,
        IPlatformService platform,
        IActivityService activities,
        ILocalizationService? localization = null)
    {
        _health = health;
        _ingest = ingest;
        _organize = organize;
        _operations = operations;
        _library = library;
        _navigation = navigation;
        _platform = platform;
        _activities = activities;
        _localization = localization;
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _health.RepairsApplied += FilesChanged;
        _health.OpenRequested += OpenRequested;
        _health.FilterChanged += HealthFilterChanged;
        _library.HealthFilterClearRequested += HealthFilterClearRequested;
        _library.SetHealthFilter(_health.FilteredPaths);
        _ingest.IngestCompleted += LibraryChanged;
        _ingest.RecoveryRequested += RecoveryRequested;
        _organize.MovesApplied += LibraryChanged;
        _operations.ArtworkNormalized += FilesChanged;

        Observe(
            _health,
            Text("Activity.HealthAnalysis"),
            ShellDestination.Health,
            () => _health.IsBusy, () => _health.StatusText,
            () => _health.LastActivityState,
            () => ExecuteIfAvailable(_health.CancelCommand),
            () => _health.IsAnalysisProgressIndeterminate
                ? null
                : _health.AnalysisProgressFraction);
    }

    private void Observe(
        INotifyPropertyChanged source,
        string title,
        ShellDestination destination,
        Func<bool> busy,
        Func<string?> status,
        Func<AppActivityState> outcome,
        Action cancel,
        Func<double?>? progress = null)
    {
        _workflows[source] = new WorkflowState(
            title, destination, busy, status, outcome, cancel, progress);
        source.PropertyChanged += WorkflowChanged;
    }

    private static void ExecuteIfAvailable(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void WorkflowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not INotifyPropertyChanged source || !_workflows.TryGetValue(source, out WorkflowState? state))
            return;
        if (e.PropertyName is nameof(AnalyzerViewModel.IsBusy))
        {
            if (state.IsBusy() && state.ActivityId is null)
                state.ActivityId = _activities.Start(
                    state.Title,
                    state.Status() ??
                        Text("Activity.Starting"),
                    state.Destination,
                    state.Cancel);
            else if (!state.IsBusy() && state.ActivityId is Guid id)
            {
                string message = state.Status() ??
                    Text("Activity.Finished");
                _activities.Finish(id, message, state.Outcome());
                state.ActivityId = null;
            }
        }
        else if ((e.PropertyName is nameof(AnalyzerViewModel.StatusText) or
                  nameof(AnalyzerViewModel.AnalysisProgressFraction) or
                  nameof(AnalyzerViewModel.IsAnalysisProgressIndeterminate)) &&
                 state.ActivityId is Guid id)
        {
            _activities.Report(
                id,
                state.Status() ??
                    Text("Activity.Working"),
                state.Progress?.Invoke());
        }
    }

    private void OpenRequested(string path) => _platform.RevealFile(path);
    private void HealthFilterChanged(IReadOnlyList<string> paths) =>
        _library.SetHealthFilter(paths);
    private void HealthFilterClearRequested() => _health.ClearFilterDispositions();
    private void LibraryChanged() => _ = _library.ReloadAsync();
    private void FilesChanged(IReadOnlyList<string> paths) => _ = _library.ReloadAsync();

    private async void RecoveryRequested(MusicLibrary.Core.Models.OperationJournalSummary summary)
    {
        _navigation.Navigate(ShellDestination.Operations);
        await _operations.OpenRunFromHistoryAsync(summary);
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    public void Dispose()
    {
        if (!_started)
            return;
        _health.RepairsApplied -= FilesChanged;
        _health.OpenRequested -= OpenRequested;
        _health.FilterChanged -= HealthFilterChanged;
        _library.HealthFilterClearRequested -= HealthFilterClearRequested;
        _ingest.IngestCompleted -= LibraryChanged;
        _ingest.RecoveryRequested -= RecoveryRequested;
        _organize.MovesApplied -= LibraryChanged;
        _operations.ArtworkNormalized -= FilesChanged;
        foreach (INotifyPropertyChanged source in _workflows.Keys)
            source.PropertyChanged -= WorkflowChanged;
        _workflows.Clear();
        _started = false;
    }

    private sealed class WorkflowState(
        string title,
        ShellDestination destination,
        Func<bool> isBusy,
        Func<string?> status,
        Func<AppActivityState> outcome,
        Action cancel,
        Func<double?>? progress)
    {
        public string Title { get; } = title;
        public ShellDestination Destination { get; } = destination;
        public Func<bool> IsBusy { get; } = isBusy;
        public Func<string?> Status { get; } = status;
        public Func<AppActivityState> Outcome { get; } = outcome;
        public Action Cancel { get; } = cancel;
        public Func<double?>? Progress { get; } = progress;
        public Guid? ActivityId { get; set; }
    }
}
