namespace MusicLibrary.App.Services;

public interface IScheduledScanService
{
    DateTimeOffset? NextRunUtc { get; }
    void Configure(TimeSpan? interval, Func<Task> callback);
}

/// <summary>One-shot recurring timer that waits a full interval after each callback completes.</summary>
public sealed class ScheduledScanService : IScheduledScanService, IDisposable
{
    private readonly object _sync = new();
    private Timer? _timer;
    private TimeSpan? _interval;
    private Func<Task>? _callback;
    private SynchronizationContext? _context;
    private bool _disposed;

    public DateTimeOffset? NextRunUtc { get; private set; }

    public void Configure(TimeSpan? interval, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer?.Dispose();
            _timer = null;
            _interval = interval is { } value && value > TimeSpan.Zero ? value : null;
            _callback = callback;
            _context = SynchronizationContext.Current;
            ScheduleNextLocked();
        }
    }

    private void ScheduleNextLocked()
    {
        if (_disposed || _interval is not { } interval)
        {
            NextRunUtc = null;
            return;
        }
        NextRunUtc = DateTimeOffset.UtcNow + interval;
        _timer = new Timer(_ => _ = TickAsync(), null, interval, Timeout.InfiniteTimeSpan);
    }

    private async Task TickAsync()
    {
        Func<Task>? callback;
        SynchronizationContext? context;
        lock (_sync)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = null;
            callback = _callback;
            context = _context;
            NextRunUtc = null;
        }
        try
        {
            if (callback is not null)
            {
                if (context is null)
                    await callback();
                else
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    context.Post(async _ =>
                    {
                        try { await callback(); completion.SetResult(); }
                        catch (Exception ex) { completion.SetException(ex); }
                    }, null);
                    await completion.Task;
                }
            }
        }
        catch
        {
            // A scheduled failure is reported by the indexing ViewModel. Keep future scans alive.
        }
        finally
        {
            lock (_sync)
                ScheduleNextLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            NextRunUtc = null;
        }
    }
}
