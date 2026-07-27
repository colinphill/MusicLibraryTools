namespace MusicLibraryManager.Presentation;

/// <summary>
/// Delivers progress on the caller's synchronization context and exposes a
/// drain point so operation completion cannot overtake already-reported UI
/// updates. Without a synchronization context (tests and command-line hosts),
/// reports are delivered synchronously.
/// </summary>
public sealed class DispatchingProgress<T> : IProgress<T>
{
    private readonly SynchronizationContext? _context =
        SynchronizationContext.Current;
    private readonly Action<T> _handler;
    private readonly object _gate = new();
    private Task? _drain;
    private TaskCompletionSource? _drainCompletion;
    private int _acceptedReports;
    private bool _completed;

    public DispatchingProgress(Action<T> handler) =>
        _handler = handler ??
            throw new ArgumentNullException(nameof(handler));

    public void Report(T value)
    {
        bool dispatch;
        lock (_gate)
        {
            // DrainAsync is the operation boundary. Reports that race after
            // that boundary belong to an already-completed producer and must
            // not overwrite this operation's terminal state (or the state of
            // a newer operation using the same view model).
            if (_completed)
                return;
            _acceptedReports++;
            dispatch =
                _context is not null &&
                !ReferenceEquals(
                    SynchronizationContext.Current,
                    _context);
        }

        if (dispatch)
        {
            try
            {
                _context!.Post(
                    static state =>
                    {
                        var report = ((
                            DispatchingProgress<T> Progress,
                            T Value))state!;
                        report.Progress.DeliverAccepted(
                            report.Value);
                    },
                    (this, value));
            }
            catch
            {
                ReleaseAccepted();
                throw;
            }
            return;
        }

        DeliverAccepted(value);
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_drain is not null)
                return _drain;

            _completed = true;
            if (_acceptedReports == 0)
                return _drain =
                    Task.CompletedTask;

            _drainCompletion =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            return _drain =
                _drainCompletion.Task;
        }
    }

    private void DeliverAccepted(
        T value)
    {
        try
        {
            _handler(value);
        }
        finally
        {
            // Preserve the handler's normal exception behavior while ensuring
            // a failing observer can never strand the operation's drain.
            ReleaseAccepted();
        }
    }

    private void ReleaseAccepted()
    {
        TaskCompletionSource? completion =
            null;
        lock (_gate)
        {
            _acceptedReports--;
            if (_acceptedReports < 0)
                throw new InvalidOperationException();
            if (_completed &&
                _acceptedReports == 0)
                completion =
                    _drainCompletion;
        }
        completion?.TrySetResult();
    }
}
