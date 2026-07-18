using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using LegacyDialogs = MusicLibraryManager.Presentation.IDialogService;

namespace MusicLibraryManager.Services;

public abstract record DialogRequest(string Title);
public sealed record MessageRequest(string DialogTitle, string Message)
    : DialogRequest(DialogTitle);
public sealed record ConfirmRequest(string DialogTitle, string Message, string PrimaryText)
    : DialogRequest(DialogTitle);
public sealed record FieldsRequest(FieldsDialogViewModel ViewModel)
    : DialogRequest(ViewModel.Title);

public sealed class DialogService(IMediaFileService media, ITagWriteService writer)
    : IDialogCoordinator, IFieldsEditorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _completion;

    public DialogRequest? Current { get; private set; }
    public event Action? Changed;

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText) =>
        await ShowAsync(new ConfirmRequest(title, message, primaryText));

    public async Task ShowMessageAsync(string title, string message) =>
        await ShowAsync(new MessageRequest(title, message));

    public async Task<bool> ShowAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;
        var viewModel = new FieldsDialogViewModel(media, writer, paths);
        await viewModel.Loading;
        void Close(bool result) => Complete(result);
        viewModel.CloseRequested += Close;
        try
        {
            return await ShowAsync(new FieldsRequest(viewModel));
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
}

public sealed class WorkflowDialogService(
    DialogService dialogs,
    INavigationService navigation) : LegacyDialogs
{
    public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => dialogs.ShowAsync(paths);

    public Task<string?> ShowConfigEditorAsync(string? existingPath)
    {
        navigation.Navigate(ShellDestination.Settings);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => dialogs.ConfirmAsync(
        "Approve CD derivation",
        $"{item.AlbumDisplay}\n\nGenerate the missing tracks below?\n\n{string.Join("\n", item.MissingTracks)}",
        "Generate");

    public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => dialogs.ConfirmAsync(
        "Restore operation items",
        $"Restore {plan.Actions.Count:N0} selected item(s)?\n\n{plan.CollisionCount:N0} existing destination(s) will be preserved as collision backups.",
        "Restore");

    public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => dialogs.ConfirmAsync(
        "Permanently purge operation history",
        $"Permanently delete {plan.Runs.Count:N0} operation run(s), {plan.FileCount:N0} file(s), and {plan.TotalBytes:N0} bytes?\n\nThis cannot be undone.",
        "Purge");
}
