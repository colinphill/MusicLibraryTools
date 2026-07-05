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
