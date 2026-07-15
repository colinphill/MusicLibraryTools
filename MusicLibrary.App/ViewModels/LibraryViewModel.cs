using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataCaching;
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
    private readonly ILibraryService _library;
    private readonly IAppSettings _settings;
    private CancellationTokenSource? _indexCts;

    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private string? _statusText;

    /// <summary>Raised after a successful (or cancelled) index so views can refresh from the cache.</summary>
    public event Action? IndexCompleted;

    public LibraryViewModel(ILibraryService library, IAppSettings settings)
    {
        _library = library;
        _settings = settings;
        // A newly-loaded config flips IsReady; re-evaluate the Index button's CanExecute and kick off
        // an index automatically so the cache reflects the just-loaded library without a manual click.
        _settings.ConfigurationChanged += (_, _) =>
        {
            IndexCommand.NotifyCanExecuteChanged();
            if (IndexCommand.CanExecute(null))
                IndexCommand.Execute(null);
        };
    }

    private bool CanIndex() => _library.IsReady && !IsIndexing;

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private async Task IndexAsync()
    {
        IsIndexing = true;
        IndexCommand.NotifyCanExecuteChanged();
        _indexCts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var progress = new Progress<IndexProgress>(p => StatusText = DescribeProgress(p));

        try
        {
            var (added, modified, removed, unchanged) = await _library.IndexAsync(progress, _indexCts.Token);
            StatusText = $"Indexed in {FormatDuration(clock.Elapsed)}: +{added} added, " +
                $"~{modified} modified, -{removed} removed, {unchanged} unchanged. Artwork remains lazy.";
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
            IsIndexing = false;
            _indexCts?.Dispose();
            _indexCts = null;
            IndexCommand.NotifyCanExecuteChanged();
            IndexCompleted?.Invoke();
        }
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

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
        string timing = $"stage {FormatDuration(progress.PhaseElapsed)}, total {FormatDuration(progress.Elapsed)}";
        return string.Join(" • ", new[]
        {
            $"{phase}: {progress.Detail}", count, rate, timing,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
        : $"{elapsed.TotalSeconds:0.0}s";
}
