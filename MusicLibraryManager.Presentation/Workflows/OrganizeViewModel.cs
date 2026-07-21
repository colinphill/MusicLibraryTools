using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Organize/Rename tool: previews the moves needed to canonicalize the library layout, then applies
/// them only after the user confirms. Nothing on disk changes until Apply.
/// </summary>
public partial class OrganizeViewModel : ViewModelBase
{
    private readonly ILibraryOrganizer _organizer;
    private readonly IAppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly IActivityService? _activities;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Preview to see what would be renamed. No files move until you Apply.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasPreview;

    public ObservableCollection<PlannedMove> Moves { get; } = [];
    public bool IsMoveListEmpty => Moves.Count == 0;

    /// <summary>Raised after moves are applied (the cache is already synced) so the grid can refresh.</summary>
    public event Action? MovesApplied;

    public OrganizeViewModel(
        ILibraryOrganizer organizer,
        IAppSettings settings,
        IDialogService dialogs,
        IActivityService? activities = null)
    {
        _organizer = organizer;
        _settings = settings;
        _dialogs = dialogs;
        _activities = activities;
        _settings.ConfigurationChanged += (_, _) => PreviewCommand.NotifyCanExecuteChanged();
    }

    private bool IsReady => _settings.Configuration is not null;
    private bool CanPreview() => IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        HasPreview = false;
        PreviewCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        Guid? activity = _activities?.Start(
            "Preview file organization", "Computing moves", ShellDestination.Organize, Cancel);
        StatusText = "Computing moves…";
        try
        {
            var moves = await _organizer.PreviewMovesAsync(_cts.Token);
            Moves.Clear();
            foreach (var m in moves)
                Moves.Add(m);
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = moves.Count > 0;
            StatusText = moves.Count == 0
                ? LibraryOrganizationPolicy.EligibleRoots(
                    _settings.Configuration!.IndexLocations).Count == 0
                    ? "No organization-eligible IndexTargets are configured."
                    : "Everything eligible is already in its canonical location."
                : $"{moves.Count:N0} files would be moved. Review below, then Apply.";
            FinishActivity(activity, StatusText ?? "Preview completed.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Preview cancelled.";
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = $"Preview failed: {ex.Message}";
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            PreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApply() => HasPreview && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        PlannedMove[] moves = [.. Moves];
        if (!await _dialogs.ConfirmApplyAsync(
                "Apply file moves",
                $"Move {moves.Length:N0} file(s) into their canonical library locations?\n\n" +
                "Recovery is available: a journal will be written, and completed moves are rolled back if the operation does not finish.",
                "Move files"))
            return;

        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        var total = moves.Length;
        Guid? activity = _activities?.Start(
            "Organize library files", $"Moving 0/{total:N0}", ShellDestination.Organize, Cancel);
        var progress = new Progress<int>(n => StatusText = $"Moving… {n:N0}/{total:N0}");
        try
        {
            var result = await _organizer.ApplyMovesAsync(moves, progress, _cts.Token);
            StatusText = result.FailedCount == 0
                ? $"Moved {result.Moved:N0} files. Cache updated."
                : $"Moved {result.Moved:N0}, {result.FailedCount:N0} failed. Cache updated.";
            Moves.Clear();
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = false;
            MovesApplied?.Invoke();
            FinishActivity(activity, StatusText,
                result.FailedCount == 0 ? AppActivityState.Completed : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled — completed moves were rolled back.";
            Moves.Clear();
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = false;
            MovesApplied?.Invoke();
            FinishActivity(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText = $"Apply failed: {ex.Message}";
            FinishActivity(activity, StatusText, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCancel() => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void FinishActivity(
        Guid? activity,
        string message,
        AppActivityState state = AppActivityState.Completed)
    {
        if (activity is { } id)
            _activities?.Finish(id, message, state);
    }
}
