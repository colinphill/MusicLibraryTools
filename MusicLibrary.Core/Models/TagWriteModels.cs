using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>A single field change to apply. A null <see cref="Value"/> removes the field.</summary>
public sealed record TagEdit(TagFields Field, string? Value);

public enum WriteOutcome { Saved, Skipped, Failed }

/// <summary>The result of applying edits to one file.</summary>
public sealed record FileWriteResult
{
    public required string Path { get; init; }
    public WriteOutcome Outcome { get; init; }

    /// <summary>Error message when <see cref="Outcome"/> is Failed.</summary>
    public string? Error { get; init; }

    /// <summary>Fields that couldn't be applied to this file's tag format (e.g. ID3 can't map them).</summary>
    public IReadOnlyList<TagFields> UnsupportedFields { get; init; } = [];

    /// <summary>
    /// A cache-refresh error after the file itself was saved successfully. The disk write remains a
    /// <see cref="WriteOutcome.Saved"/> result so callers do not retry a mutation that already happened.
    /// </summary>
    public string? CacheError { get; init; }
}

/// <summary>Aggregate result of a batch write.</summary>
public sealed record BatchWriteResult(IReadOnlyList<FileWriteResult> Files)
{
    public int SavedCount => Files.Count(f => f.Outcome == WriteOutcome.Saved);
    public int SkippedCount => Files.Count(f => f.Outcome == WriteOutcome.Skipped);
    public int FailedCount => Files.Count(f => f.Outcome == WriteOutcome.Failed);
    public int CacheFailedCount => Files.Count(f => f.CacheError is not null);

    public string Summary =>
        $"{SavedCount} saved, {SkippedCount} skipped, {FailedCount} failed" +
        (CacheFailedCount == 0 ? "" : $", {CacheFailedCount} cache refresh failed");
}
