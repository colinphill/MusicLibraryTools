using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Applies tag edits to one or many files. Runs on a background thread; a single unsupported field
/// or a single failing file never aborts the batch — each file gets its own result.
/// </summary>
public interface ITagWriteService
{
    Task<BatchWriteResult> ApplyAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<TagEdit> edits,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
