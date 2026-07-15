namespace MusicLibrary.Core.Models;

public enum OperationJournalKind
{
    Ingest,
    Organize,
    Sync,
    Device,
    Other,
}

public enum OperationJournalState
{
    Completed,
    Interrupted,
    RolledBack,
    Unknown,
}

/// <summary>A lightweight, read-only description of one discovered mutation/quarantine run.</summary>
public sealed record OperationJournalSummary(
    string ToolName,
    OperationJournalKind Kind,
    OperationJournalState State,
    string RunPath,
    string? JournalPath,
    DateTimeOffset CreatedAtUtc,
    int? AffectedItemCount);

public sealed record OperationJournalDiscoveryResult(
    IReadOnlyList<OperationJournalSummary> Runs,
    IReadOnlyList<string> Warnings);
