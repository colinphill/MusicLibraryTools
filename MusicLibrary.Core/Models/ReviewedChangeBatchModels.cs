using System.Collections.Immutable;

namespace MusicLibrary.Core.Models;

/// <summary>
/// A deterministic, versioned group of per-volume mutation plans. The coordinator manifest is
/// durable before any participant begins, so an interrupted group can be rolled back as one
/// logical reviewed change.
/// </summary>
public sealed record ReviewedChangeBatchPlan(
    Guid Id,
    ImmutableArray<FileMutationPlan> Participants,
    string CoordinatorManifestPath,
    DateTimeOffset CreatedAtUtc)
{
    public bool CanApply =>
        Participants.Length > 0 &&
        Participants.All(participant => participant.CanApply);
}

public sealed record ReviewedChangeBatchResult(
    Guid Id,
    ImmutableArray<FileMutationSummary> ParticipantResults,
    ImmutableArray<string> JournalPaths,
    string CoordinatorManifestPath);

public enum ReviewedChangeReconciliationState
{
    None = 0,
    RolledBack = 1,
    Committed = 2,
    Blocked = 3,
}

public sealed record ReviewedChangeReconciliationResult(
    int Examined,
    int RolledBack,
    int Committed,
    ImmutableArray<string> BlockedManifests);
