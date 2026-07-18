using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataCaching;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class IndexingViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly IAppSettings _settings;
    private readonly IActivityService _activities;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isIndexing;

    [ObservableProperty]
    private string _statusText = "Load a configuration to begin.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private bool _isProgressIndeterminate;

    public IndexingViewModel(ILibraryService library, IAppSettings settings, IActivityService activities)
    {
        _library = library;
        _settings = settings;
        _activities = activities;
        settings.ConfigurationChanged += (_, _) =>
        {
            StatusText = "Cached library ready. Index when convenient.";
            IndexCommand.NotifyCanExecuteChanged();
        };
    }

    public event Action? IndexCompleted;
    public string ProgressText => IsProgressIndeterminate ? "Scanning…" : $"{Progress:P0}";

    /// <summary>Starts the deferred scan after startup has restored the responsive cached view.</summary>
    public Task StartAutomaticIndexAsync() => IndexCommand.CanExecute(null)
        ? IndexCommand.ExecuteAsync(null)
        : Task.CompletedTask;

    private bool CanIndex() => _library.IsReady && !IsIndexing;

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private async Task IndexAsync()
    {
        IsIndexing = true;
        Progress = 0;
        IsProgressIndeterminate = true;
        _cancellation = new CancellationTokenSource();
        Guid activity = _activities.Start("Index library", "Preparing the library index");
        var progress = new Progress<IndexProgress>(item =>
        {
            StatusText = Describe(item);
            IsProgressIndeterminate = item.Phase is IndexPhase.Preparing or IndexPhase.Enumeration;
            Progress = Estimate(item);
            _activities.Report(activity, StatusText, Progress);
        });
        try
        {
            var result = await _library.IndexAsync(progress, _cancellation.Token);
            StatusText = $"Index complete: +{result.Added:N0}, ~{result.Modified:N0}, -{result.Removed:N0}, {result.Unchanged:N0} unchanged.";
            IsProgressIndeterminate = false;
            Progress = 1;
            _activities.Finish(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Indexing cancelled; safely committed partial progress was retained.";
            _activities.Finish(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            StatusText = $"Indexing failed: {error.Message}";
            _activities.Finish(activity, StatusText, AppActivityState.Failed);
        }
        finally
        {
            IsProgressIndeterminate = false;
            _cancellation.Dispose();
            _cancellation = null;
            IsIndexing = false;
            IndexCompleted?.Invoke();
        }
    }

    private bool CanCancel() => IsIndexing;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    private static string Describe(IndexProgress item)
    {
        string phase = item.Phase switch
        {
            IndexPhase.Preparing => "Preparing",
            IndexPhase.Enumeration => "Finding music",
            IndexPhase.Metadata => "Reading metadata",
            IndexPhase.Database => "Updating cache",
            IndexPhase.Artwork => "Indexing artwork",
            IndexPhase.Finalizing => "Finalizing",
            IndexPhase.Completed => "Complete",
            _ => "Indexing",
        };
        string count = item.Phase switch
        {
            IndexPhase.Enumeration => $"{item.Enumerated:N0} found",
            IndexPhase.Metadata => $"{item.Scanned:N0} read",
            IndexPhase.Database => $"{item.DatabaseProcessed:N0} saved",
            _ => "",
        };
        return string.Join(" · ", new[] { phase, count, item.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static double Estimate(IndexProgress item) => item.Phase switch
    {
        IndexPhase.Preparing => 0.03,
        IndexPhase.Enumeration => 0.15,
        IndexPhase.Metadata => item.Enumerated > 0
            ? 0.15 + Math.Min(0.5, 0.5 * item.Scanned / item.Enumerated)
            : 0.25,
        IndexPhase.Database => item.Scanned > 0
            ? 0.65 + Math.Min(0.22, 0.22 * item.DatabaseProcessed / item.Scanned)
            : 0.75,
        IndexPhase.Artwork => 0.9,
        IndexPhase.Finalizing => 0.96,
        IndexPhase.Completed => 1,
        _ => 0,
    };
}
