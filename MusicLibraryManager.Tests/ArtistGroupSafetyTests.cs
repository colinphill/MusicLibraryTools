using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ArtistGroupSafetyTests
{
    [Fact]
    public async Task Similar_artist_merge_requires_a_mutation_and_recovery_summary()
    {
        var reconciler = new RecordingReconciler();
        var dialogs = new RecordingDialogs(false);
        var activities = new AppActivityService();
        var viewModel = new ArtistGroupViewModel(
            reconciler, Group(), dialogs, activities);

        await viewModel.MergeCommand.ExecuteAsync(null);

        Assert.Empty(reconciler.Calls);
        Assert.Contains("2 track(s)", dialogs.Message);
        Assert.Contains("1 spelling variant(s)", dialogs.Message);
        Assert.Contains("no recovery journal", dialogs.Message);
        Assert.Contains("no files were changed", viewModel.Status);
        Assert.Empty(activities.Activities);
    }

    [Fact]
    public async Task Confirmed_similar_artist_merge_reports_a_health_activity()
    {
        var reconciler = new RecordingReconciler();
        var dialogs = new RecordingDialogs(true);
        var activities = new AppActivityService();
        var viewModel = new ArtistGroupViewModel(
            reconciler, Group(), dialogs, activities);

        await viewModel.MergeCommand.ExecuteAsync(null);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal("Canoncial", call.From);
        Assert.Equal("Canonical", call.To);
        Assert.Equal(2, call.Paths.Count);
        Assert.True(viewModel.IsMerged);
        Assert.Equal(MessageTone.Success, viewModel.StatusTone);
        AppActivity activity = Assert.Single(activities.Activities);
        Assert.Equal(ShellDestination.Health, activity.Destination);
        Assert.Equal(AppActivityState.Completed, activity.State);
        Assert.False(activity.CanCancel);
    }

    [Fact]
    public async Task Similar_artist_merge_can_be_cancelled_from_its_activity()
    {
        var reconciler = new BlockingReconciler();
        var activities = new AppActivityService();
        var viewModel = new ArtistGroupViewModel(
            reconciler, Group(), new RecordingDialogs(true), activities);

        Task merge = viewModel.MergeCommand.ExecuteAsync(null);
        await reconciler.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AppActivity running = Assert.Single(activities.Activities);
        Assert.True(activities.Cancel(running.Id));
        await merge;

        Assert.False(viewModel.IsMerged);
        Assert.Equal(MessageTone.Warning, viewModel.StatusTone);
        Assert.Equal(AppActivityState.Cancelled, Assert.Single(activities.Activities).State);
    }

    private static SimilarArtistGroup Group() => new(
    [
        new ArtistVariant("Canonical", [@"C:\one.flac", @"C:\two.flac", @"C:\three.flac"]),
        new ArtistVariant("Canoncial", [@"C:\four.flac", @"C:\five.flac"]),
    ]);

    private sealed class RecordingReconciler : IArtistReconciler
    {
        public List<(IReadOnlyList<string> Paths, string From, string To)> Calls { get; } = [];

        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2,
            CancellationToken ct = default) => [];

        public Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Calls.Add((paths, from, to));
            return Task.FromResult(paths.Count);
        }
    }

    private sealed class RecordingDialogs(bool result) : IDialogCoordinator
    {
        public string? Message { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string primaryText)
        {
            Message = message;
            return Task.FromResult(result);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class BlockingReconciler : IArtistReconciler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2,
            CancellationToken ct = default) => [];

        public async Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }
}
