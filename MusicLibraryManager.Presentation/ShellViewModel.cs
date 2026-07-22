using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

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
    private bool _hasVisibleActivity;

    [ObservableProperty]
    private AppActivity? _visibleActivity;

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
    public string ActivityTitle => VisibleActivity?.Title ?? "Activity";
    public bool CanOpenHealth => _settings.Configuration is not null;
    public bool CanOpenIngest => _settings.Configuration is { } configuration &&
        (configuration.ActiveProfile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools ||
         configuration.ActiveIngestProfile.Ingest.Enabled && configuration.PolicySnapshot.Roots.Values.Any(
             root => root.Permissions.HasFlag(LibraryRootPermissions.IngestOutput)));
    public bool CanOpenOrganize => _settings.Configuration is { } configuration &&
        configuration.PolicySnapshot.Roots.Values.Any(root =>
            root.Permissions.HasFlag(LibraryRootPermissions.OrganizeFiles));
    public bool CanOpenDevices => _settings.Configuration is { } configuration &&
        (configuration.ActiveProfile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools ||
         configuration.ExportProfiles.Any(profile =>
             profile.IsVisible &&
             profile.Transform.Mode == ExportTransformMode.SpecializedProvider));
    public double ActivityProgress => VisibleActivity?.Progress ?? 0;
    public bool HasDeterminateActivityProgress =>
        VisibleActivity is { State: AppActivityState.Running, Progress: not null };
    public bool HasIndeterminateActivityProgress =>
        VisibleActivity is { State: AppActivityState.Running, Progress: null };
    public bool HasVisibleActivityProgress =>
        HasDeterminateActivityProgress || HasIndeterminateActivityProgress;
    public bool IsActivityCancelVisible => VisibleActivity?.CanCancel == true;
    public bool IsActivityDismissVisible =>
        VisibleActivity is { State: not AppActivityState.Running };
    public bool ActivityIsInfo => VisibleActivity?.State == AppActivityState.Running;
    public bool ActivityIsSuccess => VisibleActivity?.State == AppActivityState.Completed;
    public bool ActivityIsWarning => VisibleActivity?.State == AppActivityState.Cancelled;
    public bool ActivityIsError => VisibleActivity?.State == AppActivityState.Failed;
    public string ActivityStateText => VisibleActivity?.State switch
    {
        AppActivityState.Completed => "Completed",
        AppActivityState.Failed => "Failed",
        AppActivityState.Cancelled => "Cancelled",
        _ => "In progress",
    };

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

    private bool CanOpenActivity() => VisibleActivity?.Destination is not null;

    [RelayCommand(CanExecute = nameof(CanOpenActivity))]
    private void OpenActivity()
    {
        if (VisibleActivity?.Destination is { } destination)
            _navigation.Navigate(destination);
    }

    private bool CanCancelActivity() => VisibleActivity?.CanCancel == true;

    [RelayCommand(CanExecute = nameof(CanCancelActivity))]
    private void CancelActivity()
    {
        if (VisibleActivity is { } activity)
            _activities.Cancel(activity.Id);
    }

    private bool CanDismissActivity() =>
        VisibleActivity is { State: not AppActivityState.Running };

    [RelayCommand(CanExecute = nameof(CanDismissActivity))]
    private void DismissActivity()
    {
        if (VisibleActivity is { } activity)
            _activities.Dismiss(activity.Id);
    }

    private void RefreshConfiguration()
    {
        ConfigurationName = _settings.ConfigPath is { } configPath
            ? Path.GetFileNameWithoutExtension(configPath)
            : "Choose a library";
        RecentConfigurations.Clear();
        foreach (string recentPath in _settings.RecentConfigPaths)
            RecentConfigurations.Add(recentPath);
        OnPropertyChanged(nameof(CanOpenHealth));
        OnPropertyChanged(nameof(CanOpenIngest));
        OnPropertyChanged(nameof(CanOpenOrganize));
        OnPropertyChanged(nameof(CanOpenDevices));
    }

    private void RefreshActivity()
    {
        AppActivity? current = _activities.Current;
        HasRunningActivity = current is not null;
        VisibleActivity = current ?? _activities.Activities.FirstOrDefault();
        HasVisibleActivity = VisibleActivity is not null;
        ActivityText = VisibleActivity?.Message ?? "Ready";
        OnPropertyChanged(nameof(ActivityTitle));
        OnPropertyChanged(nameof(ActivityProgress));
        OnPropertyChanged(nameof(HasDeterminateActivityProgress));
        OnPropertyChanged(nameof(HasIndeterminateActivityProgress));
        OnPropertyChanged(nameof(HasVisibleActivityProgress));
        OnPropertyChanged(nameof(IsActivityCancelVisible));
        OnPropertyChanged(nameof(IsActivityDismissVisible));
        OnPropertyChanged(nameof(ActivityIsInfo));
        OnPropertyChanged(nameof(ActivityIsSuccess));
        OnPropertyChanged(nameof(ActivityIsWarning));
        OnPropertyChanged(nameof(ActivityIsError));
        OnPropertyChanged(nameof(ActivityStateText));
        OpenActivityCommand.NotifyCanExecuteChanged();
        CancelActivityCommand.NotifyCanExecuteChanged();
        DismissActivityCommand.NotifyCanExecuteChanged();
    }
}
