using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataCaching;
using MusicLibrary.Core.Services;

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
        // A newly-loaded config flips IsReady; re-evaluate the Index button's CanExecute.
        _settings.ConfigurationChanged += (_, _) => IndexCommand.NotifyCanExecuteChanged();
    }

    private bool CanIndex() => _library.IsReady && !IsIndexing;

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private async Task IndexAsync()
    {
        IsIndexing = true;
        IndexCommand.NotifyCanExecuteChanged();
        _indexCts = new CancellationTokenSource();
        var progress = new Progress<IndexProgress>(p =>
            StatusText = $"Indexing… {p.Scanned:N0} scanned (+{p.Added} ~{p.Modified})");

        try
        {
            var (added, modified, removed, unchanged) = await _library.IndexAsync(progress, _indexCts.Token);
            StatusText = $"Indexed: +{added} added, ~{modified} modified, -{removed} removed, {unchanged} unchanged";
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
}
