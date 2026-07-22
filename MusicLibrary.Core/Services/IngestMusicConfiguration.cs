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
    {
        string profileId = string.IsNullOrWhiteSpace(recipe.NamingProfileId)
            ? Profile.Id
            : recipe.NamingProfileId;
        return Profiles.TryGetValue(profileId, out LibraryProfile? profile)
            ? profile
            : Profile;
    }

    public LibraryIndexLocation? ResolveTarget(LibraryIngestRecipe recipe)
    {
        if (recipe.DestinationRootId is Guid rootId &&
            RootTargets.TryGetValue(rootId, out LibraryIndexLocation? root))
            return root;
        return RootTargets.Values.SingleOrDefault(target =>
            target.IngestRole == recipe.DestinationLegacyRole);
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
        LibraryProfile profile = configuration.ActiveProfile;
        if (!profile.Ingest.Enabled)
            return [
                $"The active profile '{profile.Name}' does not enable ingest. " +
                "Choose an ingest-enabled profile or add an ingest recipe."
            ];
        IReadOnlyDictionary<LibraryIngestRole, LibraryIndexLocation> targets;
        try
        {
            targets = configuration.IngestTargets;
        }
        catch (InvalidDataException ex)
        {
            return [ex.Message];
        }

        var missing = new List<string>();
        foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes.Where(item => item.Enabled))
        {
            LibraryIndexLocation? target = recipe.DestinationRootId is Guid rootId
                ? configuration.IndexLocations.SingleOrDefault(location => location.RootId == rootId)
                : targets.GetValueOrDefault(recipe.DestinationLegacyRole);
            if (target is null && !(recipe.DestinationLegacyRole == LibraryIngestRole.AacFallback &&
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
        if (profile.Preset != LibraryProfilePreset.LegacyMusicLibraryTools)
            return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (!targets.ContainsKey(LibraryIngestRole.Cd))
            missing.Add("Assign an IndexTarget to the CD ingest role.");
        if (!targets.ContainsKey(LibraryIngestRole.CdFallback))
            missing.Add("Assign an IndexTarget to the CD fallback ingest role.");
        if (!targets.ContainsKey(LibraryIngestRole.HiRes))
            missing.Add("Assign an IndexTarget to the Hi-res ingest role.");
        if (string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath) &&
            !targets.ContainsKey(LibraryIngestRole.AacFallback))
            missing.Add("Assign an IndexTarget to the AAC fallback role, or configure an iTunes library.");
        return missing;
    }

    public static IngestMusicConfiguration FromLibraryConfiguration(
        LibraryConfiguration configuration)
    {
        IReadOnlyList<string> missing = MissingLibrarySettings(configuration);
        if (missing.Count > 0)
            throw new InvalidDataException(string.Join(" ", missing));
        IReadOnlyDictionary<LibraryIngestRole, LibraryIndexLocation> targets =
            configuration.IngestTargets;
        LibraryIndexLocation[] locations = configuration.IndexLocations.ToArray();
        LibraryIngestSettings settings = configuration.IngestSettings;
        LibraryProfile profile = configuration.ActiveProfile;
        LibrarySourceDisposition disposition = configuration.SchemaVersion ==
            LibraryConfigurationSchema.LegacyVersion
            ? settings.DeleteSourcesAfterIngest
                ? LibrarySourceDisposition.Delete
                : LibrarySourceDisposition.Quarantine
            : profile.Ingest.SourceDisposition;
        return new IngestMusicConfiguration
        {
            FfmpegPath = configuration.FfmpegPath,
            ItunesLibraryPath = configuration.ItunesLibraryPath,
            CdDestination = targets.GetValueOrDefault(LibraryIngestRole.Cd)?.Target ?? "",
            PairedCdDestination = targets.GetValueOrDefault(LibraryIngestRole.CdFallback)?.Target ?? "",
            HighResolutionDestination = targets.GetValueOrDefault(LibraryIngestRole.HiRes)?.Target ?? "",
            AacDestination = targets.TryGetValue(LibraryIngestRole.AacFallback, out var aac)
                ? aac.Target
                : "",
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
                item => item.Id, StringComparer.OrdinalIgnoreCase),
            ConfiguredSourceDisposition = disposition,
            PreserveSidecars = profile.Ingest.PreserveSidecars,
        };
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

        return new IngestMusicConfiguration
        {
            FfmpegPath = Required("FfmpegPath"),
            AacDestination = ResolveRoot(Required("AacDestination")),
            ItunesLibraryPath = OptionalPath("ItunesLibrary"),
            CdDestination = ResolveRoot(Required("CdDestination")),
            PairedCdDestination = ResolveRoot(Required("PairedCdDestination")),
            HighResolutionDestination = ResolveRoot(Required("HighResolutionDestination")),
            LengthLimit = Positive("LengthLimit", 255),
            DiscNumLengthLimit = Positive("DiscNumLengthLimit", 255),
            AacEncoder = ((string?)root.Element("AacEncoder") ?? "libfdk_aac").Trim(),
            AacBitrateKbps = Positive("AacBitrateKbps", 256),
            DeleteSourcesAfterIngest = (bool?)root.Element("DeleteSourcesAfterIngest") ?? false,
            RemoveNonMusicAfterIngest = (bool?)root.Element("RemoveNonMusicAfterIngest") ?? false,
        };
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
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
