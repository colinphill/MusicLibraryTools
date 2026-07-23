using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
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
    private readonly ISecretStore _secrets;
    private readonly IMetadataFieldMappingService _fieldMappings;
    private EditableLibraryConfig _editing = EditableLibraryConfig.CreateNew();
    private bool _suppressDirty = true;
    private bool _refreshingSyncTargetChoices;
    private readonly HashSet<INotifyPropertyChanged> _trackedRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCurrentConfigurationCommand))]
    private string? _activeConfigurationPath;
    [ObservableProperty] private string? _selectedRecentConfiguration;
    [ObservableProperty] private string? _editorPath;
    [ObservableProperty] private string? _machineBindingsFile;
    [ObservableProperty] private string _databaseFile = "cache.db";
    [ObservableProperty] private string? _itunesLibraryPath;
    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private string _wavpackPath = "wavpack";
    [ObservableProperty] private string _fpcalcPath = "fpcalc";
    [ObservableProperty] private string? _acoustIdClientKey;
    [ObservableProperty] private bool _offlineMode;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDiscogsTokenCommand))]
    private string? _discogsToken;
    [ObservableProperty]
    private string _discogsCredentialStatus =
        "Checking secure credential storage...";
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
    [ObservableProperty] private LibraryProfile? _selectedLibraryProfile;
    [ObservableProperty] private LibraryProfileEditorRow? _advancedProfile;
    [ObservableProperty] private LibraryIngestProfile? _selectedIngestProfile;
    [ObservableProperty] private IngestProfileEditorRow? _advancedIngestProfile;
    [ObservableProperty] private SettingsRootChoice? _selectedSyncTargetRoot;
    [ObservableProperty] private string _statusMessage = "Choose an existing configuration or create a new one.";
    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private ThemeChoice? _selectedThemeChoice;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isGuidedSetupActive;
    [ObservableProperty]
    private string _fieldMappingStatus =
        "Mappings are personal application settings and apply to Workbench and Library edits.";
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
        IThemeService theme,
        ISecretStore? secrets = null,
        IMetadataFieldMappingService? fieldMappings = null)
    {
        _settings = settings;
        _files = files;
        _dialogs = dialogs;
        _theme = theme;
        _secrets = secrets ?? new SessionSecretStore();
        _fieldMappings = fieldMappings ??
            new MetadataFieldMappingService(settings, MediaFormatRegistry.Default);
        _fpcalcPath =
            settings.GetPreference(AudioFingerprintService.ExecutablePreferenceKey) ??
            "fpcalc";
        _acoustIdClientKey =
            settings.GetPreference(AcoustIdLookupService.ClientKeyPreference);
        _offlineMode = bool.TryParse(
            settings.GetPreference(
                ProviderNetworkPolicy.OfflinePreferenceKey),
            out bool offline) && offline;
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
        PlaylistSources.CollectionChanged += OnTrackedCollectionChanged;
        PlaylistTargets.CollectionChanged += OnTrackedCollectionChanged;
        ExportProfiles.CollectionChanged += OnTrackedCollectionChanged;
        foreach (MetadataFieldMapping mapping in _fieldMappings.Load())
            FieldMappings.Add(MetadataFieldMappingEditorRow.From(mapping));
        settings.ConfigurationChanged += (_, _) => RefreshActiveConfiguration();
        RefreshProfileChoices();
        RefreshIngestProfileChoices();
        RefreshActiveConfiguration();
        TrackRows(IndexTargets);
        TrackRows(SyncPlaylists);
        TrackRows(PlaylistSources);
        TrackRows(PlaylistTargets);
        TrackRows(ExportProfiles);
        _suppressDirty = false;
        HasUnsavedChanges = false;
        _ = RefreshDiscogsCredentialStatusAsync();
    }

    public ObservableCollection<string> RecentConfigurations { get; }
    public ObservableCollection<IndexTargetEditorRow> IndexTargets { get; } = [];
    public ObservableCollection<SyncPlaylistEditorRow> SyncPlaylists { get; } = [];
    public ObservableCollection<PlaylistSourceEditorRow> PlaylistSources { get; } = [];
    public ObservableCollection<PlaylistTargetEditorRow> PlaylistTargets { get; } = [];
    public ObservableCollection<ExportProfileEditorRow> ExportProfiles { get; } = [];
    public ObservableCollection<MetadataFieldMappingEditorRow> FieldMappings { get; } = [];
    public ObservableCollection<LibraryProfile> LibraryProfiles { get; } = [];
    public ObservableCollection<LibraryIngestProfile> IngestProfiles { get; } = [];
    public ObservableCollection<SettingsRootChoice> SyncTargetRootChoices { get; } = [];
    public bool CanDeleteSelectedProfile =>
        SelectedLibraryProfile?.Preset == LibraryProfilePreset.Custom;
    public bool CanDeleteSelectedIngestProfile =>
        SelectedIngestProfile is not null && IngestProfiles.Count > 1 &&
        LibraryIngestProfilePresets.All.All(profile => !string.Equals(
            profile.Id, SelectedIngestProfile.Id, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark", "Steel Blue"];
    public IReadOnlyList<MediaFormatFamily> MetadataFormatFamilies { get; } =
        Enum.GetValues<MediaFormatFamily>();
    public IReadOnlyList<TagFields> MetadataCanonicalFields { get; } =
        Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .ToArray();
    public IReadOnlyList<ThemeChoice> ThemeChoices { get; } =
    [
        new("System", "#0D1417", "#F8FBFA", "#2CC7BC"),
        new("Light", "#EEF4F3", "#FFFFFF", "#087F8C"),
        new("Dark", "#0D1417", "#18262B", "#2CC7BC"),
        new("Steel Blue", "#101C2A", "#1D3043", "#3AAFB8"),
    ];
    public IReadOnlyList<LibraryPathCollisionPolicy> CollisionPolicies =>
        SettingsChoiceLists.CollisionPolicies;
    public IReadOnlyList<LibraryUnicodeNormalization> UnicodeNormalizations =>
        SettingsChoiceLists.UnicodeNormalizations;
    public IReadOnlyList<LibraryDiscStrategy> DiscStrategies =>
        SettingsChoiceLists.DiscStrategies;
    public IReadOnlyList<LibraryTrackTotalScope> TrackTotalScopes =>
        SettingsChoiceLists.TrackTotalScopes;
    public IReadOnlyList<LibraryHealthSeverity> HealthSeverities =>
        SettingsChoiceLists.HealthSeverities;
    public IReadOnlyList<LibrarySourceDisposition> SourceDispositions =>
        SettingsChoiceLists.SourceDispositions;
    public IReadOnlyList<LibraryIngestAction> IngestActions => SettingsChoiceLists.IngestActions;
    public IReadOnlyList<LibraryArtworkStorage> ArtworkStorageChoices =>
        SettingsChoiceLists.ArtworkStorageChoices;
    public IReadOnlyList<LibraryArtworkRoleSelection> ArtworkRoleChoices =>
        SettingsChoiceLists.ArtworkRoleChoices;
    public IReadOnlyList<LibraryArtworkEncoding> ArtworkEncodingChoices =>
        SettingsChoiceLists.ArtworkEncodingChoices;
    public IReadOnlyList<LibrarySidecarDisposition> SidecarDispositions =>
        SettingsChoiceLists.SidecarDispositions;
    public IReadOnlyList<string> PlaylistTypes => SettingsChoiceLists.PlaylistTypes;
    public IReadOnlyList<string> PlaylistSourceTypes => SettingsChoiceLists.PlaylistSourceTypes;
    public IReadOnlyList<string> PlaylistPathStyles => SettingsChoiceLists.PlaylistPathStyles;
    public IReadOnlyList<string> PlaylistEncodings => SettingsChoiceLists.PlaylistEncodings;
    public IReadOnlyList<string> PlaylistLineEndings => SettingsChoiceLists.PlaylistLineEndings;
    public IReadOnlyList<string> PlaylistFileNameTransforms =>
        SettingsChoiceLists.PlaylistFileNameTransforms;
    public IReadOnlyList<ExportSelectionKind> ExportSelectionKinds =>
        SettingsChoiceLists.ExportSelectionKinds;
    public IReadOnlyList<ExportTransformMode> ExportTransformModes =>
        SettingsChoiceLists.ExportTransformModes;
    public IReadOnlyList<ExportArtworkMode> ExportArtworkModes =>
        SettingsChoiceLists.ExportArtworkModes;
    public IReadOnlyList<ExportExtraFileDisposition> ExportExtraFileDispositions =>
        SettingsChoiceLists.ExportExtraFileDispositions;
    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationSummary);
    public bool IsEditorValid => ValidationIssues().Count == 0;
    public string EffectivePolicySummary
    {
        get
        {
            LibraryProfile? profile = AdvancedProfile?.Build() ?? SelectedLibraryProfile;
            if (profile is null)
                return "No root policy profile is selected.";

            string permissions = FormatPermissions(profile.DefaultRootPermissions);
            string naming = profile.Naming.UseItunesCanonicalNaming
                ? "iTunes-compatible paths"
                : profile.Preset == LibraryProfilePreset.CatalogOnly
                    ? "existing paths are preserved"
                    : $"{profile.Naming.DirectoryTemplate}/{profile.Naming.FileNameTemplate}";
            LibraryIngestProfile? ingestProfile = AdvancedIngestProfile?.Build() ??
                SelectedIngestProfile;
            string ingest = ingestProfile?.Ingest.Enabled == true
                ? $"enabled; sources {ingestProfile.Ingest.SourceDisposition.ToString().ToLowerInvariant()}"
                : "disabled; sources preserved";
            int writableRoots = IndexTargets.Count(root => !root.IsReadOnly);
            int profileOverrides = IndexTargets.Count(root => !string.Equals(
                root.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
            return $"Root policy being edited: {profile.Name}{Environment.NewLine}" +
                   $"New-root permissions: {permissions}{Environment.NewLine}" +
                   $"Naming: {naming}{Environment.NewLine}" +
                   $"Discs: {FormatWords(profile.Disc.Strategy.ToString())}{Environment.NewLine}" +
                   $"Ingest profile: {ingestProfile?.Name ?? "none"}; {ingest}{Environment.NewLine}" +
                   $"Configured roots: {IndexTargets.Count}; roots permitting changes: {writableRoots}; " +
                   $"root policy overrides: {profileOverrides}";
        }
    }

    public string EffectivePolicyDetails
    {
        get
        {
            LibraryProfile? profile = AdvancedProfile?.Build() ?? SelectedLibraryProfile;
            if (profile is null)
                return "No active policy is available.";

            string formats = string.Join(", ", MediaFormatRegistry.Default
                .GetExtensions(MediaFormatCapabilities.LibraryIndex));
            string roots = IndexTargets.Count == 0
                ? "  No roots configured."
                : string.Join(Environment.NewLine, IndexTargets.Select(root =>
                    $"  {(string.IsNullOrWhiteSpace(root.Path) ? "(path not set)" : root.Path)} - {FormatPermissions(root.Permissions)}" +
                    (Directory.Exists(root.Path) ? "" : " (offline warning)") +
                    $"; formats: {(string.IsNullOrWhiteSpace(root.IndexFormats) ? "all recognized" : root.IndexFormats)}" +
                    $"; include: {(string.IsNullOrWhiteSpace(root.IndexIncludePatterns) ? "all" : root.IndexIncludePatterns)}" +
                    $"; exclude: {(string.IsNullOrWhiteSpace(root.IndexExcludePatterns) ? "none" : root.IndexExcludePatterns)}"));
            string example;
            try
            {
                string exampleRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!,
                    "Music");
                var metadata = new LibraryPathMetadata(
                    "Example Artist", "Example Artist", "Example Album", "Example Song",
                    3, 1, false, "2026", "03 Example Song", ".flac")
                {
                    Genre = "Rock",
                };
                example = Path.GetRelativePath(exampleRoot,
                    LibraryPathLayoutResolver.Shared.Resolve(exampleRoot, profile, metadata,
                        _editing.LengthLimit, _editing.DiscNumLengthLimit));
            }
            catch (Exception ex)
            {
                example = $"Unavailable: {ex.Message}";
            }

            LibraryHealthRulePolicy[] enabledRules = profile.Health.Rules
                .Where(rule => rule.Enabled).ToArray();
            string rules = enabledRules.Length == 0
                ? "none"
                : string.Join(", ", enabledRules.Select(rule =>
                    $"{rule.Id} ({rule.Severity.ToString().ToLowerInvariant()}" +
                    (rule.ApplyRepair ? ", apply" : rule.ProposeRepair ? ", propose" : "") + ")"));
            LibraryIngestProfile? ingestProfile = AdvancedIngestProfile?.Build() ??
                SelectedIngestProfile;
            LibraryIngestRecipe[] recipes = (ingestProfile?.Ingest.Recipes ?? [])
                .Where(recipe => recipe.Enabled).ToArray();
            string ingest = ingestProfile?.Ingest.Enabled != true
                ? "disabled"
                : recipes.Length == 0
                    ? "enabled, but no output recipes"
                    : string.Join(", ", recipes.Select(recipe =>
                        $"{recipe.Name}: {recipe.Action} to " +
                        (recipe.DestinationRootId is { } rootId
                            ? IndexTargets.FirstOrDefault(root => root.Id == rootId)?.Path ??
                              "missing root"
                            : "no direct root")));
            string integrations = string.IsNullOrWhiteSpace(ItunesLibraryPath)
                ? "File playlists (M3U/M3U8); no media catalog integration configured"
                : $"File playlists (M3U/M3U8); iTunes catalog: {ItunesLibraryPath}";
            string exports = ExportProfiles.Count == 0
                ? "none configured"
                : string.Join(", ", ExportProfiles.Select(item =>
                    $"{item.Name} ({(item.Enabled ? "enabled" : "disabled")})"));

            return $"Profile ID: {profile.Id}; preset: {FormatWords(profile.Preset.ToString())}{Environment.NewLine}" +
                   $"Machine bindings: {(string.IsNullOrWhiteSpace(MachineBindingsFile) ? "inline paths" : MachineBindingsFile)}{Environment.NewLine}" +
                   $"Tools: FFmpeg {FfmpegPath}; WavPack {WavpackPath}{Environment.NewLine}" +
                   $"Recognized index formats: {formats}{Environment.NewLine}" +
                   $"Example destination: {example}{Environment.NewLine}" +
                   $"Collision behavior: {profile.Naming.CollisionPolicy}; Unicode preserved: {profile.Naming.PreserveUnicode}{Environment.NewLine}" +
                   $"Naming limits: components {profile.Naming.ComponentLengthLimit?.ToString() ?? "application default"}; disc albums {profile.Naming.DiscAlbumLengthLimit?.ToString() ?? "component limit"}; complete path {profile.Naming.CompletePathLengthLimit?.ToString() ?? "platform default"}{Environment.NewLine}" +
                   $"Disc identity: {FormatWords(profile.Disc.Strategy.ToString())}; disc tags: {(profile.Disc.Strategy == LibraryDiscStrategy.PreserveTags ? "preserved" : "removed")}{Environment.NewLine}" +
                   $"Metadata fidelity: ReplayGain {OnOff(profile.Metadata.PreserveReplayGain)}, MusicBrainz IDs {OnOff(profile.Metadata.PreserveMusicBrainzIdentifiers)}, custom fields {OnOff(profile.Metadata.PreserveCustomFields)}, compilation {OnOff(profile.Metadata.PreserveCompilationSemantics)}{Environment.NewLine}" +
                   $"Quality band: high resolution at {profile.Quality.HighResolutionMinimumSampleRateHz:N0} Hz or {profile.Quality.HighResolutionMinimumBitsPerSample}-bit{Environment.NewLine}" +
                   $"Enabled health rules: {rules}{Environment.NewLine}" +
                   $"Artwork: {profile.Artwork.Storage}, {FormatWords(profile.Artwork.Roles.ToString())}, {profile.Artwork.Encoding}{Environment.NewLine}" +
                   $"Unknown sidecars: {profile.Sidecars.UnknownFileDisposition}{Environment.NewLine}" +
                   $"Ingest profile: {ingestProfile?.Name ?? "none"}; recipes: {ingest}; " +
                   $"source disposition: {ingestProfile?.Ingest.SourceDisposition.ToString() ?? "Preserve"}{Environment.NewLine}" +
                   $"Integrations: {integrations}{Environment.NewLine}" +
                   $"Export profiles: {exports}{Environment.NewLine}" +
                   $"Root permissions:{Environment.NewLine}{roots}";
        }
    }
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

    partial void OnSelectedLibraryProfileChanged(
        LibraryProfile? oldValue,
        LibraryProfile? newValue)
    {
        if (!_suppressDirty && oldValue is not null)
            CommitAdvancedProfile(updateProfileChoices: true);
        if (newValue is not null)
            _editing.ActiveProfileId = newValue.Id;
        SetAdvancedProfile(newValue);
        DeleteLibraryProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedProfile));
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
        if (!_suppressDirty && newValue is not null)
            MarkDirty();
    }

    partial void OnSelectedIngestProfileChanged(
        LibraryIngestProfile? oldValue,
        LibraryIngestProfile? newValue)
    {
        if (!_suppressDirty && oldValue is not null)
            CommitAdvancedIngestProfile(updateProfileChoices: true);
        if (newValue is not null)
            _editing.ActiveIngestProfileId = newValue.Id;
        SetAdvancedIngestProfile(newValue);
        DeleteIngestProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedIngestProfile));
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
        if (!_suppressDirty && newValue is not null)
            MarkDirty();
    }

    partial void OnSelectedSyncTargetRootChanged(SettingsRootChoice? value)
    {
        if (_refreshingSyncTargetChoices || value is null)
            return;
        _refreshingSyncTargetChoices = true;
        try
        {
            foreach (IndexTargetEditorRow target in IndexTargets)
                target.IsSyncTarget = value.Id is { } selectedId && target.Id == selectedId;
        }
        finally
        {
            _refreshingSyncTargetChoices = false;
        }
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
        MarkDirty();
    }

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

    partial void OnFpcalcPathChanged(string value) =>
        _settings.SetPreference(
            AudioFingerprintService.ExecutablePreferenceKey,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnAcoustIdClientKeyChanged(string? value) =>
        _settings.SetPreference(
            AcoustIdLookupService.ClientKeyPreference,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnOfflineModeChanged(bool value) =>
        _settings.SetPreference(
            ProviderNetworkPolicy.OfflinePreferenceKey,
            value ? bool.TrueString : null);

    private bool CanSaveDiscogsToken() =>
        !string.IsNullOrWhiteSpace(DiscogsToken);

    [RelayCommand(CanExecute = nameof(CanSaveDiscogsToken))]
    private async Task SaveDiscogsTokenAsync()
    {
        string token = DiscogsToken?.Trim() ??
            throw new InvalidOperationException(
                "Enter a Discogs personal access token.");
        try
        {
            await _secrets.WriteAsync(
                DiscogsMetadataProvider.TokenSecretKey,
                token);
            DiscogsToken = null;
            DiscogsCredentialStatus = _secrets.IsPersistent
                ? $"Discogs token stored in {_secrets.Kind}."
                : "Discogs token stored for this application session only.";
        }
        catch (Exception error)
        {
            DiscogsCredentialStatus =
                $"Could not store the Discogs token: {error.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearDiscogsTokenAsync()
    {
        try
        {
            await _secrets.DeleteAsync(
                DiscogsMetadataProvider.TokenSecretKey);
            DiscogsToken = null;
            DiscogsCredentialStatus = "No Discogs token is stored.";
        }
        catch (Exception error)
        {
            DiscogsCredentialStatus =
                $"Could not clear the Discogs token: {error.Message}";
        }
    }

    private async Task RefreshDiscogsCredentialStatusAsync()
    {
        try
        {
            string? token = await _secrets.ReadAsync(
                DiscogsMetadataProvider.TokenSecretKey);
            DiscogsCredentialStatus = string.IsNullOrWhiteSpace(token)
                ? "No Discogs token is stored."
                : _secrets.IsPersistent
                    ? $"A Discogs token is stored in {_secrets.Kind}."
                    : "A Discogs token is stored for this application session only.";
        }
        catch (Exception error)
        {
            DiscogsCredentialStatus =
                $"Secure credential storage is unavailable: {error.Message}";
        }
    }

    private static readonly HashSet<string> EditorProperties =
    [
        nameof(DatabaseFile),
        nameof(MachineBindingsFile),
        nameof(ItunesLibraryPath),
        nameof(FfmpegPath),
        nameof(WavpackPath),
        nameof(OversizedArtworkByteThreshold),
        nameof(OversizedArtworkDimensionThreshold),
        nameof(ArtworkRepairTargetByteSize),
        nameof(ArtworkRepairTargetDimension),
        nameof(DeleteSourcesAfterIngest),
        nameof(RemoveNonMusicAfterIngest),
        nameof(DeleteStaleCrossSyncFiles),
        nameof(CleanCrossSyncPlaylists),
        nameof(SelectedLibraryProfile),
        nameof(SelectedIngestProfile),
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
        RefreshDestinationRootChoices();
        RefreshSyncTargetChoices();
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
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
        else if (row is LibraryProfileEditorRow profile)
        {
            profile.HealthRules.CollectionChanged += OnTrackedCollectionChanged;
            profile.SidecarRules.CollectionChanged += OnTrackedCollectionChanged;
            TrackRows(profile.HealthRules);
            TrackRows(profile.SidecarRules);
        }
        else if (row is IngestProfileEditorRow ingestProfile)
        {
            ingestProfile.Recipes.CollectionChanged += OnTrackedCollectionChanged;
            TrackRows(ingestProfile.Recipes);
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
        else if (row is LibraryProfileEditorRow profile)
        {
            profile.HealthRules.CollectionChanged -= OnTrackedCollectionChanged;
            profile.SidecarRules.CollectionChanged -= OnTrackedCollectionChanged;
            foreach (HealthRuleEditorRow rule in profile.HealthRules)
                UntrackRow(rule);
            foreach (SidecarRuleEditorRow rule in profile.SidecarRules)
                UntrackRow(rule);
        }
        else if (row is IngestProfileEditorRow ingestProfile)
        {
            ingestProfile.Recipes.CollectionChanged -= OnTrackedCollectionChanged;
            foreach (IngestRecipeEditorRow recipe in ingestProfile.Recipes)
                UntrackRow(recipe);
        }
    }

    private void ClearEditorCollections()
    {
        foreach (IndexTargetEditorRow row in IndexTargets.ToArray())
            UntrackRow(row);
        foreach (SyncPlaylistEditorRow row in SyncPlaylists.ToArray())
            UntrackRow(row);
        foreach (PlaylistSourceEditorRow row in PlaylistSources.ToArray())
            UntrackRow(row);
        foreach (PlaylistTargetEditorRow row in PlaylistTargets.ToArray())
            UntrackRow(row);
        foreach (ExportProfileEditorRow row in ExportProfiles.ToArray())
            UntrackRow(row);
        IndexTargets.Clear();
        SyncPlaylists.Clear();
        PlaylistSources.Clear();
        PlaylistTargets.Clear();
        ExportProfiles.Clear();
    }

    private void OnTrackedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is IndexTargetEditorRow && e.PropertyName is
            nameof(IndexTargetEditorRow.Id) or
            nameof(IndexTargetEditorRow.Path))
        {
            RefreshDestinationRootChoices();
            RefreshSyncTargetChoices();
        }
        else if (sender is IndexTargetEditorRow &&
                 e.PropertyName == nameof(IndexTargetEditorRow.IsSyncTarget) &&
                 !_refreshingSyncTargetChoices)
            RefreshSyncTargetChoices();
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
        MarkDirty();
    }

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
        if (SelectedLibraryProfile is null)
            issues.Add((5, "Choose a root/naming policy."));
        if (SelectedIngestProfile is null)
            issues.Add((6, "Choose an active ingest profile."));
        if (!IndexTargets.Any(target => !string.IsNullOrWhiteSpace(target.Path)))
            issues.Add((1, "Add at least one library root."));
        foreach (IndexTargetEditorRow target in IndexTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Path)))
        {
            try
            {
                _ = LibraryConfiguration.ParseIndexFormats(target.IndexFormats);
                _ = LibraryConfiguration.ParseIndexPatterns(target.IndexIncludePatterns);
                _ = LibraryConfiguration.ParseIndexPatterns(target.IndexExcludePatterns);
            }
            catch (InvalidDataException error)
            {
                issues.Add((1, $"Library root '{target.Path}': {error.Message}"));
            }
            if (target.IsSyncTarget && !target.AllowSynchronizationOutput)
                issues.Add((1,
                    $"Library root '{target.Path}' is a sync target but does not allow sync output."));
            if (!string.IsNullOrWhiteSpace(target.ProfileId) &&
                _editing.Profiles.All(profile => !string.Equals(profile.Id,
                    target.ProfileId, StringComparison.OrdinalIgnoreCase)))
                issues.Add((1,
                    $"Library root '{target.Path}' references unknown profile '{target.ProfileId}'."));
        }
        var configuredSets = IndexTargets.SelectMany(target => target.Memberships)
            .SelectMany(membership => LibraryConfiguration.ParseScanSets(membership.Name))
            .ToHashSet(LibraryConfiguration.ScanSetComparer);
        foreach (PlaylistSourceEditorRow source in PlaylistSources.Where(source =>
                     !string.IsNullOrWhiteSpace(source.Location)))
            if (!string.Equals(source.Type, "m3u", StringComparison.OrdinalIgnoreCase))
                issues.Add((2,
                    $"Playlist source '{source.Location}' must have a type of m3u."));
        foreach (PlaylistTargetEditorRow target in PlaylistTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
        {
            if (!PlaylistTypes.Contains(target.Type, StringComparer.OrdinalIgnoreCase))
                issues.Add((2,
                    $"Playlist target '{target.Target}' has unsupported type '{target.Type}'."));
            IReadOnlyList<string> selectedSets =
                LibraryConfiguration.ParseScanSets(target.Sets);
            if (selectedSets.Count == 0)
                issues.Add((2,
                    $"Playlist target '{target.Target}' must select at least one scan set."));
            string[] unknownSets = selectedSets.Where(set => !configuredSets.Contains(set))
                .ToArray();
            if (unknownSets.Length > 0)
                issues.Add((2,
                    $"Playlist target '{target.Target}' references unknown scan set(s): " +
                    string.Join(", ", unknownSets)));
            if (!PlaylistPathStyles.Contains(target.PathStyle,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistEncodings.Contains(target.Encoding,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistLineEndings.Contains(target.LineEnding,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistFileNameTransforms.Contains(target.FileNameTransform,
                    StringComparer.OrdinalIgnoreCase))
                issues.Add((2,
                    $"Playlist target '{target.Target}' contains an unsupported output option."));
            if (target.MaxTrackCount <= 0)
                issues.Add((2,
                    $"Playlist target '{target.Target}' must allow at least one track."));
        }
        LibraryProfile? editedProfile = null;
        if (AdvancedProfile is not null)
        {
            try
            {
                editedProfile = AdvancedProfile.Build();
                LibraryProfileXml.Validate(editedProfile, includeLegacyIngest: false);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add((5, error.Message));
            }
        }
        LibraryIngestProfile? editedIngestProfile = null;
        if (AdvancedIngestProfile is not null)
        {
            try
            {
                editedIngestProfile = AdvancedIngestProfile.Build();
                LibraryIngestProfileXml.Validate(editedIngestProfile);
                HashSet<Guid> rootIds = IndexTargets.Select(root => root.Id).ToHashSet();
                foreach (LibraryIngestRecipe recipe in editedIngestProfile.Ingest.Recipes)
                    if (recipe.DestinationRootId is { } destinationRootId &&
                        !rootIds.Contains(destinationRootId))
                        issues.Add((6,
                            $"Ingest recipe '{recipe.Id}' references unknown destination root " +
                            $"'{destinationRootId:D}'."));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add((6, error.Message));
            }
        }
        var exportIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportProfileEditorRow row in ExportProfiles)
        {
            try
            {
                LibraryExportProfile profile = row.Build();
                LibraryExportProfileXml.Validate(profile);
                if (!exportIds.Add(profile.Id))
                    issues.Add((2, $"Duplicate export profile ID '{profile.Id}'."));
                if (profile.Naming.LibraryProfileId is { } namingId &&
                    _editing.Profiles.All(item => !string.Equals(item.Id, namingId,
                        StringComparison.OrdinalIgnoreCase)))
                    issues.Add((2, $"Export profile '{profile.Id}' references unknown naming profile '{namingId}'."));
                if (profile.Transform.RecipeId is { } recipeId &&
                    (editedIngestProfile?.Ingest.Recipes.All(recipe => !string.Equals(recipe.Id,
                        recipeId, StringComparison.OrdinalIgnoreCase)) ?? true) &&
                    _editing.IngestProfiles.SelectMany(item => item.Ingest.Recipes).All(recipe =>
                        !string.Equals(recipe.Id, recipeId,
                            StringComparison.OrdinalIgnoreCase)))
                    issues.Add((2, $"Export profile '{profile.Id}' references unknown ingest recipe '{recipeId}'."));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add((2, error.Message));
            }
        }
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
        IsGuidedSetupActive = false;
        if (!string.IsNullOrWhiteSpace(ActiveConfigurationPath))
        {
            LoadEditor(ActiveConfigurationPath);
            StatusMessage = "Unsaved configuration changes were discarded.";
            return;
        }

        _suppressDirty = true;
        ClearEditorCollections();
        EditorPath = null;
        MachineBindingsFile = null;
        DatabaseFile = "cache.db";
        ItunesLibraryPath = null;
        FfmpegPath = "ffmpeg";
        WavpackPath = "wavpack";
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
        _editing = EditableLibraryConfig.CreateNew();
        RefreshProfileChoices();
        RefreshIngestProfileChoices();
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
        IsGuidedSetupActive = false;
        _suppressDirty = true;
        _editing = EditableLibraryConfig.CreateNew();
        RefreshProfileChoices();
        RefreshIngestProfileChoices();
        EditorPath = null;
        MachineBindingsFile = null;
        DatabaseFile = "cache.db";
        ItunesLibraryPath = null;
        FfmpegPath = "ffmpeg";
        WavpackPath = "wavpack";
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
        IndexTargets.Add(CreateIndexTargetRow(_editing.CreateIndexTarget()));
        StatusMessage = "New configuration. Add at least one library root, then Save as.";
        SelectedTabIndex = 1;
        _suppressDirty = false;
        HasUnsavedChanges = true;
        UpdateValidation();
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        SaveConfigurationAsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task StartGuidedSetupAsync()
    {
        Guid previousLibraryId = _editing.LibraryId;
        await NewConfigurationAsync();
        if (_editing.LibraryId == previousLibraryId || !HasUnsavedChanges)
            return;
        IsGuidedSetupActive = true;
        StatusMessage = "Guided setup: choose a preservation-first profile, add the library root, then review the effective policy.";
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    private void ReviewGuidedSetup()
    {
        CommitAdvancedProfile(updateProfileChoices: false);
        SelectedTabIndex = 7;
    }

    [RelayCommand]
    private void CreateLibraryProfile()
    {
        CommitAdvancedProfile(updateProfileChoices: true);
        LibraryProfile profile = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom,
            UniqueProfileId(),
            UniqueProfileName("New profile"));
        _editing.Profiles.Add(profile);
        LibraryProfiles.Add(profile);
        SelectedLibraryProfile = profile;
        StatusMessage = $"Created profile '{profile.Name}'.";
        MarkDirty();
    }

    [RelayCommand]
    private void DuplicateLibraryProfile()
    {
        if (SelectedLibraryProfile is null)
            return;
        string sourceId = SelectedLibraryProfile.Id;
        CommitAdvancedProfile(updateProfileChoices: true);
        LibraryProfile source = _editing.Profiles.Single(profile => string.Equals(
            profile.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        string id = UniqueProfileId();
        LibraryProfile duplicate = source with
        {
            Id = id,
            Name = UniqueProfileName(source.Name + " copy"),
            Preset = LibraryProfilePreset.Custom,
        };
        _editing.Profiles.Add(duplicate);
        LibraryProfiles.Add(duplicate);
        SelectedLibraryProfile = duplicate;
        StatusMessage = $"Duplicated '{source.Name}' as '{duplicate.Name}'.";
        MarkDirty();
    }

    private bool CanDeleteLibraryProfile() => CanDeleteSelectedProfile;

    [RelayCommand(CanExecute = nameof(CanDeleteLibraryProfile))]
    private async Task DeleteLibraryProfileAsync()
    {
        if (SelectedLibraryProfile is not { Preset: LibraryProfilePreset.Custom } selectedChoice)
            return;
        CommitAdvancedProfile(updateProfileChoices: true);
        LibraryProfile selected = _editing.Profiles.Single(profile => string.Equals(
            profile.Id, selectedChoice.Id, StringComparison.OrdinalIgnoreCase));
        LibraryProfile fallback = LibraryProfiles.FirstOrDefault(profile =>
            profile.Id == LibraryProfilePresets.CatalogOnlyId) ??
            LibraryProfiles.First(profile => !string.Equals(
                profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        int rootReferences = IndexTargets.Count(root => string.Equals(
            root.ProfileId, selected.Id, StringComparison.OrdinalIgnoreCase));
        int workflowReferences = ExportProfiles.Count(profile => string.Equals(
                profile.NamingProfileId, selected.Id, StringComparison.OrdinalIgnoreCase));
        string reassignment = rootReferences + workflowReferences == 0
            ? "It has no configured references."
            : $"{rootReferences} root reference(s) and {workflowReferences} workflow " +
              $"reference(s) will be reassigned to '{fallback.Name}'.";
        if (!await _dialogs.ConfirmAsync(
                $"Delete profile '{selected.Name}'?",
                reassignment + " This cannot be undone after saving.",
                "Delete profile"))
            return;

        bool previousSuppression = _suppressDirty;
        _suppressDirty = true;
        try
        {
            foreach (IndexTargetEditorRow root in IndexTargets.Where(root => string.Equals(
                         root.ProfileId, selected.Id, StringComparison.OrdinalIgnoreCase)))
                root.ProfileId = fallback.Id;
            foreach (ExportProfileEditorRow export in ExportProfiles.Where(export =>
                         string.Equals(export.NamingProfileId, selected.Id,
                             StringComparison.OrdinalIgnoreCase)))
                export.NamingProfileId = fallback.Id;
            _editing.Profiles.RemoveAll(profile => string.Equals(
                profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            LibraryProfile? profileChoice = LibraryProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            if (profileChoice is not null)
                LibraryProfiles.Remove(profileChoice);
            _editing.ActiveProfileId = fallback.Id;
            SelectedLibraryProfile = LibraryProfiles.Single(profile => string.Equals(
                profile.Id, fallback.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressDirty = previousSuppression;
        }
        StatusMessage = $"Deleted profile '{selected.Name}' and reassigned its references.";
        MarkDirty();
    }

    [RelayCommand]
    private void CreateIngestProfile()
    {
        CommitAdvancedIngestProfile(updateProfileChoices: true);
        string id = UniqueIngestProfileId();
        var profile = new LibraryIngestProfile(
            id,
            UniqueIngestProfileName("New ingest profile"),
            new(false, LibrarySourceDisposition.Preserve, true, []));
        _editing.IngestProfiles.Add(profile);
        IngestProfiles.Add(profile);
        SelectedIngestProfile = profile;
        StatusMessage = $"Created ingest profile '{profile.Name}'.";
        MarkDirty();
    }

    [RelayCommand]
    private void DuplicateIngestProfile()
    {
        if (SelectedIngestProfile is null)
            return;
        string sourceId = SelectedIngestProfile.Id;
        CommitAdvancedIngestProfile(updateProfileChoices: true);
        LibraryIngestProfile source = _editing.IngestProfiles.Single(profile => string.Equals(
            profile.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        var duplicate = source with
        {
            Id = UniqueIngestProfileId(),
            Name = UniqueIngestProfileName(source.Name + " copy"),
        };
        _editing.IngestProfiles.Add(duplicate);
        IngestProfiles.Add(duplicate);
        SelectedIngestProfile = duplicate;
        StatusMessage = $"Duplicated ingest profile '{source.Name}'.";
        MarkDirty();
    }

    private bool CanDeleteIngestProfile() => CanDeleteSelectedIngestProfile;

    [RelayCommand(CanExecute = nameof(CanDeleteIngestProfile))]
    private async Task DeleteIngestProfileAsync()
    {
        if (SelectedIngestProfile is null || !CanDeleteSelectedIngestProfile)
            return;
        string selectedId = SelectedIngestProfile.Id;
        CommitAdvancedIngestProfile(updateProfileChoices: true);
        LibraryIngestProfile selected = _editing.IngestProfiles.Single(profile =>
            string.Equals(profile.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        LibraryIngestProfile fallback = IngestProfiles.First(profile =>
            !string.Equals(profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (!await _dialogs.ConfirmAsync(
                $"Delete ingest profile '{selected.Name}'?",
                $"The active ingest workflow will change to '{fallback.Name}'. " +
                "This cannot be undone after saving.",
                "Delete ingest profile"))
            return;
        _editing.IngestProfiles.RemoveAll(profile => string.Equals(
            profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        LibraryIngestProfile? selectedChoice = IngestProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selectedChoice is not null)
            IngestProfiles.Remove(selectedChoice);
        _editing.ActiveIngestProfileId = fallback.Id;
        SelectedIngestProfile = IngestProfiles.Single(profile => string.Equals(
            profile.Id, fallback.Id, StringComparison.OrdinalIgnoreCase));
        StatusMessage = $"Deleted ingest profile '{selected.Name}'.";
        MarkDirty();
    }

    [RelayCommand]
    private async Task FinishGuidedSetupAsync()
    {
        UpdateValidation();
        if (!IsEditorValid)
        {
            StatusMessage = "Resolve the highlighted setup issues before saving.";
            SelectedTabIndex = ValidationTabIndex;
            return;
        }
        await SaveEditorAsync(null);
        if (!HasUnsavedChanges)
            IsGuidedSetupActive = false;
    }

    [RelayCommand]
    private void AddIndexTarget() =>
        IndexTargets.Add(CreateIndexTargetRow(_editing.CreateIndexTarget()));

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
    private void AddSyncPlaylist() => SyncPlaylists.Add(new SyncPlaylistEditorRow());

    [RelayCommand]
    private void RemoveSyncPlaylist(SyncPlaylistEditorRow? row)
    {
        if (row is not null)
            SyncPlaylists.Remove(row);
    }

    [RelayCommand]
    private void AddPlaylistSource() =>
        PlaylistSources.Add(new PlaylistSourceEditorRow());

    [RelayCommand]
    private void RemovePlaylistSource(PlaylistSourceEditorRow? row)
    {
        if (row is not null)
            PlaylistSources.Remove(row);
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
    private void AddExportProfile() =>
        ExportProfiles.Add(ExportProfileEditorRow.Create());

    [RelayCommand]
    private void AddFieldMapping() =>
        FieldMappings.Add(new MetadataFieldMappingEditorRow
        {
            Format = MediaFormatFamily.Flac,
            Field = TagFields.Title,
            NativeFieldName = "TITLE",
        });

    [RelayCommand]
    private void RemoveFieldMapping(MetadataFieldMappingEditorRow? row)
    {
        if (row is not null)
            FieldMappings.Remove(row);
    }

    [RelayCommand]
    private void SaveFieldMappings()
    {
        try
        {
            _fieldMappings.Save(
                FieldMappings.Select(row => row.Build()).ToArray());
            FieldMappingStatus =
                $"Saved {FieldMappings.Count:N0} format-specific field mapping(s).";
        }
        catch (Exception error)
        {
            FieldMappingStatus = $"Could not save field mappings: {error.Message}";
        }
    }

    [RelayCommand]
    private void RemoveExportProfile(ExportProfileEditorRow? row)
    {
        if (row is not null)
            ExportProfiles.Remove(row);
    }

    [RelayCommand]
    private async Task BrowseExportDestinationAsync(ExportProfileEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFolderAsync("Choose an export destination");
        if (path is not null)
            row.TransportDestination = path;
    }

    [RelayCommand]
    private void AddIngestRecipe() =>
        AdvancedIngestProfile?.Recipes.Add(IngestRecipeEditorRow.Create());

    [RelayCommand]
    private void RemoveIngestRecipe(IngestRecipeEditorRow? row)
    {
        if (row is not null)
            AdvancedIngestProfile?.Recipes.Remove(row);
    }

    [RelayCommand]
    private void AddSidecarRule() =>
        AdvancedProfile?.SidecarRules.Add(SidecarRuleEditorRow.Create());

    [RelayCommand]
    private void RemoveSidecarRule(SidecarRuleEditorRow? row)
    {
        if (row is not null)
            AdvancedProfile?.SidecarRules.Remove(row);
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
    private async Task BrowsePlaylistSourceAsync(PlaylistSourceEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFileAsync("Choose an M3U/M3U8 playlist source",
            [new FilePickerType("File playlists", [".m3u", ".m3u8"])]);
        if (path is not null)
            row.Location = path;
    }

    [RelayCommand]
    private async Task BrowseDatabaseAsync()
    {
        string? path = await _files.SaveFileAsync("Choose metadata cache", "cache.db", ".db");
        if (path is not null)
            DatabaseFile = path;
    }

    [RelayCommand]
    private async Task BrowseMachineBindingsAsync()
    {
        string? path = await _files.SaveFileAsync(
            "Choose machine-local bindings file", "library.bindings.xml", ".xml");
        if (path is not null)
            MachineBindingsFile = path;
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
        // Executables commonly have no extension on macOS and Linux. Leaving the picker
        // unfiltered supports ffmpeg, ffmpeg.exe, and user-provided wrapper scripts.
        string? path = await _files.PickFileAsync("Choose ffmpeg executable");
        if (path is not null)
            FfmpegPath = path;
    }

    [RelayCommand]
    private async Task BrowseWavpackAsync()
    {
        // Executables commonly have no extension on macOS and Linux.
        string? path = await _files.PickFileAsync("Choose WavPack executable");
        if (path is not null)
            WavpackPath = path;
    }

    [RelayCommand]
    private async Task BrowseFpcalcAsync()
    {
        string? path = await _files.PickFileAsync("Choose fpcalc executable");
        if (path is not null)
            FpcalcPath = path;
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
            IsGuidedSetupActive = false;
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
            RefreshProfileChoices();
            RefreshIngestProfileChoices();
            EditorPath = path;
            MachineBindingsFile = _editing.MachineBindingsFile;
            DatabaseFile = _editing.DatabaseFile;
            ItunesLibraryPath = _editing.ItunesLibraryPath;
            FfmpegPath = _editing.FfmpegPath;
            WavpackPath = _editing.WavpackPath;
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
                IndexTargetEditorRow row = CreateIndexTargetRow(target);
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
                IndexTargets.Add(CreateIndexTargetRow(_editing.CreateIndexTarget()));
            foreach (string playlist in _editing.SyncPlaylists)
                SyncPlaylists.Add(new SyncPlaylistEditorRow { Name = playlist });
            foreach (PlaylistSourceEntry source in _editing.PlaylistSources)
                PlaylistSources.Add(new PlaylistSourceEditorRow
                {
                    Location = source.Location,
                    Type = source.Type,
                    Recursive = source.Recursive,
                    Source = source,
                });
            foreach (PlaylistTargetEntry target in _editing.PlaylistTargets)
                PlaylistTargets.Add(new PlaylistTargetEditorRow
                {
                    Target = target.Target,
                    Type = target.Type,
                    Sets = target.Sets.Count == 0 ? null : string.Join(",", target.Sets),
                    PathStyle = target.PathStyle,
                    Encoding = target.Encoding,
                    EmitByteOrderMark = target.EmitByteOrderMark,
                    LineEnding = target.LineEnding,
                    IncludeExtendedInfo = target.IncludeExtendedInfo,
                    FileNameTransform = target.FileNameTransform,
                    MaxTrackCount = target.MaxTrackCount,
                    CollisionPolicy = target.CollisionPolicy,
                    Source = target,
                });
            foreach (LibraryExportProfile profile in _editing.ExportProfiles)
                ExportProfiles.Add(ExportProfileEditorRow.From(profile));
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
            CommitAdvancedProfile(updateProfileChoices: false);
            CommitAdvancedIngestProfile(updateProfileChoices: false);
            _editing.ActiveProfileId = SelectedLibraryProfile?.Id ??
                throw new InvalidDataException("Choose a root/naming policy.");
            _editing.ActiveIngestProfileId = SelectedIngestProfile?.Id ??
                throw new InvalidDataException("Choose an active ingest profile.");
            ApplyAdvancedCompatibilityDefaults(
                _editing.ActiveProfile, _editing.ActiveIngestProfile);
            _editing.MachineBindingsFile = CleanOptional(MachineBindingsFile);
            _editing.DatabaseFile = string.IsNullOrWhiteSpace(DatabaseFile) ? "cache.db" : DatabaseFile.Trim();
            _editing.ItunesLibraryPath = string.IsNullOrWhiteSpace(ItunesLibraryPath) ? null : ItunesLibraryPath.Trim();
            _editing.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();
            _editing.WavpackPath = string.IsNullOrWhiteSpace(WavpackPath) ? "wavpack" : WavpackPath.Trim();
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
                    IndexTargetEntry target = row.Source ?? _editing.CreateIndexTarget();
                    target.Id = row.Id;
                    target.Target = row.Path.Trim();
                    target.ProfileId = CleanOptional(row.ProfileId) ?? _editing.ActiveProfileId;
                    target.Permissions = row.Permissions;
                    target.DefaultOffset = null;
                    target.IndexFormats = [.. LibraryConfiguration.ParseIndexFormats(
                        row.IndexFormats)];
                    target.IndexIncludePatterns = [.. LibraryConfiguration.ParseIndexPatterns(
                        row.IndexIncludePatterns)];
                    target.IndexExcludePatterns = [.. LibraryConfiguration.ParseIndexPatterns(
                        row.IndexExcludePatterns)];
                    target.Filter = string.IsNullOrWhiteSpace(row.Filter) ? null : row.Filter.Trim();
                    target.Organize = row.AllowOrganization;
                    target.IsSyncTarget = row.IsSyncTarget;
                    target.Memberships = row.Memberships
                        .SelectMany(membership => LibraryConfiguration
                            .ParseScanSets(membership.Name)
                            .Select(name => new IndexTargetSetEntry
                            {
                                Name = name,
                                Offset = CleanOptional(membership.Offset),
                            })).ToList();
                    return target;
                }).ToList();
            _editing.SyncPlaylists = SyncPlaylists
                .Where(row => !string.IsNullOrWhiteSpace(row.Name))
                .Select(row => row.Name.Trim()).ToList();
            _editing.PlaylistSources = PlaylistSources
                .Where(row => !string.IsNullOrWhiteSpace(row.Location))
                .Select(row =>
                {
                    PlaylistSourceEntry source = row.Source ?? new PlaylistSourceEntry();
                    source.Location = row.Location.Trim();
                    source.Type = string.IsNullOrWhiteSpace(row.Type)
                        ? "m3u"
                        : row.Type.Trim();
                    source.Recursive = row.Recursive;
                    return source;
                }).ToList();
            _editing.PlaylistTargets = PlaylistTargets
                .Where(row => !string.IsNullOrWhiteSpace(row.Target))
                .Select(row =>
                {
                    PlaylistTargetEntry target = row.Source ?? new PlaylistTargetEntry();
                    target.Target = row.Target.Trim();
                    target.Type = string.IsNullOrWhiteSpace(row.Type) ? "m3u" : row.Type.Trim();
                    target.Sets = [.. LibraryConfiguration.ParseScanSets(row.Sets)];
                    target.PathStyle = row.PathStyle;
                    target.Encoding = row.Encoding;
                    target.EmitByteOrderMark = row.EmitByteOrderMark;
                    target.LineEnding = row.LineEnding;
                    target.IncludeExtendedInfo = row.IncludeExtendedInfo;
                    target.FileNameTransform = row.FileNameTransform;
                    target.MaxTrackCount = row.MaxTrackCount;
                    target.CollisionPolicy = row.CollisionPolicy;
                    return target;
                }).ToList();
            _editing.ExportProfiles = ExportProfiles
                .Select(row => row.Build())
                .ToList();
            _editing.Save(path);
            _settings.LoadConfig(path);
            LoadEditor(path);
            StatusMessage = "Configuration saved and loaded.";
            HasUnsavedChanges = false;
            IsGuidedSetupActive = false;
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

    private void RefreshProfileChoices()
    {
        LibraryProfiles.Clear();
        foreach (LibraryProfile profile in _editing.Profiles)
            LibraryProfiles.Add(profile);
        SelectedLibraryProfile = LibraryProfiles.FirstOrDefault(profile => string.Equals(
            profile.Id, _editing.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        foreach (IndexTargetEditorRow root in IndexTargets)
            root.RefreshProfileChoices(LibraryProfiles);
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
    }

    private void RefreshIngestProfileChoices()
    {
        IngestProfiles.Clear();
        foreach (LibraryIngestProfile profile in _editing.IngestProfiles)
            IngestProfiles.Add(profile);
        SelectedIngestProfile = IngestProfiles.FirstOrDefault(profile => string.Equals(
            profile.Id, _editing.ActiveIngestProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private void SetAdvancedProfile(LibraryProfile? profile)
    {
        if (AdvancedProfile is not null)
            UntrackRow(AdvancedProfile);
        AdvancedProfile = profile is null
            ? null
            : LibraryProfileEditorRow.From(profile);
        if (AdvancedProfile is not null)
            TrackRow(AdvancedProfile);
        RefreshDestinationRootChoices();
    }

    private void SetAdvancedIngestProfile(LibraryIngestProfile? profile)
    {
        if (AdvancedIngestProfile is not null)
            UntrackRow(AdvancedIngestProfile);
        AdvancedIngestProfile = profile is null
            ? null
            : IngestProfileEditorRow.From(profile);
        if (AdvancedIngestProfile is not null)
            TrackRow(AdvancedIngestProfile);
        RefreshDestinationRootChoices();
    }

    private void RefreshDestinationRootChoices()
    {
        if (AdvancedIngestProfile is null)
            return;
        foreach (IngestRecipeEditorRow recipe in AdvancedIngestProfile.Recipes)
            recipe.RefreshDestinationRootChoices(IndexTargets);
    }

    private void RefreshSyncTargetChoices()
    {
        Guid? selectedId = IndexTargets.FirstOrDefault(target => target.IsSyncTarget)?.Id;
        _refreshingSyncTargetChoices = true;
        try
        {
            SyncTargetRootChoices.Clear();
            SyncTargetRootChoices.Add(new(null, "No sync target"));
            foreach (IndexTargetEditorRow root in IndexTargets)
            {
                string label = string.IsNullOrWhiteSpace(root.Path)
                    ? "New library root"
                    : root.Path.Trim();
                SyncTargetRootChoices.Add(new(root.Id, label));
            }
            SelectedSyncTargetRoot = SyncTargetRootChoices.First(choice =>
                choice.Id == selectedId);
        }
        finally
        {
            _refreshingSyncTargetChoices = false;
        }
    }

    private void CommitAdvancedProfile(bool updateProfileChoices)
    {
        if (AdvancedProfile is null)
            return;
        LibraryProfile updated = AdvancedProfile.Build();
        int editingIndex = _editing.Profiles.FindIndex(profile => string.Equals(
            profile.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        if (editingIndex >= 0)
            _editing.Profiles[editingIndex] = updated;
        if (!updateProfileChoices)
            return;
        int choiceIndex = LibraryProfiles.ToList().FindIndex(profile => string.Equals(
            profile.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        if (choiceIndex >= 0)
            LibraryProfiles[choiceIndex] = updated;
    }

    private void CommitAdvancedIngestProfile(bool updateProfileChoices)
    {
        if (AdvancedIngestProfile is null)
            return;
        LibraryIngestProfile updated = AdvancedIngestProfile.Build();
        int editingIndex = _editing.IngestProfiles.FindIndex(profile => string.Equals(
            profile.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        if (editingIndex >= 0)
            _editing.IngestProfiles[editingIndex] = updated;
        if (!updateProfileChoices)
            return;
        int choiceIndex = IngestProfiles.ToList().FindIndex(profile => string.Equals(
            profile.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        if (choiceIndex >= 0)
            IngestProfiles[choiceIndex] = updated;
    }

    private void ApplyAdvancedCompatibilityDefaults(
        LibraryProfile profile,
        LibraryIngestProfile ingestProfile)
    {
        _editing.LengthLimit = profile.Naming.ComponentLengthLimit ?? 255;
        _editing.DiscNumLengthLimit = profile.Naming.DiscAlbumLengthLimit ?? 255;
        LibraryIngestRecipe? aac = ingestProfile.Ingest.Recipes.FirstOrDefault(recipe =>
            recipe.Action == LibraryIngestAction.Transcode &&
            (string.Equals(recipe.Codec, "aac", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(recipe.OutputExtension, ".m4a",
                 StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(aac?.Encoder))
            _editing.AacEncoder = aac.Encoder.Trim();
        if (aac?.BitrateKbps is { } bitrate)
            _editing.AacBitrateKbps = bitrate;
    }

    private string UniqueProfileId()
    {
        string id;
        do
            id = "profile-" + Guid.NewGuid().ToString("N")[..12];
        while (_editing.Profiles.Any(profile => string.Equals(
            profile.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private string UniqueIngestProfileId()
    {
        string id;
        do
            id = "ingest-" + Guid.NewGuid().ToString("N")[..12];
        while (_editing.IngestProfiles.Any(profile => string.Equals(
            profile.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private string UniqueIngestProfileName(string basis)
    {
        string name = basis;
        int suffix = 2;
        while (_editing.IngestProfiles.Any(profile => string.Equals(
                   profile.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{basis} {suffix++}";
        return name;
    }

    private string UniqueProfileName(string desired)
    {
        string name = desired;
        int suffix = 2;
        while (_editing.Profiles.Any(profile => string.Equals(
                   profile.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{desired} {suffix++}";
        return name;
    }

    private IndexTargetEditorRow CreateIndexTargetRow(IndexTargetEntry target)
    {
        string profileId = CleanOptional(target.ProfileId) ?? _editing.ActiveProfileId;
        LibraryProfile profile = _editing.Profiles.FirstOrDefault(candidate => string.Equals(
            candidate.Id, profileId, StringComparison.OrdinalIgnoreCase)) ?? _editing.ActiveProfile;
        var row = new IndexTargetEditorRow
        {
            Id = target.Id,
            Path = target.Target,
            ProfileId = profileId,
            Filter = target.Filter,
            IndexFormats = target.IndexFormats.Count == 0
                ? null : string.Join(", ", target.IndexFormats),
            IndexIncludePatterns = target.IndexIncludePatterns.Count == 0
                ? null : string.Join("; ", target.IndexIncludePatterns),
            IndexExcludePatterns = target.IndexExcludePatterns.Count == 0
                ? null : string.Join("; ", target.IndexExcludePatterns),
            IsSyncTarget = target.IsSyncTarget,
            Permissions = target.Permissions ?? profile.DefaultRootPermissions,
            Source = target,
        };
        row.RefreshProfileChoices(LibraryProfiles);
        return row;
    }

    private static string? EffectiveOffset(
        IndexTargetSetEntry membership,
        IndexTargetEntry target) =>
        CleanOptional(membership.Offset) ?? CleanOptional(target.DefaultOffset);

    private static string FormatPermissions(LibraryRootPermissions permissions)
    {
        if (permissions == LibraryRootPermissions.None)
            return "catalog only (read-only)";
        var labels = new List<string>();
        if (permissions.HasFlag(LibraryRootPermissions.WriteMetadata)) labels.Add("metadata");
        if (permissions.HasFlag(LibraryRootPermissions.WriteArtwork)) labels.Add("artwork");
        if (permissions.HasFlag(LibraryRootPermissions.OrganizeFiles)) labels.Add("organization");
        if (permissions.HasFlag(LibraryRootPermissions.IngestOutput)) labels.Add("ingest output");
        if (permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput)) labels.Add("sync output");
        return string.Join(", ", labels);
    }

    private static string FormatWords(string value) => string.Concat(value.Select(
        (character, index) => index > 0 && char.IsUpper(character)
            ? " " + char.ToLowerInvariant(character)
            : char.ToLowerInvariant(character).ToString()));

    private static string OnOff(bool value) => value ? "preserved" : "not copied";

    private static string? CleanOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ThemeChoice(string Name, string Canvas, string Raised, string Accent);
