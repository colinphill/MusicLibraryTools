using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public partial class SettingsViewModel : ObservableObject, INavigationGuard
{
    private const string ThemePreference = "manager.appearance.theme.v1";
    private readonly IAppSettings _settings;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IThemeService _theme;
    private EditableLibraryConfig _editing = new();
    private bool _suppressDirty = true;
    private readonly HashSet<INotifyPropertyChanged> _trackedRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCurrentConfigurationCommand))]
    private string? _activeConfigurationPath;
    [ObservableProperty] private string? _selectedRecentConfiguration;
    [ObservableProperty] private string? _editorPath;
    [ObservableProperty] private string _databaseFile = "cache.db";
    [ObservableProperty] private string? _itunesLibraryPath;
    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private int _lengthLimit = 255;
    [ObservableProperty] private int _discNumLengthLimit = 255;
    [ObservableProperty] private string _aacEncoder = "libfdk_aac";
    [ObservableProperty] private int _aacBitrateKbps = 256;
    [ObservableProperty] private int _oversizedArtworkByteThreshold =
        LibraryArtworkHealthSettings.DefaultOversizedByteThreshold;
    [ObservableProperty] private int _oversizedArtworkDimensionThreshold =
        LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold;
    [ObservableProperty] private int _artworkRepairTargetByteSize =
        LibraryArtworkHealthSettings.DefaultRepairTargetByteSize;
    [ObservableProperty] private int _artworkRepairTargetDimension =
        LibraryArtworkHealthSettings.DefaultRepairTargetDimension;
    [ObservableProperty] private bool _deleteSourcesAfterIngest;
    [ObservableProperty] private bool _removeNonMusicAfterIngest;
    [ObservableProperty] private bool _deleteStaleCrossSyncFiles;
    [ObservableProperty] private bool _cleanCrossSyncPlaylists;
    [ObservableProperty] private string _statusMessage = "Choose an existing configuration or create a new one.";
    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private ThemeChoice? _selectedThemeChoice;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _validationTabIndex = 1;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveConfigurationAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardChangesCommand))]
    private bool _hasUnsavedChanges;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string? _validationSummary;

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
        string? storedTheme = settings.GetPreference(ThemePreference);
        ThemeChoice? storedChoice = ThemeChoices.FirstOrDefault(choice => choice.Name == storedTheme);
        _selectedThemeChoice = storedChoice ?? ThemeChoices[0];
        _selectedTheme = _selectedThemeChoice.Name;
        if (storedTheme is not null && storedChoice is null)
            settings.SetPreference(ThemePreference, _selectedTheme);
        RecentConfigurations = new ObservableCollection<string>(settings.RecentConfigPaths);
        PropertyChanged += OnOwnPropertyChanged;
        IndexTargets.CollectionChanged += OnTrackedCollectionChanged;
        SyncPlaylists.CollectionChanged += OnTrackedCollectionChanged;
        PlaylistTargets.CollectionChanged += OnTrackedCollectionChanged;
        settings.ConfigurationChanged += (_, _) => RefreshActiveConfiguration();
        RefreshActiveConfiguration();
        TrackRows(IndexTargets);
        TrackRows(SyncPlaylists);
        TrackRows(PlaylistTargets);
        _suppressDirty = false;
        HasUnsavedChanges = false;
    }

    public ObservableCollection<string> RecentConfigurations { get; }
    public ObservableCollection<IndexTargetEditorRow> IndexTargets { get; } = [];
    public ObservableCollection<SyncPlaylistEditorRow> SyncPlaylists { get; } = [];
    public ObservableCollection<PlaylistTargetEditorRow> PlaylistTargets { get; } = [];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark", "Steel Blue"];
    public IReadOnlyList<ThemeChoice> ThemeChoices { get; } =
    [
        new("System", "#0D1417", "#F8FBFA", "#2CC7BC"),
        new("Light", "#EEF4F3", "#FFFFFF", "#087F8C"),
        new("Dark", "#0D1417", "#18262B", "#2CC7BC"),
        new("Steel Blue", "#101C2A", "#1D3043", "#3AAFB8"),
    ];
    public IReadOnlyList<LibraryIngestRole> IngestRoles { get; } =
        Enum.GetValues<LibraryIngestRole>();
    public IReadOnlyList<string> PlaylistTypes { get; } = ["m3u", "wpl"];
    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationSummary);
    public bool IsEditorValid => ValidationIssues().Count == 0;
    public decimal OversizedArtworkSizeThresholdMib
    {
        get => (decimal)OversizedArtworkByteThreshold / (1024 * 1024);
        set => OversizedArtworkByteThreshold = checked((int)decimal.Round(
            value * (1024 * 1024), MidpointRounding.AwayFromZero));
    }

    partial void OnOversizedArtworkByteThresholdChanged(int value) =>
        OnPropertyChanged(nameof(OversizedArtworkSizeThresholdMib));

    public decimal ArtworkRepairTargetSizeMib
    {
        get => (decimal)ArtworkRepairTargetByteSize / (1024 * 1024);
        set => ArtworkRepairTargetByteSize = checked((int)decimal.Round(
            value * (1024 * 1024), MidpointRounding.AwayFromZero));
    }

    partial void OnArtworkRepairTargetByteSizeChanged(int value) =>
        OnPropertyChanged(nameof(ArtworkRepairTargetSizeMib));

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.SetPreference(ThemePreference, value);
        _theme.Apply(value);
        ThemeChoice? choice = ThemeChoices.FirstOrDefault(item => item.Name == value);
        if (choice is not null && SelectedThemeChoice != choice)
            SelectedThemeChoice = choice;
    }

    partial void OnSelectedThemeChoiceChanged(ThemeChoice? value)
    {
        if (value is not null && SelectedTheme != value.Name)
            SelectedTheme = value.Name;
    }

    private static readonly HashSet<string> EditorProperties =
    [
        nameof(DatabaseFile),
        nameof(ItunesLibraryPath),
        nameof(FfmpegPath),
        nameof(LengthLimit),
        nameof(DiscNumLengthLimit),
        nameof(AacEncoder),
        nameof(AacBitrateKbps),
        nameof(OversizedArtworkByteThreshold),
        nameof(OversizedArtworkDimensionThreshold),
        nameof(ArtworkRepairTargetByteSize),
        nameof(ArtworkRepairTargetDimension),
        nameof(DeleteSourcesAfterIngest),
        nameof(RemoveNonMusicAfterIngest),
        nameof(DeleteStaleCrossSyncFiles),
        nameof(CleanCrossSyncPlaylists),
    ];

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && EditorProperties.Contains(name))
            MarkDirty();
    }

    private void OnTrackedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (object item in e.OldItems)
                UntrackRow(item);
        if (e.NewItems is not null)
            foreach (object item in e.NewItems)
                TrackRow(item);
        MarkDirty();
    }

    private void TrackRows(System.Collections.IEnumerable rows)
    {
        foreach (object row in rows)
            TrackRow(row);
    }

    private void TrackRow(object row)
    {
        if (row is not INotifyPropertyChanged changed || !_trackedRows.Add(changed))
            return;
        changed.PropertyChanged += OnTrackedRowChanged;
        if (row is IndexTargetEditorRow target)
        {
            target.Memberships.CollectionChanged += OnTrackedCollectionChanged;
            TrackRows(target.Memberships);
        }
    }

    private void UntrackRow(object row)
    {
        if (row is INotifyPropertyChanged changed && _trackedRows.Remove(changed))
            changed.PropertyChanged -= OnTrackedRowChanged;
        if (row is IndexTargetEditorRow target)
        {
            target.Memberships.CollectionChanged -= OnTrackedCollectionChanged;
            foreach (IndexTargetSetEditorRow membership in target.Memberships)
                UntrackRow(membership);
        }
    }

    private void ClearEditorCollections()
    {
        foreach (IndexTargetEditorRow row in IndexTargets.ToArray())
            UntrackRow(row);
        foreach (SyncPlaylistEditorRow row in SyncPlaylists.ToArray())
            UntrackRow(row);
        foreach (PlaylistTargetEditorRow row in PlaylistTargets.ToArray())
            UntrackRow(row);
        IndexTargets.Clear();
        SyncPlaylists.Clear();
        PlaylistTargets.Clear();
    }

    private void OnTrackedRowChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirty)
            return;
        HasUnsavedChanges = true;
        UpdateValidation();
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        SaveConfigurationAsCommand.NotifyCanExecuteChanged();
    }

    private void UpdateValidation()
    {
        IReadOnlyList<(int Tab, string Message)> issues = ValidationIssues();
        ValidationTabIndex = issues.FirstOrDefault().Tab;
        ValidationSummary = issues.Count == 0
            ? null
            : "Fix the following before saving:" + Environment.NewLine +
              string.Join(Environment.NewLine, issues.Select(issue => $"• {issue.Message}"));
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        SaveConfigurationAsCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<(int Tab, string Message)> ValidationIssues()
    {
        var issues = new List<(int, string)>();
        if (!IndexTargets.Any(target => !string.IsNullOrWhiteSpace(target.Path)))
            issues.Add((1, "Add at least one library root."));
        if (LengthLimit <= 0)
            issues.Add((3, "Path length limit must be greater than zero."));
        if (DiscNumLengthLimit <= 0)
            issues.Add((3, "Disc number length limit must be greater than zero."));
        if (AacBitrateKbps <= 0)
            issues.Add((3, "AAC bitrate must be greater than zero."));
        if (OversizedArtworkByteThreshold is < 262_144 or > 1_073_741_824)
            issues.Add((4, "Oversized artwork size threshold must be between 0.25 and 1,024 MiB."));
        if (OversizedArtworkDimensionThreshold is < 64 or > 100_000)
            issues.Add((4, "Oversized artwork dimension threshold must be between 64 and 100,000 pixels."));
        if (ArtworkRepairTargetByteSize is < 65_536 or > 1_073_741_824)
            issues.Add((4, "Artwork repair size target must be between 0.0625 and 1,024 MiB."));
        if (ArtworkRepairTargetDimension is < 64 or > 100_000)
            issues.Add((4, "Artwork repair dimension target must be between 64 and 100,000 pixels."));
        return issues;
    }

    [RelayCommand]
    private void OpenValidation() => SelectedTabIndex = ValidationTabIndex;

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!HasUnsavedChanges)
            return true;
        return await _dialogs.ConfirmAsync(
            "Discard unsaved configuration changes?",
            "The library configuration has changes that have not been saved.",
            "Discard changes");
    }

    public async Task<bool> ConfirmNavigationAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
            return false;
        DiscardEditorChanges();
        return true;
    }

    private void DiscardEditorChanges()
    {
        if (!HasUnsavedChanges)
            return;
        if (!string.IsNullOrWhiteSpace(ActiveConfigurationPath))
        {
            LoadEditor(ActiveConfigurationPath);
            StatusMessage = "Unsaved configuration changes were discarded.";
            return;
        }

        _suppressDirty = true;
        ClearEditorCollections();
        EditorPath = null;
        DatabaseFile = "cache.db";
        ItunesLibraryPath = null;
        FfmpegPath = "ffmpeg";
        LengthLimit = 255;
        DiscNumLengthLimit = 255;
        AacEncoder = "libfdk_aac";
        AacBitrateKbps = 256;
        OversizedArtworkByteThreshold =
            LibraryArtworkHealthSettings.DefaultOversizedByteThreshold;
        OversizedArtworkDimensionThreshold =
            LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold;
        ArtworkRepairTargetByteSize =
            LibraryArtworkHealthSettings.DefaultRepairTargetByteSize;
        ArtworkRepairTargetDimension =
            LibraryArtworkHealthSettings.DefaultRepairTargetDimension;
        DeleteSourcesAfterIngest = false;
        RemoveNonMusicAfterIngest = false;
        DeleteStaleCrossSyncFiles = false;
        CleanCrossSyncPlaylists = false;
        _suppressDirty = false;
        HasUnsavedChanges = false;
        ValidationSummary = null;
        StatusMessage = "Unsaved configuration changes were discarded.";
    }

    private bool CanDiscardChanges() => HasUnsavedChanges;

    [RelayCommand(CanExecute = nameof(CanDiscardChanges))]
    private async Task DiscardChangesAsync()
    {
        if (await ConfirmDiscardChangesAsync())
            DiscardEditorChanges();
    }

    [RelayCommand]
    private async Task BrowseConfigurationAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
            return;
        string? path = await _files.PickFileAsync("Open library configuration",
            [new FilePickerType("Library configuration", [".xml"])]);
        if (path is not null)
            LoadConfiguration(path);
    }

    [RelayCommand]
    private async Task LoadRecentConfigurationAsync()
    {
        if (!string.IsNullOrWhiteSpace(SelectedRecentConfiguration) &&
            await ConfirmDiscardChangesAsync())
            LoadConfiguration(SelectedRecentConfiguration);
    }

    private bool CanEditCurrentConfiguration() => ActiveConfigurationPath is not null;

    [RelayCommand(CanExecute = nameof(CanEditCurrentConfiguration))]
    private async Task EditCurrentConfigurationAsync()
    {
        if (ActiveConfigurationPath is not null &&
            (!HasUnsavedChanges || await ConfirmDiscardChangesAsync()))
        {
            LoadEditor(ActiveConfigurationPath);
            SelectedTabIndex = 1;
        }
    }

    [RelayCommand]
    private async Task NewConfigurationAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
            return;
        _suppressDirty = true;
        _editing = new EditableLibraryConfig();
        EditorPath = null;
        DatabaseFile = "cache.db";
        ItunesLibraryPath = null;
        FfmpegPath = "ffmpeg";
        LengthLimit = 255;
        DiscNumLengthLimit = 255;
        AacEncoder = "libfdk_aac";
        AacBitrateKbps = 256;
        OversizedArtworkByteThreshold =
            LibraryArtworkHealthSettings.DefaultOversizedByteThreshold;
        OversizedArtworkDimensionThreshold =
            LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold;
        ArtworkRepairTargetByteSize =
            LibraryArtworkHealthSettings.DefaultRepairTargetByteSize;
        ArtworkRepairTargetDimension =
            LibraryArtworkHealthSettings.DefaultRepairTargetDimension;
        DeleteSourcesAfterIngest = false;
        RemoveNonMusicAfterIngest = false;
        DeleteStaleCrossSyncFiles = false;
        CleanCrossSyncPlaylists = false;
        ClearEditorCollections();
        IndexTargets.Add(new IndexTargetEditorRow());
        StatusMessage = "New configuration. Add at least one library root, then Save as.";
        SelectedTabIndex = 1;
        _suppressDirty = false;
        HasUnsavedChanges = true;
        UpdateValidation();
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        SaveConfigurationAsCommand.NotifyCanExecuteChanged();
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

    private bool CanSaveConfiguration() => HasUnsavedChanges && IsEditorValid;

    [RelayCommand(CanExecute = nameof(CanSaveConfiguration))]
    private async Task SaveConfigurationAsync()
        => await SaveEditorAsync(EditorPath);

    [RelayCommand(CanExecute = nameof(CanSaveConfiguration))]
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
        bool previousSuppression = _suppressDirty;
        _suppressDirty = true;
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
            OversizedArtworkByteThreshold = _editing.OversizedArtworkByteThreshold;
            OversizedArtworkDimensionThreshold = _editing.OversizedArtworkDimensionThreshold;
            ArtworkRepairTargetByteSize = _editing.ArtworkRepairTargetByteSize;
            ArtworkRepairTargetDimension = _editing.ArtworkRepairTargetDimension;
            DeleteSourcesAfterIngest = _editing.DeleteSourcesAfterIngest;
            RemoveNonMusicAfterIngest = _editing.RemoveNonMusicAfterIngest;
            DeleteStaleCrossSyncFiles = _editing.DeleteStaleCrossSyncFiles;
            CleanCrossSyncPlaylists = _editing.CleanCrossSyncPlaylists;
            ClearEditorCollections();
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
            foreach (string playlist in _editing.SyncPlaylists)
                SyncPlaylists.Add(new SyncPlaylistEditorRow { Name = playlist });
            foreach (PlaylistTargetEntry target in _editing.PlaylistTargets)
                PlaylistTargets.Add(new PlaylistTargetEditorRow
                {
                    Target = target.Target,
                    Type = target.Type,
                    Sets = target.Sets.Count == 0 ? null : string.Join(",", target.Sets),
                });
            HasUnsavedChanges = false;
            ValidationSummary = null;
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not edit configuration: {error.Message}";
        }
        finally
        {
            _suppressDirty = previousSuppression;
            SaveConfigurationCommand.NotifyCanExecuteChanged();
            SaveConfigurationAsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SaveEditorAsync(string? path)
    {
        if (!IsEditorValid)
        {
            UpdateValidation();
            StatusMessage = "Resolve the validation issues before saving.";
            SelectedTabIndex = ValidationTabIndex;
            return;
        }
        path ??= await _files.SaveFileAsync("Save library configuration", "library.xml", ".xml");
        if (path is null)
            return;
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
            _editing.OversizedArtworkByteThreshold = OversizedArtworkByteThreshold;
            _editing.OversizedArtworkDimensionThreshold = OversizedArtworkDimensionThreshold;
            _editing.ArtworkRepairTargetByteSize = ArtworkRepairTargetByteSize;
            _editing.ArtworkRepairTargetDimension = ArtworkRepairTargetDimension;
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
            HasUnsavedChanges = false;
            ValidationSummary = null;
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
        {
            if (HasUnsavedChanges)
            {
                StatusMessage = "The active configuration changed, but your unsaved editor changes were retained. Save them as a separate file or discard them before editing the active configuration.";
                return;
            }
            LoadEditor(ActiveConfigurationPath);
        }
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

public sealed record ThemeChoice(string Name, string Canvas, string Raised, string Accent);
