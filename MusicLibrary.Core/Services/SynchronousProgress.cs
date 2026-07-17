namespace MusicLibrary.Core.Services;

/// <summary>
/// Delivers progress callbacks synchronously and serializes reports from concurrent producers.
/// Console applications use this instead of <see cref="Progress{T}"/>, whose callbacks are posted
/// asynchronously when no synchronization context is installed.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly object _gate = new();

    public void Report(T value)
    {
        lock (_gate)
            _handler(value);
    }
}
