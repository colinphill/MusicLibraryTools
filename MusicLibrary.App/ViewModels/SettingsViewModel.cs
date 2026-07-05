using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Loads the LibraryConfiguration XML that everything else depends on (index roots, cache DB path,
/// path-length limits). Auto-restores the last-used config on startup.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettings _settings;
    private readonly IFileDialogService _dialogs;
    private readonly IDialogService _configDialog;

    [ObservableProperty]
    private string? _configPath;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditConfigCommand))]
    private bool _isConfigLoaded;

    public SettingsViewModel(IAppSettings settings, IFileDialogService dialogs, IDialogService configDialog)
    {
        _settings = settings;
        _dialogs = dialogs;
        _configDialog = configDialog;
    }

    /// <summary>Re-load the config the user used last time, if it still exists.</summary>
    public void RestoreRememberedConfig()
    {
        var remembered = _settings.GetRememberedConfigPath();
        if (remembered is not null)
            TryLoad(remembered);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(
            "Select a LibraryConfiguration XML",
            [new FilePickerFilter("XML files", ["*.xml"]), new FilePickerFilter("All files", ["*"])]);
        if (path is not null)
            TryLoad(path);
    }

    [RelayCommand]
    private async Task NewConfigAsync()
    {
        var saved = await _configDialog.ShowConfigEditorAsync(null);
        if (saved is not null)
            TryLoad(saved);
    }

    private bool CanEditConfig() => IsConfigLoaded;

    [RelayCommand(CanExecute = nameof(CanEditConfig))]
    private async Task EditConfigAsync()
    {
        var saved = await _configDialog.ShowConfigEditorAsync(ConfigPath);
        if (saved is not null)
            TryLoad(saved);
    }

    private void TryLoad(string path)
    {
        try
        {
            _settings.LoadConfig(path);
            ConfigPath = path;
            IsConfigLoaded = true;
            var dbFile = _settings.Configuration!.DatabaseFile;
            var roots = string.Join(", ", _settings.Configuration!.IndexLocations.Select(l => l.Target));
            StatusMessage = $"Loaded. Cache: {dbFile}. Index roots: {roots}";
        }
        catch (Exception ex)
        {
            IsConfigLoaded = false;
            StatusMessage = $"Failed to load config: {ex.Message}";
        }
    }
}
