using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>A flat per-file record used by the analyzers and duplicate finder (from the cache).</summary>
public sealed record TrackRecord
{
    public required string Path { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public bool HasAlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? StrippedAlbum { get; init; }
    public string? Title { get; init; }
    public string? ReleaseDate { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public string? CodecName { get; init; }
    public CodecType CodecType { get; init; }
    public uint SampleRate { get; init; }
    public uint BitsPerSample { get; init; }
    public uint AverageBitRate { get; init; }
    public uint Channels { get; init; }
    public int DurationInSeconds { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteTime { get; init; }

    /// <summary>Best available "album artist" for grouping (AlbumArtist, else Artist).</summary>
    public string EffectiveAlbumArtist =>
        !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist! :
        !string.IsNullOrWhiteSpace(Artist) ? Artist! : "Unknown Artist";
}

/// <summary>One cache-derived tag correction shown to the user before any file is changed.</summary>
public sealed record AnalysisTagRepair(
    string Path,
    TagFields Field,
    string? Before,
    string After,
    string Reason,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc);

/// <summary>A stale-checked set of homogeneous analysis repairs.</summary>
public sealed record AnalysisRepairPlan(string Name, IReadOnlyList<AnalysisTagRepair> Items)
{
    public bool CanApply => Items.Count > 0;
}

/// <summary>One finding within an analyzer report; deep-links back to a file.</summary>
public sealed record AnalysisFinding(string Path, string Description);

/// <summary>The output of one analyzer.</summary>
public sealed record AnalysisReport(string Name, IReadOnlyList<AnalysisFinding> Findings)
{
    public int Count => Findings.Count;
}

/// <summary>A group of files considered duplicates of each other.</summary>
public sealed record DuplicateGroup(string Key, IReadOnlyList<TrackRecord> Tracks);

/// <summary>One spelling of an artist name plus the files that use it.</summary>
public sealed record ArtistVariant(string Name, IReadOnlyList<string> Paths)
{
    public int TrackCount => Paths.Count;
}

/// <summary>A cluster of near-duplicate artist-name spellings that likely refer to one artist.</summary>
public sealed record SimilarArtistGroup(IReadOnlyList<ArtistVariant> Variants)
{
    public IReadOnlyList<string> AllPaths => Variants.SelectMany(v => v.Paths).Distinct().ToList();

    /// <summary>The spelling used by the most tracks — a sensible default canonical name.</summary>
    public string Suggested => Variants.MaxBy(v => v.TrackCount)!.Name;
}
