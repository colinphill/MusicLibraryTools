using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class ShellViewModel : ObservableObject
{
    private readonly IAppSettings _settings;
    private readonly INavigationService _navigation;
    private readonly IActivityService _activities;

    [ObservableProperty]
    private string _configurationName = "Choose a library";

    [ObservableProperty]
    private string _activityText = "Ready";

    [ObservableProperty]
    private bool _hasRunningActivity;

    [ObservableProperty]
    private string? _globalSearchText;

    public ShellViewModel(IAppSettings settings, INavigationService navigation, IActivityService activities)
    {
        _settings = settings;
        _navigation = navigation;
        _activities = activities;
        RecentConfigurations = new ObservableCollection<string>(settings.RecentConfigPaths);
        settings.ConfigurationChanged += (_, _) => RefreshConfiguration();
        activities.Changed += RefreshActivity;
        RefreshConfiguration();
        RefreshActivity();
    }

    public ObservableCollection<string> RecentConfigurations { get; }
    public ReadOnlyObservableCollection<AppActivity> Activities => _activities.Activities;

    public void RestoreConfiguration()
    {
        string? remembered = _settings.GetRememberedConfigPath();
        if (remembered is null)
            return;
        try
        {
            _settings.LoadConfig(remembered);
        }
        catch
        {
            ConfigurationName = "Configuration needs attention";
        }
    }

    [RelayCommand]
    private void Search()
    {
        _navigation.Navigate(ShellDestination.Library);
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(ShellDestination.Settings);

    private void RefreshConfiguration()
    {
        ConfigurationName = _settings.ConfigPath is { } configPath
            ? Path.GetFileNameWithoutExtension(configPath)
            : "Choose a library";
        RecentConfigurations.Clear();
        foreach (string recentPath in _settings.RecentConfigPaths)
            RecentConfigurations.Add(recentPath);
    }

    private void RefreshActivity()
    {
        AppActivity? current = _activities.Current;
        HasRunningActivity = current is not null;
        ActivityText = current?.Message ?? _activities.Activities.FirstOrDefault()?.Message ?? "Ready";
    }
}
