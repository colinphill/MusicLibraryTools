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
            new FakeMetadataDocumentService(Document()),
            new FakeMetadataOperationService(),
            [@"C:\track.flac"]);
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
        var operations =
            new BlockingMetadataOperationService();
        var activities = new AppActivityService();
        var viewModel = new FieldsDialogViewModel(
            new FakeMetadataDocumentService(Document()),
            operations,
            [@"C:\track.flac"],
            activities);
        await viewModel.Loading;
        viewModel.Rows.Single(row => row.Field == TagFields.Title).Value = "Edited";
        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(
            viewModel.IsConfirmingSave,
            viewModel.StatusMessage);
        Task saving = viewModel.SaveCommand.ExecuteAsync(null);
        await operations.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        AppActivity activity = activities.Activities.First();
        Assert.Equal(ShellDestination.Library, activity.Destination);
        Assert.True(activity.CanCancel);

        Assert.True(activities.Cancel(activity.Id));
        await saving;

        Assert.Null(closed);
        Assert.True(viewModel.HasPendingChanges);
        Assert.Equal(MessageTone.Warning, viewModel.StatusTone);
        Assert.Equal(
            AppActivityState.Cancelled,
            activities.Activities.First().State);
    }

    private static MediaDocument Document() =>
        new(
            @"C:\track.flac",
            [new(
                "VorbisComment",
                [new(
                    MetadataFieldKey.Known(
                        TagFields.Title),
                    ["Original"])],
                true,
                true,
                true,
                true)],
            [],
            null,
            new(
                @"C:\track.flac",
                10,
                DateTime.UtcNow,
                "hash"),
            true);

    private sealed class BlockingMetadataOperationService :
        FakeMetadataOperationService
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<MetadataApplyResult>
            ApplyAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new MetadataApplyResult(
                0,
                [],
                []);
        }
    }
}
