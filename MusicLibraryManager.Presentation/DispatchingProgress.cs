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

    public DispatchingProgress(Action<T> handler) =>
        _handler = handler ??
            throw new ArgumentNullException(nameof(handler));

    public void Report(T value)
    {
        if (_context is null ||
            ReferenceEquals(
                SynchronizationContext.Current,
                _context))
        {
            _handler(value);
            return;
        }
        _context.Post(
            static state =>
            {
                var report = ((
                    DispatchingProgress<T> Progress,
                    T Value))state!;
                report.Progress._handler(report.Value);
            },
            (this, value));
    }

    public Task DrainAsync()
    {
        if (_context is null)
            return Task.CompletedTask;
        var completion =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            static state =>
                ((TaskCompletionSource)state!).SetResult(),
            completion);
        return completion.Task;
    }
}
