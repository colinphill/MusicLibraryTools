using System.Collections.Immutable;

namespace MusicLibrary.Core.Models;

public static class ReviewedChangeKindIds
{
    public const string AudioTranscode = "audio-transcode";
}

public sealed record ReviewedChangeHistoryEntry(
    Guid Id,
    string KindId,
    DateTimeOffset AppliedAtUtc,
    ImmutableArray<string> JournalPaths,
    ImmutableArray<string> SourcePaths,
    ImmutableArray<string> DestinationPaths,
    string CoordinatorManifestPath,
    AudioTranscodeRequest RedoRequest,
    ImmutableArray<AudioTranscodeRequest> RedoRequests = default,
    ImmutableArray<string> IndexedSourcePaths = default)
{
    public ImmutableArray<AudioTranscodeRequest>
        EffectiveRedoRequests =>
        RedoRequests.IsDefaultOrEmpty
            ? [RedoRequest]
            : RedoRequests;
}

public sealed record ReviewedChangeUndoResult(
    Guid EntryId,
    int RestoredFiles,
    ImmutableArray<string> RestoreJournalPaths)
{
    public IReadOnlyList<OperationIssue> Issues { get; init; } = [];
}
