using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Dialogs;
using MusicLibraryManager.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using LegacyDialogs = MusicLibrary.App.Services.IDialogService;
using LegacyFiles = MusicLibrary.App.Services.IFileDialogService;
using LegacyFilter = MusicLibrary.App.Services.FilePickerFilter;

namespace MusicLibraryManager.Services;

public sealed class WindowContext
{
    public Window? Window { get; set; }

    public nint Hwnd => Window is null ? 0 : WindowNative.GetWindowHandle(Window);

    public XamlRoot? XamlRoot => Window?.Content?.XamlRoot;
}

public sealed class WinUiFilePickerService(WindowContext context) : IFilePickerService
{
    public async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        AddFileTypes(picker.FileTypeFilter, types?.SelectMany(type => type.Extensions));
        StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        string normalized = NormalizeExtension(extension);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = normalized,
        };
        picker.FileTypeChoices.Add($"{normalized.TrimStart('.').ToUpperInvariant()} file", [normalized]);
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    internal static void AddFileTypes(IList<string> target, IEnumerable<string>? extensions)
    {
        string[] values = extensions?.Select(NormalizeExtension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        foreach (string extension in values.Length == 0 ? ["*"] : values)
            target.Add(extension);
    }

    internal static string NormalizeExtension(string value)
    {
        string trimmed = value.Trim().TrimStart('*');
        return trimmed == "" || trimmed == ".*" ? "*" : trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}

public sealed class WinUiDialogCoordinator(WindowContext context) : IDialogCoordinator
{
    public async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        if (context.XamlRoot is null)
            return false;
        var dialog = new ContentDialog
        {
            XamlRoot = context.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (context.XamlRoot is null)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = context.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}

public sealed class WinUiFieldsEditorService(
    WindowContext context,
    IMediaFileService media,
    ITagWriteService writer) : IFieldsEditorService
{
    public async Task<bool> ShowAsync(IReadOnlyList<string> paths)
    {
        if (context.XamlRoot is null || paths.Count == 0)
            return false;
        var viewModel = new FieldsDialogViewModel(media, writer, paths);
        var dialog = new FieldsDialog(viewModel) { XamlRoot = context.XamlRoot };
        return await dialog.ShowEditorAsync();
    }
}

public sealed class WorkflowFileDialogService(WindowContext context) : LegacyFiles
{
    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<LegacyFilter>? filters = null)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinUiFilePickerService.AddFileTypes(picker.FileTypeFilter, filters?.SelectMany(filter => filter.Patterns));
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
        string? defaultExtension = null, IReadOnlyList<LegacyFilter>? filters = null)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName ?? "export",
        };
        if (!string.IsNullOrWhiteSpace(defaultExtension))
            picker.DefaultFileExtension = WinUiFilePickerService.NormalizeExtension(defaultExtension);
        if (filters is { Count: > 0 })
        {
            foreach (LegacyFilter filter in filters)
                picker.FileTypeChoices.Add(filter.Name,
                    filter.Patterns.Select(WinUiFilePickerService.NormalizeExtension).ToList());
        }
        else
        {
            picker.FileTypeChoices.Add("All files", ["*"]);
        }
        InitializeWithWindow.Initialize(picker, context.Hwnd);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}

public sealed class WorkflowDialogService(IDialogCoordinator dialogs) : LegacyDialogs
{
    public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
    public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmCdDerivationAsync(MusicLibrary.Core.Models.IngestApprovalItem item)
        => dialogs.ConfirmAsync("Approve CD derivation",
            $"{item.AlbumDisplay}\n\nGenerate the missing tracks below?\n\n{string.Join("\n", item.MissingTracks)}", "Generate");

    public Task<bool> ConfirmRestoreAsync(MusicLibrary.Core.Models.OperationRestorePlan plan)
        => dialogs.ConfirmAsync("Restore operation items",
            $"Restore {plan.Actions.Count:N0} selected item(s)?\n\n{plan.CollisionCount:N0} existing destination(s) will be preserved as collision backups.", "Restore");

    public Task<bool> ConfirmPurgeAsync(MusicLibrary.Core.Models.OperationPurgePlan plan)
        => dialogs.ConfirmAsync("Permanently purge operation history",
            $"Permanently delete {plan.Runs.Count:N0} operation run(s), {plan.FileCount:N0} file(s), and {plan.TotalBytes:N0} bytes?\n\nThis cannot be undone.", "Purge");
}

public sealed class WinUiThumbnailService : IThumbnailService
{
    public async Task<object?> CreateImageSourceAsync(byte[] data, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}

public sealed class WindowsPlatformService : IPlatformService
{
    public Task CopyTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    public void RevealFile(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}

public sealed class SettingsWindowStateService(IAppSettings settings) : IWindowStateService
{
    private const string Preference = "manager.window.winui.v1";

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

    public void Save(WindowStateSnapshot state)
        => settings.SetPreference(Preference, JsonSerializer.Serialize(state));
}

public sealed class WinUiThemeService(WindowContext context) : IThemeService
{
    public string Current { get; private set; } = "System";

    public void Apply(string theme)
    {
        Current = theme is "Light" or "Dark" ? theme : "System";
        if (context.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Current switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
