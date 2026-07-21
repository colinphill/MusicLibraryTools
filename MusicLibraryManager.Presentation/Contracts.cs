using System.Collections.ObjectModel;

namespace MusicLibraryManager.Presentation;

public enum ShellDestination
{
    Home,
    Library,
    Health,
    Ingest,
    Organize,
    Devices,
    Operations,
    Settings,
}

public sealed record FilePickerType(string Description, IReadOnlyList<string> Extensions);

public interface INavigationService
{
    ShellDestination Current { get; }
    event Action<ShellDestination>? NavigationRequested;
    void Navigate(ShellDestination destination);
}

public interface INavigationGuard
{
    bool HasUnsavedChanges { get; }
    Task<bool> ConfirmNavigationAsync();
}

public interface IFilePickerService
{
    Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null);
    Task<string?> PickFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);
}

public interface IDialogCoordinator
{
    Task<bool> ConfirmAsync(string title, string message, string primaryText);
    Task ShowMessageAsync(string title, string message);
}

public interface IFieldsEditorService
{
    Task<bool> ShowAsync(IReadOnlyList<string> paths);
}

public interface IThumbnailService
{
    Task<object?> CreateImageSourceAsync(
        byte[] data,
        int decodePixelWidth = 0,
        CancellationToken cancellationToken = default);
}

public interface IPlatformService
{
    Task CopyTextAsync(string text);
    void RevealFile(string path);
}

public interface IWindowStateService
{
    WindowStateSnapshot? Load();
    void Save(WindowStateSnapshot state);
}

public interface IThemeService
{
    string Current { get; }
    void Apply(string theme);
}

public sealed record WindowStateSnapshot(int Version, int X, int Y, int Width, int Height, bool Maximized);

public enum AppActivityState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record AppActivity(
    Guid Id,
    string Title,
    string Message,
    AppActivityState State,
    double? Progress,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null,
    ShellDestination? Destination = null,
    bool CanCancel = false);

public interface IActivityService
{
    ReadOnlyObservableCollection<AppActivity> Activities { get; }
    AppActivity? Current { get; }
    event Action? Changed;
    Guid Start(
        string title,
        string message,
        ShellDestination? destination = null,
        Action? cancel = null);
    void Report(Guid id, string message, double? progress = null);
    void Finish(Guid id, string message, AppActivityState state = AppActivityState.Completed);
    bool Cancel(Guid id);
    void Dismiss(Guid id);
}
