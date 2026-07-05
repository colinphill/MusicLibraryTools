using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MusicLibrary.App.Services;

/// <inheritdoc cref="IFileDialogService"/>
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>Set by the main window once it exists; the picker attaches to it.</summary>
    public Window? Owner { get; set; }

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null)
    {
        var provider = Owner?.StorageProvider;
        if (provider is null)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters?.Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = f.Patterns.ToList(),
            }).ToList(),
        };

        var result = await provider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var provider = Owner?.StorageProvider;
        if (provider is null)
            return null;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
        string? defaultExtension = null, IReadOnlyList<FilePickerFilter>? filters = null)
    {
        var provider = Owner?.StorageProvider;
        if (provider is null)
            return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = filters?.Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = f.Patterns.ToList(),
            }).ToList(),
        });
        return file?.TryGetLocalPath();
    }
}
