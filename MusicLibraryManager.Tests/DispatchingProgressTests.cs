using System.Collections.Concurrent;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class DispatchingProgressTests
{
    [Fact]
    public async Task Drain_waits_for_every_report_already_queued_to_the_captured_context()
    {
        var context = new ManualSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        DispatchingProgress<int> progress;
        var delivered = new List<int>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                context);
            progress =
                new DispatchingProgress<int>(
                    delivered.Add);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previous);
        }

        await Task.Run(
            () =>
            {
                progress.Report(1);
                progress.Report(2);
            },
            TestContext.Current.CancellationToken);
        Task drain = progress.DrainAsync();

        Assert.False(drain.IsCompleted);
        Assert.Empty(delivered);

        context.RunAll();
        await drain;

        Assert.Equal([1, 2], delivered);
    }

    [Fact]
    public void Report_is_immediate_when_already_on_the_captured_context()
    {
        var context = new ManualSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                context);
            int delivered = 0;
            var progress =
                new DispatchingProgress<int>(
                    value => delivered = value);

            progress.Report(7);

            Assert.Equal(7, delivered);
            Assert.Empty(context.Pending);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previous);
        }
    }

    [Fact]
    public async Task Drain_closes_the_producer_and_ignores_every_late_report()
    {
        var delivered = new List<int>();
        var progress =
            new DispatchingProgress<int>(
                delivered.Add);

        progress.Report(1);
        Task firstDrain =
            progress.DrainAsync();
        progress.Report(2);
        Task secondDrain =
            progress.DrainAsync();

        await firstDrain;
        Assert.Same(
            firstDrain,
            secondDrain);
        Assert.Equal(
            [1],
            delivered);
    }

    [Fact]
    public async Task Drain_waits_for_accepted_reports_even_when_context_runs_them_out_of_order()
    {
        var context =
            new ReorderingSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        DispatchingProgress<int> progress;
        var delivered =
            new List<int>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                context);
            progress =
                new DispatchingProgress<int>(
                    delivered.Add);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previous);
        }

        await Task.WhenAll(
            Task.Run(
                () => progress.Report(1),
                TestContext.Current.CancellationToken),
            Task.Run(
                () => progress.Report(2),
                TestContext.Current.CancellationToken));
        Task drain =
            progress.DrainAsync();

        context.RunLast();
        Assert.False(
            drain.IsCompleted);
        Assert.Single(
            delivered);

        context.RunFirst();
        await drain;

        Assert.Equal(
            2,
            delivered.Count);
        Assert.Contains(
            1,
            delivered);
        Assert.Contains(
            2,
            delivered);
    }

    [Fact]
    public async Task Drain_waits_for_an_in_flight_report_without_a_synchronization_context()
    {
        await Task.Run(
            async () =>
            {
                Assert.Null(
                    SynchronizationContext.Current);
                using var entered =
                    new ManualResetEventSlim();
                using var release =
                    new ManualResetEventSlim();
                var progress =
                    new DispatchingProgress<int>(
                        _ =>
                        {
                            entered.Set();
                            release.Wait(
                                TestContext.Current
                                    .CancellationToken);
                        });
                Task report =
                    Task.Run(
                        () => progress.Report(1),
                        TestContext.Current
                            .CancellationToken);
                Assert.True(
                    entered.Wait(
                        TimeSpan.FromSeconds(5),
                        TestContext.Current
                            .CancellationToken));

                Task drain =
                    progress.DrainAsync();
                Assert.False(
                    drain.IsCompleted);

                release.Set();
                await report.ConfigureAwait(
                    false);
                await drain.ConfigureAwait(
                    false);
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Throwing_handler_preserves_the_exception_and_does_not_strand_drain()
    {
        var progress =
            new DispatchingProgress<int>(
                _ => throw new InvalidOperationException(
                    "observer"));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                () => progress.Report(1));

        Assert.Equal(
            "observer",
            error.Message);
        Assert.True(
            progress.DrainAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Throwing_queued_handler_releases_drain_before_exception_escapes_context()
    {
        var context =
            new ManualSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        DispatchingProgress<int> progress;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                context);
            progress =
                new DispatchingProgress<int>(
                    _ => throw new InvalidOperationException(
                        "queued observer"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previous);
        }

        await Task.Run(
            () => progress.Report(1),
            TestContext.Current.CancellationToken);
        Task drain =
            progress.DrainAsync();

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                context.RunAll);
        await drain;

        Assert.Equal(
            "queued observer",
            error.Message);
    }

    private sealed class ManualSynchronizationContext :
        SynchronizationContext
    {
        private readonly ConcurrentQueue<(
            SendOrPostCallback Callback,
            object? State)> _pending = [];

        public IReadOnlyCollection<(
            SendOrPostCallback Callback,
            object? State)> Pending =>
            _pending.ToArray();

        public override void Post(
            SendOrPostCallback callback,
            object? state) =>
            _pending.Enqueue(
                (callback, state));

        public void RunAll()
        {
            SynchronizationContext? previous =
                Current;
            SetSynchronizationContext(this);
            try
            {
                while (_pending.TryDequeue(
                           out var item))
                    item.Callback(item.State);
            }
            finally
            {
                SetSynchronizationContext(
                    previous);
            }
        }
    }

    private sealed class ReorderingSynchronizationContext :
        SynchronizationContext
    {
        private readonly object _gate =
            new();
        private readonly List<(
            SendOrPostCallback Callback,
            object? State)> _pending = [];

        public override void Post(
            SendOrPostCallback callback,
            object? state)
        {
            lock (_gate)
                _pending.Add(
                    (callback, state));
        }

        public void RunFirst() =>
            RunAt(0);

        public void RunLast()
        {
            int index;
            lock (_gate)
                index =
                    _pending.Count - 1;
            RunAt(index);
        }

        private void RunAt(
            int index)
        {
            (
                SendOrPostCallback Callback,
                object? State) item;
            lock (_gate)
            {
                item =
                    _pending[index];
                _pending.RemoveAt(
                    index);
            }
            SynchronizationContext? previous =
                Current;
            SetSynchronizationContext(
                this);
            try
            {
                item.Callback(
                    item.State);
            }
            finally
            {
                SetSynchronizationContext(
                    previous);
            }
        }
    }
}
