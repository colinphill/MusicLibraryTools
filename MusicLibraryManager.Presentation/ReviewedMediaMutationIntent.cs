using System.Collections.Immutable;
using MusicLibrary.Core.Models;

namespace MusicLibraryManager.Presentation;

public enum ReviewedMediaMutationKind
{
    Metadata = 0,
    Artwork = 1,
    TagLayers = 2,
    FileOperation = 3,
    Transcode = 4,
}

/// <summary>
/// Deterministic, immutable summary of every reviewed mutation currently
/// associated with one source file. The concrete plans remain owned by their
/// existing transaction services; this unit is the common composition and
/// prevalidation identity used by the Workbench.
/// </summary>
public sealed record ReviewedMediaMutationUnit(
    string SourcePath,
    ImmutableArray<ReviewedMediaMutationKind> MutationKinds);

/// <summary>
/// Immutable UI-level identity for recoverable media work placed in the
/// Workbench Review Changes queue. The payload remains the already-reviewed
/// core plan; execution continues through the existing mutation services.
/// </summary>
public abstract record ReviewedMediaMutationIntent(
    Guid Id,
    ReviewedMediaMutationKind Kind,
    ImmutableArray<string> SourcePaths);

public sealed record ReviewedMetadataMutationIntent(
    Guid Id,
    ReviewedMediaMutationKind MutationKind,
    ImmutableArray<string> Paths,
    MetadataOperationPlan Plan)
    : ReviewedMediaMutationIntent(Id, MutationKind, Paths)
{
    public ImmutableArray<ReviewedMediaMutationKind>
        MutationKinds { get; init; } =
        [MutationKind];

    public static ReviewedMetadataMutationIntent Create(
        MetadataOperationPlan plan,
        ReviewedMediaMutationKind kind =
            ReviewedMediaMutationKind.Metadata)
        => Create(
            plan,
            [kind]);

    public static ReviewedMetadataMutationIntent Create(
        MetadataOperationPlan plan,
        IEnumerable<ReviewedMediaMutationKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(kinds);
        ImmutableArray<ReviewedMediaMutationKind>
            normalizedKinds =
        [
            .. kinds
                .Distinct()
                .OrderBy(kind => (int)kind),
        ];
        if (normalizedKinds.IsDefaultOrEmpty ||
            normalizedKinds.Any(kind =>
                kind is not (
                    ReviewedMediaMutationKind.Metadata or
                    ReviewedMediaMutationKind.Artwork or
                    ReviewedMediaMutationKind.TagLayers)))
            throw new ArgumentOutOfRangeException(nameof(kinds));
        return new(
            Guid.NewGuid(),
            normalizedKinds[0],
            [
                .. plan.Files.Select(file => file.Path)
                    .Distinct(ReviewedMediaMutationPaths.Comparer),
            ],
            plan)
        {
            MutationKinds = normalizedKinds,
        };
    }
}

public sealed record ReviewedFileOperationMutationIntent(
    Guid Id,
    ImmutableArray<string> Paths,
    ReviewedFileOperationPlan Plan)
    : ReviewedMediaMutationIntent(
        Id,
        ReviewedMediaMutationKind.FileOperation,
        Paths)
{
    public static ReviewedFileOperationMutationIntent Create(
        ReviewedFileOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(
            Guid.NewGuid(),
            [
                .. plan.Items.Select(item => item.SourcePath)
                    .Distinct(ReviewedMediaMutationPaths.Comparer),
            ],
            plan);
    }
}

public sealed record ReviewedTranscodeMutationIntent(
    Guid Id,
    ImmutableArray<string> Paths,
    AudioTranscodePlan Plan)
    : ReviewedMediaMutationIntent(
        Id,
        ReviewedMediaMutationKind.Transcode,
        Paths)
{
    public static ReviewedTranscodeMutationIntent Create(
        AudioTranscodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(
            Guid.NewGuid(),
            [
                .. plan.Items.Select(item => item.SourcePath)
                    .Distinct(ReviewedMediaMutationPaths.Comparer),
            ],
            plan);
    }
}

public interface IWorkbenchPendingChangeCoordinator
{
    Task<bool> AddPendingMutationAsync(
        ReviewedMediaMutationIntent intent,
        CancellationToken ct = default);

    Task<bool> AddPendingTranscodeAsync(
        AudioTranscodePlan plan,
        CancellationToken ct = default) =>
        AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(plan),
            ct);

}

internal static class ReviewedMediaMutationPaths
{
    internal static readonly StringComparer Comparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
