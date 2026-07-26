using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Shared Workbench/Library controller for explicitly scoped file mutations.
/// Preview captures immutable source/destination snapshots and submits the
/// reviewed plan to the mandatory pending-change coordinator. This editor
/// never executes a mutation plan directly.
/// </summary>
public partial class ReviewedFileOperationEditorViewModel :
    ObservableObject
{
    private readonly IReviewedFileOperationService _operations;
    private readonly IFilePickerService _files;
    private readonly Func<IReadOnlyList<string>> _targetProvider;
    private readonly Func<string?>? _preflightMessage;
    private readonly Func<ReviewedFileOperationPlan, Task<bool>>
        _reviewPending;
    private readonly ILocalizationService? _localization;
    private ReviewedFileOperationPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private string? _statusKey;
    private object?[] _statusArguments = [];
    private long? _statusCount;

    public ReviewedFileOperationEditorViewModel(
        IReviewedFileOperationService operations,
        IFilePickerService files,
        Func<IReadOnlyList<string>> targetProvider,
        Func<ReviewedFileOperationPlan, Task<bool>>
            reviewPending,
        Func<string?>? preflightMessage = null,
        ILocalizationService? localization = null)
    {
        _operations = operations;
        _files = files;
        _targetProvider = targetProvider;
        _reviewPending = reviewPending ??
            throw new ArgumentNullException(
                nameof(reviewPending));
        _preflightMessage = preflightMessage;
        _localization = localization;
        RefreshLocalizedChoices();
        SetStatus(
            "ReviewedFileOperation.Status.Ready");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public ObservableCollection<ReviewedFileOperationItem>
        PreviewItems { get; } = [];

    public event EventHandler? PreviewAddedToReview;

    public IReadOnlyList<ReviewedFileOperationKind>
        OperationKinds { get; } =
            Enum.GetValues<ReviewedFileOperationKind>();

    public IReadOnlyList<ReviewedFileCollisionPolicy>
        CollisionPolicies { get; } =
            Enum.GetValues<ReviewedFileCollisionPolicy>();
    public ObservableCollection<
        LocalizedChoice<ReviewedFileOperationKind>>
        OperationKindChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<ReviewedFileCollisionPolicy>>
        CollisionPolicyChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDestination))]
    [NotifyPropertyChangedFor(nameof(DestinationPlaceholder))]
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
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _hasApplicablePreview;

    public bool NeedsDestination =>
        SelectedKind !=
        ReviewedFileOperationKind.Rename;

    public string DestinationPlaceholder =>
        L(
            SelectedKind ==
            ReviewedFileOperationKind.Quarantine
                ? "ReviewedFileOperation.QuarantineFolderPlaceholder"
                : "ReviewedFileOperation.DestinationFolderPlaceholder");

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
                ? L(
                    "ReviewedFileOperation.Target.None")
                : LC(
                    "ReviewedFileOperation.Target.Files",
                    count);
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
                    ? L(
                        "ReviewedFileOperation.Picker.Quarantine")
                    : L(
                        "ReviewedFileOperation.Picker.Destination"));
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
            _statusKey = null;
            StatusDiagnosticDetail = null;
            return;
        }

        string[] targets = _targetProvider()
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
        BeginOperation(
            LF(
                "ReviewedFileOperation.Activity.Planning",
                KindLabel(SelectedKind)));
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
            if (blockers > 0)
                SetCountStatus(
                    "ReviewedFileOperation.Status.PreviewBlocked",
                    blockers);
            else if (HasApplicablePreview)
                SetCountStatus(
                    "ReviewedFileOperation.Status.PreviewReady",
                    plan.MutationPlan.Actions.Count);
            else
                SetStatus(
                    "ReviewedFileOperation.Status.NoChanges");
            OnPropertyChanged(nameof(HasUnsavedChanges));
            if (HasApplicablePreview &&
                await _reviewPending(plan))
            {
                _plan = null;
                HasApplicablePreview = false;
                OnPropertyChanged(
                    nameof(HasUnsavedChanges));
                SetCountStatus(
                    "ReviewedFileOperation.Status.AddedToReview",
                    plan.MutationPlan.Actions.Count);
                PreviewAddedToReview?.Invoke(
                    this,
                    EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "ReviewedFileOperation.Status.PreviewCancelled");
        }
        catch (Exception error)
        {
            _plan = null;
            HasApplicablePreview = false;
            SetFailure(
                "ReviewedFileOperation.Status.PreviewFailed",
                error.Message);
            OnPropertyChanged(nameof(HasUnsavedChanges));
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
        SetStatus(
            "ReviewedFileOperation.Status.Invalidated");
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
        _statusKey = null;
        StatusDiagnosticDetail = null;
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void EndOperation()
    {
        IsBusy = false;
        _cancellation?.Dispose();
        _cancellation = null;
        CancelCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
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
                {
                    Status = progress.Message;
                    _statusKey = null;
                    StatusDiagnosticDetail = null;
                }
            });

    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(StatusDiagnosticDetail);

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private string LC(
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

    private string KindLabel(
        ReviewedFileOperationKind kind) =>
        L(
            $"ReviewedFileOperation.Choice.Kind.{kind}");

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = null;
        Status = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatus(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = count;
        Status = LC(key, count, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetFailure(
        string key,
        string? diagnosticDetail)
    {
        SetStatus(key);
        StatusDiagnosticDetail =
            diagnosticDetail;
    }

    private void RefreshLocalizedChoices()
    {
        RefreshChoices(
            OperationKindChoices,
            OperationKinds,
            "ReviewedFileOperation.Choice.Kind");
        RefreshChoices(
            CollisionPolicyChoices,
            CollisionPolicies,
            "ReviewedFileOperation.Choice.CollisionPolicy");
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        string keyPrefix)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = L($"{keyPrefix}.{value}");
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(
            nameof(DestinationPlaceholder));
        if (_statusKey is null)
            return;
        Status = _statusCount is { } count
            ? LC(
                _statusKey,
                count,
                _statusArguments)
            : LF(
                _statusKey,
                _statusArguments);
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
