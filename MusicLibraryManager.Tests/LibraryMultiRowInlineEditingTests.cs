using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryMultiRowInlineEditingTests
{
    private static readonly string AlphaPath =
        Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryManager.Tests",
            "alpha.flac");

    private static readonly string ZuluPath =
        Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryManager.Tests",
            "zulu.flac");

    [Fact]
    public async Task Multi_row_preview_batches_paths_and_fields_deterministically()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow alpha = Row(context, AlphaPath);
        LibraryRow zulu = Row(context, ZuluPath);
        string customKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));

        // Deliberately mutate the last path first and use a different
        // property order for each row. The reviewed plan must not depend
        // on the source collection or event-arrival order.
        zulu.MetadataValues[customKey] = "Zulu evening";
        zulu.TrackTotalEditValue = "24";
        zulu.Title = "Zulu reviewed";
        alpha.Title = "Alpha reviewed";
        alpha.MetadataValues[customKey] = "Alpha evening";
        alpha.TrackTotalEditValue = "12";

        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.Equal(
            [AlphaPath, ZuluPath],
            context.Operations
                .PreviewedValueEdits.Keys);
        Assert.Equal(
            [AlphaPath, ZuluPath],
            context.Operations
                .PreviewedSourceExpectations.Keys);
        AssertCanonicalFields(
            context.Operations
                .PreviewedValueEdits[AlphaPath],
            "Alpha reviewed",
            "12",
            "Alpha evening");
        AssertCanonicalFields(
            context.Operations
                .PreviewedValueEdits[ZuluPath],
            "Zulu reviewed",
            "24",
            "Zulu evening");
    }

    [Fact]
    public async Task Discard_all_restores_every_field_on_every_row()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow alpha = Row(context, AlphaPath);
        LibraryRow zulu = Row(context, ZuluPath);
        string customKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));

        alpha.Title = "Alpha draft";
        alpha.TrackTotalEditValue = "12";
        alpha.MetadataValues[customKey] =
            "Alpha evening";
        zulu.Title = "Zulu draft";
        zulu.TrackTotalEditValue = "24";
        zulu.MetadataValues[customKey] =
            "Zulu evening";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        await context.ViewModel
            .RevertPendingChangesCommand
            .ExecuteAsync(null);

        AssertRestored(
            alpha,
            "Alpha original",
            "10",
            "Alpha morning",
            customKey);
        AssertRestored(
            zulu,
            "Zulu original",
            "20",
            "Zulu morning",
            customKey);
        Assert.Empty(
            context.ViewModel.PendingChanges);
        Assert.False(
            context.ViewModel.HasPendingChanges);
        Assert.False(
            context.ViewModel.HasInlinePendingChanges);
        Assert.Null(
            context.Operations.AppliedPlan);
    }

    [Fact]
    public async Task Edits_during_multi_file_apply_remain_pending_against_each_applied_baseline()
    {
        TestContext context = CreateLibrary();
        context.Operations.ApplyRelease =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await context.ViewModel.ReloadAsync();
        LibraryRow alpha = Row(context, AlphaPath);
        LibraryRow zulu = Row(context, ZuluPath);
        alpha.Title = "Alpha applied";
        zulu.Title = "Zulu applied";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Task apply = context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null);
        await context.Operations
            .ApplyStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                Xunit.TestContext.Current
                    .CancellationToken);
        alpha.Title = "Alpha late";
        zulu.Title = "Zulu late";
        context.Operations.ApplyRelease
            .TrySetResult(true);
        await apply;
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataOperationPlan appliedPlan =
            Assert.IsType<MetadataOperationPlan>(
                context.Operations.AppliedPlan);
        Assert.Equal(
            [AlphaPath, ZuluPath],
            appliedPlan.Files.Select(
                file => file.Path));
        AssertAppliedTitle(
            appliedPlan,
            AlphaPath,
            "Alpha applied");
        AssertAppliedTitle(
            appliedPlan,
            ZuluPath,
            "Zulu applied");
        Assert.Equal(
            "Alpha applied",
            alpha.Record.Title);
        Assert.Equal(
            "Zulu applied",
            zulu.Record.Title);
        Assert.Equal("Alpha late", alpha.Title);
        Assert.Equal("Zulu late", zulu.Title);
        AssertPendingTitle(
            context.ViewModel,
            AlphaPath,
            "Alpha applied",
            "Alpha late");
        AssertPendingTitle(
            context.ViewModel,
            ZuluPath,
            "Zulu applied",
            "Zulu late");
        Assert.True(alpha.HasChanges);
        Assert.True(zulu.HasChanges);
        Assert.True(
            context.ViewModel.HasPendingChanges);
    }

    private static TestContext CreateLibrary()
    {
        List<TrackRecord> records =
        [
            CreateRecord(
                ZuluPath,
                "Zulu original",
                20,
                "Zulu morning"),
            CreateRecord(
                AlphaPath,
                "Alpha original",
                10,
                "Alpha morning"),
        ];
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var operations =
            new FakeMetadataOperationService();
        var reindex = new FakeReindex();
        MediaFileModel[] media =
        [
            .. records.Select(record =>
                new MediaFileModel
                {
                    Path = record.Path,
                    Title = record.Title,
                    Artist = record.Artist,
                    IsWritable = true,
                    KnownFields =
                    [
                        new(
                            TagFields.Title,
                            record.Title ?? ""),
                        new(
                            TagFields.Artist,
                            record.Artist ?? ""),
                    ],
                }),
        ];
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(media),
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
        return new(
            new LibraryViewModel(
                library,
                reindex,
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

    private static TrackRecord CreateRecord(
        string path,
        string title,
        int trackTotal,
        string customValue) =>
        new()
        {
            Path = path,
            Artist = "Original artist",
            AlbumArtist = "Original artist",
            Album = "Original album",
            Title = title,
            TrackTotal = trackTotal,
            CodecName = "FLAC",
            CodecType = CodecType.Lossless,
            LastWriteTime =
                new DateTime(2026, 7, 26),
            Metadata =
                new Dictionary<string, string[]>
                {
                    [CachedMetadataKeys.Custom(
                        "DJ_SET")] =
                        [customValue],
                },
        };

    private static LibraryRow Row(
        TestContext context,
        string path) =>
        Assert.Single(
            context.ViewModel.Rows,
            row => StringComparer
                .OrdinalIgnoreCase.Equals(
                    row.Path,
                    path));

    private static void AssertCanonicalFields(
        IReadOnlyList<MetadataValueEdit> edits,
        string title,
        string total,
        string customValue)
    {
        Assert.Equal(
            [
                MetadataGridValueKey.For(
                    MetadataFieldKey.Known(
                        TagFields.Title)),
                MetadataGridValueKey.For(
                    MetadataFieldKey.Known(
                        TagFields.TotalTracks)),
                MetadataGridValueKey.For(
                    MetadataFieldKey.Custom(
                        "DJ_SET")),
            ],
            edits.Select(edit =>
                MetadataGridValueKey.For(
                    edit.Field)));
        Assert.Equal(
            [title],
            edits[0].Values);
        Assert.Equal(
            [total],
            edits[1].Values);
        Assert.Equal(
            [customValue],
            edits[2].Values);
    }

    private static void AssertRestored(
        LibraryRow row,
        string title,
        string total,
        string customValue,
        string customKey)
    {
        Assert.Equal(title, row.Title);
        Assert.Equal(total, row.TrackTotalEditValue);
        Assert.Equal(
            customValue,
            row.MetadataValues[customKey]);
        Assert.False(row.HasChanges);
    }

    private static void AssertAppliedTitle(
        MetadataOperationPlan plan,
        string path,
        string expected)
    {
        MetadataValueEdit edit =
            Assert.Single(
                Assert.Single(
                    plan.Files,
                    file => StringComparer
                        .OrdinalIgnoreCase.Equals(
                            file.Path,
                            path))
                    .Edits);
        Assert.Equal(
            TagFields.Title,
            edit.Field.KnownField);
        Assert.Equal([expected], edit.Values);
    }

    private static void AssertPendingTitle(
        LibraryViewModel viewModel,
        string path,
        string before,
        string after)
    {
        MetadataPreviewRow pending =
            Assert.Single(
                viewModel.PendingChanges,
                change =>
                    StringComparer
                        .OrdinalIgnoreCase.Equals(
                            change.File,
                            Path.GetFileName(path)) &&
                    change.Field == "Title");
        Assert.Equal(before, pending.Before);
        Assert.Equal(after, pending.After);
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
                Xunit.TestContext.Current
                    .CancellationToken);
        Assert.True(
            viewModel
                .IsDirectPendingPreviewReady);
    }

    private sealed record TestContext(
        LibraryViewModel ViewModel,
        FakeMetadataOperationService Operations);
}
