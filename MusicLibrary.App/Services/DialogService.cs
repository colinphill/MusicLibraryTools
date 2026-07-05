using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.ViewModels;
using MusicLibrary.App.Views;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.Services;

/// <inheritdoc cref="IDialogService"/>
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    /// <summary>Set by the main window at startup; dialogs are shown modally over it.</summary>
    public Window? Owner { get; set; }

    public DialogService(IServiceProvider services) => _services = services;

    public async Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths)
    {
        if (Owner is null || paths.Count == 0)
            return false;

        var vm = new FieldsDialogViewModel(
            _services.GetRequiredService<IMediaFileService>(),
            _services.GetRequiredService<ITagWriteService>(),
            paths);
        var dialog = new FieldsDialog { DataContext = vm };
        return await dialog.ShowDialog<bool>(Owner);
    }

    public async Task<string?> ShowConfigEditorAsync(string? existingPath)
    {
        if (Owner is null)
            return null;

        var vm = new ConfigDialogViewModel(_services.GetRequiredService<IFileDialogService>(), existingPath);
        var dialog = new ConfigDialog { DataContext = vm };
        return await dialog.ShowDialog<string?>(Owner);
    }
}
