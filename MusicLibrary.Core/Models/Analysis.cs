using MusicFileUtilities;
using MusicLibraryTools;

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
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public string? Grouping { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public string? CodecName { get; init; }
    public string? TagType { get; init; }
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

public enum AnalysisRepairKind
{
    Tag,
    Path,
}

/// <summary>One cache-derived correction shown to the user before any file is changed.</summary>
public sealed record AnalysisTagRepair(
    string Path,
    TagFields Field,
    string? Before,
    string After,
    string Reason,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc,
    AnalysisRepairKind Kind = AnalysisRepairKind.Tag,
    OperationPathSnapshot? ExpectedDestination = null,
    string? BlockingReason = null,
    ID3v2Version? TargetId3Version = null,
    string? RuleId = null)
{
    public bool CanApply => string.IsNullOrWhiteSpace(BlockingReason);
}

/// <summary>A stale-checked set of reviewed analysis repairs.</summary>
public sealed record AnalysisRepairPlan(
    string Name,
    IReadOnlyList<AnalysisTagRepair> Items,
    string? PolicyFingerprint = null,
    Guid? LibraryId = null)
{
    public bool CanApply => Items.Any(item => item.CanApply);
}

public sealed record AnalysisRepairItemResult(
    AnalysisTagRepair Repair,
    WriteOutcome Outcome,
    string? Error = null,
    string? AppliedPath = null,
    string? CacheError = null);

public sealed record AnalysisRepairApplyResult(IReadOnlyList<AnalysisRepairItemResult> Items)
{
    public int SavedCount => Items.Count(item => item.Outcome == WriteOutcome.Saved);
    public int SkippedCount => Items.Count(item => item.Outcome == WriteOutcome.Skipped);
    public int FailedCount => Items.Count(item => item.Outcome == WriteOutcome.Failed);
    public int CacheFailedCount => Items.Count(item => item.CacheError is not null);

    public string Summary =>
        $"{SavedCount} applied, {SkippedCount} skipped, {FailedCount} failed" +
        (CacheFailedCount == 0 ? "" : $", {CacheFailedCount} cache refresh failed");
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
public sealed record AnalysisFinding(
    string Path,
    string Description,
    string? Problem = null,
    string? RuleId = null,
    LibraryHealthSeverity Severity = LibraryHealthSeverity.Warning);

/// <summary>The output of one analyzer.</summary>
public sealed record AnalysisReport(string Name, IReadOnlyList<AnalysisFinding> Findings)
{
    public int Count => Findings.Count;
}

public sealed record ResolutionAlbum(string Artist, string Album, string Directory);

public enum ResolutionComparisonKind
{
    TrackCountMismatch,
    MetadataDifference,
    Missing,
    Ambiguous,
}

public sealed record ResolutionComparisonFinding(
    ResolutionComparisonKind Kind,
    ResolutionAlbum HighResolution,
    ResolutionAlbum? Standard,
    double MatchThreshold,
    IReadOnlyList<ResolutionAlbum>? Candidates = null,
    int HighTrackCount = 0,
    int StandardTrackCount = 0,
    int ArtistDistance = 0,
    int AlbumDistance = 0);

public sealed record ResolutionComparisonReport(
    int AlbumCount,
    int MatchedCount,
    int MissingCount,
    int AmbiguousCount,
    IReadOnlyList<ResolutionComparisonFinding> Findings);

public sealed record DecodedAudioPair(string FirstPath, string SecondPath, string Description);

public sealed record DecodedAudioProgress(int CompletedFiles, int TotalFiles, string Path);

/// <summary>Progress for a bounded analysis phase, with a user-facing unit and stage.</summary>
public sealed record AnalysisProgress(
    long Completed,
    long Total,
    string Unit,
    string Stage,
    string? CurrentItem = null);

public enum RepresentationRepairKind
{
    DeriveCdFlac,
    DeriveAac,
    Organize,
}

/// <summary>A cache-derived file operation shown for review; preview never changes the filesystem.</summary>
public sealed record RepresentationRepairAction(
    RepresentationRepairKind Kind,
    string SourcePath,
    string DestinationPath,
    string Description,
    OperationPathSnapshot? ExpectedSource = null,
    OperationPathSnapshot? ExpectedDestination = null,
    string? PolicyFingerprint = null,
    Guid? LibraryId = null);

public enum RepresentationRepairOutcome
{
    Applied,
    Skipped,
    Failed,
}

public sealed record RepresentationRepairActionResult(
    RepresentationRepairAction Action,
    RepresentationRepairOutcome Outcome,
    string? Error = null);

public sealed record RepresentationRepairProgress(
    int Completed,
    int Total,
    string SourcePath,
    RepresentationRepairKind Kind);

public sealed record RepresentationRepairApplyResult(
    IReadOnlyList<RepresentationRepairActionResult> Results,
    bool Cancelled = false)
{
    public int Applied => Results.Count(result => result.Outcome == RepresentationRepairOutcome.Applied);
    public int Failed => Results.Count(result => result.Outcome == RepresentationRepairOutcome.Failed);
    public IReadOnlyList<string> ChangedPaths => Results
        .Where(result => result.Outcome == RepresentationRepairOutcome.Applied)
        .Select(result => result.Action.DestinationPath)
        .ToList();
}

/// <summary>
/// Representation repair opportunities split between immediately stale-checkable tag edits and
/// file operations that require a later apply workflow.
/// </summary>
public sealed record RepresentationRepairPreview(
    AnalysisRepairPlan MetadataCopies,
    IReadOnlyList<RepresentationRepairAction> FileActions,
    IReadOnlyList<string> Warnings);

public sealed record ArtworkAuditImage(
    string Hash,
    string ImageType,
    string Category,
    int Width,
    int Height,
    int Size);

public sealed record ArtworkAuditFile(
    string Path,
    bool ArtworkScanned,
    IReadOnlyList<ArtworkAuditImage> Images);

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
    private IReadOnlyList<AnalysisMatrixCell> Cells =>
        [DiscNumber, TrackNumber, TrackTotal, DiscTotal, Artist, AlbumArtist, Album, ReleaseDate, Title];

    public string FileName => System.IO.Path.GetFileName(Path);
    public string TitleToolTip => Title.IsInconsistent
        ? $"{Path}{Environment.NewLine}{Title.Reason}"
        : Path;
    public int InconsistentCellCount =>
        Cells.Count(cell => cell.IsInconsistent);
    public bool HasIssues => InconsistentCellCount > 0;
    public string IssueSummary => string.Join(" ", Cells
        .Where(cell => cell.IsInconsistent && !string.IsNullOrWhiteSpace(cell.Reason))
        .Select(cell => cell.Reason!)
        .Distinct(StringComparer.Ordinal));
}

/// <summary>An album package matrix containing only cache-derived values.</summary>
public sealed record AlbumMetadataMatrix(
    string Root,
    string DisplayName,
    IReadOnlyList<AlbumMetadataRow> Rows)
{
    public int TrackCount => Rows.Count;
    public int InconsistentCellCount => Rows.Sum(row => row.InconsistentCellCount);
    public string Summary =>
        $"{TrackCount:N0} {(TrackCount == 1 ? "track" : "tracks")} · " +
        $"{InconsistentCellCount:N0} {(InconsistentCellCount == 1 ? "flagged value" : "flagged values")}";
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
