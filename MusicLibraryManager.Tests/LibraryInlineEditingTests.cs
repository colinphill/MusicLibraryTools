using MetadataCaching;
using System.Collections.Specialized;
using System.Globalization;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryInlineEditingTests
{
    [Fact]
    public async Task Standard_total_and_custom_edits_stage_immediately_without_writing()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        string customKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));

        row.Title = "Edited title";
        row.TrackTotalEditValue = "24";
        row.MetadataValues[customKey] =
            "Evening";

        Assert.True(row.HasChanges);
        Assert.True(
            context.ViewModel.HasInlinePendingChanges);
        Assert.True(
            context.ViewModel.HasPendingChanges);
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);
        Assert.True(
            context.ViewModel
                .ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.Collection(
            context.ViewModel.PendingChanges
                .OrderBy(change => change.Field),
            change =>
            {
                Assert.Equal("DJ_SET", change.Field);
                Assert.Equal(
                    "Morning",
                    change.Before);
                Assert.Equal(
                    "Evening",
                    change.After);
            },
            change =>
            {
                Assert.Equal("Title", change.Field);
                Assert.Equal(
                    "Original title",
                    change.Before);
                Assert.Equal(
                    "Edited title",
                    change.After);
            },
            change =>
            {
                Assert.Equal(
                    "Track total",
                    change.Field);
                Assert.Equal("12", change.Before);
                Assert.Equal("24", change.After);
            });

        MetadataValueEdit[] intent =
        [
            .. row.CreatePendingEdits()
                .OrderBy(edit =>
                    MetadataGridValueKey.For(
                        edit.Field)),
        ];
        Assert.Equal(3, intent.Length);
        Assert.Contains(
            intent,
            edit =>
                edit.Field.KnownField ==
                    TagFields.Title &&
                edit.Values.SequenceEqual(
                    ["Edited title"]));
        Assert.Contains(
            intent,
            edit =>
                edit.Field.KnownField ==
                    TagFields.TotalTracks &&
                edit.Values.SequenceEqual(["24"]));
        Assert.Contains(
            intent,
            edit =>
                edit.Field.CustomName ==
                    "DJ_SET" &&
                edit.Values.SequenceEqual(
                    ["Evening"]));

        Assert.Null(
            context.Operations.AppliedPlan);
        Assert.Single(
            context.Operations
                .PreviewedValueEdits);
        Assert.Empty(context.Reindex.Paths);
        Assert.Equal(
            "Original title",
            row.Record.Title);
        Assert.Equal(0, context.Library.IndexCallCount);
    }

    [Fact]
    public async Task Immediate_pending_rows_use_localized_known_field_labels()
    {
        var localization =
            new KeyLocalizationService(
                "de-DE");
        TestContext context = CreateLibrary(
            localization: localization);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);

        row.Title = "Bearbeiteter Titel";

        MetadataPreviewRow pending =
            Assert.Single(
                context.ViewModel
                    .PendingChanges);
        Assert.Equal(
            "de-DE:Settings.Choice.TagFields.Title",
            pending.Field);
        Assert.False(
            context.ViewModel
                .IsDirectPendingPreviewReady);
    }

    [Fact]
    public async Task Stable_stale_field_codes_render_a_localized_field_summary()
    {
        var localization =
            new KeyLocalizationService(
                "de-DE");
        var operations =
            new FakeMetadataOperationService
            {
                ValuePlanTransform = plan =>
                    plan with
                    {
                        Files =
                        [
                            .. plan.Files.Select(
                                file =>
                                    file with
                                    {
                                        Issues =
                                        [
                                            .. file.Issues,
                                            new(
                                                "metadata.edit-field-changed:K_Title",
                                                OperationIssueSeverity.Blocker,
                                                "The Title value changed after editing started.",
                                                file.Path),
                                        ],
                                    }),
                        ],
                    },
            };
        TestContext context = CreateLibrary(
            localization: localization,
            operations: operations);
        await context.ViewModel.ReloadAsync();
        Assert.Single(
                context.ViewModel.Rows)
            .Title = "Bearbeiteter Titel";

        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataPreviewRow issue =
            Assert.Single(
                context.ViewModel
                    .PendingChanges,
                change =>
                    change
                        .HasDiagnosticDetail);
        Assert.Equal(
            "de-DE:Library.PendingChanges.FieldChanged" +
            "(de-DE:Settings.Choice.TagFields.Title)",
            issue.After);
        Assert.Equal(
            "The Title value changed after editing started.",
            issue.DiagnosticDetail);
        Assert.False(
            context.ViewModel
                .CanApplyPendingChanges);
    }

    [Fact]
    public async Task Editing_back_to_the_original_value_removes_the_draft()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        row.Title = "Temporary title";
        Assert.True(row.HasChanges);

        row.Title = "Original title";

        Assert.False(row.HasChanges);
        Assert.False(
            context.ViewModel
                .HasInlinePendingChanges);
        Assert.Empty(
            context.ViewModel.PendingChanges);
        Assert.False(
            context.ViewModel
                .ApplyLibraryOperationCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Rapid_edits_publish_only_the_latest_authoritative_value()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);

        row.Title = "First";
        row.Title = "Second";
        row.Title = "Final";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataValueEdit edit =
            Assert.Single(
                Assert.Single(
                    context.Operations
                        .PreviewedValueEdits)
                    .Value);
        Assert.Equal(
            ["Final"],
            edit.Values);
        Assert.Equal(
            "Final",
            Assert.Single(
                context.ViewModel
                    .PendingChanges).After);
    }

    [Fact]
    public async Task Discard_restores_all_inline_values_and_never_applies()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        string customKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));
        row.Title = "Edited title";
        row.TrackTotalEditValue = "24";
        row.MetadataValues[customKey] =
            "Evening";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        await context.ViewModel
            .RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Equal(
            "Original title",
            row.Title);
        Assert.Equal(
            "12",
            row.TrackTotalEditValue);
        Assert.Equal(
            "Morning",
            row.MetadataValues[customKey]);
        Assert.False(row.HasChanges);
        Assert.False(
            context.ViewModel
                .HasInlinePendingChanges);
        Assert.Empty(
            context.ViewModel.PendingChanges);
        Assert.Null(
            context.Operations.AppliedPlan);
        Assert.Empty(context.Reindex.Paths);
        Assert.Equal(0, context.Library.IndexCallCount);
    }

    [Fact]
    public async Task Apply_builds_one_reviewed_value_plan_and_clears_drafts()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        string customKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));
        row.Title = "Applied title";
        row.TrackTotalEditValue = "24";
        row.MetadataValues[customKey] =
            "Evening";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        IReadOnlyList<MetadataValueEdit> previewed =
            Assert.Single(
                    context.Operations
                        .PreviewedValueEdits)
                .Value;
        Assert.Equal(3, previewed.Count);
        Assert.Contains(
            previewed,
            edit =>
                edit.Field.KnownField ==
                    TagFields.Title &&
                edit.Values.SequenceEqual(
                    ["Applied title"]));
        Assert.Contains(
            previewed,
            edit =>
                edit.Field.KnownField ==
                    TagFields.TotalTracks &&
                edit.Values.SequenceEqual(["24"]));
        Assert.Contains(
            previewed,
            edit =>
                edit.Field.CustomName ==
                    "DJ_SET" &&
                edit.Values.SequenceEqual(
                    ["Evening"]));
        MetadataFilePlan applied =
            Assert.Single(
                context.Operations
                    .AppliedPlan!.Files);
        Assert.Equal(
            3,
            applied.Edits.Length);
        Assert.Single(
            context.Operations
                .PreviewedSourceExpectations);
        Assert.Empty(
            context.ViewModel.PendingChanges);
        Assert.False(
            context.ViewModel
                .HasInlinePendingChanges);
        Assert.Equal(
            "Applied title",
            row.Title);
        Assert.Equal(
            "Applied title",
            row.Record.Title);
        Assert.Equal(
            "24",
            row.TrackTotalEditValue);
        Assert.Equal(
            24,
            row.Record.TrackTotal);
        Assert.Equal(
            "Evening",
            row.MetadataValues[customKey]);
        await context.ViewModel.ReloadAsync();
        Assert.Same(
            row,
            Assert.Single(
                context.ViewModel.Rows));
        Assert.Equal(
            "Applied title",
            row.Title);
        Assert.Empty(context.Reindex.Paths);
        Assert.Equal(0, context.Library.IndexCallCount);
    }

    [Fact]
    public async Task Reload_preserves_staged_row_and_its_original_snapshot()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow draft =
            Assert.Single(context.ViewModel.Rows);
        draft.Title = "Draft title";

        await context.ViewModel.ReloadAsync();

        Assert.NotSame(
            draft,
            Assert.Single(
                context.ViewModel.Rows));
        MetadataPreviewRow pending =
            Assert.Single(
                context.ViewModel.PendingChanges);
        Assert.Equal(
            "Original title",
            pending.Before);
        Assert.Equal(
            "Draft title",
            pending.After);
        Assert.Null(
            context.Operations.AppliedPlan);
    }

    [Fact]
    public async Task Reload_merges_a_draft_onto_fresh_untouched_cache_fields()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow draft =
            Assert.Single(context.ViewModel.Rows);
        draft.Title = "Draft title";
        context.Records[0] =
            context.Records[0] with
            {
                Title =
                    "Fresh cached title",
                Album = "Fresh cached album",
            };

        await context.ViewModel.ReloadAsync();

        LibraryRow merged =
            Assert.Single(context.ViewModel.Rows);
        Assert.NotSame(draft, merged);
        Assert.Equal("Draft title", merged.Title);
        Assert.Equal(
            "Fresh cached album",
            merged.Album);
        Assert.Equal(
            ["Original title"],
            merged.CreatePendingSourceExpectation()
                .OriginalValues[
                    MetadataFieldKey.Known(
                        TagFields.Title)]);
        await context.ViewModel
            .RevertPendingChangesCommand
            .ExecuteAsync(null);
        Assert.Equal(
            "Fresh cached title",
            merged.Title);
        Assert.Equal(
            "Fresh cached album",
            merged.Album);
        Assert.Equal(
            "Fresh cached title",
            merged.MetadataValues[
                MetadataGridValueKey.For(
                    MetadataFieldKey.Known(
                        TagFields.Title))]);
        Assert.False(merged.HasChanges);
    }

    [Fact]
    public async Task Known_metadata_columns_share_the_same_pending_value_as_standard_columns()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        string titleKey =
            MetadataGridValueKey.For(
                MetadataFieldKey.Known(
                    TagFields.Title));
        string totalKey =
            MetadataGridValueKey.For(
                MetadataFieldKey.Known(
                    TagFields.TotalTracks));

        row.MetadataValues[titleKey] =
            "Column title";
        row.MetadataValues[totalKey] =
            "18";

        Assert.Equal(
            "Column title",
            row.Title);
        Assert.Equal(
            "18",
            row.TrackTotalEditValue);
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);
        Assert.Collection(
            context.ViewModel.PendingChanges
                .OrderBy(change => change.Field),
            change =>
            {
                Assert.Equal(
                    "Title",
                    change.Field);
                Assert.Equal(
                    "Column title",
                    change.After);
            },
            change =>
            {
                Assert.Equal(
                    "Track total",
                    change.Field);
                Assert.Equal(
                    "18",
                    change.After);
            });
    }

    [Fact]
    public void Multi_value_columns_use_lossless_escaped_semicolon_syntax()
    {
        string key =
            MetadataGridValueKey.For(
                MetadataFieldKey.Custom(
                    "MOODS"));
        var row = new LibraryRow(
            new TrackRecord
            {
                Path =
                    @"C:\Music\Multi.flac",
                Metadata =
                    new Dictionary<
                        string,
                        string[]>
                    {
                        [CachedMetadataKeys.Custom(
                            "MOODS")] =
                        [
                            "Warm; acoustic",
                            "Late night",
                        ],
                    },
            });

        Assert.Equal(
            @"Warm\; acoustic; Late night",
            row.MetadataValues[key]);
        row.MetadataValues[key] =
            @"Warm\; electric; Late night";

        MetadataValueEdit edit =
            Assert.Single(
                row.CreatePendingEdits());
        Assert.Equal(
            ["Warm; electric", "Late night"],
            edit.Values);
        MetadataPreviewRow preview =
            Assert.Single(
                row.CreatePendingChangeRows());
        Assert.Equal(
            @"Warm\; acoustic; Late night",
            preview.Before);
        Assert.Equal(
            @"Warm\; electric; Late night",
            preview.After);
    }

    [Fact]
    public void Direct_known_fields_use_lossless_escaped_semicolon_syntax()
    {
        var row = new LibraryRow(
            new TrackRecord
            {
                Path =
                    @"C:\Music\Known-direct.flac",
                Artist = "Primary artist",
                Metadata =
                    new Dictionary<
                        string,
                        string[]>
                    {
                        [nameof(TagFields.Artist)] =
                        [
                            "Primary artist",
                            "Featured; artist",
                        ],
                    },
            });

        row.Artist =
            @"Replacement\; artist; Guest";

        LibraryPendingMetadataEdit pending =
            Assert.Single(
                row.CreatePendingEditStates());
        Assert.Equal(
            [
                "Replacement; artist",
                "Guest",
            ],
            pending.Edit.Values);
        Assert.Equal(
            [
                "Primary artist",
                "Featured; artist",
            ],
            pending.OriginalValues);
        MetadataPreviewRow preview =
            Assert.Single(
                row.CreatePendingChangeRows());
        Assert.Equal(
            @"Primary artist; Featured\; artist",
            preview.Before);
        Assert.Equal(
            @"Replacement\; artist; Guest",
            preview.After);
    }

    [Fact]
    public void Every_supported_built_in_inline_field_stages_its_semantic_edit()
    {
        var row = new LibraryRow(
            new TrackRecord
            {
                Path =
                    @"C:\Music\All-inline.flac",
                Title = "Original title",
                Artist = "Original artist",
                AlbumArtist = "Original album artist",
                Album = "Original album",
                TrackNumber = 1,
                TrackTotal = 10,
                DiscNumber = 1,
                DiscTotal = 2,
                ReleaseDate = "2025",
            });
        row.Title = "";
        row.Artist = "Artist";
        row.AlbumArtist = "Album artist";
        row.Album = "Album";
        row.Genre = "Genre";
        row.Composer = "Composer";
        row.Grouping = "Grouping";
        row.Year = "2026";
        row.TrackEditValue = "2";
        row.TrackTotalEditValue = "12";
        row.DiscEditValue = "2";
        row.DiscTotalEditValue = "3";
        row.Comment = "Comment";

        IReadOnlyList<MetadataValueEdit> edits =
            row.CreatePendingEdits();

        Assert.Equal(13, edits.Count);
        Assert.Equal(
            Enum.GetValues<TagFields>()
                .Where(field => field is
                    TagFields.Title or
                    TagFields.Artist or
                    TagFields.AlbumArtist or
                    TagFields.Album or
                    TagFields.Genre or
                    TagFields.Composer or
                    TagFields.Grouping or
                    TagFields.Date or
                    TagFields.TrackNumber or
                    TagFields.TotalTracks or
                    TagFields.DiscNumber or
                    TagFields.TotalDiscs or
                    TagFields.Comment)
                .OrderBy(field => field),
            edits.Select(edit =>
                    edit.Field.KnownField!.Value)
                .OrderBy(field => field));
        Assert.Empty(
            Assert.Single(
                edits,
                edit =>
                    edit.Field.KnownField ==
                    TagFields.Title).Values);
    }

    [Fact]
    public void Reverting_a_known_multi_value_column_restores_every_original_value()
    {
        string key =
            MetadataGridValueKey.For(
                MetadataFieldKey.Known(
                    TagFields.Title));
        var row = new LibraryRow(
            new TrackRecord
            {
                Path =
                    @"C:\Music\Known multi.flac",
                Title = "Part one",
                Metadata =
                    new Dictionary<
                        string,
                        string[]>
                    {
                        [nameof(TagFields.Title)] =
                        [
                            "Part one",
                            "Part two",
                        ],
                    },
            });
        row.MetadataValues[key] =
            "Part one; Part three";

        row.RevertPendingChanges();

        Assert.Equal(
            "Part one; Part two",
            row.MetadataValues[key]);
        Assert.Equal(
            "Part one",
            row.Title);
        Assert.False(row.HasChanges);
    }

    [Fact]
    public void Missing_title_is_not_a_filename_backed_pending_deletion()
    {
        var row = new LibraryRow(
            new TrackRecord
            {
                Path =
                    @"C:\Music\No title.flac",
                Title = null,
            });

        Assert.Equal("", row.Title);
        Assert.False(row.HasChanges);
        Assert.Empty(
            row.CreatePendingEdits());
        row.Title = "";
        Assert.False(row.HasChanges);
        Assert.Empty(
            row.CreatePendingChangeRows());
    }

    [Fact]
    public async Task Conflicting_inline_and_inspector_values_block_the_authoritative_preview()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        Assert.True(
            await context.ViewModel.SelectAsync(
                [row]));
        row.Title = "Inline title";
        EditableTagField title =
            Assert.Single(
                context.Inspector.Fields,
                field =>
                    field.Field ==
                    TagFields.Title);
        title.Value = "Inspector title";

        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataEditSourceExpectation expectation =
            context.Operations
                .PreviewedSourceExpectations[
                    row.Path];
        Assert.Equal(
            ["Original title"],
            expectation.OriginalValues[
                MetadataFieldKey.Known(
                    TagFields.Title)]);
        Assert.Equal(
            ["Original artist"],
            expectation.OriginalValues[
                MetadataFieldKey.Known(
                    TagFields.Artist)]);
        Assert.False(
            context.ViewModel
                .CanApplyPendingChanges);
        Assert.Contains(
            context.ViewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail &&
                change.DiagnosticDetail!.Contains(
                    "different values",
                    StringComparison.Ordinal));
        Assert.Null(
            context.Operations.AppliedPlan);
    }

    [Fact]
    public async Task Edit_made_during_apply_remains_pending_against_the_applied_value()
    {
        TestContext context = CreateLibrary();
        context.Operations.ApplyRelease =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        row.Title = "First applied title";
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
        row.Title = "Late title";
        context.Operations.ApplyRelease
            .TrySetResult(true);
        await apply;
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataValueEdit applied =
            Assert.Single(
                Assert.Single(
                    context.Operations
                        .AppliedPlan!.Files)
                    .Edits);
        Assert.Equal(
            ["First applied title"],
            applied.Values);
        Assert.Equal(
            "Late title",
            row.Title);
        Assert.Equal(
            "First applied title",
            row.Record.Title);
        MetadataPreviewRow pending =
            Assert.Single(
                context.ViewModel
                    .PendingChanges,
                change =>
                    change.Field ==
                    "Title");
        Assert.Equal(
            "First applied title",
            pending.Before);
        Assert.Equal(
            "Late title",
            pending.After);
    }

    [Fact]
    public async Task Artwork_only_apply_invalidates_the_visible_thumbnail_cache()
    {
        TestContext context = CreateLibrary(
            new PreparedImage(
                [7, 8, 9],
                "image/jpeg",
                640,
                640));
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        await context.ViewModel
            .LoadThumbnailAsync(row);
        Assert.True(row.ThumbnailLoaded);
        Assert.Equal(
            1,
            context.Library.ArtworkReadCount);
        Assert.True(
            await context.ViewModel.SelectAsync(
                [row]));

        await context.Inspector
            .AddArtworkCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);
        Assert.Contains(
            row.Path,
            context.Operations
                .PreviewedArtworkSourceExpectations
                .Keys);
        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.False(row.ThumbnailLoaded);
        Assert.Null(row.ThumbnailSource);
        await context.ViewModel
            .LoadThumbnailAsync(row);
        Assert.Equal(
            2,
            context.Library.ArtworkReadCount);
    }

    [Fact]
    public async Task Failed_inline_apply_retains_the_draft_and_original()
    {
        var operations =
            new ThrowingMetadataOperationService();
        TestContext context = CreateLibrary(
            operations: operations);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        row.Title = "Unapplied title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.True(row.HasChanges);
        Assert.Equal(
            "Original title",
            row.Record.Title);
        Assert.Equal(
            "Unapplied title",
            row.Title);
        Assert.True(
            context.ViewModel
                .CanApplyPendingChanges);
        Assert.Single(
            context.ViewModel
                .PendingChanges);
        Assert.Null(operations.AppliedPlan);
    }

    [Fact]
    public async Task Inspector_edit_made_during_apply_remains_pending()
    {
        TestContext context = CreateLibrary();
        context.Operations.ApplyRelease =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        await context.ViewModel.ReloadAsync();
        Assert.True(
            await context.ViewModel.SelectAsync(
                [Assert.Single(
                    context.ViewModel.Rows)]));
        EditableTagField artist =
            Assert.Single(
                context.Inspector.Fields,
                field =>
                    field.Field ==
                    TagFields.Artist);
        artist.Value =
            "First inspector artist";
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
        artist.Value =
            "Late inspector artist";
        context.Operations.ApplyRelease
            .TrySetResult(true);
        await apply;
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        MetadataValueEdit applied =
            Assert.Single(
                Assert.Single(
                    context.Operations
                        .AppliedPlan!.Files)
                    .Edits);
        Assert.Equal(
            ["First inspector artist"],
            applied.Values);
        Assert.True(
            context.Inspector
                .HasUnsavedChanges);
        Assert.Equal(
            "Late inspector artist",
            artist.Value);
        Assert.Contains(
            context.ViewModel.PendingChanges,
            change =>
                change.Field == "Artist" &&
                change.After ==
                "Late inspector artist");
    }

    [Fact]
    public async Task Apply_reapplies_the_current_filter_and_republishes_rows()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        context.ViewModel.FilterText =
            "Original title";
        await context.ViewModel
            .ApplyFilterNowAsync(
                Xunit.TestContext.Current
                    .CancellationToken);
        LibraryRow row =
            Assert.Single(
                context.ViewModel.Rows);
        int rowsChanged = 0;
        context.ViewModel.PropertyChanged +=
            (_, args) =>
            {
                if (args.PropertyName ==
                    nameof(
                        LibraryViewModel.Rows))
                    rowsChanged++;
            };
        row.Title = "Applied title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Empty(context.ViewModel.Rows);
        Assert.True(
            rowsChanged > 0);
    }

    [Fact]
    public async Task Discard_reapplies_the_current_filter_and_republishes_rows()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        context.ViewModel.FilterText =
            "Original title";
        await context.ViewModel
            .ApplyFilterNowAsync(
                Xunit.TestContext.Current
                    .CancellationToken);
        LibraryRow row =
            Assert.Single(
                context.ViewModel.Rows);
        row.Title = "Draft title";
        int rowsChanged = 0;
        context.ViewModel.PropertyChanged +=
            (_, args) =>
            {
                if (args.PropertyName ==
                    nameof(
                        LibraryViewModel.Rows))
                    rowsChanged++;
            };

        await context.ViewModel
            .RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Equal(
            "Original title",
            Assert.Single(
                context.ViewModel.Rows)
                .Title);
        Assert.True(
            rowsChanged > 0);
    }

    [Fact]
    public async Task Reload_refreshes_the_selected_record_when_no_draft_exists()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        Assert.True(
            await context.ViewModel.SelectAsync(
                [Assert.Single(
                    context.ViewModel.Rows)]));
        TrackRecord refreshed =
            context.Records[0] with
            {
                Title =
                    "Fresh cached title",
            };
        context.Records[0] = refreshed;

        await context.ViewModel.ReloadAsync();

        Assert.Equal(
            [refreshed.Path],
            context.ViewModel
                .SelectedPaths);
        Assert.Same(
            refreshed,
            Assert.Single(
                context.Inspector
                    .Selection
                    .Records!));
    }

    [Fact]
    public async Task Reload_clears_a_selection_that_disappeared_without_a_draft()
    {
        TestContext context = CreateLibrary();
        await context.ViewModel.ReloadAsync();
        Assert.True(
            await context.ViewModel.SelectAsync(
                [Assert.Single(
                    context.ViewModel.Rows)]));
        context.Records.Clear();

        await context.ViewModel.ReloadAsync();

        Assert.Empty(
            context.ViewModel
                .SelectedPaths);
        Assert.False(
            context.Inspector
                .Selection.HasSelection);
    }

    [Fact]
    public async Task Rejected_combined_navigation_confirmation_preserves_inspector_edits()
    {
        var inspectorDialogs =
            new RecordingDialogs(true);
        var libraryDialogs =
            new RecordingDialogs(false);
        TestContext context = CreateLibrary(
            inspectorDialogs:
                inspectorDialogs,
            libraryDialogs:
                libraryDialogs);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(
                context.ViewModel.Rows);
        Assert.True(
            await context.ViewModel.SelectAsync(
                [row]));
        EditableTagField artist =
            Assert.Single(
                context.Inspector.Fields,
                field =>
                    field.Field ==
                    TagFields.Artist);
        artist.Value =
            "Unsaved inspector artist";
        row.Title = "Unsaved inline title";

        bool allowed =
            await context.ViewModel
                .ConfirmCanNavigateAwayAsync();

        Assert.False(allowed);
        Assert.Equal(
            1,
            libraryDialogs
                .ConfirmationCount);
        Assert.Equal(
            0,
            inspectorDialogs
                .ConfirmationCount);
        Assert.True(
            context.Inspector
                .HasUnsavedChanges);
        Assert.Equal(
            "Unsaved inspector artist",
            artist.Value);
        Assert.True(row.HasChanges);
    }

    [Fact]
    public async Task Failed_automatic_preview_can_retry_without_altering_the_draft()
    {
        var operations =
            new FlakyPreviewMetadataOperationService();
        TestContext context = CreateLibrary(
            operations: operations);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(
                context.ViewModel.Rows);
        row.Title = "Retry title";
        await WaitForPreviewFailureAsync(
            context.ViewModel);

        Assert.True(
            context.ViewModel
                .HasDirectPendingPreviewFailure);
        Assert.True(
            context.ViewModel
                .CanRetryDirectPendingPreview);
        Assert.True(row.HasChanges);
        Assert.Equal(
            "Retry title",
            row.Title);

        context.ViewModel
            .RetryDirectPendingPreviewCommand
            .Execute(null);
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.Equal(
            2,
            operations.PreviewAttempts);
        Assert.False(
            context.ViewModel
                .HasDirectPendingPreviewFailure);
        Assert.False(
            context.ViewModel
                .CanRetryDirectPendingPreview);
        Assert.True(row.HasChanges);
        Assert.Equal(
            "Retry title",
            Assert.Single(
                context.ViewModel
                    .PendingChanges).After);
    }

    [Fact]
    public async Task Large_inspector_preview_publishes_pending_rows_with_atomic_resets()
    {
        TestContext context = CreateLibrary(
            recordCount: 250);
        await context.ViewModel.ReloadAsync();
        Assert.True(
            await context.ViewModel.SelectAsync(
                context.ViewModel.Rows));
        var actions = new List<
            NotifyCollectionChangedAction>();
        context.ViewModel.PendingChanges
            .CollectionChanged +=
            (_, args) =>
                actions.Add(args.Action);

        Assert.Single(
                context.Inspector.Fields,
                field =>
                    field.Field ==
                    TagFields.Title)
            .Value = "Shared title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.Equal(
            250,
            context.ViewModel
                .PendingChanges.Count);
        Assert.NotEmpty(actions);
        Assert.All(
            actions,
            action => Assert.Equal(
                NotifyCollectionChangedAction
                    .Reset,
                action));
    }

    private static TestContext CreateLibrary(
        PreparedImage? preparedArtwork = null,
        ILocalizationService? localization =
            null,
        FakeMetadataOperationService? operations =
            null,
        int recordCount = 1,
        IDialogCoordinator? inspectorDialogs =
            null,
        IDialogCoordinator? libraryDialogs =
            null)
    {
        const string path =
            @"C:\Music\Inline-edit.flac";
        var record = new TrackRecord
        {
            Path = path,
            Artist = "Original artist",
            AlbumArtist = "Original artist",
            Album = "Album",
            Title = "Original title",
            TrackTotal = 12,
            CodecName = "FLAC",
            CodecType = CodecType.Lossless,
            LastWriteTime =
                new DateTime(2026, 7, 26),
            Metadata =
                new Dictionary<string, string[]>
                {
                    [CachedMetadataKeys.Custom(
                        "DJ_SET")] =
                        ["Morning"],
                },
        };
        var records = Enumerable
            .Range(
                0,
                recordCount)
            .Select(index =>
                index == 0
                    ? record
                    : record with
                    {
                        Path =
                            $@"C:\Music\" +
                            $"Inline-edit-{index}" +
                            ".flac",
                    })
            .ToList();
        var library =
            new FakeLibrary(records);
        var settings =
            new FakeSettings();
        operations ??=
            new FakeMetadataOperationService();
        var reindex =
            new FakeReindex();
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(
                    [.. records.Select(item =>
                        new MediaFileModel
                        {
                            Path = item.Path,
                            Title = item.Title,
                            Artist = item.Artist,
                            IsWritable = true,
                            KnownFields =
                            [
                                new(
                                    TagFields.Title,
                                    item.Title ??
                                    ""),
                                new(
                                    TagFields.Artist,
                                    item.Artist ??
                                    ""),
                            ],
                        })]),
                library,
                new FakeTagWriter(),
                new FakeArtworkService(
                    preparedArtwork),
                new FakeFilePicker(
                    preparedArtwork is null
                        ? null
                        : @"C:\cover.jpg"),
                inspectorDialogs ??
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
                    new MetadataOperationCatalog(),
                dialogs:
                    libraryDialogs,
                localization:
                    localization),
            operations,
            reindex,
            library,
            inspector,
            records);
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

    private static async Task
        WaitForPreviewFailureAsync(
            LibraryViewModel viewModel)
    {
        for (int attempt = 0;
             attempt < 100 &&
             !viewModel
                 .HasDirectPendingPreviewFailure;
             attempt++)
            await Task.Delay(
                20,
                Xunit.TestContext.Current
                    .CancellationToken);
        Assert.True(
            viewModel
                .HasDirectPendingPreviewFailure);
    }

    private sealed record TestContext(
        LibraryViewModel ViewModel,
        FakeMetadataOperationService Operations,
        FakeReindex Reindex,
        FakeLibrary Library,
        SelectionInspectorViewModel Inspector,
        List<TrackRecord> Records);

    private sealed class
        KeyLocalizationService(
            string cultureName) :
        ILocalizationService
    {
        private readonly CultureInfo _culture =
            CultureInfo.GetCultureInfo(
                cultureName);

        public CultureInfo CurrentUICulture =>
            _culture;
        public IReadOnlyList<CultureInfo>
            SupportedCultures => [_culture];
        public event EventHandler?
            CultureChanged
        {
            add { }
            remove { }
        }

        public string Get(string key) =>
            $"{_culture.Name}:{key}";

        public string Format(
            string key,
            params object?[] arguments) =>
            arguments.Length == 0
                ? Get(key)
                : $"{Get(key)}(" +
                  string.Join(
                      ",",
                      arguments.Select(
                          argument =>
                              argument?.ToString() ??
                              "")) +
                  ")";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            Get(key);

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(
            string requestedCultureName)
        {
        }
    }

    private sealed class
        ThrowingMetadataOperationService :
        FakeMetadataOperationService
    {
        public override Task<MetadataApplyResult>
            ApplyAsync(
                MetadataOperationPlan plan,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Simulated apply failure.");
    }

    private sealed class
        FlakyPreviewMetadataOperationService :
        FakeMetadataOperationService,
        IMetadataOperationService
    {
        public int PreviewAttempts
            { get; private set; }

        Task<MetadataOperationPlan>
            IMetadataOperationService
                .PreviewValueEditsAsync(
                    IReadOnlyDictionary<
                        string,
                        IReadOnlyList<
                            MetadataValueEdit>>
                        editsByPath,
                    IReadOnlyDictionary<
                        string,
                        MetadataEditSourceExpectation>
                        sourceExpectations,
                    string name,
                    IProgress<
                        OperationProgress>?
                        progress,
                    CancellationToken ct)
        {
            PreviewAttempts++;
            if (PreviewAttempts == 1)
                throw new InvalidOperationException(
                    "Simulated preview failure.");
            return base.PreviewValueEditsAsync(
                editsByPath,
                sourceExpectations,
                name,
                progress,
                ct);
        }
    }

    private sealed class RecordingDialogs(
        bool confirmation) :
        IDialogCoordinator
    {
        public int ConfirmationCount
            { get; private set; }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            ConfirmationCount++;
            return Task.FromResult(
                confirmation);
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }
}
