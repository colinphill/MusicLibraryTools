using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

[Flags]
public enum LibraryOperationCapabilities
{
    None = 0,
    IndexedCatalog = 1 << 0,
    PlaylistTargets = 1 << 1,
    CatalogIntegration = 1 << 2,
    CrossLibraryTarget = 1 << 3,
    LegacyArchivePolicy = 1 << 4,
    ArtworkWrites = 1 << 5,
}

public sealed record LibraryOperationAvailability(bool Available, string? Reason = null)
{
    public static LibraryOperationAvailability Enabled { get; } = new(true);
    public static LibraryOperationAvailability Disabled(string reason) => new(false, reason);
}

/// <summary>
/// Internal extension point for specialized operations. Providers describe their capability
/// requirements independently of the Operations UI and can disappear when a library does not
/// configure the required integration or output target.
/// </summary>
public interface ILibraryOperationProvider
{
    UnifiedJobDescriptor Descriptor { get; }
    LibraryOperationCapabilities RequiredCapabilities { get; }
    LibraryOperationAvailability GetAvailability(LibraryConfiguration? configuration);
}

public sealed class BuiltInLibraryOperationProvider : ILibraryOperationProvider
{
    public BuiltInLibraryOperationProvider(
        UnifiedJobDescriptor descriptor,
        LibraryOperationCapabilities requiredCapabilities)
    {
        Descriptor = descriptor;
        RequiredCapabilities = requiredCapabilities;
    }

    public UnifiedJobDescriptor Descriptor { get; }
    public LibraryOperationCapabilities RequiredCapabilities { get; }

    public LibraryOperationAvailability GetAvailability(LibraryConfiguration? configuration)
    {
        if (RequiredCapabilities == LibraryOperationCapabilities.None)
            return LibraryOperationAvailability.Enabled;
        if (configuration is null)
            return LibraryOperationAvailability.Disabled("Load a library configuration.");

        LibraryOperationCapabilities actual = LibraryOperationCapabilities.IndexedCatalog;
        if (configuration.PlaylistTargets.Count > 0)
            actual |= LibraryOperationCapabilities.PlaylistTargets;
        if (!string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath))
            actual |= LibraryOperationCapabilities.CatalogIntegration;
        if (configuration.CrossSyncTarget is not null)
            actual |= LibraryOperationCapabilities.CrossLibraryTarget;
        if (configuration.ActiveProfile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools)
            actual |= LibraryOperationCapabilities.LegacyArchivePolicy;
        if (configuration.IndexLocations.Any(location =>
                location.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork) &&
                configuration.GetEffectiveProfile(location).Artwork.Storage is
                    LibraryArtworkStorage.Embedded or LibraryArtworkStorage.Both))
            actual |= LibraryOperationCapabilities.ArtworkWrites;

        LibraryOperationCapabilities missing = RequiredCapabilities & ~actual;
        return missing == 0
            ? LibraryOperationAvailability.Enabled
            : LibraryOperationAvailability.Disabled(
                $"The active library does not provide: {Describe(missing)}.");
    }

    private static string Describe(LibraryOperationCapabilities capabilities) => string.Join(", ",
        Enum.GetValues<LibraryOperationCapabilities>()
            .Where(value => value != LibraryOperationCapabilities.None &&
                capabilities.HasFlag(value))
            .Select(value => value switch
            {
                LibraryOperationCapabilities.IndexedCatalog => "an indexed catalog",
                LibraryOperationCapabilities.PlaylistTargets => "playlist export targets",
                LibraryOperationCapabilities.CatalogIntegration => "a media catalog integration",
                LibraryOperationCapabilities.CrossLibraryTarget => "a synchronization target",
                LibraryOperationCapabilities.LegacyArchivePolicy => "the legacy archive profile",
                LibraryOperationCapabilities.ArtworkWrites => "an embedded-artwork write target",
                _ => value.ToString(),
            }));
}

public static class BuiltInLibraryOperationProviders
{
    public static IReadOnlyList<ILibraryOperationProvider> All { get; } =
    [
        Provider("playlist-sync", "Playlist sync",
            "Synchronize every playlist export target from the active library configuration.",
            UnifiedJobApplyMode.ApplyFlag, "", 0,
            LibraryOperationCapabilities.PlaylistTargets),
        Provider("artwork-normalization", "Artwork normalization",
            "Normalize embedded artwork for tracks supplied by the configured catalog.",
            UnifiedJobApplyMode.ApplyFlag, "<playlist>", 1,
            LibraryOperationCapabilities.CatalogIntegration |
            LibraryOperationCapabilities.ArtworkWrites),
        Provider("smart-storage", "Smart storage",
            "Project the configured catalog into a bucketed portable-storage export.",
            UnifiedJobApplyMode.ApplyFlag,
            "<destination> [--initialize] [--max-removals <count>]", 1,
            LibraryOperationCapabilities.CatalogIntegration |
            LibraryOperationCapabilities.LegacyArchivePolicy),
        Provider("car-card", "Car card",
            "Project the indexed library into a balanced removable-media export.",
            UnifiedJobApplyMode.ApplyFlag,
            "[rebalance] [fixerrors] [--initialize] [--max-removals <count>]", 0,
            LibraryOperationCapabilities.IndexedCatalog |
            LibraryOperationCapabilities.LegacyArchivePolicy),
        Provider("cross-library-sync", "Cross-library sync",
            "Synchronize configured playlists into the library target.",
            UnifiedJobApplyMode.ApplyFlag, "", 0,
            LibraryOperationCapabilities.CrossLibraryTarget),
        Provider("redundancies", "Redundancy report",
            "Report likely duplicate tracks from the configured catalog integration.",
            UnifiedJobApplyMode.ReadOnly, "", 0,
            LibraryOperationCapabilities.CatalogIntegration),
        Provider("itunes-validation", "iTunes library validation",
            "Validate structural and referential invariants in an ITL file.",
            UnifiedJobApplyMode.ReadOnly, "<iTunes Library.itl>", 1,
            LibraryOperationCapabilities.None),
    ];

    private static ILibraryOperationProvider Provider(
        string id,
        string name,
        string description,
        UnifiedJobApplyMode applyMode,
        string usage,
        int minimumArguments,
        LibraryOperationCapabilities capabilities) =>
        new BuiltInLibraryOperationProvider(
            new UnifiedJobDescriptor(id, name, description, applyMode, [], usage,
                minimumArguments), capabilities);
}
