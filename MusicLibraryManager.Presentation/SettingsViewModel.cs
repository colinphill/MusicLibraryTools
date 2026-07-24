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
    private readonly ILocalizationService _localization;
    private readonly ISecretStore _secrets;
    private readonly IMetadataFieldMappingService _fieldMappings;
    private EditableLibraryConfig _editing = EditableLibraryConfig.CreateNew();
    private bool _suppressDirty = true;
    private bool _refreshingSyncTargetChoices;
    private bool _refreshingDisplayLanguage;
    private readonly HashSet<INotifyPropertyChanged> _trackedRows = [];
    private string? _statusMessageKey;
    private object?[] _statusMessageArguments = [];
    private string? _discogsStatusKey;
    private object?[] _discogsStatusArguments = [];
    private string? _fieldMappingStatusKey;
    private object?[] _fieldMappingStatusArguments = [];

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
    [ObservableProperty] private string? _optimFrogToolsDirectory;
    [ObservableProperty] private string? _acoustIdClientKey;
    [ObservableProperty] private bool _offlineMode;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDiscogsTokenCommand))]
    private string? _discogsToken;
    [ObservableProperty]
    private string _discogsCredentialStatus = "";
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
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private ThemeChoice? _selectedThemeChoice;
    [ObservableProperty]
    private LocalizedChoice<string>?
        _selectedDisplayLanguage;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isGuidedSetupActive;
    [ObservableProperty]
    private string _fieldMappingStatus = "";
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
        IMetadataFieldMappingService? fieldMappings = null,
        ILocalizationService? localization = null)
    {
        _settings = settings;
        _files = files;
        _dialogs = dialogs;
        _theme = theme;
        _localization = localization ??
            new ResourceLocalizationService(settings);
        _secrets = secrets ?? new SessionSecretStore();
        _fieldMappings = fieldMappings ??
            new MetadataFieldMappingService(settings, MediaFormatRegistry.Default);
        _fpcalcPath =
            settings.GetPreference(AudioFingerprintService.ExecutablePreferenceKey) ??
            "fpcalc";
        _optimFrogToolsDirectory =
            settings.GetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey);
        _acoustIdClientKey =
            settings.GetPreference(AcoustIdLookupService.ClientKeyPreference);
        _offlineMode = bool.TryParse(
            settings.GetPreference(
                ProviderNetworkPolicy.OfflinePreferenceKey),
            out bool offline) && offline;
        RefreshLocalizedChoices();
        string? storedTheme = settings.GetPreference(ThemePreference);
        ThemeChoice? storedChoice = ThemeChoices.FirstOrDefault(choice =>
            string.Equals(choice.Value, storedTheme, StringComparison.Ordinal));
        _selectedThemeChoice = storedChoice ?? ThemeChoices[0];
        _selectedTheme = _selectedThemeChoice.Value;
        if (storedTheme is not null && storedChoice is null)
            settings.SetPreference(ThemePreference, _selectedTheme);
        RefreshDisplayLanguageChoices();
        SetStatus("Settings.Status.ChooseConfiguration");
        SetDiscogsStatus("Settings.Discogs.Checking");
        SetFieldMappingStatus("Settings.FieldMappings.Status.Description");
        _localization.CultureChanged +=
            OnLocalizationCultureChanged;
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
    public ObservableCollection<LocalizedChoice<string>>
        DisplayLanguageChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryPathCollisionPolicy>>
        CollisionPolicyChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryUnicodeNormalization>>
        UnicodeNormalizationChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryDiscStrategy>>
        DiscStrategyChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryTrackTotalScope>>
        TrackTotalScopeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryHealthSeverity>>
        HealthSeverityChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibrarySourceDisposition>>
        SourceDispositionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryIngestAction>>
        IngestActionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryIngestAlbumCondition>>
        IngestAlbumConditionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryIngestSourceSelection>>
        IngestSourceSelectionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<SettingsChannelChoice>>
        ChannelLocalizedChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryArtworkStorage>>
        ArtworkStorageLocalizedChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryArtworkRoleSelection>>
        ArtworkRoleLocalizedChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryArtworkEncoding>>
        ArtworkEncodingLocalizedChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibrarySidecarDisposition>>
        SidecarDispositionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistTypeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistSourceTypeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistPathStyleChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistEncodingChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistLineEndingChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        PlaylistFileNameTransformChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ExportSelectionKind>>
        ExportSelectionKindChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ExportTransformMode>>
        ExportTransformModeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ExportArtworkMode>>
        ExportArtworkModeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ExportExtraFileDisposition>>
        ExportExtraFileDispositionChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<MediaFormatFamily>>
        MetadataFormatFamilyChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<TagFields>>
        MetadataCanonicalFieldChoices { get; } = [];
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
        Enum.GetValues<MediaFormatFamily>().Distinct().ToArray();
    public IReadOnlyList<TagFields> MetadataCanonicalFields { get; } =
        Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .ToArray();
    public ObservableCollection<ThemeChoice> ThemeChoices { get; } =
    [
        new("System", "System", "#0D1417", "#F8FBFA", "#2CC7BC"),
        new("Light", "Light", "#EEF4F3", "#FFFFFF", "#087F8C"),
        new("Dark", "Dark", "#0D1417", "#18262B", "#2CC7BC"),
        new("Steel Blue", "Steel Blue", "#101C2A", "#1D3043", "#3AAFB8"),
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
    public string ActiveConfigurationDisplay =>
        string.IsNullOrWhiteSpace(ActiveConfigurationPath)
            ? _localization.Get("Settings.Configuration.NoneLoaded")
            : ActiveConfigurationPath;
    public string EffectivePolicySummary
    {
        get
        {
            LibraryProfile? profile = AdvancedProfile?.Build() ?? SelectedLibraryProfile;
            if (profile is null)
                return _localization.Get("Settings.PolicySummary.NoRootPolicy");

            string permissions = FormatPermissions(profile.DefaultRootPermissions);
            string naming = profile.Naming.UseItunesCanonicalNaming
                ? _localization.Get("Settings.PolicySummary.Naming.Itunes")
                : profile.Preset == LibraryProfilePreset.CatalogOnly
                    ? _localization.Get("Settings.PolicySummary.Naming.Preserved")
                    : _localization.Format("Settings.PolicySummary.Naming.Template",
                        profile.Naming.DirectoryTemplate,
                        profile.Naming.FileNameTemplate);
            LibraryIngestProfile? ingestProfile = AdvancedIngestProfile?.Build() ??
                SelectedIngestProfile;
            string ingest = ingestProfile?.Ingest.Enabled == true
                ? _localization.Format("Settings.PolicySummary.Ingest.Enabled",
                    ChoiceLabel(SourceDispositionChoices,
                        ingestProfile.Ingest.SourceDisposition))
                : _localization.Get("Settings.PolicySummary.Ingest.Disabled");
            int writableRoots = IndexTargets.Count(root => !root.IsReadOnly);
            int profileOverrides = IndexTargets.Count(root => !string.Equals(
                root.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
            return string.Join(Environment.NewLine,
                _localization.Format("Settings.PolicySummary.RootPolicy", profile.Name),
                _localization.Format("Settings.PolicySummary.Permissions", permissions),
                _localization.Format("Settings.PolicySummary.Naming", naming),
                _localization.Format("Settings.PolicySummary.Discs",
                    ChoiceLabel(DiscStrategyChoices, profile.Disc.Strategy)),
                _localization.Format("Settings.PolicySummary.Ingest",
                    ingestProfile?.Name ??
                    _localization.Get("Settings.PolicySummary.None"), ingest),
                _localization.Format("Settings.PolicySummary.RootCounts",
                    IndexTargets.Count, writableRoots, profileOverrides));
        }
    }

    public string EffectivePolicyDetails
    {
        get
        {
            LibraryProfile? profile = AdvancedProfile?.Build() ?? SelectedLibraryProfile;
            if (profile is null)
                return _localization.Get("Settings.PolicyDetails.NoActivePolicy");

            string formats = string.Join(", ", MediaFormatRegistry.Default
                .GetExtensions(MediaFormatCapabilities.LibraryIndex));
            string roots = IndexTargets.Count == 0
                ? _localization.Get("Settings.PolicyDetails.NoRoots")
                : string.Join(Environment.NewLine, IndexTargets.Select(root =>
                {
                    string path = string.IsNullOrWhiteSpace(root.Path)
                        ? _localization.Get("Settings.PolicyDetails.UnsetPath")
                        : root.Path;
                    string availability = Directory.Exists(root.Path)
                        ? ""
                        : _localization.Get("Settings.PolicyDetails.RootOffline");
                    string included = string.IsNullOrWhiteSpace(root.IndexIncludePatterns)
                        ? _localization.Get("Settings.PolicyDetails.AllIncluded")
                        : _localization.Format("Settings.PolicyDetails.PatternsIncluded",
                            root.IndexIncludePatterns);
                    string excluded = string.IsNullOrWhiteSpace(root.IndexExcludePatterns)
                        ? ""
                        : _localization.Format("Settings.PolicyDetails.PatternsExcluded",
                            root.IndexExcludePatterns);
                    string rootFormats = string.IsNullOrWhiteSpace(root.IndexFormats)
                        ? _localization.Get("Settings.PolicyDetails.AllFormats")
                        : root.IndexFormats;
                    return _localization.Format("Settings.PolicyDetails.RootLine",
                        path, rootFormats, DescribeRootPermissions(root.Permissions),
                        included, excluded, availability);
                }));
            string example;
            try
            {
                string exampleRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!,
                    _localization.Get("Settings.PolicyDetails.Example.MusicFolder"));
                var metadata = new LibraryPathMetadata(
                    _localization.Get("Settings.PolicyDetails.Example.Artist"),
                    _localization.Get("Settings.PolicyDetails.Example.Artist"),
                    _localization.Get("Settings.PolicyDetails.Example.Album"),
                    _localization.Get("Settings.PolicyDetails.Example.Song"),
                    3, 1, false, "2026",
                    _localization.Get("Settings.PolicyDetails.Example.FileName"), ".flac")
                {
                    Genre = _localization.Get("Settings.PolicyDetails.Example.Genre"),
                };
                example = Path.GetRelativePath(exampleRoot,
                    LibraryPathLayoutResolver.Shared.Resolve(exampleRoot, profile, metadata,
                        _editing.LengthLimit, _editing.DiscNumLengthLimit));
            }
            catch (Exception ex)
            {
                example = _localization.Format(
                    "Settings.PolicyDetails.ExampleUnavailable", ex.Message);
            }

            LibraryHealthRulePolicy[] enabledRules = profile.Health.Rules
                .Where(rule => rule.Enabled).ToArray();
            string rules = enabledRules.Length == 0
                ? _localization.Get("Settings.PolicyDetails.None")
                : string.Join(", ", enabledRules.Select(rule =>
                    _localization.Format("Settings.PolicyDetails.HealthRule",
                        rule.Id,
                        ChoiceLabel(HealthSeverityChoices, rule.Severity),
                        rule.ApplyRepair
                            ? _localization.Get("Settings.PolicyDetails.Repair.Apply")
                            : rule.ProposeRepair
                                ? _localization.Get(
                                    "Settings.PolicyDetails.Repair.Propose")
                                : _localization.Get(
                                    "Settings.PolicyDetails.Repair.None"))));
            LibraryIngestProfile? ingestProfile = AdvancedIngestProfile?.Build() ??
                SelectedIngestProfile;
            LibraryIngestRecipe[] recipes = (ingestProfile?.Ingest.Recipes ?? [])
                .Where(recipe => recipe.Enabled).ToArray();
            string ingest = ingestProfile?.Ingest.Enabled != true
                ? _localization.Get("Settings.PolicyDetails.Ingest.Disabled")
                : recipes.Length == 0
                    ? _localization.Get("Settings.PolicyDetails.Ingest.NoRecipes")
                    : string.Join(", ", recipes.Select(recipe =>
                        _localization.Format("Settings.PolicyDetails.Ingest.Recipe",
                            recipe.Name,
                            ChoiceLabel(IngestActionChoices, recipe.Action),
                            recipe.DestinationRootId is { } rootId
                                ? IndexTargets.FirstOrDefault(root => root.Id == rootId)?.Path ??
                                  _localization.Get(
                                      "Settings.PolicyDetails.Ingest.MissingRoot")
                                : _localization.Get(
                                    "Settings.PolicyDetails.Ingest.NoDirectRoot"))));
            string integrations = string.IsNullOrWhiteSpace(ItunesLibraryPath)
                ? _localization.Get("Settings.PolicyDetails.Integrations.NoCatalog")
                : _localization.Format("Settings.PolicyDetails.Integrations.Itunes",
                    ItunesLibraryPath);
            string exports = ExportProfiles.Count == 0
                ? _localization.Get("Settings.PolicyDetails.Exports.None")
                : string.Join(", ", ExportProfiles.Select(item =>
                    _localization.Format("Settings.PolicyDetails.Export",
                        item.Name,
                        _localization.Get(item.Enabled
                            ? "Settings.PolicyDetails.Enabled"
                            : "Settings.PolicyDetails.Disabled"))));

            string naming = profile.Preset == LibraryProfilePreset.CatalogOnly
                ? _localization.Get("Settings.PolicyDetails.Naming.CatalogOnly")
                : _localization.Format("Settings.PolicyDetails.Naming.Organized",
                    example,
                    ChoiceLabel(CollisionPolicyChoices,
                        profile.Naming.CollisionPolicy));
            string artworkReading = profile.Artwork.ReadAtIndexTime
                ? _localization.Get("Settings.PolicyDetails.Artwork.Eager")
                : _localization.Get("Settings.PolicyDetails.Artwork.Lazy");
            string discHandling = profile.Disc.Strategy ==
                LibraryDiscStrategy.PreserveTags
                ? _localization.Get("Settings.PolicyDetails.Discs.Preserve")
                : _localization.Format("Settings.PolicyDetails.Discs.Transform",
                    ChoiceLabel(DiscStrategyChoices, profile.Disc.Strategy));
            string ingestBehavior = ingestProfile?.Ingest.Enabled == true
                ? _localization.Format("Settings.PolicyDetails.Ingest.Enabled",
                    ingestProfile.Name, ingest,
                    ChoiceLabel(SourceDispositionChoices,
                        ingestProfile.Ingest.SourceDisposition))
                : _localization.Get("Settings.PolicyDetails.Ingest.Preserved");

            return string.Join(Environment.NewLine + Environment.NewLine,
                _localization.Format("Settings.PolicyDetails.Introduction",
                    profile.Name,
                    ChoiceLabelForProfilePreset(profile.Preset),
                    formats),
                _localization.Format("Settings.PolicyDetails.NamingParagraph",
                    naming,
                    _localization.Get(profile.Naming.PreserveUnicode
                        ? "Settings.PolicyDetails.Unicode.Kept"
                        : "Settings.PolicyDetails.Unicode.Normalized")),
                _localization.Format("Settings.PolicyDetails.QualityParagraph",
                    discHandling,
                    profile.Quality.HighResolutionMinimumSampleRateHz,
                    profile.Quality.HighResolutionMinimumBitsPerSample),
                _localization.Format("Settings.PolicyDetails.MetadataParagraph",
                    OnOff(profile.Metadata.PreserveReplayGain),
                    OnOff(profile.Metadata.PreserveMusicBrainzIdentifiers),
                    OnOff(profile.Metadata.PreserveCustomFields),
                    OnOff(profile.Metadata.PreserveCompilationSemantics)),
                _localization.Format("Settings.PolicyDetails.ArtworkParagraph",
                    artworkReading,
                    ChoiceLabel(ArtworkStorageLocalizedChoices,
                        profile.Artwork.Storage),
                    ChoiceLabel(ArtworkRoleLocalizedChoices,
                        profile.Artwork.Roles),
                    ChoiceLabel(ArtworkEncodingLocalizedChoices,
                        profile.Artwork.Encoding)),
                _localization.Format("Settings.PolicyDetails.HealthParagraph",
                    rules,
                    ChoiceLabel(SidecarDispositionChoices,
                        profile.Sidecars.UnknownFileDisposition)),
                ingestBehavior,
                _localization.Format("Settings.PolicyDetails.ConnectionsParagraph",
                    integrations, exports),
                _localization.Format("Settings.PolicyDetails.RootsParagraph",
                    Environment.NewLine, roots),
                _localization.Format("Settings.PolicyDetails.ToolsParagraph",
                    FfmpegPath, WavpackPath,
                    string.IsNullOrWhiteSpace(MachineBindingsFile)
                        ? _localization.Get(
                            "Settings.PolicyDetails.Paths.StoredDirectly")
                        : _localization.Format(
                            "Settings.PolicyDetails.Paths.MachineBindings",
                            MachineBindingsFile)));
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
        ThemeChoice? choice = ThemeChoices.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.Ordinal));
        if (choice is not null && SelectedThemeChoice != choice)
            SelectedThemeChoice = choice;
    }

    partial void OnSelectedThemeChoiceChanged(ThemeChoice? value)
    {
        if (value is not null && SelectedTheme != value.Value)
            SelectedTheme = value.Value;
    }

    partial void OnActiveConfigurationPathChanged(string? value) =>
        OnPropertyChanged(nameof(ActiveConfigurationDisplay));

    partial void OnSelectedDisplayLanguageChanged(
        LocalizedChoice<string>? value)
    {
        if (!_refreshingDisplayLanguage &&
            value is not null)
            _localization.SetCulture(value.Value);
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshDisplayLanguageChoices();
        RefreshLocalizedChoices();
        RefreshLocalizedRuntimeText();
    }

    private void RefreshDisplayLanguageChoices()
    {
        _refreshingDisplayLanguage = true;
        try
        {
            foreach (var culture in
                     _localization.SupportedCultures)
            {
                LocalizedChoice<string>? choice =
                    DisplayLanguageChoices.FirstOrDefault(
                        item => string.Equals(
                            item.Value,
                            culture.Name,
                            StringComparison.OrdinalIgnoreCase));
                string label = _localization.Get(
                    LocalizationKeys.CultureName(
                        culture.Name));
                if (choice is null)
                    DisplayLanguageChoices.Add(
                        new(culture.Name, label));
                else
                    choice.Label = label;
            }
            SelectedDisplayLanguage =
                DisplayLanguageChoices.First(
                    item => string.Equals(
                        item.Value,
                        _localization.CurrentUICulture.Name,
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshingDisplayLanguage = false;
        }
    }

    private void RefreshLocalizedChoices()
    {
        RefreshChoices(CollisionPolicyChoices, SettingsChoiceLists.CollisionPolicies);
        RefreshChoices(UnicodeNormalizationChoices, SettingsChoiceLists.UnicodeNormalizations);
        RefreshChoices(DiscStrategyChoices, SettingsChoiceLists.DiscStrategies);
        RefreshChoices(TrackTotalScopeChoices, SettingsChoiceLists.TrackTotalScopes);
        RefreshChoices(HealthSeverityChoices, SettingsChoiceLists.HealthSeverities);
        RefreshChoices(SourceDispositionChoices, SettingsChoiceLists.SourceDispositions);
        RefreshChoices(IngestActionChoices, SettingsChoiceLists.IngestActions);
        RefreshChoices(IngestAlbumConditionChoices,
            SettingsChoiceLists.IngestAlbumConditions);
        RefreshChoices(IngestSourceSelectionChoices,
            SettingsChoiceLists.IngestSourceSelections);
        RefreshChoices(ArtworkStorageLocalizedChoices,
            SettingsChoiceLists.ArtworkStorageChoices);
        RefreshChoices(ArtworkRoleLocalizedChoices,
            SettingsChoiceLists.ArtworkRoleChoices);
        RefreshChoices(ArtworkEncodingLocalizedChoices,
            SettingsChoiceLists.ArtworkEncodingChoices);
        RefreshChoices(SidecarDispositionChoices,
            SettingsChoiceLists.SidecarDispositions);
        RefreshChoices(ExportSelectionKindChoices,
            SettingsChoiceLists.ExportSelectionKinds);
        RefreshChoices(ExportTransformModeChoices,
            SettingsChoiceLists.ExportTransformModes);
        RefreshChoices(ExportArtworkModeChoices,
            SettingsChoiceLists.ExportArtworkModes);
        RefreshChoices(ExportExtraFileDispositionChoices,
            SettingsChoiceLists.ExportExtraFileDispositions);
        RefreshChoices(MetadataFormatFamilyChoices, MetadataFormatFamilies);
        RefreshChoices(MetadataCanonicalFieldChoices, MetadataCanonicalFields);
        RefreshStringChoices(PlaylistTypeChoices, "PlaylistType",
            SettingsChoiceLists.PlaylistTypes);
        RefreshStringChoices(PlaylistSourceTypeChoices, "PlaylistSourceType",
            SettingsChoiceLists.PlaylistSourceTypes);
        RefreshStringChoices(PlaylistPathStyleChoices, "PlaylistPathStyle",
            SettingsChoiceLists.PlaylistPathStyles);
        RefreshStringChoices(PlaylistEncodingChoices, "PlaylistEncoding",
            SettingsChoiceLists.PlaylistEncodings);
        RefreshStringChoices(PlaylistLineEndingChoices, "PlaylistLineEnding",
            SettingsChoiceLists.PlaylistLineEndings);
        RefreshStringChoices(PlaylistFileNameTransformChoices,
            "PlaylistFileNameTransform",
            SettingsChoiceLists.PlaylistFileNameTransforms);

        foreach (SettingsChannelChoice value in SettingsChoiceLists.ChannelChoices)
        {
            LocalizedChoice<SettingsChannelChoice>? choice =
                ChannelLocalizedChoices.FirstOrDefault(item =>
                    item.Value.Value == value.Value);
            string label = _localization.Get(
                $"Settings.Choice.LibraryChannelSelection.{value.Value}");
            if (choice is null)
            {
                choice = new(value, label);
                ChannelLocalizedChoices.Add(choice);
            }
            else
                choice.Label = label;
        }

        foreach (ThemeChoice theme in ThemeChoices)
            theme.Name = _localization.Get(
                $"Settings.Choice.Theme.{ChoiceToken(theme.Value)}");
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IReadOnlyList<T> values)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice = target.FirstOrDefault(item =>
                EqualityComparer<T>.Default.Equals(item.Value, value));
            string label = _localization.Get(
                $"Settings.Choice.{typeof(T).Name}.{value}");
            if (choice is null)
            {
                choice = new(value, label);
                target.Add(choice);
            }
            else
                choice.Label = label;
        }
    }

    private void RefreshStringChoices(
        ObservableCollection<LocalizedChoice<string>> target,
        string group,
        IReadOnlyList<string> values)
    {
        foreach (string value in values)
        {
            LocalizedChoice<string>? choice = target.FirstOrDefault(item =>
                string.Equals(item.Value, value, StringComparison.Ordinal));
            string label = _localization.Get(
                $"Settings.Choice.{group}.{ChoiceToken(value)}");
            if (choice is null)
            {
                choice = new(value, label);
                target.Add(choice);
            }
            else
                choice.Label = label;
        }
    }

    private static string ChoiceToken(string value) =>
        string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_'));

    private string ChoiceLabel<T>(
        IEnumerable<LocalizedChoice<T>> choices,
        T value) =>
        choices.FirstOrDefault(choice =>
            EqualityComparer<T>.Default.Equals(choice.Value, value))?.Label ??
        value?.ToString() ?? "";

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusMessageKey = key;
        _statusMessageArguments = arguments;
        StatusMessage = _localization.Format(key, arguments);
    }

    private void SetDiscogsStatus(string key, params object?[] arguments)
    {
        _discogsStatusKey = key;
        _discogsStatusArguments = arguments;
        DiscogsCredentialStatus = _localization.Format(key, arguments);
    }

    private void SetFieldMappingStatus(string key, params object?[] arguments)
    {
        _fieldMappingStatusKey = key;
        _fieldMappingStatusArguments = arguments;
        FieldMappingStatus = _localization.Format(key, arguments);
    }

    private void RefreshLocalizedRuntimeText()
    {
        if (_statusMessageKey is not null)
            StatusMessage = _localization.Format(
                _statusMessageKey, _statusMessageArguments);
        if (_discogsStatusKey is not null)
            DiscogsCredentialStatus = _localization.Format(
                _discogsStatusKey, _discogsStatusArguments);
        if (_fieldMappingStatusKey is not null)
            FieldMappingStatus = _localization.Format(
                _fieldMappingStatusKey, _fieldMappingStatusArguments);
        foreach (IndexTargetEditorRow root in IndexTargets)
            root.RefreshPermissionSummary();
        if (AdvancedIngestProfile is not null)
            foreach (IngestRecipeEditorRow recipe in
                     AdvancedIngestProfile.Recipes)
                recipe.RefreshLocalizedText(
                    _localization);
        OnPropertyChanged(nameof(ActiveConfigurationDisplay));
        OnPropertyChanged(nameof(EffectivePolicySummary));
        OnPropertyChanged(nameof(EffectivePolicyDetails));
        RefreshSyncTargetChoices();
        RefreshDestinationRootChoices();
        if (ValidationSummary is not null)
            UpdateValidation();
    }

    partial void OnFpcalcPathChanged(string value) =>
        _settings.SetPreference(
            AudioFingerprintService.ExecutablePreferenceKey,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnOptimFrogToolsDirectoryChanged(
        string? value) =>
        _settings.SetPreference(
            OptimFrogFingerprintInputService
                .ToolsDirectoryPreferenceKey,
            string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim());

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
                _localization.Get("Settings.Discogs.EnterToken"));
        try
        {
            await _secrets.WriteAsync(
                DiscogsMetadataProvider.TokenSecretKey,
                token);
            DiscogsToken = null;
            if (_secrets.IsPersistent)
                SetDiscogsStatus("Settings.Discogs.StoredPersistent", _secrets.Kind);
            else
                SetDiscogsStatus("Settings.Discogs.StoredSession");
        }
        catch (Exception error)
        {
            SetDiscogsStatus("Settings.Discogs.StoreFailed", error.Message);
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
            SetDiscogsStatus("Settings.Discogs.NoneStored");
        }
        catch (Exception error)
        {
            SetDiscogsStatus("Settings.Discogs.ClearFailed", error.Message);
        }
    }

    private async Task RefreshDiscogsCredentialStatusAsync()
    {
        try
        {
            string? token = await _secrets.ReadAsync(
                DiscogsMetadataProvider.TokenSecretKey);
            if (string.IsNullOrWhiteSpace(token))
                SetDiscogsStatus("Settings.Discogs.NoneStored");
            else if (_secrets.IsPersistent)
                SetDiscogsStatus("Settings.Discogs.ExistsPersistent", _secrets.Kind);
            else
                SetDiscogsStatus("Settings.Discogs.ExistsSession");
        }
        catch (Exception error)
        {
            SetDiscogsStatus("Settings.Discogs.Unavailable", error.Message);
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
        else if (row is IngestRecipeEditorRow recipe)
            recipe.RefreshLocalizedText(
                _localization);
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
            : _localization.Get("Settings.Validation.Header") + Environment.NewLine +
              string.Join(Environment.NewLine, issues.Select(issue =>
                  _localization.Format("Settings.Validation.Item", issue.Message)));
        SaveConfigurationCommand.NotifyCanExecuteChanged();
        SaveConfigurationAsCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<(int Tab, string Message)> ValidationIssues()
    {
        var issues = new List<(int, string)>();
        if (SelectedLibraryProfile is null)
            issues.Add((5, _localization.Get(
                "Settings.Validation.ChooseRootPolicy")));
        if (SelectedIngestProfile is null)
            issues.Add((6, _localization.Get(
                "Settings.Validation.ChooseIngestProfile")));
        if (!IndexTargets.Any(target => !string.IsNullOrWhiteSpace(target.Path)))
            issues.Add((1, _localization.Get(
                "Settings.Validation.AddLibraryRoot")));
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
                issues.Add((1, _localization.Format(
                    "Settings.Validation.LibraryRootError",
                    target.Path, error.Message)));
            }
            if (target.IsSyncTarget && !target.AllowSynchronizationOutput)
                issues.Add((1, _localization.Format(
                    "Settings.Validation.SyncOutputNotAllowed", target.Path)));
            if (!string.IsNullOrWhiteSpace(target.ProfileId) &&
                _editing.Profiles.All(profile => !string.Equals(profile.Id,
                    target.ProfileId, StringComparison.OrdinalIgnoreCase)))
                issues.Add((1, _localization.Format(
                    "Settings.Validation.UnknownRootProfile",
                    target.Path, target.ProfileId)));
        }
        var configuredSets = IndexTargets.SelectMany(target => target.Memberships)
            .SelectMany(membership => LibraryConfiguration.ParseScanSets(membership.Name))
            .ToHashSet(LibraryConfiguration.ScanSetComparer);
        foreach (PlaylistSourceEditorRow source in PlaylistSources.Where(source =>
                     !string.IsNullOrWhiteSpace(source.Location)))
            if (!string.Equals(source.Type, "m3u", StringComparison.OrdinalIgnoreCase))
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistSourceType", source.Location)));
        foreach (PlaylistTargetEditorRow target in PlaylistTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
        {
            if (!PlaylistTypes.Contains(target.Type, StringComparer.OrdinalIgnoreCase))
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistTargetType",
                    target.Target, target.Type)));
            IReadOnlyList<string> selectedSets =
                LibraryConfiguration.ParseScanSets(target.Sets);
            if (selectedSets.Count == 0)
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistTargetSetRequired",
                    target.Target)));
            string[] unknownSets = selectedSets.Where(set => !configuredSets.Contains(set))
                .ToArray();
            if (unknownSets.Length > 0)
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistUnknownSets",
                    target.Target, string.Join(", ", unknownSets))));
            if (!PlaylistPathStyles.Contains(target.PathStyle,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistEncodings.Contains(target.Encoding,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistLineEndings.Contains(target.LineEnding,
                    StringComparer.OrdinalIgnoreCase) ||
                !PlaylistFileNameTransforms.Contains(target.FileNameTransform,
                    StringComparer.OrdinalIgnoreCase))
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistUnsupportedOption",
                    target.Target)));
            if (target.MaxTrackCount <= 0)
                issues.Add((2, _localization.Format(
                    "Settings.Validation.PlaylistTrackRequired",
                    target.Target)));
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
                issues.Add((5, _localization.Format(
                    "Settings.Validation.PolicyError", error.Message)));
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
                        issues.Add((6, _localization.Format(
                            "Settings.Validation.IngestUnknownRoot",
                            recipe.Id, destinationRootId)));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add((6, _localization.Format(
                    "Settings.Validation.IngestError", error.Message)));
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
                    issues.Add((2, _localization.Format(
                        "Settings.Validation.DuplicateExportId", profile.Id)));
                if (profile.Naming.LibraryProfileId is { } namingId &&
                    _editing.Profiles.All(item => !string.Equals(item.Id, namingId,
                        StringComparison.OrdinalIgnoreCase)))
                    issues.Add((2, _localization.Format(
                        "Settings.Validation.ExportUnknownNamingProfile",
                        profile.Id, namingId)));
                if (profile.Transform.RecipeId is { } recipeId &&
                    (editedIngestProfile?.Ingest.Recipes.All(recipe => !string.Equals(recipe.Id,
                        recipeId, StringComparison.OrdinalIgnoreCase)) ?? true) &&
                    _editing.IngestProfiles.SelectMany(item => item.Ingest.Recipes).All(recipe =>
                        !string.Equals(recipe.Id, recipeId,
                            StringComparison.OrdinalIgnoreCase)))
                    issues.Add((2, _localization.Format(
                        "Settings.Validation.ExportUnknownRecipe",
                        profile.Id, recipeId)));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add((2, _localization.Format(
                    "Settings.Validation.ExportError", error.Message)));
            }
        }
        if (OversizedArtworkByteThreshold is < 262_144 or > 1_073_741_824)
            issues.Add((4, _localization.Get(
                "Settings.Validation.ArtworkSizeThreshold")));
        if (OversizedArtworkDimensionThreshold is < 64 or > 100_000)
            issues.Add((4, _localization.Get(
                "Settings.Validation.ArtworkDimensionThreshold")));
        if (ArtworkRepairTargetByteSize is < 65_536 or > 1_073_741_824)
            issues.Add((4, _localization.Get(
                "Settings.Validation.ArtworkRepairSize")));
        if (ArtworkRepairTargetDimension is < 64 or > 100_000)
            issues.Add((4, _localization.Get(
                "Settings.Validation.ArtworkRepairDimension")));
        return issues;
    }

    [RelayCommand]
    private void OpenValidation() => SelectedTabIndex = ValidationTabIndex;

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!HasUnsavedChanges)
            return true;
        return await _dialogs.ConfirmAsync(
            _localization.Get("Settings.Dialog.Discard.Title"),
            _localization.Get("Settings.Dialog.Discard.Message"),
            _localization.Get("Settings.Dialog.Discard.Accept"));
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
            SetStatus("Settings.Status.ChangesDiscarded");
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
        SetStatus("Settings.Status.ChangesDiscarded");
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
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.OpenConfiguration"),
            [new FilePickerType(
                _localization.Get("Settings.FilePicker.ConfigurationType"),
                [".xml"])]);
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
        SetStatus("Settings.Status.NewConfiguration");
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
        SetStatus("Settings.Status.GuidedSetup");
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
            UniqueProfileName(_localization.Get("Settings.Profile.NewName")));
        _editing.Profiles.Add(profile);
        LibraryProfiles.Add(profile);
        SelectedLibraryProfile = profile;
        SetStatus("Settings.Status.ProfileCreated", profile.Name);
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
            Name = UniqueProfileName(source.Name +
                _localization.Get("Settings.Profile.CopySuffix")),
            Preset = LibraryProfilePreset.Custom,
        };
        _editing.Profiles.Add(duplicate);
        LibraryProfiles.Add(duplicate);
        SelectedLibraryProfile = duplicate;
        SetStatus("Settings.Status.ProfileDuplicated",
            source.Name, duplicate.Name);
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
            ? _localization.Get(
                "Settings.Dialog.DeleteProfile.NoReferences")
            : _localization.Format(
                "Settings.Dialog.DeleteProfile.Reassignment",
                _localization.FormatCount(
                    "Settings.Dialog.DeleteProfile.RootReferences",
                    rootReferences),
                _localization.FormatCount(
                    "Settings.Dialog.DeleteProfile.WorkflowReferences",
                    workflowReferences),
                fallback.Name);
        if (!await _dialogs.ConfirmAsync(
                _localization.Format(
                    "Settings.Dialog.DeleteProfile.Title", selected.Name),
                _localization.Format(
                    "Settings.Dialog.DeleteProfile.Message", reassignment),
                _localization.Get(
                    "Settings.Dialog.DeleteProfile.Accept")))
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
        SetStatus("Settings.Status.ProfileDeleted", selected.Name);
        MarkDirty();
    }

    [RelayCommand]
    private void CreateIngestProfile()
    {
        CommitAdvancedIngestProfile(updateProfileChoices: true);
        string id = UniqueIngestProfileId();
        var profile = new LibraryIngestProfile(
            id,
            UniqueIngestProfileName(
                _localization.Get("Settings.IngestProfile.NewName")),
            new(false, LibrarySourceDisposition.Preserve, true, []));
        _editing.IngestProfiles.Add(profile);
        IngestProfiles.Add(profile);
        SelectedIngestProfile = profile;
        SetStatus("Settings.Status.IngestProfileCreated", profile.Name);
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
            Name = UniqueIngestProfileName(source.Name +
                _localization.Get("Settings.Profile.CopySuffix")),
        };
        _editing.IngestProfiles.Add(duplicate);
        IngestProfiles.Add(duplicate);
        SelectedIngestProfile = duplicate;
        SetStatus("Settings.Status.IngestProfileDuplicated", source.Name);
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
                _localization.Format(
                    "Settings.Dialog.DeleteIngestProfile.Title",
                    selected.Name),
                _localization.Format(
                    "Settings.Dialog.DeleteIngestProfile.Message",
                    fallback.Name),
                _localization.Get(
                    "Settings.Dialog.DeleteIngestProfile.Accept")))
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
        SetStatus("Settings.Status.IngestProfileDeleted", selected.Name);
        MarkDirty();
    }

    [RelayCommand]
    private async Task FinishGuidedSetupAsync()
    {
        UpdateValidation();
        if (!IsEditorValid)
        {
            SetStatus("Settings.Status.ResolveSetupIssues");
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
            SetFieldMappingStatus(
                FieldMappings.Count == 1
                    ? "Settings.FieldMappings.Status.Saved.One"
                    : "Settings.FieldMappings.Status.Saved.Other",
                FieldMappings.Count);
        }
        catch (Exception error)
        {
            SetFieldMappingStatus(
                "Settings.FieldMappings.Status.SaveFailed", error.Message);
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
        string? path = await _files.PickFolderAsync(
            _localization.Get("Settings.FilePicker.ExportDestination"));
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
        string? path = await _files.PickFolderAsync(
            _localization.Get("Settings.FilePicker.LibraryRoot"));
        if (path is not null)
            row.Path = path;
    }

    [RelayCommand]
    private async Task BrowsePlaylistTargetAsync(PlaylistTargetEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFolderAsync(
            _localization.Get("Settings.FilePicker.PlaylistExportFolder"));
        if (path is not null)
            row.Target = path;
    }

    [RelayCommand]
    private async Task BrowsePlaylistSourceAsync(PlaylistSourceEditorRow? row)
    {
        if (row is null)
            return;
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.PlaylistSource"),
            [new FilePickerType(
                _localization.Get("Settings.FilePicker.PlaylistType"),
                [".m3u", ".m3u8"])]);
        if (path is not null)
            row.Location = path;
    }

    [RelayCommand]
    private async Task BrowseDatabaseAsync()
    {
        string? path = await _files.SaveFileAsync(
            _localization.Get("Settings.FilePicker.MetadataCache"),
            "cache.db", ".db");
        if (path is not null)
            DatabaseFile = path;
    }

    [RelayCommand]
    private async Task BrowseMachineBindingsAsync()
    {
        string? path = await _files.SaveFileAsync(
            _localization.Get("Settings.FilePicker.MachineBindings"),
            "library.bindings.xml", ".xml");
        if (path is not null)
            MachineBindingsFile = path;
    }

    [RelayCommand]
    private async Task BrowseItunesLibraryAsync()
    {
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.ItunesLibrary"),
            [new FilePickerType(
                _localization.Get("Settings.FilePicker.ItunesLibraryType"),
                [".itl"])]);
        if (path is not null)
            ItunesLibraryPath = path;
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        // Executables commonly have no extension on macOS and Linux. Leaving the picker
        // unfiltered supports ffmpeg, ffmpeg.exe, and user-provided wrapper scripts.
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.Ffmpeg"));
        if (path is not null)
            FfmpegPath = path;
    }

    [RelayCommand]
    private async Task BrowseWavpackAsync()
    {
        // Executables commonly have no extension on macOS and Linux.
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.Wavpack"));
        if (path is not null)
            WavpackPath = path;
    }

    [RelayCommand]
    private async Task BrowseFpcalcAsync()
    {
        string? path = await _files.PickFileAsync(
            _localization.Get("Settings.FilePicker.Fpcalc"));
        if (path is not null)
            FpcalcPath = path;
    }

    [RelayCommand]
    private async Task BrowseOptimFrogToolsAsync()
    {
        string? path = await _files.PickFolderAsync(
            _localization.Get("Settings.FilePicker.OptimFrog"));
        if (path is not null)
            OptimFrogToolsDirectory = path;
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
        SetStatus("Settings.Status.RecentConfigurationsCleared");
    }

    private void LoadConfiguration(string path)
    {
        try
        {
            IsGuidedSetupActive = false;
            _settings.LoadConfig(path);
            LoadEditor(path);
            SelectedTabIndex = 1;
            SetStatus("Settings.Status.ConfigurationLoaded");
        }
        catch (Exception error)
        {
            SetStatus("Settings.Status.ConfigurationLoadFailed", error.Message);
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
            SetStatus("Settings.Status.ConfigurationEditFailed", error.Message);
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
            SetStatus("Settings.Status.ResolveValidationIssues");
            SelectedTabIndex = ValidationTabIndex;
            return;
        }
        path ??= await _files.SaveFileAsync(
            _localization.Get("Settings.FilePicker.SaveConfiguration"),
            "library.xml", ".xml");
        if (path is null)
            return;
        try
        {
            CommitAdvancedProfile(updateProfileChoices: false);
            CommitAdvancedIngestProfile(updateProfileChoices: false);
            _editing.ActiveProfileId = SelectedLibraryProfile?.Id ??
                throw new InvalidDataException(_localization.Get(
                    "Settings.Validation.ChooseRootPolicy"));
            _editing.ActiveIngestProfileId = SelectedIngestProfile?.Id ??
                throw new InvalidDataException(_localization.Get(
                    "Settings.Validation.ChooseIngestProfile"));
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
            SetStatus("Settings.Status.ConfigurationSaved");
            HasUnsavedChanges = false;
            IsGuidedSetupActive = false;
            ValidationSummary = null;
        }
        catch (Exception error)
        {
            SetStatus("Settings.Status.ConfigurationSaveFailed", error.Message);
            await _dialogs.ShowMessageAsync(
                _localization.Get("Settings.Dialog.SaveFailed.Title"),
                error.Message);
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
                SetStatus("Settings.Status.ActiveConfigurationChanged");
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
            recipe.RefreshDestinationRootChoices(
                IndexTargets,
                _localization.Get("Settings.DestinationRoot.None"),
                _localization.Get("Settings.DestinationRoot.NewRoot"),
                id => _localization.Format(
                    "Settings.DestinationRoot.Missing", id));
    }

    private void RefreshSyncTargetChoices()
    {
        Guid? selectedId = IndexTargets.FirstOrDefault(target => target.IsSyncTarget)?.Id;
        _refreshingSyncTargetChoices = true;
        try
        {
            SyncTargetRootChoices.Clear();
            SyncTargetRootChoices.Add(new(null,
                _localization.Get("Settings.SyncTarget.None")));
            foreach (IndexTargetEditorRow root in IndexTargets)
            {
                string label = string.IsNullOrWhiteSpace(root.Path)
                    ? _localization.Get("Settings.SyncTarget.NewRoot")
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
        row.SetPermissionSummaryFormatter(FormatPermissionSummary);
        row.RefreshProfileChoices(LibraryProfiles);
        return row;
    }

    private static string? EffectiveOffset(
        IndexTargetSetEntry membership,
        IndexTargetEntry target) =>
        CleanOptional(membership.Offset) ?? CleanOptional(target.DefaultOffset);

    private string FormatPermissions(LibraryRootPermissions permissions)
    {
        if (permissions == LibraryRootPermissions.None)
            return _localization.Get("Settings.Permissions.CatalogOnly");
        var labels = new List<string>();
        if (permissions.HasFlag(LibraryRootPermissions.WriteMetadata))
            labels.Add(_localization.Get("Settings.Permissions.Metadata"));
        if (permissions.HasFlag(LibraryRootPermissions.WriteArtwork))
            labels.Add(_localization.Get("Settings.Permissions.Artwork"));
        if (permissions.HasFlag(LibraryRootPermissions.OrganizeFiles))
            labels.Add(_localization.Get("Settings.Permissions.Organization"));
        if (permissions.HasFlag(LibraryRootPermissions.IngestOutput))
            labels.Add(_localization.Get("Settings.Permissions.IngestOutput"));
        if (permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput))
            labels.Add(_localization.Get("Settings.Permissions.SyncOutput"));
        return string.Join(", ", labels);
    }

    private string FormatPermissionSummary(
        LibraryRootPermissions permissions) =>
        permissions == LibraryRootPermissions.None
            ? _localization.Get("Settings.Permissions.Summary.ReadOnly")
            : _localization.Format("Settings.Permissions.Summary.Allowed",
                FormatPermissions(permissions));

    private string DescribeRootPermissions(
        LibraryRootPermissions permissions)
    {
        if (permissions == LibraryRootPermissions.None)
            return _localization.Get("Settings.Permissions.Description.ReadOnly");
        var actions = new List<string>();
        if (permissions.HasFlag(LibraryRootPermissions.WriteMetadata))
            actions.Add(_localization.Get("Settings.Permissions.Action.Metadata"));
        if (permissions.HasFlag(LibraryRootPermissions.WriteArtwork))
            actions.Add(_localization.Get("Settings.Permissions.Action.Artwork"));
        if (permissions.HasFlag(LibraryRootPermissions.OrganizeFiles))
            actions.Add(_localization.Get("Settings.Permissions.Action.Organize"));
        if (permissions.HasFlag(LibraryRootPermissions.IngestOutput))
            actions.Add(_localization.Get("Settings.Permissions.Action.Ingest"));
        if (permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput))
            actions.Add(_localization.Get("Settings.Permissions.Action.Sync"));
        return _localization.Format("Settings.Permissions.Description.Writable",
            string.Join(", ", actions));
    }

    private string ChoiceLabelForProfilePreset(LibraryProfilePreset value) =>
        _localization.Get($"Settings.Choice.LibraryProfilePreset.{value}");

    private string OnOff(bool value) => _localization.Get(value
        ? "Settings.PolicyDetails.Preserved"
        : "Settings.PolicyDetails.NotCopied");

    private static string? CleanOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ThemeChoice(
    string value,
    string name,
    string canvas,
    string raised,
    string accent) : ObservableObject
{
    private string _name = name;

    public string Value { get; } = value;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
    public string Canvas { get; } = canvas;
    public string Raised { get; } = raised;
    public string Accent { get; } = accent;
}
