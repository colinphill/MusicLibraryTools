using MusicLibrary.Core.Models;

namespace MusicLibraryManager.Presentation;

/// <summary>Opens the app's modal dialogs so ViewModels don't reference Window types directly.</summary>
public interface IDialogService
{
    /// <summary>
    /// Edit arbitrary tag fields on the given files. Returns true when the
    /// reviewed preview was accepted into Pending Changes.
    /// </summary>
    Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths);

    /// <summary>Create/edit a LibraryConfiguration. Returns the saved config path, or null if cancelled.</summary>
    Task<string?> ShowConfigEditorAsync(string? existingPath);

    /// <summary>Confirm a reviewed workflow plan immediately before it mutates files.</summary>
    Task<bool> ConfirmApplyAsync(string title, string message, string primaryText);

    Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item);

    Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan);

    Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan);
}
