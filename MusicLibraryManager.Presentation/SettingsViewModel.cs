using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public partial class SettingsViewModel : ObservableObject
{
    private const string ThemePreference = "manager.appearance.theme.v1";
    private readonly IAppSettings _settings;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IThemeService _theme;
    private EditableLibraryConfig _editing = new();

    [ObservableProperty] private string? _activeConfigurationPath;
    [ObservableProperty] private string? _selectedRecentConfiguration;
    [ObservableProperty] private string? _editorPath;
    [ObservableProperty] private string _databaseFile = "cache.db";
    [ObservableProperty] private string? _itunesLibraryPath;
    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private int _lengthLimit = 255;
    [ObservableProperty] private int _discNumLengthLimit = 255;
    [ObservableProperty] private string _aacEncoder = "libfdk_aac";
    [ObservableProperty] private int _aacBitrateKbps = 256;
    [ObservableProperty] private bool _deleteSourcesAfterIngest;
    [ObservableProperty] private bool _removeNonMusicAfterIngest;
    [ObservableProperty] private bool _deleteStaleCrossSyncFiles;
    [ObservableProperty] private bool _cleanCrossSyncPlaylists;
    [ObservableProperty] private string _statusMessage = "Choose an existing configuration or create a new one.";
    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private int _selectedTabIndex;

    public SettingsViewModel(
        IAppSettings settings,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IThemeService theme)
    {
        _settings = settings;
        _files = files;
        _dialogs = dialogs;
        _theme = theme;
        _selectedTheme = settings.GetPreference(ThemePreference) ?? "System";
        RecentConfigurations = new ObservableCollection<string>(settings.RecentConfigPaths);
        settings.ConfigurationChanged += (_, _) => RefreshActiveConfiguration();
        RefreshActiveConfiguration();
    }

    public ObservableCollection<string> RecentConfigurations { get; }
    public ObservableCollection<IndexTargetEditorRow> IndexTargets { get; } = [];
    public ObservableCollection<SyncPlaylistEditorRow> SyncPlaylists { get; } = [];
    public ObservableCollection<PlaylistTargetEditorRow> PlaylistTargets { get; } = [];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<LibraryIngestRole> IngestRoles { get; } =
        Enum.GetValues<LibraryIngestRole>();
    public IReadOnlyList<string> PlaylistTypes { get; } = ["m3u", "wpl"];

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.SetPreference(ThemePreference, value);
        _theme.Apply(value);
    }

    [RelayCommand]
    private async Task BrowseConfigurationAsync()
    {
        string? path = await _files.PickFileAsync("Open library configuration",
            [new FilePickerType("Library configuration", [".xml"])]);
        if (path is not null)
            LoadConfiguration(path);
    }

    [RelayCommand]
    private void LoadRecentConfiguration()
    {
        if (!string.IsNullOrWhiteSpace(SelectedRecentConfiguration))
            LoadConfiguration(SelectedRecentConfiguration);
    }

    [RelayCommand]
    private void EditCurrentConfiguration()
    {
        if (ActiveConfigurationPath is not null)
        {
            LoadEditor(ActiveConfigurationPath);
            SelectedTabIndex = 1;
        }
    }

    [RelayCommand]
    private void NewConfiguration()
    {
        _editing = new EditableLibraryConfig();
        EditorPath = null;
        DatabaseFile = "cache.db";
        ItunesLibraryPath = null;
        FfmpegPath = "ffmpeg";
        LengthLimit = 255;
        DiscNumLengthLimit = 255;
        AacEncoder = "libfdk_aac";
        AacBitrateKbps = 256;
        DeleteSourcesAfterIngest = false;
        RemoveNonMusicAfterIngest = false;
        DeleteStaleCrossSyncFiles = false;
        CleanCrossSyncPlaylists = false;
        IndexTargets.Clear();
        IndexTargets.Add(new IndexTargetEditorRow());
        SyncPlaylists.Clear();
        PlaylistTargets.Clear();
        StatusMessage = "New configuration. Add at least one library root, then Save as.";
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    private void AddIndexTarget() => IndexTargets.Add(new IndexTargetEditorRow());

    [RelayCommand]
    private void RemoveIndexTarget(IndexTargetEditorRow? row)
    {
        if (row is not null)
            IndexTargets.Remove(row);
    }

    [RelayCommand]
    private void AddIndexTargetSet(IndexTargetEditorRow? row)
        => row?.Memberships.Add(new IndexTargetSetEditorRow());

    [RelayCommand]
    private void RemoveIndexTargetSet(IndexTargetSetEditorRow? membership)
    {
        if (membership is null)
            return;
        foreach (IndexTargetEditorRow target in IndexTargets)
            if (target.Memberships.Remove(membership))
                return;
    }

    [RelayCommand]
    private void ClearSyncTarget()
    {
        foreach (IndexTargetEditorRow target in IndexTargets)
            target.IsSyncTarget = false;
    }

    [RelayCommand]
    private void AddSyncPlaylist() => SyncPlaylists.Add(new SyncPlaylistEditorRow());

    [RelayCommand]
    private void RemoveSyncPlaylist(SyncPlaylistEditorRow? row)
    {
        if (row is not null)
            SyncPlaylists.Remove(row);
    }

    [RelayCommand]
    private void AddPlaylistTarget() => PlaylistTargets.Add(new PlaylistTargetEditorRow());

    [RelayCommand]
    private void RemovePlaylistTarget(PlaylistTargetEditorRow? row)
    {
        if (row is not null)
            PlaylistTargets.Remove(row);
    }

    [RelayCommand]
    private async Task BrowseIndexTargetAsync(IndexTargetEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFolderAsync("Choose a music library root");
        if (path is not null)
            row.Path = path;
    }

    [RelayCommand]
    private async Task BrowsePlaylistTargetAsync(PlaylistTargetEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFolderAsync("Choose a playlist export folder");
        if (path is not null)
            row.Target = path;
    }

    [RelayCommand]
    private async Task BrowseDatabaseAsync()
    {
        string? path = await _files.SaveFileAsync("Choose metadata cache", "cache.db", ".db");
        if (path is not null)
            DatabaseFile = path;
    }

    [RelayCommand]
    private async Task BrowseItunesLibraryAsync()
    {
        string? path = await _files.PickFileAsync("Choose iTunes library",
            [new FilePickerType("iTunes library", [".itl"])]);
        if (path is not null)
            ItunesLibraryPath = path;
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        string? path = await _files.PickFileAsync("Choose ffmpeg",
            [new FilePickerType("Executable", [".exe"])]);
        if (path is not null)
            FfmpegPath = path;
    }

    [RelayCommand]
    private async Task SaveConfigurationAsync()
        => await SaveEditorAsync(EditorPath);

    [RelayCommand]
    private async Task SaveConfigurationAsAsync()
        => await SaveEditorAsync(null);

    [RelayCommand]
    private void ClearRecentConfigurations()
    {
        _settings.ClearRecentConfigs();
        RefreshRecentConfigurations();
        StatusMessage = "Recent configuration history cleared.";
    }

    private void LoadConfiguration(string path)
    {
        try
        {
            _settings.LoadConfig(path);
            LoadEditor(path);
            SelectedTabIndex = 1;
            StatusMessage = "Configuration loaded. Cached browsing is available while roots are offline.";
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not load configuration: {error.Message}";
        }
    }

    private void LoadEditor(string path)
    {
        try
        {
            _editing = EditableLibraryConfig.Load(path);
            EditorPath = path;
            DatabaseFile = _editing.DatabaseFile;
            ItunesLibraryPath = _editing.ItunesLibraryPath;
            FfmpegPath = _editing.FfmpegPath;
            LengthLimit = _editing.LengthLimit;
            DiscNumLengthLimit = _editing.DiscNumLengthLimit;
            AacEncoder = _editing.AacEncoder;
            AacBitrateKbps = _editing.AacBitrateKbps;
            DeleteSourcesAfterIngest = _editing.DeleteSourcesAfterIngest;
            RemoveNonMusicAfterIngest = _editing.RemoveNonMusicAfterIngest;
            DeleteStaleCrossSyncFiles = _editing.DeleteStaleCrossSyncFiles;
            CleanCrossSyncPlaylists = _editing.CleanCrossSyncPlaylists;
            IndexTargets.Clear();
            foreach (IndexTargetEntry target in _editing.IndexTargets)
            {
                var row = new IndexTargetEditorRow
                {
                    Path = target.Target,
                    Filter = target.Filter,
                    Organize = target.Organize,
                    UseItunesCanonicalNaming = target.UseItunesCanonicalNaming,
                    IngestRole = target.IngestRole,
                    IsSyncTarget = target.IsSyncTarget,
                    Source = target,
                };
                foreach (IGrouping<string?, IndexTargetSetEntry> memberships in target.Memberships
                             .GroupBy(membership => EffectiveOffset(membership, target),
                                 StringComparer.Ordinal))
                    row.Memberships.Add(new IndexTargetSetEditorRow
                    {
                        Name = string.Join(", ", memberships.Select(membership => membership.Name)),
                        Offset = memberships.Key,
                    });
                IndexTargets.Add(row);
            }
            if (IndexTargets.Count == 0)
                IndexTargets.Add(new IndexTargetEditorRow());
            SyncPlaylists.Clear();
            foreach (string playlist in _editing.SyncPlaylists)
                SyncPlaylists.Add(new SyncPlaylistEditorRow { Name = playlist });
            PlaylistTargets.Clear();
            foreach (PlaylistTargetEntry target in _editing.PlaylistTargets)
                PlaylistTargets.Add(new PlaylistTargetEditorRow
                {
                    Target = target.Target,
                    Type = target.Type,
                    Sets = target.Sets.Count == 0 ? null : string.Join(",", target.Sets),
                });
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not edit configuration: {error.Message}";
        }
    }

    private async Task SaveEditorAsync(string? path)
    {
        path ??= await _files.SaveFileAsync("Save library configuration", "library.xml", ".xml");
        if (path is null)
            return;
        if (IndexTargets.All(target => string.IsNullOrWhiteSpace(target.Path)))
        {
            StatusMessage = "Add at least one library root before saving.";
            return;
        }
        try
        {
            _editing.DatabaseFile = string.IsNullOrWhiteSpace(DatabaseFile) ? "cache.db" : DatabaseFile.Trim();
            _editing.ItunesLibraryPath = string.IsNullOrWhiteSpace(ItunesLibraryPath) ? null : ItunesLibraryPath.Trim();
            _editing.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();
            _editing.LengthLimit = LengthLimit;
            _editing.DiscNumLengthLimit = DiscNumLengthLimit;
            _editing.AacEncoder = string.IsNullOrWhiteSpace(AacEncoder)
                ? "libfdk_aac" : AacEncoder.Trim();
            _editing.AacBitrateKbps = AacBitrateKbps;
            _editing.DeleteSourcesAfterIngest = DeleteSourcesAfterIngest;
            _editing.RemoveNonMusicAfterIngest = RemoveNonMusicAfterIngest;
            _editing.DeleteStaleCrossSyncFiles = DeleteStaleCrossSyncFiles;
            _editing.CleanCrossSyncPlaylists = CleanCrossSyncPlaylists;
            _editing.IndexTargets = IndexTargets
                .Where(row => !string.IsNullOrWhiteSpace(row.Path))
                .Select(row =>
                {
                    return new IndexTargetEntry
                    {
                        Target = row.Path.Trim(),
                        Filter = string.IsNullOrWhiteSpace(row.Filter) ? null : row.Filter.Trim(),
                        Organize = row.Organize,
                        UseItunesCanonicalNaming = row.UseItunesCanonicalNaming,
                        IngestRole = row.IngestRole,
                        IsSyncTarget = row.IsSyncTarget,
                        Memberships = row.Memberships
                            .SelectMany(membership => LibraryConfiguration
                                .ParseScanSets(membership.Name)
                                .Select(name => new IndexTargetSetEntry
                                {
                                    Name = name,
                                    Offset = CleanOptional(membership.Offset),
                                })).ToList(),
                    };
                }).ToList();
            _editing.SyncPlaylists = SyncPlaylists
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .Select(row => row.Name.Trim()).ToList();
            _editing.PlaylistTargets = PlaylistTargets
                .Where(row => !string.IsNullOrWhiteSpace(row.Target))
                .Select(row => new PlaylistTargetEntry
                {
                    Target = row.Target.Trim(),
                    Type = string.IsNullOrWhiteSpace(row.Type) ? "m3u" : row.Type.Trim(),
                    Sets = [.. LibraryConfiguration.ParseScanSets(row.Sets)],
                }).ToList();
            _editing.Save(path);
            _settings.LoadConfig(path);
            LoadEditor(path);
            StatusMessage = "Configuration saved and loaded.";
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not save configuration: {error.Message}";
            await _dialogs.ShowMessageAsync("Configuration was not saved", error.Message);
        }
    }

    private void RefreshActiveConfiguration()
    {
        ActiveConfigurationPath = _settings.ConfigPath;
        RefreshRecentConfigurations();
        if (!string.IsNullOrWhiteSpace(ActiveConfigurationPath) &&
            !string.Equals(EditorPath, ActiveConfigurationPath, StringComparison.OrdinalIgnoreCase))
            LoadEditor(ActiveConfigurationPath);
    }

    private void RefreshRecentConfigurations()
    {
        RecentConfigurations.Clear();
        foreach (string path in _settings.RecentConfigPaths)
            RecentConfigurations.Add(path);
    }

    private static string? EffectiveOffset(
        IndexTargetSetEntry membership,
        IndexTargetEntry target) =>
        CleanOptional(membership.Offset) ?? CleanOptional(target.DefaultOffset);

    private static string? CleanOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
