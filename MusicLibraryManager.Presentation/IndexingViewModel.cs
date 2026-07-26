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
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cancellation;
    private string? _statusResourceKey =
        "Index.Status.LoadConfiguration";
    private object?[] _statusArguments = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isIndexing;

    [ObservableProperty]
    private string _statusText =
        LocalizedText.Get(
            "Index.Status.LoadConfiguration");

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasDiagnosticDetail))]
    private string? _diagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private bool _isProgressIndeterminate;

    public IndexingViewModel(
        ILibraryService library,
        IAppSettings settings,
        IActivityService activities,
        ILocalizationService? localization = null)
    {
        _library = library;
        _settings = settings;
        _activities = activities;
        _localization = localization;
        SetStatus(
            settings.Configuration is null
                ? "Index.Status.LoadConfiguration"
                : "Index.Status.CachedLibraryReady");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnCultureChanged;
        settings.ConfigurationChanged += (_, _) =>
        {
            SetStatus(
                "Index.Status.CachedLibraryReady");
            IndexCommand.NotifyCanExecuteChanged();
        };
    }

    public event Action? IndexCompleted;
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DiagnosticDetail);

    public string ProgressText =>
        IsProgressIndeterminate
            ? Text("Index.Progress.Scanning")
            : $"{Progress:P0}";

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
        DiagnosticDetail = null;
        _cancellation = new CancellationTokenSource();
        Guid activity = _activities.Start(
            Text("Index.Activity.Title"),
            Text("Index.Activity.Preparing"),
            ShellDestination.Library,
            () => _cancellation?.Cancel());
        var progress = new Progress<IndexProgress>(item =>
        {
            _statusResourceKey = null;
            _statusArguments = [];
            StatusText = Describe(item);
            IsProgressIndeterminate = item.Phase is IndexPhase.Preparing or IndexPhase.Enumeration;
            Progress = Estimate(item);
            _activities.Report(activity, StatusText, Progress);
        });
        try
        {
            var result = await _library.IndexAsync(progress, _cancellation.Token);
            SetStatus(
                "Index.Status.CompleteFormat",
                result.Added,
                result.Modified,
                result.Removed,
                result.Unchanged);
            IsProgressIndeterminate = false;
            Progress = 1;
            _activities.Finish(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Index.Status.Cancelled");
            _activities.Finish(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            SetStatus("Index.Status.Failed");
            DiagnosticDetail = error.Message;
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

    private string Describe(IndexProgress item)
    {
        string phase = item.Phase switch
        {
            IndexPhase.Preparing =>
                Text("Index.Phase.Preparing"),
            IndexPhase.Enumeration =>
                Text("Index.Phase.Enumeration"),
            IndexPhase.Metadata =>
                Text("Index.Phase.Metadata"),
            IndexPhase.Database =>
                Text("Index.Phase.Database"),
            IndexPhase.Artwork =>
                Text("Index.Phase.Artwork"),
            IndexPhase.Finalizing =>
                Text("Index.Phase.Finalizing"),
            IndexPhase.Completed =>
                Text("Index.Phase.Completed"),
            _ => Text("Index.Phase.Default"),
        };
        string count = item.Phase switch
        {
            IndexPhase.Enumeration =>
                Format(
                    "Index.Progress.FoundFormat",
                    item.Enumerated),
            IndexPhase.Metadata =>
                Format(
                    "Index.Progress.ReadFormat",
                    item.Scanned),
            IndexPhase.Database =>
                Format(
                    "Index.Progress.SavedFormat",
                    item.DatabaseProcessed),
            _ => "",
        };
        return string.Join(" · ", new[] { phase, count, item.Detail }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusResourceKey = key;
        _statusArguments = arguments;
        StatusText = Format(key, arguments);
    }

    private void OnCultureChanged(
        object? sender,
        EventArgs e)
    {
        OnPropertyChanged(nameof(ProgressText));
        if (_statusResourceKey is { } key)
            StatusText = Format(
                key,
                _statusArguments);
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
