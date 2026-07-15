using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Read-only operation catalog. Execution belongs to operation-specific typed services; this
/// catalog carries only presentation metadata used by the Operations tab.
/// </summary>
public interface IUnifiedJobService
{
    IReadOnlyList<UnifiedJobDescriptor> Catalog { get; }
}

public sealed class UnifiedJobService : IUnifiedJobService
{
    public IReadOnlyList<UnifiedJobDescriptor> Catalog { get; } =
    [
        new("playlist-sync", "Playlist sync",
            "Synchronize every configured playlist export target.",
            UnifiedJobApplyMode.ApplyFlag, [], "<libraryconfiguration.xml> [clean]", 1),
        new("artwork-normalization", "Artwork normalization",
            "Normalize embedded artwork for the tracks in an iTunes playlist.",
            UnifiedJobApplyMode.ApplyFlag, [], "<playlist> [--library <file.itl>]", 1),
        new("device-sync", "Device sync",
            "Synchronize local or adb: file trees with quarantine and removal limits.",
            UnifiedJobApplyMode.ApplyFlag, [],
            "<source> <destination> [remap] [--max-removals <count>]", 2),
        new("smart-storage", "Smart storage",
            "Project the iTunes library into a bucketed portable-storage catalog.",
            UnifiedJobApplyMode.ApplyFlag, [],
            "<destination> [--initialize] [--library <file.itl>] [--max-removals <count>]", 1),
        new("car-card", "Car card",
            "Project the indexed library into a balanced removable-media layout.",
            UnifiedJobApplyMode.ApplyFlag, [],
            "<libraryconfiguration.xml> [rebalance] [fixerrors] [--initialize] [--library <file.itl>] [--max-removals <count>]", 1),
        new("cross-library-sync", "Cross-library sync",
            "Synchronize configured playlists into the library target.",
            UnifiedJobApplyMode.ApplyFlag, [],
            "<libraryconfiguration.xml> [--library <file.itl>] [--max-removals <count>]", 1),
        new("redundancies", "Redundancy report",
            "Report likely duplicate iTunes tracks.", UnifiedJobApplyMode.ReadOnly,
            [], "[--library <file.itl>]", 0),
        new("itunes-validation", "iTunes library validation",
            "Validate structural and referential invariants in an ITL file.",
            UnifiedJobApplyMode.ReadOnly, [], "<iTunes Library.itl>", 1),
    ];
}
