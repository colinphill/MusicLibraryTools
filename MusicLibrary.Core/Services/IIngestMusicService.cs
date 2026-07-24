using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IIngestMusicService
{
    Task<IngestPlan> PreviewAsync(IngestRequest request, CancellationToken ct = default);

    Task<IngestPlan> PreviewAsync(
        IngestRequest request,
        IProgress<IngestProgress>? progress,
        CancellationToken ct = default) =>
        PreviewAsync(request, ct);

    Task<IngestResult> ApplyAsync(
        IngestPlan plan,
        IReadOnlyList<IngestApprovalDecision> approvals,
        IProgress<IngestProgress>? progress = null,
        CancellationToken ct = default);
}
