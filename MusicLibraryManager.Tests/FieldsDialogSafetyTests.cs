using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class FieldsDialogSafetyTests
{
    [Fact]
    public async Task Dirty_fields_dialog_requires_explicit_discard_confirmation()
    {
        var viewModel = new FieldsDialogViewModel(
            new FakeMediaService(Model()), new FakeTagWriter(), [@"C:\track.flac"]);
        await viewModel.Loading;
        viewModel.Rows.Single(row => row.Field == TagFields.Title).Value = "Edited";
        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;

        viewModel.CancelCommand.Execute(null);

        Assert.Null(closed);
        Assert.True(viewModel.IsConfirmingCancel);
        Assert.Equal(MessageTone.Warning, viewModel.StatusTone);
        Assert.Contains("Discard", viewModel.StatusMessage);

        viewModel.CancelCommand.Execute(null);
        Assert.False(closed);
    }

    [Fact]
    public async Task Fields_save_is_persistent_and_cancellable_after_summary_confirmation()
    {
        var writer = new BlockingTagWriter();
        var activities = new AppActivityService();
        var viewModel = new FieldsDialogViewModel(
            new FakeMediaService(Model()), writer, [@"C:\track.flac"], activities);
        await viewModel.Loading;
        viewModel.Rows.Single(row => row.Field == TagFields.Title).Value = "Edited";
        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsConfirmingSave);
        Task saving = viewModel.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AppActivity activity = Assert.Single(activities.Activities);
        Assert.Equal(ShellDestination.Library, activity.Destination);
        Assert.True(activity.CanCancel);

        Assert.True(activities.Cancel(activity.Id));
        await saving;

        Assert.Null(closed);
        Assert.True(viewModel.HasPendingChanges);
        Assert.Equal(MessageTone.Warning, viewModel.StatusTone);
        Assert.Equal(AppActivityState.Cancelled, Assert.Single(activities.Activities).State);
    }

    private static MediaFileModel Model() => new()
    {
        Path = @"C:\track.flac",
        Title = "Original",
        IsWritable = true,
        KnownFields = [new TagFieldValue(TagFields.Title, "Original")],
    };

    private sealed class BlockingTagWriter : ITagWriteService
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BatchWriteResult> ApplyAsync(
            IReadOnlyList<string> paths,
            IReadOnlyList<TagEdit> edits,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new BatchWriteResult([]);
        }
    }
}
