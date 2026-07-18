using System.Diagnostics;
using System.Text.Json;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Styling;
using global::Avalonia.Threading;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Services;
using LegacyFilter = MusicLibraryManager.Presentation.FilePickerFilter;
using LegacyFiles = MusicLibraryManager.Presentation.IFileDialogService;

namespace MusicLibraryManager.Services;

public sealed class WindowContext
{
    public Window? Window { get; set; }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();
        return await Dispatcher.UIThread.InvokeAsync(action);
    }
}

public sealed class FilePickerService(WindowContext context) : IFilePickerService
{
    public async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null)
    {
        var provider = context.Window?.StorageProvider;
        if (provider is null)
            return null;
        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types?.Select(type => new FilePickerFileType(type.Description)
            {
                Patterns = type.Extensions.Select(extension => $"*{NormalizeExtension(extension)}").ToArray(),
            }).ToArray(),
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var provider = context.Window?.StorageProvider;
        if (provider is null)
            return null;
        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var provider = context.Window?.StorageProvider;
        if (provider is null)
            return null;
        string normalized = NormalizeExtension(extension);
        var result = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = normalized.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType($"{normalized.TrimStart('.').ToUpperInvariant()} file")
            {
                Patterns = [$"*{normalized}"],
            }],
        });
        return result?.TryGetLocalPath();
    }

    public static string NormalizeExtension(string value)
    {
        string trimmed = value.Trim().TrimStart('*');
        return trimmed.Length == 0 || trimmed == ".*" ? ".*" : trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}

public sealed class WorkflowFileService(FilePickerService files) : LegacyFiles
{
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<LegacyFilter>? filters = null) =>
        files.PickFileAsync(title, filters?.Select(filter => new FilePickerType(
            filter.Name,
            filter.Patterns.Select(FilePickerService.NormalizeExtension).ToArray())).ToArray());

    public Task<string?> PickFolderAsync(string title) => files.PickFolderAsync(title);

    public Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
        string? defaultExtension = null, IReadOnlyList<LegacyFilter>? filters = null) =>
        files.SaveFileAsync(title, suggestedName ?? "export",
            defaultExtension ?? filters?.FirstOrDefault()?.Patterns.FirstOrDefault() ?? ".dat");
}

public sealed class ThumbnailService : IThumbnailService
{
    public Task<object?> CreateImageSourceAsync(byte[] data, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(data, writable: false);
        object result = decodePixelWidth > 0
            ? Bitmap.DecodeToWidth(stream, decodePixelWidth)
            : new Bitmap(stream);
        return Task.FromResult<object?>(result);
    }
}

public sealed class PlatformService(WindowContext context) : IPlatformService
{
    public async Task CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(context.Window)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetValueAsync(DataFormat.Text, text);
    }

    public void RevealFile(string path)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "explorer.exe";
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            start.FileName = "open";
            start.ArgumentList.Add("-R");
            start.ArgumentList.Add(path);
        }
        else
        {
            start.FileName = "xdg-open";
            start.ArgumentList.Add(Path.GetDirectoryName(path) ?? path);
        }
        Process.Start(start);
    }
}

public sealed class WindowStateService(IAppSettings settings) : IWindowStateService
{
    private const string Preference = "manager.window.v2";

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

public sealed class ThemeService : IThemeService
{
    public string Current { get; private set; } = "System";
    public event Action? Changed;

    public void Apply(string theme)
    {
        Current = theme is "Light" or "Dark" ? theme : "System";
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Current switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
        Changed?.Invoke();
    }
}
