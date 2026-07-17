using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class SynchronousProgressTests
{
    [Fact]
    public void ReportInvokesTheHandlerInline()
    {
        int reportingThread = Environment.CurrentManagedThreadId;
        int callbackThread = -1;
        var progress = new SynchronousProgress<int>(_ =>
            callbackThread = Environment.CurrentManagedThreadId);

        progress.Report(1);

        Assert.Equal(reportingThread, callbackThread);
    }

    [Fact]
    public void ConcurrentReportsAreSerialized()
    {
        int activeCallbacks = 0;
        int maximumActiveCallbacks = 0;
        int reports = 0;
        var progress = new SynchronousProgress<int>(_ =>
        {
            int active = Interlocked.Increment(ref activeCallbacks);
            maximumActiveCallbacks = Math.Max(maximumActiveCallbacks, active);
            Thread.SpinWait(50_000);
            Interlocked.Increment(ref reports);
            Interlocked.Decrement(ref activeCallbacks);
        });

        Parallel.For(0, 32, progress.Report);

        Assert.Equal(32, reports);
        Assert.Equal(1, maximumActiveCallbacks);
    }
}
