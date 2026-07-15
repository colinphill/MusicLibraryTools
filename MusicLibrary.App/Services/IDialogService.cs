using MusicLibrary.Core.Models;

namespace MusicLibrary.App.Services;

/// <summary>Opens the app's modal dialogs so ViewModels don't reference Window types directly.</summary>
public interface IDialogService
{
    /// <summary>Edit arbitrary tag fields on the given files. Returns true if changes were saved.</summary>
    Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths);

    /// <summary>Create/edit a LibraryConfiguration. Returns the saved config path, or null if cancelled.</summary>
    Task<string?> ShowConfigEditorAsync(string? existingPath);

    Task<string?> ShowIngestConfigEditorAsync(string? existingPath);

    Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item);

    Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan);

    Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan);
}
