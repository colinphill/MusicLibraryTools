using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using MusicFileUtilities;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using LegacyFilter = MusicLibrary.App.Services.FilePickerFilter;
using LegacyFiles = MusicLibrary.App.Services.IFileDialogService;

namespace MusicLibraryManager.Studio.Services;

public sealed class StudioWindowContext
{
    public Window? Window { get; set; }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (Window is null || Window.Dispatcher.CheckAccess())
            return action();
        return await Window.Dispatcher.InvokeAsync(action);
    }
}

public sealed class StudioFilePickerService(StudioWindowContext context) : IFilePickerService
{
    public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null) =>
        context.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog { Title = title, CheckFileExists = true, Multiselect = false };
            string filter = BuildFilter(types?.Select(type => (type.Description, type.Extensions)));
            if (filter.Length > 0)
                dialog.Filter = filter;
            return dialog.ShowDialog(context.Window) == true ? dialog.FileName : null;
        });

    public Task<string?> PickFolderAsync(string title) => context.InvokeAsync(() =>
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(context.Window) == true ? dialog.FolderName : null;
    });

    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) =>
        context.InvokeAsync(() =>
        {
            string normalized = NormalizeExtension(extension);
            var dialog = new SaveFileDialog
            {
                Title = title,
                FileName = suggestedName,
                DefaultExt = normalized,
                AddExtension = true,
                Filter = $"{normalized.TrimStart('.').ToUpperInvariant()} file|*{normalized}|All files|*.*",
            };
            return dialog.ShowDialog(context.Window) == true ? dialog.FileName : null;
        });

    public static string NormalizeExtension(string value)
    {
        string trimmed = value.Trim().TrimStart('*');
        return trimmed.Length == 0 || trimmed == ".*" ? ".*" : trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }

    public static string BuildFilter(IEnumerable<(string Name, IReadOnlyList<string> Extensions)>? types)
    {
        if (types is null)
            return "";
        return string.Join('|', types.SelectMany(type => new[]
        {
            type.Name,
            string.Join(';', type.Extensions.Select(extension => $"*{NormalizeExtension(extension)}")),
        }));
    }
}

public sealed class StudioWorkflowFileService(StudioFilePickerService files) : LegacyFiles
{
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<LegacyFilter>? filters = null) =>
        files.PickFileAsync(title, filters?.Select(filter =>
            new FilePickerType(filter.Name, filter.Patterns.Select(StudioFilePickerService.NormalizeExtension).ToArray())).ToArray());

    public Task<string?> PickFolderAsync(string title) => files.PickFolderAsync(title);

    public Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
        string? defaultExtension = null, IReadOnlyList<LegacyFilter>? filters = null) =>
        files.SaveFileAsync(title, suggestedName ?? "export", defaultExtension ?? filters?.FirstOrDefault()?.Patterns.FirstOrDefault() ?? ".dat");
}

public sealed class StudioThumbnailService : IThumbnailService
{
    public Task<object?> CreateImageSourceAsync(byte[] data, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string mime = ImageFile.DetectImageFormat(data) switch
        {
            ImageFile.ImageFormat.Png => "image/png",
            ImageFile.ImageFormat.Gif => "image/gif",
            ImageFile.ImageFormat.Bmp => "image/bmp",
            _ => "image/jpeg",
        };
        return Task.FromResult<object?>($"data:{mime};base64,{Convert.ToBase64String(data)}");
    }
}

public sealed class StudioPlatformService(StudioWindowContext context) : IPlatformService
{
    public async Task CopyTextAsync(string text) => await context.InvokeAsync(() =>
    {
        Clipboard.SetText(text);
        return true;
    });

    public void RevealFile(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}

public sealed class StudioWindowStateService(IAppSettings settings) : IWindowStateService
{
    private const string Preference = "manager.window.studio.v1";

    public WindowStateSnapshot? Load()
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WindowStateSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(WindowStateSnapshot state) =>
        settings.SetPreference(Preference, JsonSerializer.Serialize(state));
}

public sealed class StudioThemeService : IThemeService
{
    public string Current { get; private set; } = "System";
    public event Action? Changed;

    public void Apply(string theme)
    {
        Current = theme is "Light" or "Dark" ? theme : "System";
        Changed?.Invoke();
    }
}
