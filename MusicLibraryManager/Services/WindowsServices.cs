using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MusicLibrary.Core.Services;
using MusicLibrary.App.ViewModels;
using MusicLibraryManager.Dialogs;
using MusicLibraryManager.Presentation;
using LegacyDialogs = MusicLibrary.App.Services.IDialogService;
using LegacyFiles = MusicLibrary.App.Services.IFileDialogService;
using LegacyFilter = MusicLibrary.App.Services.FilePickerFilter;

namespace MusicLibraryManager.Services;

public sealed class WindowContext
{
    public Window? Window { get; set; }
}

public sealed class WpfFilePickerService(WindowContext context) : IFilePickerService
{
    public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null)
    {
        var picker = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = BuildFilter(types),
        };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FileName : null);
    }

    public Task<string?> PickFolderAsync(string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = false };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FolderName : null);
    }

    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var picker = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            DefaultExt = extension,
            AddExtension = true,
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} file|*{extension}|All files|*.*",
        };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FileName : null);
    }

    private static string BuildFilter(IReadOnlyList<FilePickerType>? types)
    {
        if (types is null || types.Count == 0)
            return "All files|*.*";
        return string.Join("|", types.Select(type =>
            $"{type.Description}|{string.Join(';', type.Extensions.Select(extension => $"*{extension}"))}")) +
            "|All files|*.*";
    }
}

public sealed class WpfDialogCoordinator(WindowContext context) : IDialogCoordinator
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryText)
        => Task.FromResult(MessageBox.Show(context.Window, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    public Task ShowMessageAsync(string title, string message)
    {
        MessageBox.Show(context.Window, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }
}

public sealed class WpfFieldsEditorService(
    WindowContext context,
    IMediaFileService media,
    ITagWriteService writer) : IFieldsEditorService
{
    public Task<bool> ShowAsync(IReadOnlyList<string> paths)
    {
        if (context.Window is null || paths.Count == 0)
            return Task.FromResult(false);
        var viewModel = new FieldsDialogViewModel(media, writer, paths);
        var dialog = new FieldsDialog(viewModel) { Owner = context.Window };
        return Task.FromResult(dialog.ShowDialog() == true);
    }
}

public sealed class WorkflowFileDialogService(WindowContext context) : LegacyFiles
{
    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<LegacyFilter>? filters = null)
    {
        var picker = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = BuildFilter(filters),
        };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FileName : null);
    }

    public Task<string?> PickFolderAsync(string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = false };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FolderName : null);
    }

    public Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
        string? defaultExtension = null, IReadOnlyList<LegacyFilter>? filters = null)
    {
        var picker = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName ?? "",
            DefaultExt = defaultExtension ?? "",
            AddExtension = true,
            Filter = BuildFilter(filters),
        };
        return Task.FromResult(picker.ShowDialog(context.Window) == true ? picker.FileName : null);
    }

    private static string BuildFilter(IReadOnlyList<LegacyFilter>? filters)
        => filters is null || filters.Count == 0
            ? "All files|*.*"
            : string.Join("|", filters.Select(filter =>
                $"{filter.Name}|{string.Join(";", filter.Patterns)}")) + "|All files|*.*";
}

public sealed class WorkflowDialogService(WindowContext context) : LegacyDialogs
{
    public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
    public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmCdDerivationAsync(MusicLibrary.Core.Models.IngestApprovalItem item)
        => Confirm("Approve CD derivation",
            $"{item.AlbumDisplay}\n\nGenerate the missing tracks below?\n\n" +
            string.Join("\n", item.MissingTracks));

    public Task<bool> ConfirmRestoreAsync(MusicLibrary.Core.Models.OperationRestorePlan plan)
        => Confirm("Restore operation items",
            $"Restore {plan.Actions.Count:N0} selected item(s)?\n\n" +
            $"{plan.CollisionCount:N0} existing destination(s) will be preserved as collision backups.");

    public Task<bool> ConfirmPurgeAsync(MusicLibrary.Core.Models.OperationPurgePlan plan)
        => Confirm("Permanently purge operation history",
            $"Permanently delete {plan.Runs.Count:N0} operation run(s), {plan.FileCount:N0} file(s), " +
            $"and {plan.TotalBytes:N0} bytes?\n\nThis cannot be undone.");

    private Task<bool> Confirm(string title, string message)
        => Task.FromResult(MessageBox.Show(context.Window, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
}

public sealed class WpfThumbnailService : IThumbnailService
{
    public Task<object?> CreateImageSourceAsync(
        byte[] data,
        int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(data, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return Task.FromResult<object?>(bitmap);
    }
}

public sealed class WindowsPlatformService : IPlatformService
{
    public Task CopyTextAsync(string text)
    {
        Clipboard.SetText(text);
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
    private const string Preference = "manager.window.wpf.v1";

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

public sealed class WpfThemeService : IThemeService
{
    public string Current { get; private set; } = "System";

    public WpfThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Current == "System" && Application.Current is { } application)
                application.Dispatcher.BeginInvoke(UpdateNavigationForeground);
        };
    }

    public void Apply(string theme)
    {
        Current = theme is "Light" or "Dark" ? theme : "System";
        Application.Current.ThemeMode = Current switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        UpdateNavigationForeground();
    }

    private void UpdateNavigationForeground()
    {
        if (Application.Current is not { } application)
            return;

        SolidColorBrush brush;
        if (SystemParameters.HighContrast)
        {
            brush = SystemColors.ControlTextBrush.CloneCurrentValue();
        }
        else
        {
            bool dark = Current == "Dark" || Current == "System" && IsSystemDark();
            brush = new SolidColorBrush(dark
                ? Color.FromRgb(243, 243, 243)
                : Color.FromRgb(31, 31, 31));
        }
        brush.Freeze();
        application.Resources["NavigationForegroundBrush"] = brush;
    }

    private static bool IsSystemDark()
        => Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1) is int value && value == 0;
}
