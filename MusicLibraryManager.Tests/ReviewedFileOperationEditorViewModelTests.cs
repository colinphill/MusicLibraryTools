using System.Globalization;
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

    [Fact]
    public void Destination_placeholder_tracks_operation_and_culture_without_changing_semantic_choices()
    {
        var localization =
            new SwitchingLocalizationService();
        var viewModel =
            new ReviewedFileOperationEditorViewModel(
                new RecordingFileOperations(),
                new NoFiles(),
                () => [],
                _ => Task.FromResult(true),
                localization: localization);
        Dictionary<
            ReviewedFileOperationKind,
            LocalizedChoice<ReviewedFileOperationKind>>
            kindChoices =
                viewModel.OperationKindChoices
                    .ToDictionary(
                        choice => choice.Value);
        Dictionary<
            ReviewedFileCollisionPolicy,
            LocalizedChoice<ReviewedFileCollisionPolicy>>
            collisionChoices =
                viewModel.CollisionPolicyChoices
                    .ToDictionary(
                        choice => choice.Value);
        var changed = new List<string?>();
        viewModel.PropertyChanged +=
            (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(
            "en-US:ReviewedFileOperation.DestinationFolderPlaceholder",
            viewModel.DestinationPlaceholder);

        changed.Clear();
        viewModel.SelectedKind =
            ReviewedFileOperationKind.Quarantine;

        Assert.Contains(
            nameof(viewModel.DestinationPlaceholder),
            changed);
        Assert.Equal(
            "en-US:ReviewedFileOperation.QuarantineFolderPlaceholder",
            viewModel.DestinationPlaceholder);
        Assert.Equal(
            ReviewedFileOperationKind.Quarantine,
            viewModel.SelectedKind);

        changed.Clear();
        localization.SetCulture("fr-FR");

        Assert.Contains(
            nameof(viewModel.DestinationPlaceholder),
            changed);
        Assert.Equal(
            "fr-FR:ReviewedFileOperation.QuarantineFolderPlaceholder",
            viewModel.DestinationPlaceholder);
        Assert.Equal(
            ReviewedFileOperationKind.Quarantine,
            viewModel.SelectedKind);
        foreach (
            ReviewedFileOperationKind value in
            Enum.GetValues<
                ReviewedFileOperationKind>())
        {
            LocalizedChoice<
                ReviewedFileOperationKind> choice =
                viewModel.OperationKindChoices
                    .Single(item =>
                        item.Value == value);
            Assert.Same(
                kindChoices[value],
                choice);
            Assert.Equal(value, choice.Value);
            Assert.StartsWith(
                "fr-FR:",
                choice.Label,
                StringComparison.Ordinal);
        }
        foreach (
            ReviewedFileCollisionPolicy value in
            Enum.GetValues<
                ReviewedFileCollisionPolicy>())
        {
            LocalizedChoice<
                ReviewedFileCollisionPolicy> choice =
                viewModel.CollisionPolicyChoices
                    .Single(item =>
                        item.Value == value);
            Assert.Same(
                collisionChoices[value],
                choice);
            Assert.Equal(value, choice.Value);
            Assert.StartsWith(
                "fr-FR:",
                choice.Label,
                StringComparison.Ordinal);
        }

        viewModel.SelectedKind =
            ReviewedFileOperationKind.Move;
        Assert.Equal(
            "fr-FR:ReviewedFileOperation.DestinationFolderPlaceholder",
            viewModel.DestinationPlaceholder);
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

    private sealed class SwitchingLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture =>
            _culture;

        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];

        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            $"{_culture.Name}:{key}";

        public string Format(
            string key,
            params object?[] arguments) =>
            $"{Get(key)}:{string.Join("|", arguments)}";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}:{count}";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(
            string cultureName)
        {
            _culture =
                CultureInfo.GetCultureInfo(
                    cultureName);
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }
}
