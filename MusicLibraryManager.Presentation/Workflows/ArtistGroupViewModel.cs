using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>One cluster of similar artist spellings, with an editable canonical name and a Merge action.</summary>
public partial class ArtistGroupViewModel : ViewModelBase
{
    private readonly IArtistReconciler _reconciler;
    private readonly SimilarArtistGroup _group;
    private readonly IDialogCoordinator? _dialogs;
    private readonly IActivityService? _activities;

    public IReadOnlyList<ArtistVariant> Variants => _group.Variants;
    public int TrackCount => _group.AllPaths.Count;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private string _canonicalName;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isMerged;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfo))]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageTone _statusTone = MessageTone.Info;

    public ArtistGroupViewModel(
        IArtistReconciler reconciler,
        SimilarArtistGroup group,
        IDialogCoordinator? dialogs = null,
        IActivityService? activities = null)
    {
        _reconciler = reconciler;
        _group = group;
        _dialogs = dialogs;
        _activities = activities;
        _canonicalName = group.Suggested;
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool IsStatusInfo => StatusTone == MessageTone.Info;
    public bool IsStatusSuccess => StatusTone == MessageTone.Success;
    public bool IsStatusWarning => StatusTone == MessageTone.Warning;
    public bool IsStatusError => StatusTone == MessageTone.Error;
    public string StatusIcon => StatusTone switch
    {
        MessageTone.Success => "✓",
        MessageTone.Warning => "⚠",
        MessageTone.Error => "!",
        _ => "i",
    };

    private bool CanMerge() => !IsBusy && !IsMerged && !string.IsNullOrWhiteSpace(CanonicalName);

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        string canonical = CanonicalName.Trim();
        ArtistVariant[] variants = _group.Variants
            .Where(variant => !string.Equals(variant.Name, canonical, StringComparison.Ordinal))
            .ToArray();
        string[] affectedPaths = variants.SelectMany(variant => variant.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (affectedPaths.Length == 0)
        {
            StatusTone = MessageTone.Info;
            Status = "Nothing to change.";
            return;
        }
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                "Merge similar artist names?",
                $"Update artist metadata on {affectedPaths.Length:N0} track(s) across " +
                $"{variants.Length:N0} spelling variant(s) to ‘{canonical}’? " +
                "This writes the files directly; no recovery journal is created.",
                "Merge artists"))
        {
            StatusTone = MessageTone.Info;
            Status = "Merge cancelled; no files were changed.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        IsBusy = true;
        MergeCommand.NotifyCanExecuteChanged();
        StatusTone = MessageTone.Info;
        Status = $"Updating {affectedPaths.Length:N0} track(s)…";
        Guid? activity = _activities?.Start(
            "Merge similar artists",
            $"Updating {affectedPaths.Length:N0} track(s) to ‘{canonical}’",
            ShellDestination.Health,
            cancellation.Cancel);
        try
        {
            int changed = 0;
            int processed = 0;
            foreach (ArtistVariant variant in variants)
            {
                changed += await _reconciler.RenameArtistAsync(
                    variant.Paths, variant.Name, canonical, ct: cancellation.Token);
                processed += variant.Paths.Count;
                if (activity.HasValue)
                    _activities!.Report(activity.Value,
                        $"Reviewed {Math.Min(processed, affectedPaths.Length):N0} of {affectedPaths.Length:N0} track(s)",
                        Math.Min(1, (double)processed / affectedPaths.Length));
            }
            IsMerged = true;
            StatusTone = MessageTone.Success;
            Status = changed == 0
                ? "Nothing to change."
                : $"Renamed {changed:N0} file(s) to ‘{canonical}’. Re-index to refresh.";
            if (activity.HasValue)
                _activities!.Finish(activity.Value, Status, AppActivityState.Completed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusTone = MessageTone.Warning;
            Status = "Artist merge cancelled. Review the affected files before retrying.";
            if (activity.HasValue)
                _activities!.Finish(activity.Value, Status, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusTone = MessageTone.Error;
            Status = $"Failed: {ex.Message}";
            if (activity.HasValue)
                _activities!.Finish(activity.Value, Status, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            MergeCommand.NotifyCanExecuteChanged();
        }
    }
}
