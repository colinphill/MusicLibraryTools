using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Loads the LibraryConfiguration XML that everything else depends on (index roots, cache DB path,
/// path-length limits). Auto-restores the last-used config on startup and keeps a recent-files list.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettings _settings;
    private readonly IFileDialogService _dialogs;
    private readonly IDialogService _configDialog;
    private bool _suppressRecentLoad;

    [ObservableProperty]
    private string? _configPath;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditConfigCommand))]
    private bool _isConfigLoaded;

    /// <summary>The recent-configurations dropdown (most recent first, existing files only).</summary>
    public ObservableCollection<string> RecentConfigs { get; } = [];

    /// <summary>Two-way bound to the dropdown; picking an entry loads that configuration.</summary>
    [ObservableProperty]
    private string? _selectedRecent;

    public SettingsViewModel(IAppSettings settings, IFileDialogService dialogs, IDialogService configDialog)
    {
        _settings = settings;
        _dialogs = dialogs;
        _configDialog = configDialog;
        RefreshRecents();
    }

    partial void OnSelectedRecentChanged(string? value)
    {
        // Ignore programmatic updates (RefreshRecents) and re-selecting the already-loaded config.
        if (_suppressRecentLoad || value is null || string.Equals(value, ConfigPath, StringComparison.OrdinalIgnoreCase))
            return;
        TryLoad(value);
    }

    private void RefreshRecents()
    {
        _suppressRecentLoad = true;
        RecentConfigs.Clear();
        foreach (var path in _settings.RecentConfigPaths.Where(File.Exists))
            RecentConfigs.Add(path);
        // Always keep the loaded config visible/selectable, even after the history is cleared.
        if (ConfigPath is not null && File.Exists(ConfigPath)
            && !RecentConfigs.Any(p => string.Equals(p, ConfigPath, StringComparison.OrdinalIgnoreCase)))
            RecentConfigs.Insert(0, ConfigPath);
        SelectedRecent = RecentConfigs.FirstOrDefault(p => string.Equals(p, ConfigPath, StringComparison.OrdinalIgnoreCase));
        _suppressRecentLoad = false;
    }

    private bool CanClearRecent() => RecentConfigs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRecent))]
    private void ClearRecent()
    {
        _settings.ClearRecentConfigs();
        RefreshRecents();
        ClearRecentCommand.NotifyCanExecuteChanged();
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
            RefreshRecents();
            ClearRecentCommand.NotifyCanExecuteChanged();
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
