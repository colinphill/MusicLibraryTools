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

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _trackCount = "—";
    [ObservableProperty] private string _albumCount = "—";
    [ObservableProperty] private string _artistCount = "—";
    [ObservableProperty] private string _artworkCount = "—";
    [ObservableProperty] private string _rootHealth = "Load a configuration to see library health.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public HomeViewModel(ILibraryService library, IAppSettings settings, INavigationService navigation, IndexingViewModel indexing)
    {
        _library = library;
        _settings = settings;
        _navigation = navigation;
        Indexing = indexing;
        settings.ConfigurationChanged += (_, _) => _ = RefreshAsync();
        indexing.IndexCompleted += () => _ = RefreshAsync();
    }

    public IndexingViewModel Indexing { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_library.IsReady || IsBusy)
            return;
        IsBusy = true;
        ErrorMessage = null;
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
            RootHealth = roots.Count == 0
                ? "No scan history yet. Your cached library remains available offline."
                : DescribeRoots(roots);
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLibrary() => _navigation.Navigate(ShellDestination.Library);

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(ShellDestination.Settings);

    private static string DescribeRoots(IReadOnlyList<ScanRootHealth> roots)
    {
        int available = roots.Count(root => root.State == ScanRootState.Healthy);
        int degraded = roots.Count(root => root.State == ScanRootState.Degraded);
        int unavailable = roots.Count(root => root.State == ScanRootState.Unavailable);
        return $"{available:N0} healthy · {degraded:N0} need attention · {unavailable:N0} offline";
    }
}
