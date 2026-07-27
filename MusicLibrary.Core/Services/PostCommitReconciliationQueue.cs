using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Runs post-commit work without allowing a catalog gate, caller
/// cancellation, or a late exception to change an already durable operation
/// into an apparent failure. Restore journals remain the durable retry
/// mechanism when queued work cannot finish.
/// </summary>
internal sealed class PostCommitReconciliationQueue
{
    public static PostCommitReconciliationQueue Shared { get; } = new();

    public PostCommitReconciliationHandle Enqueue(
        Func<Task<IReadOnlyList<OperationIssue>>> work,
        string failureCode,
        string failureMessage,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        var completion =
            new TaskCompletionSource<IReadOnlyList<OperationIssue>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunAsync(
            work,
            failureCode,
            failureMessage,
            path,
            completion);
        return new(completion.Task);
    }

    private static async Task RunAsync(
        Func<Task<IReadOnlyList<OperationIssue>>> work,
        string failureCode,
        string failureMessage,
        string? path,
        TaskCompletionSource<IReadOnlyList<OperationIssue>> completion)
    {
        await Task.Yield();
        IReadOnlyList<OperationIssue> issues;
        try
        {
            issues = await work().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            issues =
            [
                new(
                    failureCode,
                    OperationIssueSeverity.Warning,
                    failureMessage + ": " + error.Message,
                    path),
            ];
        }
        completion.TrySetResult(issues);
    }
}
