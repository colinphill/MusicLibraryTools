using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataCaching;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class HomeViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly IAppSettings _settings;
    private readonly INavigationService _navigation;
    private readonly IActivityService _activities;
    private readonly ILocalizationService? _localization;
    private DateTime? _lastSuccessfulIndex;
    private (int Healthy, int Degraded, int Unavailable)?
        _rootCounts;
    private bool _hasNoScanHistory;
    private bool _refreshFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLibraryCommand))]
    private bool _hasConfiguration;
    [ObservableProperty] private bool _hasRecentActivity;
    [ObservableProperty] private string _trackCount = "—";
    [ObservableProperty] private string _albumCount = "—";
    [ObservableProperty] private string _artistCount = "—";
    [ObservableProperty] private string _artworkCount = "—";
    [ObservableProperty]
    private string _lastIndexTime =
        LocalizedText.Get(
            "Home.NotIndexedYet");
    [ObservableProperty] private string _attentionCount = "—";
    [ObservableProperty]
    private string _rootHealth =
        LocalizedText.Get(
            "Home.LoadConfigurationHealth");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;

    public HomeViewModel(
        ILibraryService library,
        IAppSettings settings,
        INavigationService navigation,
        IndexingViewModel indexing,
        IActivityService activities,
        ILocalizationService? localization = null)
    {
        _library = library;
        _settings = settings;
        _navigation = navigation;
        _activities = activities;
        _localization = localization;
        Indexing = indexing;
        HasConfiguration = settings.Configuration is not null;
        RefreshLocalizedText();
        if (_localization is not null)
            _localization.CultureChanged +=
                OnCultureChanged;
        HasRecentActivity = activities.Activities.Count > 0;
        settings.ConfigurationChanged += (_, _) =>
        {
            HasConfiguration = settings.Configuration is not null;
            _ = RefreshAsync();
        };
        activities.Changed += () =>
        {
            HasRecentActivity = activities.Activities.Count > 0;
            OnPropertyChanged(nameof(RecentActivityItems));
        };
        indexing.IndexCompleted += () => _ = RefreshAsync();
    }

    public IndexingViewModel Indexing { get; }
    public ReadOnlyObservableCollection<AppActivity> RecentActivities => _activities.Activities;
    public IReadOnlyList<AppActivity> RecentActivityItems => _activities.Activities.Take(3).ToArray();
    public bool NeedsSetup => !HasConfiguration;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasErrorDetail =>
        !string.IsNullOrWhiteSpace(ErrorDetail);

    private bool CanRefresh() => HasConfiguration && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (!_library.IsReady || IsBusy)
            return;
        IsBusy = true;
        ErrorMessage = null;
        ErrorDetail = null;
        _refreshFailed = false;
        try
        {
            var records = await _library.GetAllRecordsAsync();
            var counts = await Task.Run(() => new
            {
                Tracks = records.Count,
                Albums = records.Select(record => (record.EffectiveAlbumArtist, record.Album ?? ""))
                    .Distinct().Count(),
                Artists = records.Select(record => record.EffectiveAlbumArtist)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            });
            TrackCount = counts.Tracks.ToString("N0");
            AlbumCount = counts.Albums.ToString("N0");
            ArtistCount = counts.Artists.ToString("N0");
            ArtworkCount = (await _library.GetMaterializedArtworkFileCountAsync()).ToString("N0");
            IReadOnlyList<ScanRootHealth> roots = await _library.GetScanRootHealthAsync();
            _lastSuccessfulIndex = roots
                .Where(root => root.LastSuccessUtc is not null)
                .Select(root => root.LastSuccessUtc)
                .Max();
            LastIndexTime = _lastSuccessfulIndex is { } completed
                ? completed.ToLocalTime().ToString("g")
                : Text("Home.NotIndexedYet");
            AttentionCount = roots.Count(root => root.State is
                ScanRootState.Degraded or ScanRootState.Unavailable).ToString("N0");
            _hasNoScanHistory = roots.Count == 0;
            RootHealth = _hasNoScanHistory
                ? Text("Home.NoScanHistory")
                : DescribeRoots(roots);
        }
        catch (Exception error)
        {
            _refreshFailed = true;
            ErrorMessage = Text(
                "Home.RefreshFailed");
            ErrorDetail = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOpenLibrary() => HasConfiguration;

    [RelayCommand(CanExecute = nameof(CanOpenLibrary))]
    private void OpenLibrary() => _navigation.Navigate(ShellDestination.Library);

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(ShellDestination.Settings);

    [RelayCommand]
    private void OpenHealth() => _navigation.Navigate(ShellDestination.Health);

    private string DescribeRoots(
        IReadOnlyList<ScanRootHealth> roots)
    {
        int available = roots.Count(root => root.State == ScanRootState.Healthy);
        int degraded = roots.Count(root => root.State == ScanRootState.Degraded);
        int unavailable = roots.Count(root => root.State == ScanRootState.Unavailable);
        _rootCounts = (
            available,
            degraded,
            unavailable);
        return Format(
            "Home.RootHealthFormat",
            available,
            degraded,
            unavailable);
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private void OnCultureChanged(
        object? sender,
        EventArgs e) =>
        RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        if (_lastSuccessfulIndex is null)
            LastIndexTime = Text(
                "Home.NotIndexedYet");
        if (!HasConfiguration)
            RootHealth = Text(
                "Home.LoadConfigurationHealth");
        else if (_hasNoScanHistory)
            RootHealth = Text(
                "Home.NoScanHistory");
        else if (_rootCounts is { } counts)
            RootHealth = Format(
                "Home.RootHealthFormat",
                counts.Healthy,
                counts.Degraded,
                counts.Unavailable);
        if (_refreshFailed)
            ErrorMessage = Text(
                "Home.RefreshFailed");
    }
}
