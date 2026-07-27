using System.Collections.Immutable;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class WorkbenchMetadataCompositionTests
{
    [Fact]
    public async Task Discard_all_is_enabled_only_while_changes_are_pending()
    {
        string path = Path.GetFullPath(
            "pending-command-state.flac");
        MediaDocument document = Document(path);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                new FakeMetadataOperationService());
        await viewModel.AddSourcesAsync([path]);

        Assert.False(viewModel.HasPendingChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));

        viewModel.Files.Single().Title =
            "Pending title";

        Assert.True(viewModel.HasPendingChanges);
        Assert.True(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));

        await viewModel.RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.False(viewModel.HasPendingChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Bulk_preview_and_later_grid_edit_remain_pending_and_apply_together()
    {
        string path = Path.GetFullPath("composed-grid.flac");
        MediaDocument document = Document(path);
        var operations = new FakeMetadataOperationService();
        WorkbenchViewModel viewModel =
            CreateWorkbench(document, operations);
        await viewModel.AddSourcesAsync([path]);

        bool accepted = await viewModel.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                Plan(
                    document,
                    TagFields.Title,
                    "Bulk title")),
            TestContext.Current.CancellationToken);
        viewModel.Files.Single().Artist = "Grid artist";

        Assert.True(accepted);
        Assert.Collection(
            viewModel.PendingChanges.OrderBy(row => row.Field),
            row =>
            {
                Assert.Equal("Artist", row.Field);
                Assert.Equal("Grid artist", row.After);
            },
            row =>
            {
                Assert.Equal("Title", row.Field);
                Assert.Equal("Bulk title", row.After);
            });

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(operations.AppliedPlan);
        MetadataFilePlan appliedFile = Assert.Single(
            operations.AppliedPlan.Files);
        Assert.Equal(
            [TagFields.Title, TagFields.Artist],
            appliedFile.Edits
                .Select(edit => edit.Field.KnownField)
                .ToArray());
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Grid_edit_made_during_apply_remains_pending_against_the_committed_baseline()
    {
        string path = Path.GetFullPath(
            "grid-edit-during-apply.flac");
        MediaDocument document = Document(path);
        var operations =
            new FakeMetadataOperationService
            {
                ApplyRelease = new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously),
            };
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations);
        await viewModel.AddSourcesAsync([path]);
        WorkbenchTrackViewModel file =
            Assert.Single(viewModel.Files);
        file.Title = "Committed title";

        Task apply =
            viewModel.ApplyCommand.ExecuteAsync(null);
        await operations.ApplyStarted.Task;
        file.Artist = "Later artist";
        operations.ApplyRelease.SetResult(true);
        await apply;

        MetadataFilePlan applied =
            Assert.Single(
                operations.AppliedPlan!.Files);
        Assert.Contains(
            applied.Edits,
            edit =>
                edit.Field.KnownField ==
                TagFields.Title &&
                edit.Values.SequenceEqual(
                    ["Committed title"]));
        Assert.DoesNotContain(
            applied.Edits,
            edit =>
                edit.Field.KnownField ==
                TagFields.Artist);
        MetadataPreviewRow pending =
            Assert.Single(
                viewModel.PendingChanges);
        Assert.Equal("Artist", pending.Field);
        Assert.Equal("Later artist", pending.After);
        Assert.True(file.HasChanges);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Inspector_edit_made_during_apply_remains_pending_against_the_committed_baseline()
    {
        string path = Path.GetFullPath(
            "inspector-edit-during-apply.flac");
        MediaDocument document = Document(path);
        var operations =
            new FakeMetadataOperationService
            {
                ApplyRelease = new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously),
            };
        WorkbenchSelectionInspectorViewModel inspector =
            CreateInspector(
                document,
                operations);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations,
                inspector);
        await viewModel.AddSourcesAsync([path]);
        Assert.True(
            await viewModel.TrySetSelectedFilesAsync(
                viewModel.Files));
        EditableTagField artist =
            inspector.Fields.Single(field =>
                field.Field == TagFields.Artist);
        artist.Value = "Committed artist";

        Task apply =
            viewModel.ApplyCommand.ExecuteAsync(null);
        await operations.ApplyStarted.Task;
        artist.Value = "Later artist";
        operations.ApplyRelease.SetResult(true);
        await apply;

        MetadataFilePlan applied =
            Assert.Single(
                operations.AppliedPlan!.Files);
        Assert.Contains(
            applied.Edits,
            edit =>
                edit.Field.KnownField ==
                TagFields.Artist &&
                edit.Values.SequenceEqual(
                    ["Committed artist"]));
        MetadataPreviewRow pending =
            Assert.Single(
                viewModel.PendingChanges);
        Assert.Equal("Artist", pending.Field);
        Assert.Equal("Later artist", pending.After);
        Assert.True(artist.IsModified);
        Assert.True(inspector.HasUnsavedChanges);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Bulk_preview_and_later_inspector_edit_are_discarded_together()
    {
        string path = Path.GetFullPath(
            "composed-inspector.flac");
        MediaDocument document = Document(path);
        var operations = new FakeMetadataOperationService();
        WorkbenchSelectionInspectorViewModel inspector =
            CreateInspector(document, operations);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations,
                inspector);
        await viewModel.AddSourcesAsync([path]);
        Assert.True(await viewModel.TrySetSelectedFilesAsync(
            viewModel.Files));

        bool accepted = await viewModel.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                Plan(
                    document,
                    TagFields.Title,
                    "Bulk title")),
            TestContext.Current.CancellationToken);
        inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Inspector artist";

        Assert.True(accepted);
        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Title" &&
                row.After == "Bulk title");
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Artist" &&
                row.After == "Inspector artist");

        await viewModel.RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Null(operations.AppliedPlan);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(
            "Original artist",
            inspector.Fields.Single(field =>
                field.Field == TagFields.Artist).Value);
    }

    [Fact]
    public async Task Inspector_artwork_draft_coexists_with_metadata_and_only_applies_from_review()
    {
        string path = Path.GetFullPath(
            "composed-artwork.flac");
        MediaDocument document = Document(path) with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    Description = "Original cover",
                    ImageType = "image/jpeg",
                    Width = 1200,
                    Height = 1200,
                    Size = 3,
                    Data = [1, 2, 3],
                },
            ],
        };
        var operations = new FakeMetadataOperationService();
        WorkbenchSelectionInspectorViewModel inspector =
            CreateInspector(document, operations);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations,
                inspector);
        await viewModel.AddSourcesAsync([path]);
        Assert.True(await viewModel.TrySetSelectedFilesAsync(
            viewModel.Files));
        Assert.True(await viewModel.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                Plan(
                    document,
                    TagFields.Title,
                    "Reviewed title")),
            TestContext.Current.CancellationToken));

        inspector.ArtworkMaxDimension = 600;
        await inspector.ScrubArtworkCommand.ExecuteAsync(null);

        Assert.Null(operations.AppliedPlan);
        Assert.Empty(operations.PreviewedArtworkSets);
        Assert.True(inspector.HasPendingArtworkChanges);
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Artwork");
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Title");

        await viewModel.ApplyCommand.ExecuteAsync(null);

        MetadataFilePlan applied =
            Assert.Single(operations.AppliedPlan!.Files);
        Assert.Single(applied.Edits);
        Assert.NotNull(applied.ArtworkEdit);
        Assert.NotNull(applied.ArtworkDifference);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(inspector.HasUnsavedChanges);
    }

    [Fact]
    public async Task Staged_artwork_editor_hydrates_the_selected_file_before_building_a_full_set_replacement()
    {
        string path = Path.GetFullPath(
            "lazy-artwork-authority.flac");
        MediaDocument shallow = Document(path);
        byte[] existingArtwork = [1, 2, 3];
        MediaDocument hydrated = shallow with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    Description = "Existing cover",
                    ImageType = "image/jpeg",
                    Width = 1200,
                    Height = 1200,
                    Size = existingArtwork.Length,
                    Data = existingArtwork,
                },
            ],
        };
        byte[] addedArtwork = [9, 8, 7, 6];
        string addedArtworkPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(
            addedArtworkPath,
            addedArtwork,
            TestContext.Current.CancellationToken);
        try
        {
            var operations =
                new FakeMetadataOperationService();
            WorkbenchViewModel viewModel =
                CreateWorkbench(
                    shallow,
                    operations,
                    files: new FakeFilePicker(
                        addedArtworkPath),
                    metadataDocuments:
                        new FakeMetadataDocumentService(
                            hydrated));

            await viewModel.AddSourcesAsync([path]);

            WorkbenchTrackViewModel file =
                Assert.Single(viewModel.Files);
            Assert.True(
                file.HasAuthoritativeArtwork);
            Assert.Single(
                viewModel.StagedArtworkItems);
            Assert.True(
                viewModel.AddStagedArtworkCommand
                    .CanExecute(null));

            await viewModel.AddStagedArtworkCommand
                .ExecuteAsync(null);
            await viewModel.PreviewStagedArtworkCommand
                .ExecuteAsync(null);

            ArtworkSetPreviewRequest request =
                Assert.Single(
                    operations.PreviewedArtworkSets).Value;
            Assert.Equal(2, request.Images.Length);
            Assert.Equal(
                existingArtwork,
                request.Images[0].Data);
            Assert.Equal(
                addedArtwork,
                request.Images[1].Data);
        }
        finally
        {
            File.Delete(addedArtworkPath);
        }
    }

    [Fact]
    public async Task Staged_artwork_editor_remains_disabled_when_document_hydration_is_unavailable()
    {
        string path = Path.GetFullPath(
            "artwork-hydration-unavailable.flac");
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                Document(path),
                new FakeMetadataOperationService());

        await viewModel.AddSourcesAsync([path]);

        WorkbenchTrackViewModel file =
            Assert.Single(viewModel.Files);
        Assert.False(
            file.HasAuthoritativeArtwork);
        Assert.False(
            viewModel.AddStagedArtworkCommand
                .CanExecute(null));
        Assert.False(
            viewModel.PreviewStagedArtworkCommand
                .CanExecute(null));
        Assert.False(
            viewModel.PreviewLocalArtworkCommand
                .CanExecute(null));
        Assert.Contains(
            "unavailable",
            viewModel.StatusDiagnosticDetail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selecting_another_file_cancels_the_previous_artwork_hydration_generation()
    {
        string firstPath = Path.GetFullPath(
            "artwork-hydration-first.flac");
        string secondPath = Path.GetFullPath(
            "artwork-hydration-second.flac");
        MediaDocument first = Document(firstPath);
        byte[] secondArtwork = [4, 5, 6];
        MediaDocument second = Document(secondPath) with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    Description = "Second cover",
                    ImageType = "image/jpeg",
                    Width = 600,
                    Height = 600,
                    Size = secondArtwork.Length,
                    Data = secondArtwork,
                },
            ],
        };
        var documents =
            new SelectionCancellableMetadataDocumentService(
                first,
                second);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                first,
                new FakeMetadataOperationService(),
                metadataDocuments: documents,
                workbench:
                    new MultiDocumentWorkbenchService(
                        first,
                        second));

        await viewModel.AddSourcesAsync(
            [firstPath, secondPath]);
        await documents.FirstLoadStarted.Task
            .WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current
                    .CancellationToken);

        viewModel.SelectedFile =
            viewModel.Files.Single(file =>
                file.Path == secondPath);
        await documents.FirstLoadCancelled.Task
            .WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current
                    .CancellationToken);

        WorkbenchTrackViewModel firstRow =
            viewModel.Files.Single(file =>
                file.Path == firstPath);
        WorkbenchTrackViewModel secondRow =
            viewModel.Files.Single(file =>
                file.Path == secondPath);
        Assert.False(
            firstRow.HasAuthoritativeArtwork);
        Assert.True(
            secondRow.HasAuthoritativeArtwork);
        Assert.Equal(
            secondArtwork,
            Assert.Single(
                secondRow.Document.Artwork).Data);
    }

    [Fact]
    public async Task Artwork_hydration_cancellation_without_selection_cancellation_is_reported()
    {
        string path = Path.GetFullPath(
            "artwork-hydration-unexpected-cancel.flac");
        var documents =
            new UnexpectedCancellationMetadataDocumentService();
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                Document(path),
                new FakeMetadataOperationService(),
                metadataDocuments:
                    documents);

        await viewModel.AddSourcesAsync([path]);
        await documents.LoadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        documents.ReleaseLoad.TrySetResult(
            true);
        DateTime deadline =
            DateTime.UtcNow.AddSeconds(5);
        while (viewModel.StatusDiagnosticDetail?.Contains(
                   "Injected artwork hydration cancellation.",
                   StringComparison.Ordinal) != true)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "Artwork hydration cancellation was not reported.");
            await Task.Delay(
                10,
                TestContext.Current.CancellationToken);
        }

        Assert.Contains(
            "Injected artwork hydration cancellation.",
            viewModel.StatusDiagnosticDetail);
        Assert.Contains(
            "load",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Assert.Single(viewModel.Files)
                .HasAuthoritativeArtwork);
    }

    [Fact]
    public async Task Discard_all_restores_inspector_artwork_without_writing()
    {
        string path = Path.GetFullPath(
            "discard-artwork.flac");
        MediaDocument document = Document(path) with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    ImageType = "image/jpeg",
                    Width = 800,
                    Height = 800,
                    Size = 3,
                    Data = [4, 5, 6],
                },
            ],
        };
        var operations = new FakeMetadataOperationService();
        WorkbenchSelectionInspectorViewModel inspector =
            CreateInspector(document, operations);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations,
                inspector);
        await viewModel.AddSourcesAsync([path]);
        Assert.True(await viewModel.TrySetSelectedFilesAsync(
            viewModel.Files));

        await inspector.RemoveArtworkCommand.ExecuteAsync(null);
        Assert.Empty(inspector.ArtworkItems);
        Assert.Single(viewModel.PendingChanges);

        await viewModel.RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Null(operations.AppliedPlan);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Single(inspector.ArtworkItems);
        Assert.False(inspector.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Inspector_drafts_report_metadata_artwork_and_mixed_intent_kinds(
        bool editMetadata,
        bool editArtwork)
    {
        string path = Path.GetFullPath(
            $"inspector-intent-{editMetadata}-{editArtwork}.flac");
        MediaDocument document = Document(path) with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    ImageType = "image/jpeg",
                    Width = 800,
                    Height = 800,
                    Size = 3,
                    Data = [7, 8, 9],
                },
            ],
        };
        var operations =
            new FakeMetadataOperationService();
        WorkbenchSelectionInspectorViewModel inspector =
            CreateInspector(
                document,
                operations);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                operations,
                inspector);
        await viewModel.AddSourcesAsync([path]);
        Assert.True(
            await viewModel.TrySetSelectedFilesAsync(
                viewModel.Files));

        if (editMetadata)
        {
            inspector.Fields.Single(field =>
                field.Field == TagFields.Artist).Value =
                    "Reviewed artist";
        }
        if (editArtwork)
        {
            await inspector.RemoveArtworkCommand
                .ExecuteAsync(null);
        }

        Assert.Equal(
            editMetadata,
            inspector.HasUnsavedMetadataChanges);
        Assert.Equal(
            editArtwork,
            inspector.HasUnsavedArtworkChanges);
        ReviewedMediaMutationUnit unit =
            Assert.Single(
                viewModel.PendingMutationUnits);
        ReviewedMediaMutationKind[] expected =
        [
            .. new[]
            {
                editMetadata
                    ? ReviewedMediaMutationKind
                        .Metadata
                    : (ReviewedMediaMutationKind?)null,
                editArtwork
                    ? ReviewedMediaMutationKind
                        .Artwork
                    : null,
            }.Where(kind => kind.HasValue)
                .Select(kind => kind!.Value),
        ];
        Assert.Equal(
            expected,
            unit.MutationKinds);
    }

    [Fact]
    public async Task Online_workflow_records_successfully_completed_discovery_and_search_steps()
    {
        string path = Path.GetFullPath(
            "online-completion.flac");
        MediaDocument document =
            Document(path);
        WorkbenchViewModel viewModel =
            CreateWorkbench(
                document,
                new FakeMetadataOperationService());
        await viewModel.AddSourcesAsync([path]);
        viewModel.SelectedFile =
            Assert.Single(viewModel.Files);

        Assert.False(
            viewModel.HasCompletedOnlineDiscovery);
        await viewModel.DiscoverOnlineAudioCommand
            .ExecuteAsync(null);
        Assert.True(
            viewModel.HasCompletedOnlineDiscovery);

        viewModel.ReleaseSearch.Artist =
            "Matched Artist";
        viewModel.ReleaseSearch.Album =
            "Matched Album";
        Assert.False(
            viewModel.HasCompletedOnlineSearch);
        await viewModel.SearchOnlineReleasesCommand
            .ExecuteAsync(null);
        Assert.True(
            viewModel.HasCompletedOnlineSearch);
        Assert.Single(
            viewModel.ReleaseMatches);

        viewModel.ReleaseSearch.Album =
            "Changed Album";
        Assert.False(
            viewModel.HasCompletedOnlineSearch);

        viewModel.HasCompletedOnlineDiscovery =
            true;
        viewModel.HasCompletedOnlineSearch =
            true;
        viewModel.SelectedOnlineMetadataScope =
            viewModel.OnlineMetadataScopeOptions
                .Single(option =>
                    option.Scope ==
                    WorkbenchOnlineMetadataScope
                        .AllFiles);
        Assert.False(
            viewModel.HasCompletedOnlineDiscovery);
        Assert.False(
            viewModel.HasCompletedOnlineSearch);
    }

    [Fact]
    public async Task Conflicting_metadata_intent_is_rejected_without_consuming_existing_preview()
    {
        string path = Path.GetFullPath(
            "conflicting-preview.flac");
        MediaDocument document = Document(path);
        var operations = new FakeMetadataOperationService();
        WorkbenchViewModel viewModel =
            CreateWorkbench(document, operations);
        await viewModel.AddSourcesAsync([path]);

        Assert.True(await viewModel.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                Plan(
                    document,
                    TagFields.Title,
                    "First title")),
            TestContext.Current.CancellationToken));
        bool accepted = await viewModel.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                Plan(
                    document,
                    TagFields.Title,
                    "Second title")),
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
        MetadataPreviewRow pending =
            Assert.Single(viewModel.PendingChanges);
        Assert.Equal("First title", pending.After);
    }

    private static WorkbenchViewModel CreateWorkbench(
        MediaDocument document,
        IMetadataOperationService operations,
        WorkbenchSelectionInspectorViewModel? inspector = null,
        IFilePickerService? files = null,
        IMetadataDocumentService? metadataDocuments = null,
        IWorkbenchService? workbench = null)
    {
        var settings = new FakeSettings();
        var journals = new OperationJournalService(
            new FileMutationCoordinator());
        return new(
            workbench ??
                new DocumentWorkbenchService(document),
            operations,
            new MetadataOperationCatalog(),
            new OperationRecipeStore(settings),
            new FakeAcoustIdDiscoveryService(),
            new FakeMusicBrainzMetadataProvider(),
            new MusicBrainzReleaseMappingService(),
            new FakeCoverArtArchiveProvider(),
            new FakeThumbnails(),
            new EditHistoryService(
                settings,
                journals),
            files ?? new FakeFilePicker(),
            new FakeDialogs(),
            settings,
            inspector: inspector,
            metadataDocuments: metadataDocuments);
    }

    private static WorkbenchSelectionInspectorViewModel
        CreateInspector(
            MediaDocument document,
            IMetadataOperationService operations) =>
        new(
            new FakeMediaService(),
            new FakeLibrary([]),
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService(),
            operations,
            new FakeMetadataDocumentService(document));

    private static MediaDocument Document(string path)
    {
        DateTime timestamp = new(
            2026,
            7,
            25,
            12,
            0,
            0,
            DateTimeKind.Utc);
        return new(
            path,
            [
                new(
                    "Vorbis comments",
                    [
                        new(
                            MetadataFieldKey.Known(
                                TagFields.Title),
                            ["Original title"]),
                        new(
                            MetadataFieldKey.Known(
                                TagFields.Artist),
                            ["Original artist"]),
                    ],
                    true,
                    true,
                    true,
                    true),
            ],
            [],
            null,
            new(
                path,
                1,
                timestamp,
                "metadata-hash"),
            true);
    }

    private static MetadataOperationPlan Plan(
        MediaDocument document,
        TagFields field,
        string value)
    {
        MetadataFieldKey key =
            MetadataFieldKey.Known(field);
        string before =
            document.FirstValue(field) ?? "";
        return new(
            Guid.NewGuid(),
            "Reviewed preview",
            [
                new(
                    document.Path,
                    document.Snapshot,
                    [
                        new(
                            key,
                            [before],
                            [value]),
                    ],
                    [
                        new(
                            key,
                            [value]),
                    ],
                    []),
            ],
            DateTimeOffset.UtcNow);
    }

    private sealed class DocumentWorkbenchService(
        MediaDocument document) :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            bool requested = request.Sources.Any(source =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(source),
                    document.Path));
            return Task.FromResult(
                new WorkbenchLoadResult(
                    requested ? [document] : [],
                    []));
        }
    }

    private sealed class MultiDocumentWorkbenchService(
        params MediaDocument[] documents) :
        IWorkbenchService
    {
        private readonly Dictionary<string, MediaDocument>
            _documents = documents.ToDictionary(
                document => document.Path,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WorkbenchLoadResult(
                    [
                        .. request.Sources
                            .Select(Path.GetFullPath)
                            .Where(_documents.ContainsKey)
                            .Select(path =>
                                _documents[path]),
                    ],
                    []));
        }
    }

    private sealed class
        SelectionCancellableMetadataDocumentService(
            MediaDocument first,
            MediaDocument second) :
            IMetadataDocumentService
    {
        public TaskCompletionSource<bool>
            FirstLoadStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            FirstLoadCancelled { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public async Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    path,
                    first.Path))
            {
                FirstLoadStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        ct);
                }
                catch (OperationCanceledException)
                {
                    FirstLoadCancelled.TrySetResult(
                        true);
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            return second;
        }
    }

    private sealed class
        UnexpectedCancellationMetadataDocumentService :
            IMetadataDocumentService
    {
        public TaskCompletionSource<bool>
            LoadStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleaseLoad { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public async Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            LoadStarted.TrySetResult(
                true);
            await ReleaseLoad.Task;
            throw new OperationCanceledException(
                "Injected artwork hydration cancellation.");
        }
    }
}
