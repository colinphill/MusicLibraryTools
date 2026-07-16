namespace MusicLibrary.Core.Models;

public sealed record IngestRequest(string SourceDirectory, string? ConfigurationPath = null);

public enum IngestPreflightSeverity { Pass, Warning, Error }

public sealed record IngestPreflightCheck(
    string Name,
    IngestPreflightSeverity Severity,
    string Message);

public sealed record IngestPreflightResult(IReadOnlyList<IngestPreflightCheck> Checks)
{
    public bool CanProceed => Checks.All(check => check.Severity != IngestPreflightSeverity.Error);
    public int WarningCount => Checks.Count(check => check.Severity == IngestPreflightSeverity.Warning);
    public int ErrorCount => Checks.Count(check => check.Severity == IngestPreflightSeverity.Error);
}

public sealed record IngestFileSummary(
    string Source,
    string SourceType,
    string Summary);

public sealed record IngestConflict(string AlbumKey, string Path, string Message);

public sealed record IngestApprovalItem(
    string AlbumKey,
    string AlbumDisplay,
    IReadOnlyList<string> MissingTracks);

public sealed record IngestApprovalDecision(string AlbumKey, bool Approved);

public sealed record IngestFileSnapshot(string Path, long Length, DateTime LastWriteTimeUtc);

public sealed record IngestTrackPlan
{
    public required string Identity { get; init; }
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string AlbumArtist { get; init; }
    public required string Album { get; init; }
    public required int TrackNumber { get; init; }
    public required int TrackTotal { get; init; }
    public required int OriginalDiscNumber { get; init; }
    public required uint SampleRate { get; init; }
    public required uint BitsPerSample { get; init; }
    public required uint Channels { get; init; }
    public required uint DurationInSeconds { get; init; }
    public required bool IsAlac { get; init; }
    public required bool IsHighResolution { get; init; }
    public bool Compilation { get; init; }
}

public enum IngestOutputKind { HighResolutionFlac, CdFlac, Aac }

public sealed record IngestOutputPlan
{
    public required string Identity { get; init; }
    public required IngestOutputKind Kind { get; init; }
    public required IngestTrackPlan Metadata { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public bool DeriveCd { get; init; }
}

public sealed record IngestAlbumPlan
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public required IReadOnlyList<IngestTrackPlan> Tracks { get; init; }
    public required IReadOnlyList<IngestOutputPlan> Outputs { get; init; }
    public required IReadOnlyList<IngestFileSnapshot> Sources { get; init; }
    public bool HasHighResolution { get; init; }
}

public sealed record IngestPlan
{
    public required IngestRequest Request { get; init; }
    public required IngestMusicConfiguration Configuration { get; init; }
    public required IReadOnlyList<IngestAlbumPlan> Albums { get; init; }
    public required IReadOnlyList<IngestFileSummary> Files { get; init; }
    public required IReadOnlyList<IngestApprovalItem> RequiredApprovals { get; init; }
    public required IReadOnlyList<IngestConflict> Conflicts { get; init; }
    public required IReadOnlyList<string> IgnoredFiles { get; init; }
    public IReadOnlyList<IngestFileSnapshot> IgnoredFileSnapshots { get; init; } = [];
    public IReadOnlyList<string> SourceDirectories { get; init; } = [];
    public IngestFileSnapshot? ItunesLibrarySnapshot { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public bool CanApply => Conflicts.Count == 0 &&
        (Albums.Count > 0 || Configuration.RemoveNonMusicAfterIngest &&
            (IgnoredFileSnapshots.Count > 0 || SourceDirectories.Count > 0));
}

public enum IngestFileProgressState { InProgress, Completed, Failed }

public sealed record IngestProgress(
    string Album,
    string Operation,
    int CompletedItems,
    int TotalItems,
    string? SourcePath = null,
    IngestFileProgressState? FileState = null);

public sealed record IngestAlbumResult(string AlbumKey, bool Success, int Installed, string? Error = null);

public sealed record IngestResult(
    IReadOnlyList<IngestAlbumResult> Albums,
    bool Cancelled,
    string? Message = null)
{
    public int Installed => Albums.Sum(a => a.Installed);
    public int Failed => Albums.Count(a => !a.Success);
}
