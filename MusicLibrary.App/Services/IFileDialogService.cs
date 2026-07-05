namespace MusicLibrary.App.Services;

/// <summary>
/// Abstracts Avalonia's StorageProvider so ViewModels can request file/folder pickers without a
/// reference to a Window. The main window registers itself as the owner at startup.
/// </summary>
public interface IFileDialogService
{
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveFileAsync(string title, string? suggestedName = null, string? defaultExtension = null,
        IReadOnlyList<FilePickerFilter>? filters = null);
}

public sealed record FilePickerFilter(string Name, IReadOnlyList<string> Patterns);
