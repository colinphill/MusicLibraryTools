using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>One cluster of similar artist spellings, with an editable canonical name and a Merge action.</summary>
public partial class ArtistGroupViewModel : ViewModelBase
{
    private readonly IArtistReconciler _reconciler;
    private readonly SimilarArtistGroup _group;

    public IReadOnlyList<ArtistVariant> Variants => _group.Variants;
    public int TrackCount => _group.AllPaths.Count;

    [ObservableProperty] private string _canonicalName;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isMerged;
    [ObservableProperty] private string? _status;

    public ArtistGroupViewModel(IArtistReconciler reconciler, SimilarArtistGroup group)
    {
        _reconciler = reconciler;
        _group = group;
        _canonicalName = group.Suggested;
    }

    private bool CanMerge() => !IsBusy && !IsMerged && !string.IsNullOrWhiteSpace(CanonicalName);

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        IsBusy = true;
        MergeCommand.NotifyCanExecuteChanged();
        Status = "Merging…";
        try
        {
            int changed = 0;
            foreach (var variant in _group.Variants)
            {
                if (string.Equals(variant.Name, CanonicalName, StringComparison.Ordinal))
                    continue;   // already the canonical spelling
                changed += await _reconciler.RenameArtistAsync(variant.Paths, variant.Name, CanonicalName);
            }
            IsMerged = true;
            Status = changed == 0
                ? "Nothing to change."
                : $"Renamed {changed} file(s) to “{CanonicalName}”. Re-index to refresh.";
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            MergeCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>
/// "Check artists": finds clusters of similar artist-name spellings and lets you merge each cluster
/// onto one canonical spelling (rewriting the tags of the affected files).
/// </summary>
public partial class ArtistsViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusText = "Scan the library for near-duplicate artist names.";

    public ObservableCollection<ArtistGroupViewModel> Groups { get; } = [];

    public ArtistsViewModel(ILibraryService library, IArtistReconciler reconciler, IAppSettings settings)
    {
        _library = library;
        _reconciler = reconciler;
        settings.ConfigurationChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
    }

    private bool CanScan() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        ScanCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StatusText = "Scanning…";
        try
        {
            var records = await _library.GetAllRecordsAsync(ct);
            var groups = await Task.Run(() => _reconciler.FindSimilarArtists(records, ct: ct), ct);

            Groups.Clear();
            foreach (var g in groups)
                Groups.Add(new ArtistGroupViewModel(_reconciler, g));

            StatusText = groups.Count == 0
                ? "No similar artist names found."
                : $"{groups.Count:N0} cluster(s) of similar artist names.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
