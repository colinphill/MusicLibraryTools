using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ScheduledScanServiceTests
{
    [Fact]
    public async Task OneShotTimerRunsCallbackAndSchedulesTheNextIntervalAfterCompletion()
    {
        using var scheduler = new ScheduledScanService();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Configure(TimeSpan.FromMilliseconds(30), () =>
        {
            called.TrySetResult();
            return Task.CompletedTask;
        });

        await called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => scheduler.NextRunUtc is not null,
            TimeSpan.FromSeconds(1)));
        Assert.True(scheduler.NextRunUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ViewModelPersistsAndConfiguresTheDeltaScanInterval()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        settings.SetPreference("Index.ScheduledScanMinutes", "15");
        using var library = new LibraryService(settings);
        var scheduler = new RecordingScheduler();

        var viewModel = new LibraryViewModel(
            library, new IndexBenchmarkService(settings), settings, scheduler);

        Assert.Equal(15, viewModel.ScheduledScanMinutes);
        Assert.Equal(TimeSpan.FromMinutes(15), scheduler.Interval);
        Assert.NotNull(scheduler.Callback);

        viewModel.ScheduledScanMinutes = 0;

        Assert.Null(scheduler.Interval);
        Assert.Equal("0", settings.GetPreference("Index.ScheduledScanMinutes"));
    }

    private sealed class RecordingScheduler : IScheduledScanService
    {
        public DateTimeOffset? NextRunUtc => null;
        public TimeSpan? Interval { get; private set; }
        public Func<Task>? Callback { get; private set; }
        public void Configure(TimeSpan? interval, Func<Task> callback)
        {
            Interval = interval;
            Callback = callback;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "scheduled-scan-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
