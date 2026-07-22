using System.Xml.Linq;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibrary.Core.Models;

public sealed record IngestMusicConfiguration
{
    private static readonly LibraryProfile LegacyProfile =
        LibraryProfilePresets.Create(LibraryProfilePreset.LegacyMusicLibraryTools);
    private static readonly IReadOnlyDictionary<Guid, LibraryIndexLocation> EmptyRootTargets =
        new Dictionary<Guid, LibraryIndexLocation>();
    private static readonly IReadOnlyDictionary<string, LibraryProfile> LegacyProfiles =
        new Dictionary<string, LibraryProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [LibraryProfilePresets.LegacyId] = LegacyProfile,
        };

    public required string FfmpegPath { get; init; }
    public string WavpackPath { get; init; } = "wavpack";
    public required string AacDestination { get; init; }
    public string? ItunesLibraryPath { get; init; }
    public required string CdDestination { get; init; }
    public required string PairedCdDestination { get; init; }
    public required string HighResolutionDestination { get; init; }
    public int LengthLimit { get; init; } = 255;
    public int DiscNumLengthLimit { get; init; } = 255;
    public string AacEncoder { get; init; } = "libfdk_aac";
    public int AacBitrateKbps { get; init; } = 256;
    public bool DeleteSourcesAfterIngest { get; init; }
    public bool RemoveNonMusicAfterIngest { get; init; }
    public LibraryProfile Profile { get; init; } = LegacyProfile;
    public LibraryPolicySnapshot? PolicySnapshot { get; init; }
    public IReadOnlyDictionary<Guid, LibraryIndexLocation> RootTargets { get; init; } =
        EmptyRootTargets;
    public IReadOnlyDictionary<string, LibraryProfile> Profiles { get; init; } =
        LegacyProfiles;
    public LibrarySourceDisposition? ConfiguredSourceDisposition { get; init; }
    public LibrarySourceDisposition SourceDisposition => ConfiguredSourceDisposition ??
        (DeleteSourcesAfterIngest
            ? LibrarySourceDisposition.Delete
            : LibrarySourceDisposition.Quarantine);
    public bool PreserveSidecars { get; init; }

    public LibrarySidecarDisposition SidecarDispositionFor(
        string path,
        string sourceDirectory)
    {
        if (PreserveSidecars &&
            Profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools)
            return LibrarySidecarDisposition.Preserve;
        string relative = Path.GetRelativePath(sourceDirectory, path);
        return Profile.Sidecars.ResolveDisposition(relative, SourceDisposition);
    }

    public bool HasSidecarCleanup(
        IEnumerable<IngestFileSnapshot> files,
        string sourceDirectory) =>
        files.Any(file => SidecarDispositionFor(file.Path, sourceDirectory) is
            LibrarySidecarDisposition.Quarantine or LibrarySidecarDisposition.Delete);

    public LibraryProfile ResolveProfile(LibraryIngestRecipe recipe)
        => ResolveDestinationProfile(recipe);

    public LibraryIndexLocation? ResolveTarget(LibraryIngestRecipe recipe)
    {
        if (recipe.DestinationRootId is Guid rootId &&
            RootTargets.TryGetValue(rootId, out LibraryIndexLocation? root))
            return root;
        return null;
    }

    public LibraryProfile ResolveDestinationProfile(LibraryIngestRecipe recipe)
    {
        LibraryIndexLocation? target = ResolveTarget(recipe);
        return target is not null &&
               Profiles.TryGetValue(target.ProfileId, out LibraryProfile? profile)
            ? profile
            : Profile;
    }

    public static IReadOnlyList<string> MissingLibrarySettings(LibraryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        LibraryIndexLocation[] locations = configuration.IndexLocations.ToArray();
        LibraryProfile profile = ResolveLegacyRecipeDestinations(
            configuration.ActiveProfile with
            {
                Ingest = configuration.ActiveIngestProfile.Ingest,
            }, locations, configuration.ItunesLibraryPath);
        if (!profile.Ingest.Enabled)
            return [
            $"The active ingest profile '{configuration.ActiveIngestProfile.Name}' does not " +
                "enable ingest. Choose an ingest-enabled profile or add an ingest recipe."
            ];
        var missing = new List<string>();
        foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes.Where(item => item.Enabled))
        {
            if (recipe.AddToMediaCatalog &&
                string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath))
            {
                missing.Add($"Ingest recipe '{recipe.Name}' adds output to the media catalog, " +
                    "but no iTunes library is configured.");
                continue;
            }
            LibraryIndexLocation? target = recipe.DestinationRootId is Guid rootId
                ? locations.SingleOrDefault(location => location.RootId == rootId)
                : null;
            if (target is null && !(recipe.AddToMediaCatalog &&
                                    !string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath)))
            {
                missing.Add($"Ingest recipe '{recipe.Name}' has no destination root.");
                continue;
            }
            if (target is not null &&
                !target.Permissions.HasFlag(LibraryRootPermissions.IngestOutput))
                missing.Add($"Ingest recipe '{recipe.Name}' targets '{target.Target}', which does " +
                    "not permit ingest output.");
        }
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IngestMusicConfiguration FromLibraryConfiguration(
        LibraryConfiguration configuration)
    {
        IReadOnlyList<string> missing = MissingLibrarySettings(configuration);
        if (missing.Count > 0)
            throw new InvalidDataException(string.Join(" ", missing));
        LibraryIndexLocation[] locations = configuration.IndexLocations.ToArray();
        LibraryIngestSettings settings = configuration.IngestSettings;
        LibraryProfile profile = ResolveLegacyRecipeDestinations(
            configuration.ActiveProfile with
            {
                Ingest = configuration.ActiveIngestProfile.Ingest,
            }, locations, configuration.ItunesLibraryPath);
        string RecipeDestination(string recipeId) => profile.Ingest.Recipes
            .FirstOrDefault(recipe => string.Equals(
                recipe.Id, recipeId, StringComparison.OrdinalIgnoreCase)) is { } recipe
            ? locations.SingleOrDefault(location =>
                location.RootId == recipe.DestinationRootId)?.Target ?? ""
            : "";
        LibrarySourceDisposition disposition = configuration.SchemaVersion ==
            LibraryConfigurationSchema.LegacyVersion
            ? settings.DeleteSourcesAfterIngest
                ? LibrarySourceDisposition.Delete
                : LibrarySourceDisposition.Quarantine
            : profile.Ingest.SourceDisposition;
        return new IngestMusicConfiguration
        {
            FfmpegPath = configuration.FfmpegPath,
            WavpackPath = configuration.WavpackPath,
            ItunesLibraryPath = configuration.ItunesLibraryPath,
            CdDestination = RecipeDestination("legacy-cd-flac"),
            PairedCdDestination = RecipeDestination("legacy-paired-cd-flac"),
            HighResolutionDestination = RecipeDestination("legacy-hires-flac"),
            AacDestination = RecipeDestination("legacy-aac"),
            LengthLimit = configuration.LengthLimit,
            DiscNumLengthLimit = configuration.DiscNumLengthLimit,
            AacEncoder = settings.AacEncoder,
            AacBitrateKbps = settings.AacBitrateKbps,
            DeleteSourcesAfterIngest = settings.DeleteSourcesAfterIngest,
            RemoveNonMusicAfterIngest = settings.RemoveNonMusicAfterIngest,
            Profile = profile,
            PolicySnapshot = configuration.PolicySnapshot,
            RootTargets = locations.ToDictionary(location => location.RootId),
            Profiles = configuration.Profiles.ToDictionary(
                item => item.Id,
                item => item.Id == profile.Id ? profile : item,
                StringComparer.OrdinalIgnoreCase),
            ConfiguredSourceDisposition = disposition,
            PreserveSidecars = profile.Ingest.PreserveSidecars,
        };
    }

    private static LibraryProfile ResolveLegacyRecipeDestinations(
        LibraryProfile profile,
        IReadOnlyList<LibraryIndexLocation> locations,
        string? itunesLibraryPath)
    {
        IReadOnlyDictionary<LibraryIngestRole, LibraryIndexLocation> targets =
            LegacyTargets(locations);
        LibraryIngestRecipe[] recipes = profile.Ingest.Recipes.Select(recipe =>
        {
            bool catalogFallback = recipe.DestinationLegacyRole ==
                LibraryIngestRole.AacFallback &&
                !string.IsNullOrWhiteSpace(itunesLibraryPath);
            Guid? destinationRootId = recipe.DestinationRootId;
            if (catalogFallback)
                destinationRootId = null;
            else if (destinationRootId is null &&
                     recipe.DestinationLegacyRole != LibraryIngestRole.None &&
                     targets.TryGetValue(recipe.DestinationLegacyRole, out var target))
                destinationRootId = target.RootId;
            bool unresolvedLegacyDestination = destinationRootId is null &&
                !catalogFallback &&
                recipe.DestinationLegacyRole != LibraryIngestRole.None;
            return recipe with
            {
                Enabled = recipe.Enabled && !unresolvedLegacyDestination,
                DestinationRootId = destinationRootId,
                DestinationLegacyRole = LibraryIngestRole.None,
                OutputRepresentationRole = LibraryRepresentationRole.Ignore,
                AddToMediaCatalog = recipe.AddToMediaCatalog || catalogFallback,
            };
        }).ToArray();
        return profile with
        {
            Ingest = profile.Ingest with
            {
                Enabled = profile.Ingest.Enabled && recipes.Any(recipe => recipe.Enabled),
                Recipes = recipes,
            },
        };
    }

    private static IReadOnlyDictionary<LibraryIngestRole, LibraryIndexLocation> LegacyTargets(
        IEnumerable<LibraryIndexLocation> locations)
    {
        var targets = new Dictionary<LibraryIngestRole, LibraryIndexLocation>();
        foreach (LibraryIndexLocation location in locations.Where(location =>
                     location.IngestRole != LibraryIngestRole.None))
        {
            if (!targets.TryAdd(location.IngestRole, location))
                throw new InvalidDataException(
                    $"More than one IndexTarget is assigned legacy ingest role " +
                    $"'{location.IngestRole}'.");
        }
        return targets;
    }

    public static (IngestMusicConfiguration Configuration, string? ConfigurationPath) Resolve(
        IngestRequest request, IAppSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        AppConfigurationSnapshot? snapshot = settings?.GetSnapshot();
        if (string.IsNullOrWhiteSpace(request.ConfigurationPath))
        {
            if (snapshot?.Configuration is null)
                throw new InvalidOperationException("Load a library configuration before using Ingest.");
            return (FromLibraryConfiguration(snapshot.Configuration), snapshot.ConfigPath);
        }

        string fullPath = Path.GetFullPath(request.ConfigurationPath);
        if (snapshot?.Configuration is not null && snapshot.ConfigPath is not null &&
            PathComparer.Equals(Path.GetFullPath(snapshot.ConfigPath), fullPath))
            return (FromLibraryConfiguration(snapshot.Configuration), fullPath);

        XElement root = XDocument.Load(fullPath).Root
            ?? throw new InvalidDataException("The configuration file is empty.");
        return root.Name.LocalName switch
        {
            "LibraryConfiguration" =>
                (FromLibraryConfiguration(new LibraryConfiguration(fullPath)), fullPath),
            "IngestMusicConfiguration" => (Load(fullPath), fullPath),
            _ => throw new InvalidDataException(
                "Expected a LibraryConfiguration or legacy IngestMusicConfiguration root element."),
        };
    }

    public static IngestMusicConfiguration Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var root = XDocument.Load(fullPath).Element("IngestMusicConfiguration")
            ?? throw new InvalidDataException("Missing <IngestMusicConfiguration> root element.");
        string Required(string name)
        {
            string? value = (string?)root.Element(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Missing or empty <{name}>.");
            return value.Trim();
        }
        int Positive(string name, int fallback)
        {
            string? value = (string?)root.Element(name);
            if (value is null) return fallback;
            if (!int.TryParse(value, out int parsed) || parsed <= 0)
                throw new InvalidDataException($"<{name}> must be a positive integer.");
            return parsed;
        }
        string ResolveRoot(string value) => Path.GetFullPath(value, Path.GetDirectoryName(fullPath)!);
        string? OptionalPath(string name)
        {
            string? value = (string?)root.Element(name);
            return string.IsNullOrWhiteSpace(value) ? null : ResolveRoot(value.Trim());
        }

        string aacDestination = ResolveRoot(Required("AacDestination"));
        string cdDestination = ResolveRoot(Required("CdDestination"));
        string pairedCdDestination = ResolveRoot(Required("PairedCdDestination"));
        string highResolutionDestination = ResolveRoot(Required("HighResolutionDestination"));
        string? itunesLibraryPath = OptionalPath("ItunesLibrary");
        LibraryIndexLocation LegacyRoot(string destination, LibraryIngestRole role) => new(
            destination, null, [], null, false, role, false, false,
            LibraryConfigurationSchema.CreateStableId(
                $"legacy-ingest-root|{fullPath}|{role}"),
            LibraryProfilePresets.LegacyId,
            LibraryRootPermissions.IngestOutput);
        LibraryIndexLocation[] locations =
        [
            LegacyRoot(cdDestination, LibraryIngestRole.Cd),
            LegacyRoot(pairedCdDestination, LibraryIngestRole.CdFallback),
            LegacyRoot(highResolutionDestination, LibraryIngestRole.HiRes),
            LegacyRoot(aacDestination, LibraryIngestRole.AacFallback),
        ];
        LibraryProfile profile = ResolveLegacyRecipeDestinations(
            LegacyProfile, locations, itunesLibraryPath);

        return new IngestMusicConfiguration
        {
            FfmpegPath = Required("FfmpegPath"),
            WavpackPath = ((string?)root.Element("WavpackPath") ?? "wavpack").Trim(),
            AacDestination = aacDestination,
            ItunesLibraryPath = itunesLibraryPath,
            CdDestination = cdDestination,
            PairedCdDestination = pairedCdDestination,
            HighResolutionDestination = highResolutionDestination,
            LengthLimit = Positive("LengthLimit", 255),
            DiscNumLengthLimit = Positive("DiscNumLengthLimit", 255),
            AacEncoder = ((string?)root.Element("AacEncoder") ?? "libfdk_aac").Trim(),
            AacBitrateKbps = Positive("AacBitrateKbps", 256),
            DeleteSourcesAfterIngest = (bool?)root.Element("DeleteSourcesAfterIngest") ?? false,
            RemoveNonMusicAfterIngest = (bool?)root.Element("RemoveNonMusicAfterIngest") ?? false,
            Profile = profile,
            Profiles = new Dictionary<string, LibraryProfile>(
                StringComparer.OrdinalIgnoreCase) { [profile.Id] = profile },
            RootTargets = locations.ToDictionary(location => location.RootId),
        };
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WavpackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(AacDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(CdDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(PairedCdDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(HighResolutionDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(AacEncoder);
        if (LengthLimit <= 0 || DiscNumLengthLimit <= 0 || AacBitrateKbps <= 0)
            throw new InvalidDataException("Length limits and AAC bitrate must be positive integers.");

        var document = new XDocument(
            new XElement("IngestMusicConfiguration",
                new XElement("FfmpegPath", FfmpegPath),
                new XElement("WavpackPath", WavpackPath),
                new XElement("AacDestination", AacDestination),
                string.IsNullOrWhiteSpace(ItunesLibraryPath) ? null : new XElement("ItunesLibrary", ItunesLibraryPath),
                new XElement("CdDestination", CdDestination),
                new XElement("PairedCdDestination", PairedCdDestination),
                new XElement("HighResolutionDestination", HighResolutionDestination),
                new XElement("LengthLimit", LengthLimit),
                new XElement("DiscNumLengthLimit", DiscNumLengthLimit),
                new XElement("AacEncoder", AacEncoder),
                new XElement("AacBitrateKbps", AacBitrateKbps),
                new XElement("DeleteSourcesAfterIngest", DeleteSourcesAfterIngest),
                new XElement("RemoveNonMusicAfterIngest", RemoveNonMusicAfterIngest)));
        AtomicFile.Write(path, document.Save);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
