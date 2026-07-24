using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using LegacyDialogs = MusicLibraryManager.Presentation.IDialogService;

namespace MusicLibraryManager.Services;

public enum DialogTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Error,
    Danger,
}

public enum DialogDefaultAction
{
    None,
    Cancel,
    Primary,
}

/// <summary>Controls which implicit gestures may dismiss a dialog.</summary>
public sealed record DialogDismissalPolicy(
    bool AllowEscape,
    bool AllowScrim,
    bool ShowCloseButton,
    Func<bool>? CanDismissImplicitly = null)
{
    public static DialogDismissalPolicy Standard { get; } = new(true, true, true);
    public static DialogDismissalPolicy ExplicitOnly { get; } = new(false, false, false);

    public static DialogDismissalPolicy BlockWhile(Func<bool> shouldBlock) =>
        new(true, true, true, () => !shouldBlock());

    public bool CanEscape => AllowEscape && (CanDismissImplicitly?.Invoke() ?? true);
    public bool CanDismissFromScrim => AllowScrim && (CanDismissImplicitly?.Invoke() ?? true);
    public bool CanDismissFromCloseButton => ShowCloseButton && (CanDismissImplicitly?.Invoke() ?? true);
}

public abstract record DialogRequest(string Title)
{
    public DialogTone Tone { get; init; } = DialogTone.Neutral;
    public DialogDefaultAction DefaultAction { get; init; } = DialogDefaultAction.Cancel;
    public DialogDismissalPolicy DismissalPolicy { get; init; } = DialogDismissalPolicy.Standard;
}

public sealed record MessageRequest(string DialogTitle, string Message)
    : DialogRequest(DialogTitle);
public sealed record ConfirmRequest(string DialogTitle, string Message, string PrimaryText)
    : DialogRequest(DialogTitle);
public sealed record FieldsRequest(FieldsDialogViewModel ViewModel)
    : DialogRequest(ViewModel.Title);

public sealed class DialogService(
    IMetadataDocumentService documents,
    IMetadataOperationService operations,
    IActivityService activities,
    ILocalizationService? localization = null)
    : IDialogCoordinator, IFieldsEditorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _completion;

    public DialogRequest? Current { get; private set; }
    public event Action? Changed;

    /// <summary>
    /// Handles the native close gesture while an overlay is active. A dismissible dialog is
    /// cancelled; a protected dialog remains intact. Either way the owner close is consumed.
    /// </summary>
    public bool HandleOwnerWindowClose()
    {
        DialogRequest? request = Current;
        if (request is null)
            return false;
        if (request.DismissalPolicy.CanDismissFromCloseButton)
            Complete(false);
        return true;
    }

    public Task<bool> ConfirmAsync(string title, string message, string primaryText) =>
        ConfirmAsync(title, message, primaryText, DialogTone.Warning);

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryText,
        DialogTone tone,
        DialogDefaultAction defaultAction = DialogDefaultAction.Cancel,
        DialogDismissalPolicy? dismissalPolicy = null) =>
        ShowAsync(new ConfirmRequest(title, message, primaryText)
        {
            Tone = tone,
            DefaultAction = defaultAction,
            DismissalPolicy = dismissalPolicy ?? DialogDismissalPolicy.Standard,
        });

    public Task ShowMessageAsync(string title, string message) =>
        ShowMessageAsync(title, message, DialogTone.Error);

    public async Task ShowMessageAsync(
        string title,
        string message,
        DialogTone tone,
        DialogDismissalPolicy? dismissalPolicy = null) =>
        _ = await ShowAsync(new MessageRequest(title, message)
        {
            Tone = tone,
            DefaultAction = DialogDefaultAction.Primary,
            DismissalPolicy = dismissalPolicy ?? DialogDismissalPolicy.Standard,
        });

    public async Task<bool> ShowAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;
        var viewModel = new FieldsDialogViewModel(
            documents,
            operations,
            paths,
            activities);
        Guid loadActivity = activities.Start(
            Text("Dialog.Fields.LoadActivity"),
            FormatCount(
                "Dialog.Fields.ReadingSelectedFiles",
                paths.Count),
            ShellDestination.Library);
        try
        {
            await viewModel.Loading;
            activities.Finish(loadActivity,
                FormatCount(
                    "Dialog.Fields.LoadedSelectedFiles",
                    paths.Count));
        }
        catch (Exception)
        {
            activities.Finish(
                loadActivity,
                Text("Dialog.Fields.LoadFailed"),
                AppActivityState.Failed);
            throw;
        }
        void Close(bool result) => Complete(result);
        viewModel.CloseRequested += Close;
        try
        {
            return await ShowAsync(new FieldsRequest(viewModel)
            {
                DefaultAction = DialogDefaultAction.None,
                DismissalPolicy = DialogDismissalPolicy.BlockWhile(() =>
                    viewModel.IsBusy ||
                    !string.IsNullOrWhiteSpace(viewModel.NewUserStringName) ||
                    viewModel.Rows.Any(row => row.IsModified || row.MarkedForRemoval)),
            });
        }
        finally
        {
            viewModel.CloseRequested -= Close;
        }
    }

    public void Complete(bool result)
    {
        TaskCompletionSource<bool>? completion = _completion;
        if (completion is null)
            return;
        _completion = null;
        Current = null;
        Changed?.Invoke();
        completion.TrySetResult(result);
    }

    private async Task<bool> ShowAsync(DialogRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Current = request;
            Changed?.Invoke();
            return await _completion.Task;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Text(string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private string FormatCount(
        string key,
        long count) =>
        localization?.FormatCount(
            key,
            count) ??
        LocalizedText.FormatCount(
            key,
            count);
}

public sealed class WorkflowDialogService(
    DialogService dialogs,
    INavigationService navigation,
    ILocalizationService? localization = null) : LegacyDialogs
{
    public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => dialogs.ShowAsync(paths);

    public Task<string?> ShowConfigEditorAsync(string? existingPath)
    {
        navigation.Navigate(ShellDestination.Settings);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> ConfirmApplyAsync(string title, string message, string primaryText) =>
        dialogs.ConfirmAsync(title, message, primaryText, DialogTone.Warning);

    public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => dialogs.ConfirmAsync(
        Text("Dialog.CdDerivation.Title"),
        Format(
            "Dialog.CdDerivation.MessageFormat",
            item.AlbumDisplay,
            string.Join(
                "\n",
                item.MissingTracks)),
        Text("Dialog.CdDerivation.Action"),
        DialogTone.Warning);

    public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => dialogs.ConfirmAsync(
        Text("Dialog.Restore.Title"),
        Format(
            "Dialog.Restore.MessageFormat",
            plan.Actions.Count,
            plan.CollisionCount),
        Text("Dialog.Restore.Action"),
        DialogTone.Warning);

    public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => dialogs.ConfirmAsync(
        Text("Dialog.Purge.Title"),
        Format(
            "Dialog.Purge.MessageFormat",
            plan.Runs.Count,
            plan.FileCount,
            plan.TotalBytes),
        Text("Dialog.Purge.Action"),
        DialogTone.Danger);

    private string Text(string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);
}
