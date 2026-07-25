using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ReviewedFileOperationEditorViewModelTests
{
    [Fact]
    public async Task Preview_queues_the_same_captured_plan_without_direct_apply()
    {
        string source =
            Path.GetFullPath("source.flac");
        string destination =
            Path.GetFullPath("destination");
        var service =
            new RecordingFileOperations();
        ReviewedFileOperationPlan? reviewed = null;
        var viewModel =
            new ReviewedFileOperationEditorViewModel(
                service,
                new NoFiles(),
                () => [source],
                plan =>
                {
                    reviewed = plan;
                    return Task.FromResult(true);
                })
            {
                SelectedKind =
                    ReviewedFileOperationKind.Move,
                DestinationDirectory =
                    destination,
            };

        await viewModel.PreviewCommand
            .ExecuteAsync(null);

        Assert.Same(
            service.Previewed,
            service.LastPlan);
        Assert.Same(
            service.Previewed,
            reviewed);
        Assert.Single(
            viewModel.PreviewItems);
        Assert.False(
            viewModel.HasApplicablePreview);
        Assert.False(
            viewModel.HasUnsavedChanges);
        Assert.Null(service.Applied);
        Assert.Null(
            typeof(ReviewedFileOperationEditorViewModel)
                .GetProperty("ApplyCommand"));
    }

    [Fact]
    public async Task Editing_after_rejected_preview_invalidates_the_captured_plan()
    {
        var service =
            new RecordingFileOperations();
        var viewModel =
            new ReviewedFileOperationEditorViewModel(
                service,
                new NoFiles(),
                () =>
                    [Path.GetFullPath("song.flac")],
                _ => Task.FromResult(false))
            {
                SelectedKind =
                    ReviewedFileOperationKind.Copy,
                DestinationDirectory =
                    Path.GetFullPath("first"),
            };
        await viewModel.PreviewCommand
            .ExecuteAsync(null);

        viewModel.DestinationDirectory =
            Path.GetFullPath("second");

        Assert.False(
            viewModel.HasApplicablePreview);
        Assert.False(
            viewModel.HasUnsavedChanges);
        Assert.Empty(
            viewModel.PreviewItems);
    }

    [Fact]
    public async Task Preview_enters_review_queue_without_exposing_direct_apply()
    {
        string source =
            Path.GetFullPath("review-source.flac");
        string destination =
            Path.GetFullPath("review-destination");
        var service =
            new RecordingFileOperations();
        ReviewedFileOperationPlan? reviewed = null;
        int reviewNotifications = 0;
        var viewModel =
            new ReviewedFileOperationEditorViewModel(
                service,
                new NoFiles(),
                () => [source],
                plan =>
                {
                    reviewed = plan;
                    return Task.FromResult(true);
                })
            {
                SelectedKind =
                    ReviewedFileOperationKind.Move,
                DestinationDirectory =
                    destination,
            };
        viewModel.PreviewAddedToReview +=
            (_, _) => reviewNotifications++;

        await viewModel.PreviewCommand
            .ExecuteAsync(null);

        Assert.Same(
            service.Previewed,
            reviewed);
        Assert.Equal(
            1,
            reviewNotifications);
        Assert.Single(
            viewModel.PreviewItems);
        Assert.False(
            viewModel.HasApplicablePreview);
        Assert.False(
            viewModel.HasUnsavedChanges);
        Assert.Null(service.Applied);
        Assert.Contains(
            "review",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsavedMetadataPreflightPreventsPlanning()
    {
        var service =
            new RecordingFileOperations();
        var viewModel =
            new ReviewedFileOperationEditorViewModel(
                service,
                new NoFiles(),
                () =>
                    [Path.GetFullPath("song.flac")],
                _ => Task.FromResult(true),
                () =>
                    "Apply metadata first.")
            {
                SelectedKind =
                    ReviewedFileOperationKind.Rename,
                FileNameTemplate =
                    "renamed{Extension}",
            };

        await viewModel.PreviewCommand
            .ExecuteAsync(null);

        Assert.Null(
            service.Previewed);
        Assert.Equal(
            "Apply metadata first.",
            viewModel.Status);
    }

    private sealed class RecordingFileOperations :
        IReviewedFileOperationService
    {
        public ReviewedFileOperationPlan?
            Previewed { get; private set; }

        public ReviewedFileOperationPlan?
            LastPlan => Previewed;

        public ReviewedFileOperationPlan?
            Applied { get; private set; }

        public Task<ReviewedFileOperationPlan>
            PreviewAsync(
                ReviewedFileOperationRequest request,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default)
        {
            string source =
                request.SourcePaths[0];
            string destination =
                Path.Combine(
                    request.DestinationDirectory ??
                    Path.GetDirectoryName(source)!,
                    request.Kind ==
                    ReviewedFileOperationKind.Rename
                        ? "renamed.flac"
                        : Path.GetFileName(source));
            FileMutationKind kind =
                request.Kind switch
                {
                    ReviewedFileOperationKind.Copy =>
                        FileMutationKind.Copy,
                    ReviewedFileOperationKind.Quarantine =>
                        FileMutationKind.Quarantine,
                    _ => FileMutationKind.Move,
                };
            var item =
                new ReviewedFileOperationItem(
                    source,
                    destination,
                    kind,
                    []);
            var mutations =
                new FileMutationPlan(
                    "test",
                    Path.GetDirectoryName(
                        destination)!,
                    Path.Combine(
                        Path.GetDirectoryName(
                            destination)!,
                        "recovery"),
                    [new(
                        kind,
                        source,
                        destination,
                        null,
                        null)],
                    [],
                    DateTimeOffset.UtcNow);
            Previewed =
                new(
                    request,
                    [item],
                    mutations);
            return Task.FromResult(
                Previewed);
        }

        public Task<FileMutationSummary> ApplyAsync(
            ReviewedFileOperationPlan plan,
            IProgress<OperationProgress>? progress =
                null,
            CancellationToken ct = default)
        {
            Applied = plan;
            return Task.FromResult(
                new FileMutationSummary(
                    plan.Request.Kind ==
                    ReviewedFileOperationKind.Copy
                        ? 1
                        : 0,
                    0,
                    plan.Request.Kind ==
                    ReviewedFileOperationKind.Quarantine
                        ? 1
                        : 0,
                    0,
                    "journal.tsv",
                    [])
                {
                    Moved =
                        plan.Request.Kind is
                            ReviewedFileOperationKind
                                .Move or
                            ReviewedFileOperationKind
                                .Rename
                            ? 1
                            : 0,
                });
        }
    }

    private sealed class NoFiles :
        IFilePickerService
    {
        public Task<string?> PickFileAsync(
            string title,
            IReadOnlyList<FilePickerType>? types =
                null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(
            string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(
            string title,
            string suggestedName,
            string extension) =>
            Task.FromResult<string?>(null);
    }
}
