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
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cts;
    private string? _statusKey = "Organize.Status.Ready";
    private object?[] _statusArguments = [];
    private long? _statusCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText =
        LocalizedText.Get("Organize.Status.Ready");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticDetail))]
    private string? _diagnosticDetail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasPreview;

    public ObservableCollection<PlannedMove> Moves { get; } = [];
    public bool IsMoveListEmpty => Moves.Count == 0;
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(DiagnosticDetail);

    /// <summary>Raised after moves are applied (the cache is already synced) so the grid can refresh.</summary>
    public event Action? MovesApplied;

    public OrganizeViewModel(
        ILibraryOrganizer organizer,
        IAppSettings settings,
        IDialogService dialogs,
        IActivityService? activities = null,
        ILocalizationService? localization = null)
    {
        _organizer = organizer;
        _settings = settings;
        _dialogs = dialogs;
        _activities = activities;
        _localization = localization;
        SetStatus("Organize.Status.Ready");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
        _settings.ConfigurationChanged += (_, _) =>
            PreviewCommand.NotifyCanExecuteChanged();
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
        DiagnosticDetail = null;
        Guid? activity = _activities?.Start(
            L("Organize.Activity.Preview.Title"),
            L("Organize.Activity.Preview.Starting"),
            ShellDestination.Organize,
            Cancel);
        SetStatus("Organize.Status.Computing");
        try
        {
            IReadOnlyList<PlannedMove> moves =
                await _organizer.PreviewMovesAsync(_cts.Token);
            Moves.Clear();
            foreach (PlannedMove move in moves)
                Moves.Add(move);
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = moves.Count > 0;
            if (moves.Count > 0)
                SetCountStatus(
                    "Organize.Status.PreviewMoves",
                    moves.Count);
            else if (LibraryOrganizationPolicy.EligibleRoots(
                         _settings.Configuration!.IndexLocations).Count == 0)
                SetStatus("Organize.Status.NoEligibleRoots");
            else
                SetStatus("Organize.Status.AlreadyCanonical");
            FinishActivity(activity, StatusText!);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Organize.Status.PreviewCancelled");
            FinishActivity(
                activity,
                StatusText!,
                AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            SetFailure(
                "Organize.Status.PreviewFailed",
                error);
            FinishActivity(
                activity,
                StatusText!,
                AppActivityState.Failed);
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
                L("Organize.Dialog.Apply.Title"),
                LFC(
                    "Organize.Dialog.Apply.Message",
                    moves.Length),
                L("Organize.Dialog.Apply.Primary")))
            return;

        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        DiagnosticDetail = null;
        int total = moves.Length;
        Guid? activity = _activities?.Start(
            L("Organize.Activity.Apply.Title"),
            LF(
                "Organize.Status.Moving",
                0,
                total),
            ShellDestination.Organize,
            Cancel);
        var progress = new Progress<int>(completed =>
            SetStatus(
                "Organize.Status.Moving",
                completed,
                total));
        try
        {
            OrganizeResult result = await _organizer.ApplyMovesAsync(
                moves,
                progress,
                _cts.Token);
            SetStatus(
                result.FailedCount == 0
                    ? "Organize.Status.ApplyComplete"
                    : "Organize.Status.ApplyPartial",
                result.Moved,
                result.FailedCount);
            Moves.Clear();
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = false;
            MovesApplied?.Invoke();
            FinishActivity(
                activity,
                StatusText!,
                result.FailedCount == 0
                    ? AppActivityState.Completed
                    : AppActivityState.Failed);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Organize.Status.ApplyCancelled");
            Moves.Clear();
            OnPropertyChanged(nameof(IsMoveListEmpty));
            HasPreview = false;
            MovesApplied?.Invoke();
            FinishActivity(
                activity,
                StatusText!,
                AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            SetFailure(
                "Organize.Status.ApplyFailed",
                error);
            FinishActivity(
                activity,
                StatusText!,
                AppActivityState.Failed);
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

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private string LFC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = null;
        StatusText = LF(
            key,
            arguments);
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = count;
        StatusText = LFC(
            key,
            count,
            arguments);
    }

    private void SetFailure(
        string key,
        Exception error)
    {
        SetStatus(key);
        DiagnosticDetail = error.Message;
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_statusKey is not { } key)
            return;
        StatusText = _statusCount is { } count
            ? LFC(
                key,
                count,
                _statusArguments)
            : LF(
                key,
                _statusArguments);
    }

    private void FinishActivity(
        Guid? activity,
        string message,
        AppActivityState state = AppActivityState.Completed)
    {
        if (activity is { } id)
            _activities?.Finish(
                id,
                message,
                state);
    }
}
