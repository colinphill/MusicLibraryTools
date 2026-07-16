using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataCaching;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;
using System.Diagnostics;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Library indexing: scans the configured locations into the SQLite cache with progress/cancel.
/// Browsing and selection now happen entirely in the details grid (<see cref="DetailsGridViewModel"/>);
/// this VM just owns the Index command and raises <see cref="IndexCompleted"/> so the grid can reload.
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private const string ScheduledScanMinutesPreference = "Index.ScheduledScanMinutes";
    private readonly ILibraryService _library;
    private readonly IIndexBenchmarkService _benchmarks;
    private readonly IAppSettings _settings;
    private readonly IScheduledScanService? _scheduledScans;
    private CancellationTokenSource? _indexCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(IndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(BenchmarkReadersCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseBenchmarkRecommendationCommand))]
    private bool _isIndexing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(IndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(BenchmarkReadersCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseBenchmarkRecommendationCommand))]
    private bool _isBenchmarking;

    [ObservableProperty]
    private int _readerParallelism = 16;

    [ObservableProperty]
    private int _scheduledScanMinutes;

    [ObservableProperty]
    private string _scheduledScanText = "Automatic delta scans are off.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseBenchmarkRecommendationCommand))]
    [NotifyPropertyChangedFor(nameof(HasBenchmarkRecommendation))]
    private int? _benchmarkRecommendation;

    [ObservableProperty] private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBenchmarkDetails))]
    private string? _benchmarkDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRootHealthDetails))]
    private string? _rootHealthDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItunesMediaDetails))]
    private string? _itunesMediaDetails;

    public bool IsBusy => IsIndexing || IsBenchmarking;
    public bool HasBenchmarkRecommendation => BenchmarkRecommendation is not null;
    public bool HasBenchmarkDetails => !string.IsNullOrWhiteSpace(BenchmarkDetails);
    public bool HasRootHealthDetails => !string.IsNullOrWhiteSpace(RootHealthDetails);
    public bool HasItunesMediaDetails => !string.IsNullOrWhiteSpace(ItunesMediaDetails);

    /// <summary>Raised after a successful (or cancelled) index so views can refresh from the cache.</summary>
    public event Action? IndexCompleted;

    public LibraryViewModel(
        ILibraryService library,
        IIndexBenchmarkService benchmarks,
        IAppSettings settings,
        IScheduledScanService? scheduledScans = null)
    {
        _library = library;
        _benchmarks = benchmarks;
        _settings = settings;
        _scheduledScans = scheduledScans;
        if (int.TryParse(settings.GetPreference(IndexBenchmarkService.ReaderParallelismPreference),
                out int parallelism))
            ReaderParallelism = Math.Clamp(parallelism, 1, 64);
        if (int.TryParse(settings.GetPreference(ScheduledScanMinutesPreference), out int minutes))
            ScheduledScanMinutes = Math.Clamp(minutes, 0, 1440);
        // A newly-loaded config flips IsReady. The main window first restores cached browsing, then
        // calls StartAutomaticIndexAsync after the UI has had an opportunity to render.
        _settings.ConfigurationChanged += (_, _) =>
        {
            IndexCommand.NotifyCanExecuteChanged();
            BenchmarkReadersCommand.NotifyCanExecuteChanged();
            StatusText = "Loading the cached library; automatic indexing will follow.";
        };
    }

    /// <summary>Start the deferred scan once startup has restored the responsive cached view.</summary>
    public Task StartAutomaticIndexAsync() => IndexCommand.CanExecute(null)
        ? IndexCommand.ExecuteAsync(null)
        : Task.CompletedTask;

    partial void OnReaderParallelismChanged(int value)
    {
        if (value is < 1 or > 64)
            return;
        _settings.SetPreference(IndexBenchmarkService.ReaderParallelismPreference,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    partial void OnScheduledScanMinutesChanged(int value)
    {
        if (value is < 0 or > 1440)
            return;
        _settings.SetPreference(ScheduledScanMinutesPreference,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_scheduledScans is null)
            return;
        TimeSpan? interval = value == 0 ? null : TimeSpan.FromMinutes(value);
        _scheduledScans.Configure(interval, RunScheduledScanAsync);
        ScheduledScanText = value == 0
            ? "Automatic delta scans are off."
            : $"Delta scan every {value:N0} minute(s); unchanged files use the cache.";
    }

    private async Task RunScheduledScanAsync()
    {
        if (!IndexCommand.CanExecute(null))
        {
            ScheduledScanText = $"Scheduled delta scan skipped because another operation was active; " +
                $"the next attempt remains every {ScheduledScanMinutes:N0} minute(s).";
            return;
        }
        ScheduledScanText = "Running scheduled delta scan…";
        await IndexCommand.ExecuteAsync(null);
        ScheduledScanText = $"Last scheduled delta scan finished {DateTime.Now:g}; " +
            $"next interval {ScheduledScanMinutes:N0} minute(s).";
    }

    private bool CanIndex() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private async Task IndexAsync()
    {
        IsIndexing = true;
        _indexCts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var progress = new Progress<IndexProgress>(p => StatusText = DescribeProgress(p));

        try
        {
            var (added, modified, removed, unchanged) = await _library.IndexAsync(progress, _indexCts.Token);
            StatusText = $"Indexed in {FormatDuration(clock.Elapsed)}: +{added} added, " +
                $"~{modified} modified, -{removed} removed, {unchanged} unchanged. Artwork remains lazy.";
            ItunesMediaReconciliationResult itunes =
                await _library.GetLastItunesReconciliationAsync(_indexCts.Token);
            ItunesMediaDetails = DescribeItunesMediaReconciliation(itunes);
            if (itunes.Configured)
            {
                if (itunes.Error is not null)
                    StatusText += $" iTunes synchronization pending: {itunes.Error}";
                else
                {
                    if (itunes.ChangedFiles > 0)
                        StatusText += $" Updated {itunes.UpdatedTracks:N0} iTunes track record(s) " +
                                      $"for {itunes.ChangedFiles:N0} changed file(s).";
                    if (itunes.Issues.Count > 0)
                        StatusText += $" {itunes.Issues.Count:N0} iTunes Media item(s) need review.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Indexing cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Indexing failed: {ex.Message}";
        }
        finally
        {
            await RefreshRootHealthAsync();
            IsIndexing = false;
            _indexCts?.Dispose();
            _indexCts = null;
            IndexCompleted?.Invoke();
        }
    }

    private async Task RefreshRootHealthAsync()
    {
        try
        {
            var health = await _library.GetScanRootHealthAsync();
            RootHealthDetails = health.Count == 0 ? null : DescribeRootHealth(health);
            int unavailable = health.Count(root => root.State == ScanRootState.Unavailable);
            int degraded = health.Count(root => root.State == ScanRootState.Degraded);
            if (unavailable > 0)
                StatusText += $" {unavailable:N0} root(s) unavailable; cached entries were retained.";
            if (degraded > 0)
                StatusText += $" {degraded:N0} root(s) completed with warnings; review Root health.";
        }
        catch
        {
            // Index outcome remains primary; health is supplemental and can refresh next scan.
        }
    }

    private bool CanBenchmarkReaders() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBenchmarkReaders))]
    private async Task BenchmarkReadersAsync()
    {
        IsBenchmarking = true;
        BenchmarkRecommendation = null;
        BenchmarkDetails = null;
        _indexCts = new CancellationTokenSource();
        var progress = new Progress<MusicLibrary.Core.Models.IndexBenchmarkProgress>(item =>
        {
            string level = item.Parallelism > 0 ? $" at {item.Parallelism:N0} reader(s)" : "";
            StatusText = $"Benchmarking root {item.RootIndex:N0}/{item.RootCount:N0}{level}: " +
                $"{item.Phase} {item.CompletedReads:N0}/{item.TotalReads:N0} — {item.Root}";
        });
        try
        {
            var result = await _benchmarks.BenchmarkAsync(
                ReaderParallelism, progress, _indexCts.Token);
            var successful = result.Roots.Where(root => root.Succeeded).ToList();
            if (successful.Count > 0)
                BenchmarkRecommendation = successful.Min(root => root.RecommendedParallelism);
            BenchmarkDetails = DescribeBenchmark(result, BenchmarkRecommendation);
            StatusText = BenchmarkRecommendation is int recommendation
                ? $"Reader benchmark complete. Conservative all-root recommendation: {recommendation:N0}. Open results for per-root measurements."
                : "Reader benchmark completed without a usable recommendation. Open results for details.";
        }
        catch (OperationCanceledException) { StatusText = "Reader benchmark cancelled."; }
        catch (Exception ex) { StatusText = $"Reader benchmark failed: {ex.Message}"; }
        finally
        {
            _indexCts?.Dispose();
            _indexCts = null;
            IsBenchmarking = false;
        }
    }

    private bool CanUseBenchmarkRecommendation() => !IsBusy && BenchmarkRecommendation is not null;

    [RelayCommand(CanExecute = nameof(CanUseBenchmarkRecommendation))]
    private void UseBenchmarkRecommendation()
    {
        if (BenchmarkRecommendation is int recommendation)
        {
            ReaderParallelism = recommendation;
            StatusText = $"Index reader parallelism set to {recommendation:N0}.";
        }
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    public static string DescribeBenchmark(
        MusicLibrary.Core.Models.IndexBenchmarkResult result,
        int? globalRecommendation)
    {
        var lines = result.Roots.Select(root =>
        {
            if (!root.Succeeded)
                return $"{root.Root}: unavailable ({root.Error})";
            var baseline = root.Trials.FirstOrDefault(trial => trial.Parallelism == 1)?.FilesPerSecond ?? 0;
            string trials = string.Join(", ", root.Trials.Select(trial =>
                $"{trial.Parallelism}× {trial.FilesPerSecond:N1}/s"));
            double recommendedRate = root.Trials.First(trial =>
                trial.Parallelism == root.RecommendedParallelism).FilesPerSecond;
            string speedup = baseline > 0 ? $", {recommendedRate / baseline:N2}× baseline" : "";
            return $"{root.Root}: {root.SampleCount:N0} sampled in {FormatDuration(root.EnumerationElapsed)}; " +
                $"{trials}; recommend {root.RecommendedParallelism:N0}{speedup}.";
        });
        string heading = globalRecommendation is int recommendation
            ? $"Reader benchmark complete. Conservative all-root recommendation: {recommendation:N0}."
            : "Reader benchmark completed without a usable recommendation.";
        return heading + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    public static string DescribeRootHealth(IReadOnlyList<ScanRootHealth> roots) =>
        string.Join(Environment.NewLine, roots.Select(root =>
        {
            string attempt = root.LastAttemptUtc is DateTime attempted
                ? attempted.ToLocalTime().ToString("g") : "never";
            string success = root.LastSuccessUtc is DateTime succeeded
                ? succeeded.ToLocalTime().ToString("g") : "never";
            string error = string.IsNullOrWhiteSpace(root.Error) ? "" : $" — {root.Error}";
            return $"{root.Root}: {root.State}; last attempt {attempt}; last success {success}; " +
                $"{root.Enumerated:N0} enumerated, {root.MetadataRead:N0} metadata read{error}";
        }));

    public static string? DescribeItunesMediaReconciliation(
        ItunesMediaReconciliationResult result)
    {
        if (!result.Configured)
            return null;
        var lines = new List<string>
        {
            $"Library: {result.LibraryPath ?? "(unavailable)"}",
            $"Media Folder: {result.MediaFolder ?? "(unavailable)"}",
        };
        if (result.Error is not null)
            lines.Add("Synchronization pending: " + result.Error);
        else
        {
            lines.Add($"Last reconciliation: {result.ChangedFiles:N0} changed file(s), " +
                      $"{result.UpdatedTracks:N0} updated track record(s), " +
                      $"{result.Issues.Count:N0} review item(s).");
            lines.AddRange(result.Warnings.Select(warning => "Warning: " + warning));
            lines.AddRange(result.Issues.Select(issue =>
                issue.RelatedPath is null
                    ? $"{issue.Kind}: {issue.Path} — {issue.Message}"
                    : $"{issue.Kind}: {issue.Path} -> {issue.RelatedPath} — {issue.Message}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string DescribeProgress(IndexProgress progress)
    {
        string phase = progress.Phase switch
        {
            IndexPhase.Preparing => "Preparing",
            IndexPhase.Enumeration => "Enumerating",
            IndexPhase.Metadata => "Reading metadata",
            IndexPhase.Database => "Updating database",
            IndexPhase.Artwork => "Artwork",
            IndexPhase.Finalizing => "Finalizing",
            IndexPhase.Completed => "Complete",
            _ => "Indexing",
        };
        string count = progress.Phase switch
        {
            IndexPhase.Enumeration => $"{progress.Enumerated:N0} found",
            IndexPhase.Metadata => $"{progress.Scanned:N0} read",
            IndexPhase.Database => $"{progress.DatabaseProcessed:N0} applied",
            IndexPhase.Completed => $"{progress.Enumerated:N0} found, {progress.Scanned:N0} read",
            _ => "",
        };
        string rate = progress.Phase is IndexPhase.Enumeration or IndexPhase.Metadata or IndexPhase.Database
            ? $"{progress.FilesPerSecond:N1} files/s"
            : "";
        string readers = progress.ReaderParallelism > 0 &&
            progress.Phase is IndexPhase.Enumeration or IndexPhase.Metadata
            ? $"{progress.ReaderParallelism:N0} readers"
            : "";
        string timing = $"stage {FormatDuration(progress.PhaseElapsed)}, total {FormatDuration(progress.Elapsed)}";
        return string.Join(" • ", new[]
        {
            $"{phase}: {progress.Detail}", count, readers, rate, timing,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
        : $"{elapsed.TotalSeconds:0.0}s";
}
