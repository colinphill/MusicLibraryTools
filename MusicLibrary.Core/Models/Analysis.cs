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

/// <summary>One existing value the user can choose while resolving an ambiguous tag conflict.</summary>
public sealed record AnalysisConflictOption(string Value, int FileCount);

/// <summary>A file that will be included if the user chooses a canonical value for its conflict.</summary>
public sealed record AnalysisConflictTarget(
    string Path,
    string? Before,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc);

/// <summary>
/// A cache-derived album-level conflict. Options are existing values only; no value is inferred.
/// </summary>
public sealed record AnalysisTagConflict(
    string Album,
    string Directory,
    TagFields Field,
    IReadOnlyList<AnalysisConflictOption> Options,
    IReadOnlyList<AnalysisConflictTarget> Targets);

/// <summary>An explicit user choice for one ambiguous conflict.</summary>
public sealed record AnalysisConflictResolution(
    AnalysisTagConflict Conflict,
    string SelectedValue);

/// <summary>One finding within an analyzer report; deep-links back to a file.</summary>
public sealed record AnalysisFinding(string Path, string Description, string? Problem = null);

/// <summary>The output of one analyzer.</summary>
public sealed record AnalysisReport(string Name, IReadOnlyList<AnalysisFinding> Findings)
{
    public int Count => Findings.Count;
}

/// <summary>One display cell in an album metadata matrix.</summary>
public sealed record AnalysisMatrixCell(string? Value, bool IsInconsistent = false, string? Reason = null)
{
    public string Display => string.IsNullOrWhiteSpace(Value) ? "(missing)" : Value;
}

/// <summary>Cached metadata for one file, annotated with album/disc invariant violations.</summary>
public sealed record AlbumMetadataRow(
    string Path,
    AnalysisMatrixCell DiscNumber,
    AnalysisMatrixCell TrackNumber,
    AnalysisMatrixCell TrackTotal,
    AnalysisMatrixCell DiscTotal,
    AnalysisMatrixCell Artist,
    AnalysisMatrixCell AlbumArtist,
    AnalysisMatrixCell Album,
    AnalysisMatrixCell ReleaseDate,
    AnalysisMatrixCell Title)
{
    public int InconsistentCellCount =>
        new[] { DiscNumber, TrackNumber, TrackTotal, DiscTotal, Artist, AlbumArtist, Album, ReleaseDate, Title }
            .Count(cell => cell.IsInconsistent);
}

/// <summary>An album package matrix containing only cache-derived values.</summary>
public sealed record AlbumMetadataMatrix(
    string Root,
    string DisplayName,
    IReadOnlyList<AlbumMetadataRow> Rows)
{
    public int TrackCount => Rows.Count;
    public int InconsistentCellCount => Rows.Sum(row => row.InconsistentCellCount);
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
