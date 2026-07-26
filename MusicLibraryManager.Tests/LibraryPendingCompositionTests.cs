using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryPendingCompositionTests
{
    [Fact]
    public async Task Inspector_then_legacy_preview_apply_as_one_plan()
    {
        const string path =
            @"C:\Music\Library-composed.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Artist" &&
                row.After == "Inspector artist");
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Title" &&
                row.After == "Reviewed");
        Assert.True(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        MetadataFilePlan applied = Assert.Single(
            operations.AppliedPlan!.Files);
        Assert.Equal(
            [TagFields.Title, TagFields.Artist],
            applied.Edits.Select(edit =>
                    edit.Field.KnownField)
                .ToArray());
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Inspector_then_legacy_preview_discard_clears_both_without_apply()
    {
        const string path =
            @"C:\Music\Library-discard.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(2, viewModel.PendingChanges.Count);

        await viewModel.RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Null(operations.AppliedPlan);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Empty(
            viewModel.OperationPreviewChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Failed_composed_apply_preserves_the_unapplied_inspector_draft()
    {
        const string path =
            @"C:\Music\Library-failed-composed.flac";
        var operations =
            new FailingApplyMetadataOperationService();
        (LibraryViewModel viewModel, _) =
            CreateLibrary(
                path,
                operations);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        EditableTagField artist =
            viewModel.Inspector.Fields.Single(field =>
                field.Field == TagFields.Artist);
        artist.Value = "Unapplied inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(1, operations.ApplyCalls);
        Assert.True(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.Equal(
            "Unapplied inspector artist",
            artist.Value);
        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Single(
            viewModel.OperationPreviewChanges);
        Assert.True(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Conflicting_legacy_and_inline_values_block_the_combined_plan()
    {
        const string path =
            @"C:\Music\Library-conflict.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        row.Title = "Different inline title";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.False(
            viewModel.CanApplyPendingChanges);
        Assert.False(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.Contains(
            viewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail &&
                change.DiagnosticDetail!.Contains(
                    "different values",
                    StringComparison.Ordinal));
        Assert.Null(operations.AppliedPlan);
    }

    [Fact]
    public async Task Different_source_snapshots_cannot_bypass_a_stale_direct_draft()
    {
        const string path =
            @"C:\Music\Library-snapshot-conflict.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        operations.Snapshots[path] = new(
            path,
            2,
            new DateTime(
                2026,
                7,
                26,
                1,
                0,
                0,
                DateTimeKind.Utc),
            "changed-hash");

        row.Artist = "Inline artist";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.False(
            viewModel.CanApplyPendingChanges);
        Assert.Contains(
            viewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail &&
                change.DiagnosticDetail!.Contains(
                    "different source snapshots",
                    StringComparison.Ordinal));
        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);
        Assert.Null(operations.AppliedPlan);
        Assert.True(row.HasChanges);
    }

    [Fact]
    public async Task Identical_legacy_and_inline_edits_are_deduplicated()
    {
        const string path =
            @"C:\Music\Library-identical.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        row.Title = "Reviewed";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.True(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        MetadataFilePlan applied =
            Assert.Single(
                operations.AppliedPlan!.Files);
        MetadataValueEdit edit =
            Assert.Single(applied.Edits);
        Assert.Equal(
            TagFields.Title,
            edit.Field.KnownField);
        Assert.Equal(
            ["Reviewed"],
            edit.Values);
    }

    private static (
        LibraryViewModel ViewModel,
        FakeMetadataOperationService Operations)
        CreateLibrary(
            string path,
            FakeMetadataOperationService? operations =
                null)
    {
        var record = new TrackRecord
        {
            Path = path,
            Artist = "Original artist",
            AlbumArtist = "Original artist",
            Album = "Album",
            Title = "Original title",
            CodecName = "FLAC",
            CodecType = CodecType.Lossless,
            LastWriteTime =
                new DateTime(2026, 7, 25),
        };
        var library =
            new FakeLibrary([record]);
        var settings =
            new FakeSettings();
        operations ??=
            new FakeMetadataOperationService();
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(
                    new MediaFileModel
                    {
                        Path = path,
                        Title = record.Title,
                        Artist = record.Artist,
                        IsWritable = true,
                        KnownFields =
                        [
                            new(
                                TagFields.Title,
                                record.Title),
                            new(
                                TagFields.Artist,
                                record.Artist),
                        ],
                    }),
                library,
                new FakeTagWriter(),
                new FakeArtworkService(),
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeFieldsEditor(),
                new FakeThumbnails(),
                new AppActivityService(),
                operations);
        var indexing =
            new IndexingViewModel(
                library,
                settings,
                new AppActivityService());
        return (
            new LibraryViewModel(
                library,
                new FakeReindex(),
                settings,
                inspector,
                new NavigationService(),
                indexing,
                new FakeThumbnails(),
                metadataOperations: operations,
                operationCatalog:
                    new MetadataOperationCatalog()),
            operations);
    }

    private static async Task
        WaitForAuthoritativePreviewAsync(
            LibraryViewModel viewModel)
    {
        for (int attempt = 0;
             attempt < 100 &&
             !viewModel
                 .IsDirectPendingPreviewReady;
             attempt++)
            await Task.Delay(
                20,
                TestContext.Current
                    .CancellationToken);
        Assert.True(
            viewModel
                .IsDirectPendingPreviewReady);
    }

    private sealed class
        FailingApplyMetadataOperationService :
        FakeMetadataOperationService
    {
        public int ApplyCalls { get; private set; }

        public override Task<MetadataApplyResult>
            ApplyAsync(
                MetadataOperationPlan plan,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default)
        {
            ApplyCalls++;
            throw new InvalidOperationException(
                "Simulated apply failure.");
        }
    }
}
