using System.ComponentModel;
using MusicLibrary.App.ViewModels;
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
        IActivityService activities)
    {
        _health = health;
        _ingest = ingest;
        _organize = organize;
        _operations = operations;
        _library = library;
        _navigation = navigation;
        _platform = platform;
        _activities = activities;
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _health.RepairsApplied += FilesChanged;
        _health.OpenRequested += OpenRequested;
        _ingest.IngestCompleted += LibraryChanged;
        _ingest.RecoveryRequested += RecoveryRequested;
        _organize.MovesApplied += LibraryChanged;
        _operations.ArtworkNormalized += FilesChanged;

        Observe(_health, "Health analysis", () => _health.IsBusy, () => _health.StatusText);
        Observe(_ingest, "Ingest", () => _ingest.IsBusy, () => _ingest.StatusText);
        Observe(_organize, "Organize", () => _organize.IsBusy, () => _organize.StatusText);
        Observe(_operations, "Library operation", () => _operations.IsBusy, () => _operations.StatusText);
    }

    private void Observe(INotifyPropertyChanged source, string title, Func<bool> busy, Func<string?> status)
    {
        _workflows[source] = new WorkflowState(title, busy, status);
        source.PropertyChanged += WorkflowChanged;
    }

    private void WorkflowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not INotifyPropertyChanged source || !_workflows.TryGetValue(source, out WorkflowState? state))
            return;
        if (e.PropertyName is nameof(AnalyzerViewModel.IsBusy))
        {
            if (state.IsBusy() && state.ActivityId is null)
                state.ActivityId = _activities.Start(state.Title, state.Status() ?? "Starting…");
            else if (!state.IsBusy() && state.ActivityId is Guid id)
            {
                string message = state.Status() ?? "Finished.";
                _activities.Finish(id, message, InferState(message));
                state.ActivityId = null;
            }
        }
        else if (e.PropertyName is nameof(AnalyzerViewModel.StatusText) && state.ActivityId is Guid id)
        {
            _activities.Report(id, state.Status() ?? "Working…");
        }
    }

    private static AppActivityState InferState(string message)
    {
        if (message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return AppActivityState.Cancelled;
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("error", StringComparison.OrdinalIgnoreCase))
            return AppActivityState.Failed;
        return AppActivityState.Completed;
    }

    private void OpenRequested(string path) => _platform.RevealFile(path);
    private void LibraryChanged() => _ = _library.ReloadAsync();
    private void FilesChanged(IReadOnlyList<string> paths) => _ = _library.ReloadAsync();

    private async void RecoveryRequested(MusicLibrary.Core.Models.OperationJournalSummary summary)
    {
        _navigation.Navigate(ShellDestination.Operations);
        await _operations.OpenRunFromHistoryAsync(summary);
    }

    public void Dispose()
    {
        if (!_started)
            return;
        _health.RepairsApplied -= FilesChanged;
        _health.OpenRequested -= OpenRequested;
        _ingest.IngestCompleted -= LibraryChanged;
        _ingest.RecoveryRequested -= RecoveryRequested;
        _organize.MovesApplied -= LibraryChanged;
        _operations.ArtworkNormalized -= FilesChanged;
        foreach (INotifyPropertyChanged source in _workflows.Keys)
            source.PropertyChanged -= WorkflowChanged;
        _workflows.Clear();
        _started = false;
    }

    private sealed class WorkflowState(string title, Func<bool> isBusy, Func<string?> status)
    {
        public string Title { get; } = title;
        public Func<bool> IsBusy { get; } = isBusy;
        public Func<string?> Status { get; } = status;
        public Guid? ActivityId { get; set; }
    }
}
