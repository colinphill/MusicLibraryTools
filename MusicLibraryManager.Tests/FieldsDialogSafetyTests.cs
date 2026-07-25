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
            [@"C:\track.flac"],
            (_, _) => Task.FromResult(true));
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
    public async Task Fields_review_handoff_is_cancellable_after_preview_confirmation()
    {
        var operations =
            new RecordingMetadataOperationService();
        var review =
            new BlockingReviewCoordinator();
        var activities = new AppActivityService();
        var viewModel = new FieldsDialogViewModel(
            new FakeMetadataDocumentService(Document()),
            operations,
            [@"C:\track.flac"],
            review.AddAsync,
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
        await review.Started.Task.WaitAsync(
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
        Assert.Equal(0, operations.ApplyCalls);
    }

    [Fact]
    public async Task Fields_confirmed_preview_is_staged_for_review_without_writing()
    {
        var operations =
            new RecordingMetadataOperationService();
        MetadataOperationPlan? reviewed = null;
        var viewModel = new FieldsDialogViewModel(
            new FakeMetadataDocumentService(Document()),
            operations,
            [@"C:\track.flac"],
            (plan, _) =>
            {
                reviewed = plan;
                return Task.FromResult(true);
            });
        await viewModel.Loading;
        viewModel.Rows.Single(row =>
            row.Field == TagFields.Title).Value =
                "Edited";
        bool? closed = null;
        viewModel.CloseRequested +=
            result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.NotNull(reviewed);
        Assert.Equal("Edited", Assert.Single(
            Assert.Single(reviewed!.Files).Edits).Values.Single());
        Assert.Equal(0, operations.ApplyCalls);
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

    private sealed class RecordingMetadataOperationService :
        FakeMetadataOperationService
    {
        public int ApplyCalls { get; private set; }

        public override Task<MetadataApplyResult>
            ApplyAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            throw new InvalidOperationException(
                "FieldsDialog must not apply directly.");
        }
    }

    private sealed class BlockingReviewCoordinator
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> AddAsync(
            MetadataOperationPlan plan,
            CancellationToken ct)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }
    }
}
