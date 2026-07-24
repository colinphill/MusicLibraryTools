using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Shared Workbench/Library controller for explicitly scoped file mutations.
/// Preview captures immutable source/destination snapshots; apply accepts only
/// that reviewed plan.
/// </summary>
public partial class ReviewedFileOperationEditorViewModel :
    ObservableObject
{
    private readonly IReviewedFileOperationService _operations;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly Func<IReadOnlyList<string>> _targetProvider;
    private readonly Func<string?>? _preflightMessage;
    private readonly Func<ReviewedFileOperationPlan, Task>?
        _applied;
    private ReviewedFileOperationPlan? _plan;
    private CancellationTokenSource? _cancellation;

    public ReviewedFileOperationEditorViewModel(
        IReviewedFileOperationService operations,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        Func<IReadOnlyList<string>> targetProvider,
        Func<string?>? preflightMessage = null,
        Func<ReviewedFileOperationPlan, Task>? applied = null)
    {
        _operations = operations;
        _files = files;
        _dialogs = dialogs;
        _targetProvider = targetProvider;
        _preflightMessage = preflightMessage;
        _applied = applied;
    }

    public ObservableCollection<ReviewedFileOperationItem>
        PreviewItems { get; } = [];

    public IReadOnlyList<ReviewedFileOperationKind>
        OperationKinds { get; } =
            Enum.GetValues<ReviewedFileOperationKind>();

    public IReadOnlyList<ReviewedFileCollisionPolicy>
        CollisionPolicies { get; } =
            Enum.GetValues<ReviewedFileCollisionPolicy>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDestination))]
    private ReviewedFileOperationKind _selectedKind =
        ReviewedFileOperationKind.Copy;

    [ObservableProperty]
    private string? _destinationDirectory;

    [ObservableProperty]
    private string _fileNameTemplate =
        "{Name}{Extension}";

    [ObservableProperty]
    private bool _preserveRelativeLayout;

    [ObservableProperty]
    private ReviewedFileCollisionPolicy _selectedCollisionPolicy =
        ReviewedFileCollisionPolicy.Stop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _status =
        "Choose an operation and preview the current scope.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _hasApplicablePreview;

    public bool NeedsDestination =>
        SelectedKind !=
        ReviewedFileOperationKind.Rename;

    public bool HasPreview =>
        PreviewItems.Count > 0;

    public bool HasUnsavedChanges =>
        _plan is not null;

    public string TargetSummary
    {
        get
        {
            int count =
                _targetProvider().Count;
            return count == 0
                ? "No files in the current scope."
                : $"{count:N0} " +
                  (count == 1 ? "file" : "files") +
                  " in the current scope.";
        }
    }

    partial void OnSelectedKindChanged(
        ReviewedFileOperationKind value) =>
        InvalidatePreview();

    partial void OnDestinationDirectoryChanged(
        string? value) =>
        InvalidatePreview();

    partial void OnFileNameTemplateChanged(
        string value) =>
        InvalidatePreview();

    partial void OnPreserveRelativeLayoutChanged(
        bool value) =>
        InvalidatePreview();

    partial void OnSelectedCollisionPolicyChanged(
        ReviewedFileCollisionPolicy value) =>
        InvalidatePreview();

    public void InvalidateTargets()
    {
        InvalidatePreview();
        OnPropertyChanged(nameof(TargetSummary));
        PreviewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBrowseDestination))]
    private async Task BrowseDestinationAsync()
    {
        string? path =
            await _files.PickFolderAsync(
                SelectedKind ==
                ReviewedFileOperationKind.Quarantine
                    ? "Choose reviewed quarantine folder"
                    : "Choose file-operation destination");
        if (path is not null)
            DestinationDirectory = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        string? preflight =
            _preflightMessage?.Invoke();
        if (!string.IsNullOrWhiteSpace(preflight))
        {
            Status = preflight;
            return;
        }

        string[] targets = _targetProvider()
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
        BeginOperation(
            $"Planning {SelectedKind.ToString().ToLowerInvariant()}");
        try
        {
            ReviewedFileOperationPlan plan =
                await _operations.PreviewAsync(
                    new(
                        targets,
                        SelectedKind,
                        DestinationDirectory,
                        FileNameTemplate,
                        PreserveRelativeLayout,
                        SelectedCollisionPolicy),
                    CreateProgress(),
                    _cancellation!.Token);
            _plan = plan;
            PreviewItems.Clear();
            foreach (ReviewedFileOperationItem item in
                     plan.Items)
                PreviewItems.Add(item);
            OnPropertyChanged(nameof(HasPreview));
            HasApplicablePreview =
                plan.CanApply &&
                plan.MutationPlan.Actions.Count > 0;
            int blockers = plan.MutationPlan.Issues.Count(
                issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
            Status = blockers > 0
                ? $"Preview blocked by {blockers:N0} " +
                  (blockers == 1 ? "issue." : "issues.")
                : HasApplicablePreview
                    ? $"{plan.MutationPlan.Actions.Count:N0} " +
                      "reviewed file mutation(s) ready to apply."
                    : "The reviewed operation has no filesystem changes.";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            Status = "File-operation preview cancelled.";
        }
        catch (Exception error)
        {
            _plan = null;
            HasApplicablePreview = false;
            Status =
                $"Could not preview file operations: {error.Message}";
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ReviewedFileOperationPlan plan =
            _plan ??
            throw new InvalidOperationException(
                "Preview the file operation first.");
        int count =
            plan.MutationPlan.Actions.Count;
        bool confirmed =
            await _dialogs.ConfirmAsync(
                $"Apply {plan.Request.Kind.ToString().ToLowerInvariant()}?",
                $"Apply the {count:N0} reviewed file " +
                (count == 1 ? "mutation" : "mutations") +
                "? A durable journal will be retained for history and eligible restores.",
                "Apply");
        if (!confirmed)
            return;

        BeginOperation("Applying reviewed file operation");
        try
        {
            FileMutationSummary result =
                await _operations.ApplyAsync(
                    plan,
                    CreateProgress(),
                    _cancellation!.Token);
            _plan = null;
            HasApplicablePreview = false;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            if (_applied is not null)
                await _applied(plan);
            Status =
                $"Completed: {result.Copied:N0} copied, " +
                $"{result.Moved:N0} moved, " +
                $"{result.Quarantined:N0} quarantined.";
        }
        catch (OperationCanceledException)
        {
            Status = "File operation cancelled; completed actions were rolled back.";
        }
        catch (Exception error)
        {
            Status =
                $"Could not apply file operation: {error.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private void Cancel() =>
        _cancellation?.Cancel();

    private bool CanBrowseDestination() =>
        !IsBusy && NeedsDestination;

    private bool CanPreview() =>
        !IsBusy &&
        _targetProvider().Count > 0 &&
        (!NeedsDestination ||
         !string.IsNullOrWhiteSpace(
             DestinationDirectory)) &&
        !string.IsNullOrWhiteSpace(
            FileNameTemplate);

    private bool CanApply() =>
        !IsBusy &&
        HasApplicablePreview &&
        _plan is not null;

    private void InvalidatePreview()
    {
        if (_plan is null &&
            PreviewItems.Count == 0)
        {
            PreviewCommand.NotifyCanExecuteChanged();
            BrowseDestinationCommand
                .NotifyCanExecuteChanged();
            return;
        }
        _plan = null;
        HasApplicablePreview = false;
        PreviewItems.Clear();
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        Status =
            "Operation settings or scope changed. Preview again.";
        PreviewCommand.NotifyCanExecuteChanged();
        BrowseDestinationCommand
            .NotifyCanExecuteChanged();
    }

    private void BeginOperation(
        string status)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation =
            new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        Status = status;
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void EndOperation()
    {
        IsBusy = false;
        _cancellation?.Dispose();
        _cancellation = null;
        CancelCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private IProgress<OperationProgress>
        CreateProgress() =>
        new Progress<OperationProgress>(
            progress =>
            {
                if (progress.Total is > 0)
                {
                    IsProgressIndeterminate =
                        false;
                    ProgressMaximum =
                        progress.Total.Value;
                    ProgressValue = Math.Clamp(
                        progress.Completed,
                        0,
                        progress.Total.Value);
                }
                else
                {
                    IsProgressIndeterminate =
                        true;
                }
                if (!string.IsNullOrWhiteSpace(
                        progress.Message))
                    Status = progress.Message;
            });

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
