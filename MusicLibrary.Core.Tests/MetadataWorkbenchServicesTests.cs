using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MetadataWorkbenchServicesTests
{
    private readonly MetadataDocumentService _documents =
        new(MediaFormatRegistry.Default);

    [Fact]
    public void OperationCatalog_ExposesEveryBuiltInOperationToWorkbenchAndLibrary()
    {
        var catalog = new MetadataOperationCatalog();

        Assert.Equal(Enum.GetValues<MetadataOperationKind>().Length,
            catalog.Operations.Count);
        Assert.All(catalog.Operations, operation =>
        {
            Assert.True(operation.Supports(MetadataOperationSurface.Workbench));
            Assert.True(operation.Supports(MetadataOperationSurface.Library));
        });
    }

    [Fact]
    public void RecipeStore_PersistsOrderedNamedAndDisabledSteps()
    {
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-recipes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var store = new OperationRecipeStore(settings);
            OperationRecipe recipe = OperationRecipe.FromSteps(
                Guid.NewGuid(),
                "Cleanup",
                [
                    new(
                        Guid.NewGuid(),
                        "Trim title",
                        new TrimFieldOperation(
                            MetadataFieldKey.Known(TagFields.Title),
                            When: new MetadataCondition(
                                MetadataFieldKey.Known(TagFields.Title),
                                MetadataConditionOperator.Present))),
                    new(
                        Guid.NewGuid(),
                        "Split genre",
                        new SplitFieldOperation(
                            MetadataFieldKey.Known(TagFields.Genre), ";"),
                        Enabled: false),
                ]);

            store.Save(recipe);
            var restarted = new OperationRecipeStore(new AppSettings(statePath));

            OperationRecipe loaded = Assert.Single(restarted.Recipes);
            Assert.Equal("Cleanup", loaded.Name);
            Assert.Equal(["Trim title", "Split genre"],
                loaded.Steps.Select(step => step.Name));
            Assert.False(loaded.Steps[1].Enabled);
            Assert.IsType<SplitFieldOperation>(loaded.Steps[1].Operation);
            Assert.Single(loaded.EnabledOperations);
            Assert.Equal(MetadataConditionOperator.Present,
                loaded.Steps[0].Operation.Condition?.Operator);
            Assert.True(restarted.Delete(loaded.Id));
            Assert.Empty(new OperationRecipeStore(
                new AppSettings(statePath)).Recipes);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Preview_ReportsUnavailableLibraryCandidateWithoutAbortingScope()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string missing = Path.Combine(
            Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Library scope",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Title), "Updated"));

            MetadataOperationPlan plan =
                await service.PreviewAsync([media.Path, missing], recipe);

            Assert.Equal(2, plan.Files.Length);
            Assert.True(plan.Files[0].HasChanges);
            Assert.Contains(plan.Files[1].Issues, issue =>
                issue.Code == "metadata.unavailable" &&
                issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(plan.CanApply);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Document_PreservesKnownFieldsAndTechnicalProperties()
    {
        using var media = MediaFixtures.Copy("sample.flac");

        MediaDocument document = await _documents.LoadAsync(media.Path);

        Assert.Equal("TestTitle", document.FirstValue(TagFields.Title));
        Assert.Equal("TestArtist", document.FirstValue(TagFields.Artist));
        Assert.NotEmpty(document.TagLayers);
        Assert.NotNull(document.Codec);
        Assert.True(document.Snapshot.Length > 0);
        Assert.Equal(64, document.Snapshot.MetadataHash.Length);
        Assert.True(document.IsWritable);
    }

    [Fact]
    public async Task Document_ExposesDistinctRawAacId3AndApeLayers()
    {
        using var media = MediaFixtures.Copy("sample.aac");
        var aac = Assert.IsType<AACFile>(
            MediaFile.GetFile(media.Path, readOnly: false));
        aac.SetField(TagFields.Title, "ID3 layer");
        aac.SaveTags();
        var ape = new APETag();
        ape.SetField(TagFields.Title, "APE layer");
        await using (var stream = new FileStream(
                         media.Path, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(ape.ToByteArray());

        MediaDocument document = await _documents.LoadAsync(media.Path);

        Assert.Equal(["ID3v23", "APE"],
            document.TagLayers.Select(layer => layer.TagType));
        Assert.Equal("ID3 layer", document.FirstValue(TagFields.Title));
        Assert.Equal(ID3v2Version.V23, document.Id3Version);
        Assert.All(document.TagLayers, layer => Assert.True(layer.IsWritable));
        Assert.Contains(
            document.TagLayers[1].Fields,
            field => field.Field.KnownField == TagFields.Title &&
                     field.Values.SequenceEqual(["APE layer"]));
        Assert.Collection(
            document.EditableTagLayers.OrderBy(layer => layer.Kind),
            id3 =>
            {
                Assert.Equal(TagLayerKind.Id3v2, id3.Kind);
                Assert.True(id3.IsPresent);
                Assert.True(id3.IsPrimary);
                Assert.True(id3.CanRemove);
            },
            apeLayer =>
            {
                Assert.Equal(TagLayerKind.ApeV2, apeLayer.Kind);
                Assert.True(apeLayer.IsPresent);
                Assert.False(apeLayer.IsPrimary);
                Assert.True(apeLayer.CanRemove);
            });
    }

    [Fact]
    public async Task Document_ExposesWritableMonkeyAudioApeLayer()
    {
        using var media = MediaFixtures.Copy("sample.ape");

        MediaDocument document = await _documents.LoadAsync(media.Path);

        TagLayerDocument layer = Assert.Single(document.TagLayers);
        Assert.Equal("APE", layer.TagType);
        Assert.True(layer.SupportsCustomFields);
        Assert.True(layer.IsWritable);
        Assert.Equal("TestTitle", document.FirstValue(TagFields.Title));
        Assert.Equal("Monkey's Audio", document.Codec?.CodecName);
        Assert.True(document.IsWritable);
    }

    [Theory]
    [InlineData("sample.mpc", "Musepack")]
    [InlineData("sample.tta", "TTA")]
    [InlineData("sample.tak", "TAK")]
    [InlineData("sample.ofr", "OptimFROG")]
    [InlineData("sample.ofs", "OptimFROG DualStream")]
    [InlineData("sample.off", "OptimFROG Float")]
    public async Task Document_ExposesWritableAdditionalApeV2Layers(
        string fixture,
        string codecName)
    {
        using var media = MediaFixtures.Copy(fixture);

        MediaDocument document = await _documents.LoadAsync(media.Path);

        TagLayerDocument layer = Assert.Single(document.TagLayers);
        Assert.Equal("APE", layer.TagType);
        Assert.True(layer.SupportsCustomFields);
        Assert.True(layer.IsWritable);
        Assert.Equal("TestTitle", document.FirstValue(TagFields.Title));
        Assert.Equal(codecName, document.Codec?.CodecName);
        Assert.True(document.IsWritable);
    }

    [Fact]
    public async Task Document_ExposesWritableAsfLayer()
    {
        using var media = MediaFixtures.Copy("sample.wma");

        MediaDocument document = await _documents.LoadAsync(media.Path);

        TagLayerDocument layer = Assert.Single(document.TagLayers);
        Assert.Equal("ASF", layer.TagType);
        Assert.True(layer.SupportsCustomFields);
        Assert.True(layer.IsWritable);
        Assert.Equal("TestTitle", document.FirstValue(TagFields.Title));
        Assert.Equal(
            "Windows Media Audio 2",
            document.Codec?.CodecName);
        Assert.True(document.IsWritable);
    }

    [Fact]
    public async Task Document_ExposesMatroskaTagsArtworkAndChapters()
    {
        using var media = MediaFixtures.Copy("sample.mka");

        MediaDocument document = await _documents.LoadAsync(media.Path);

        TagLayerDocument layer = Assert.Single(document.TagLayers);
        Assert.Equal("Matroska Tags", layer.TagType);
        Assert.True(layer.SupportsCustomFields);
        Assert.True(layer.SupportsMultipleValues);
        Assert.True(layer.IsWritable);
        Assert.Equal("TestTitle", document.FirstValue(TagFields.Title));
        Assert.Equal("FLAC", document.Codec?.CodecName);
        Assert.Single(document.Artwork);
        Assert.Equal(
            ["Opening", "Closing"],
            document.Chapters.Select(chapter => chapter.Title));
        Assert.True(document.IsWritable);
    }

    [Fact]
    public async Task Workbench_LoadsFoldersAndPlaylistOrderWithoutDuplicates()
    {
        using var first = MediaFixtures.Copy("sample.flac");
        using var second = MediaFixtures.Copy("sample.mp3");
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string firstPath = Path.Combine(session, "first.flac");
        string secondPath = Path.Combine(session, "second.mp3");
        string playlist = Path.Combine(session, "selection.m3u8");
        try
        {
            File.Copy(first.Path, firstPath);
            File.Copy(second.Path, secondPath);
            await File.WriteAllLinesAsync(playlist, ["second.mp3", "first.flac", "second.mp3"]);
            var service = new WorkbenchService(_documents, MediaFormatRegistry.Default);

            WorkbenchLoadResult result = await service.LoadAsync(
                new([playlist], Recursive: false));

            Assert.Empty(result.Issues);
            Assert.Equal([secondPath, firstPath],
                result.Documents.Select(document => document.Path));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Preview_AppliesTypedOperationsInOrderWithoutWriting()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Clean title",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Title), "  a NEW title  "),
                new TrimFieldOperation(
                    MetadataFieldKey.Known(TagFields.Title), NormalizeInternalWhitespace: true),
                new ChangeCaseOperation(
                    MetadataFieldKey.Known(TagFields.Title), MetadataCaseMode.Title));
            var progress = new RecordingProgress();

            MetadataOperationPlan plan =
                await service.PreviewAsync([media.Path], recipe, progress);

            MetadataFilePlan file = Assert.Single(plan.Files);
            MetadataFieldDifference difference = Assert.Single(file.Differences);
            Assert.Equal(TagFields.Title, difference.Field.KnownField);
            Assert.Equal(["TestTitle"], difference.Before);
            Assert.Equal(["A New Title"], difference.After);
            Assert.True(plan.CanApply);
            Assert.Equal([0, 1], progress.Items.Select(item => item.Completed));
            Assert.Equal("TestTitle",
                MediaFile.GetFile(media.Path, readOnly: true).Tags.First().Title);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Preview_ShapesAndCombinesOrderedValuesWithoutWriting()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Shape values",
                new CombineFieldsOperation(
                    MetadataFieldKey.Known(TagFields.Artist),
                    MetadataFieldKey.Known(TagFields.Title),
                    MetadataFieldKey.Known(TagFields.Composer),
                    " — "),
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Genre), " Rock ;Pop;rock "),
                new SplitFieldOperation(
                    MetadataFieldKey.Known(TagFields.Genre), ";"),
                new TrimFieldOperation(
                    MetadataFieldKey.Known(TagFields.Genre)),
                new DeduplicateFieldValuesOperation(
                    MetadataFieldKey.Known(TagFields.Genre)),
                new ReorderFieldValuesOperation(
                    MetadataFieldKey.Known(TagFields.Genre)),
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Comment), "one|two"),
                new SplitFieldOperation(
                    MetadataFieldKey.Known(TagFields.Comment), "|"),
                new JoinFieldValuesOperation(
                    MetadataFieldKey.Known(TagFields.Comment), " / "));

            MetadataOperationPlan plan = await service.PreviewAsync([media.Path], recipe);

            MetadataFilePlan file = Assert.Single(plan.Files);
            Assert.Equal(
                ["TestArtist — TestTitle"],
                Assert.Single(file.Differences.Where(difference =>
                    difference.Field.KnownField == TagFields.Composer)).After);
            Assert.Equal(
                ["Pop", "Rock"],
                Assert.Single(file.Differences.Where(difference =>
                    difference.Field.KnownField == TagFields.Genre)).After);
            Assert.Equal(
                ["one / two"],
                Assert.Single(file.Differences.Where(difference =>
                    difference.Field.KnownField == TagFields.Comment)).After);
            Assert.True(plan.CanApply);
            Assert.Equal("TestTitle",
                MediaFile.GetFile(media.Path, readOnly: true).Tags.First().Title);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Preview_ExtractsMetadataFromFileAndFolderComponents()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string root = Path.Combine(
            Path.GetTempPath(), "mlm-path-extract-" + Guid.NewGuid().ToString("N"));
        string albumDirectory = Path.Combine(root, "Extracted Album");
        string path = Path.Combine(albumDirectory, "01 - Extracted Title.flac");
        string statePath = Path.Combine(root, "settings.json");
        try
        {
            Directory.CreateDirectory(albumDirectory);
            File.Copy(media.Path, path);
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Extract path",
                new ExtractPathComponentOperation(
                    MetadataFieldKey.Known(TagFields.Title),
                    Pattern: "^\\d+ - (?<title>.+)$",
                    CaptureGroup: "title"),
                new ExtractPathComponentOperation(
                    MetadataFieldKey.Known(TagFields.Album),
                    MetadataPathComponent.ParentFolder));

            MetadataOperationPlan plan = await service.PreviewAsync([path], recipe);

            MetadataFilePlan file = Assert.Single(plan.Files);
            Assert.Equal(
                ["Extracted Title"],
                Assert.Single(file.Differences.Where(difference =>
                    difference.Field.KnownField == TagFields.Title)).After);
            Assert.Equal(
                ["Extracted Album"],
                Assert.Single(file.Differences.Where(difference =>
                    difference.Field.KnownField == TagFields.Album)).After);
            Assert.True(plan.CanApply);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Preview_RejectsUnknownExtractionCaptureGroup()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Invalid extraction",
                new ExtractPathComponentOperation(
                    MetadataFieldKey.Known(TagFields.Title),
                    Pattern: "(.+)",
                    CaptureGroup: "missing"));

            MetadataOperationPlan plan = await service.PreviewAsync([media.Path], recipe);

            MetadataFilePlan file = Assert.Single(plan.Files);
            Assert.Contains(file.Issues, issue =>
                issue.Code == "metadata.operation" &&
                issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(plan.CanApply);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Preview_RejectsInvalidRegularExpressionAsAFileIssue()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Invalid expression",
                new ReplaceTextOperation(
                    MetadataFieldKey.Known(TagFields.Title),
                    "[",
                    "",
                    RegularExpression: true));

            MetadataOperationPlan plan = await service.PreviewAsync([media.Path], recipe);

            MetadataFilePlan file = Assert.Single(plan.Files);
            Assert.Contains(file.Issues, issue =>
                issue.Code == "metadata.operation" &&
                issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(plan.CanApply);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Apply_StagesReplacementAndPersistentUndoRestoresOriginal()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-apply-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var journals = new OperationJournalService();
            var history = new EditHistoryService(settings, journals);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                history: history);
            OperationRecipe recipe = OperationRecipe.Create(
                "Change title",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Title), "Workbench title"));

            MetadataOperationPlan plan = await service.PreviewAsync([mediaPath], recipe);
            MetadataApplyResult applied = await service.ApplyAsync(plan);

            Assert.Equal(1, applied.ChangedFiles);
            Assert.Single(applied.JournalPaths);
            Assert.True(File.Exists(applied.JournalPaths[0]));
            Assert.Equal("Workbench title",
                MediaFile.GetFile(mediaPath, readOnly: true).Tags.First().Title);
            Assert.True(history.CanUndo);

            var restartedHistory = new EditHistoryService(
                new AppSettings(statePath), journals);
            Assert.True(restartedHistory.CanUndo);
            int restored = await restartedHistory.UndoLatestAsync();

            Assert.Equal(1, restored);
            Assert.Equal("TestTitle",
                MediaFile.GetFile(mediaPath, readOnly: true).Tags.First().Title);
            Assert.False(restartedHistory.CanUndo);
            Assert.True(restartedHistory.CanRedo);
            var afterRestart = new EditHistoryService(
                new AppSettings(statePath), journals);
            Assert.True(afterRestart.CanRedo);
            Assert.Equal(recipe.Id,
                Assert.Single(afterRestart.RedoEntries).Recipe?.Id);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TagLayerPreview_AddsCopiedApeAndUndoRestoresExactFile()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-layers-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.aac");
        File.Copy(MediaFixtures.Path_("sample.aac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var seed = Assert.IsType<AACFile>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            seed.SetField(TagFields.Title, "Layer title");
            seed.SaveTags();
            byte[] original = await File.ReadAllBytesAsync(mediaPath);
            var settings = new AppSettings(statePath);
            var history = new EditHistoryService(
                settings, new OperationJournalService());
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                history: history);

            MetadataOperationPlan plan =
                await service.PreviewTagLayerEditsAsync(
                    new Dictionary<string, IReadOnlyList<TagLayerEdit>>
                    {
                        [mediaPath] =
                        [
                            new(
                                TagLayerKind.ApeV2,
                                TagLayerEditMode.Add,
                                TagLayerCopyMode.CopyPrimary),
                        ],
                    },
                    "Add APEv2 layer");

            MetadataFilePlan filePlan = Assert.Single(plan.Files);
            TagLayerDifference difference =
                Assert.Single(filePlan.TagLayerDifferences);
            Assert.Equal(TagLayerKind.ApeV2, difference.Kind);
            Assert.False(difference.WasPresent);
            Assert.True(difference.WillBePresent);
            Assert.True(plan.CanApply);
            Assert.Equal(original, await File.ReadAllBytesAsync(mediaPath));

            MetadataApplyResult result = await service.ApplyAsync(plan);

            Assert.Equal(1, result.ChangedFiles);
            var applied = Assert.IsType<AACFile>(
                MediaFile.GetFile(mediaPath));
            Assert.All(applied.EditableTagLayers, layer =>
                Assert.True(layer.IsPresent));
            Assert.Equal(
                ["Layer title", "Layer title"],
                applied.Tags.Select(tag => tag.Title));

            Assert.Equal(1, await history.UndoLatestAsync());
            Assert.Equal(original, await File.ReadAllBytesAsync(mediaPath));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TagLayerPreview_RemovesOnlyRequestedEnvelope()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-layer-remove-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.aac");
        File.Copy(MediaFixtures.Path_("sample.aac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var seed = Assert.IsType<AACFile>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            seed.SetField(TagFields.Title, "Keep ID3");
            seed.AddTagLayer(
                TagLayerKind.ApeV2, TagLayerCopyMode.CopyPrimary);
            seed.SaveTags();
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);

            MetadataOperationPlan plan =
                await service.PreviewTagLayerEditsAsync(
                    new Dictionary<string, IReadOnlyList<TagLayerEdit>>
                    {
                        [mediaPath] =
                        [
                            new(
                                TagLayerKind.ApeV2,
                                TagLayerEditMode.Remove),
                        ],
                    },
                    "Remove APEv2 layer");
            await service.ApplyAsync(plan);

            var applied = Assert.IsType<AACFile>(
                MediaFile.GetFile(mediaPath));
            IMetadataProvider remaining = Assert.Single(applied.Tags);
            Assert.Equal("ID3v23", remaining.TagType);
            Assert.Equal("Keep ID3", remaining.Title);
            Assert.False(Assert.Single(
                applied.EditableTagLayers,
                layer => layer.Kind == TagLayerKind.ApeV2).IsPresent);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TagLayerPreview_RejectsUnsupportedFormatAndStaleSource()
    {
        using var flac = MediaFixtures.Copy("sample.flac");
        using var aac = MediaFixtures.Copy("sample.aac");
        string statePath = Path.Combine(
            Path.GetTempPath(),
            "mlm-layer-stale-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            MetadataOperationPlan unsupported =
                await service.PreviewTagLayerEditsAsync(
                    new Dictionary<string, IReadOnlyList<TagLayerEdit>>
                    {
                        [flac.Path] =
                        [
                            new(
                                TagLayerKind.ApeV2,
                                TagLayerEditMode.Add),
                        ],
                    },
                    "Unsupported layer");
            Assert.Contains(
                Assert.Single(unsupported.Files).Issues,
                issue => issue.Code == "tag-layer.unsupported" &&
                    issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(unsupported.CanApply);

            MetadataOperationPlan stale =
                await service.PreviewTagLayerEditsAsync(
                    new Dictionary<string, IReadOnlyList<TagLayerEdit>>
                    {
                        [aac.Path] =
                        [
                            new(
                                TagLayerKind.ApeV2,
                                TagLayerEditMode.Add),
                        ],
                    },
                    "Stale layer plan");
            var changed = Assert.IsType<AACFile>(
                MediaFile.GetFile(aac.Path, readOnly: false));
            changed.SetField(TagFields.Title, "Changed after preview");
            changed.SaveTags();

            InvalidOperationException error = await Assert.ThrowsAsync<
                InvalidOperationException>(() => service.ApplyAsync(stale));
            Assert.Contains("Stale plan", error.Message);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Id3VersionPreview_AppliesThroughRecoveryAndUndo()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-id3-version-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var seed = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            seed.ChangeVersion(ID3v2Version.V22);
            seed.SaveTags();
            byte[] original = await File.ReadAllBytesAsync(mediaPath);
            var settings = new AppSettings(statePath);
            var history = new EditHistoryService(
                settings, new OperationJournalService());
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                history: history);

            MetadataOperationPlan plan =
                await service.PreviewId3VersionEditsAsync(
                    new Dictionary<string, Id3VersionEdit>
                    {
                        [mediaPath] = new(ID3v2Version.V24),
                    },
                    "Upgrade ID3");

            MetadataFilePlan filePlan = Assert.Single(plan.Files);
            Id3VersionDifference difference = Assert.IsType<
                Id3VersionDifference>(filePlan.Id3VersionDifference);
            Assert.Equal(ID3v2Version.V22, difference.SourceVersion);
            Assert.Equal(ID3v2Version.V24, difference.TargetVersion);
            Assert.True(difference.ConvertedFrameCount > 0);
            Assert.True(plan.CanApply);
            Assert.Equal(original, await File.ReadAllBytesAsync(mediaPath));

            await service.ApplyAsync(plan);

            var applied = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath));
            Assert.Equal(4, applied.Version);
            Assert.Equal("TestTitle", applied.Title);
            Assert.Equal(1, await history.UndoLatestAsync());
            Assert.Equal(original, await File.ReadAllBytesAsync(mediaPath));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Id3VersionPreview_BlocksLossUnlessDropIsExplicit()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-id3-loss-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var seed = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            seed.ChangeVersion(ID3v2Version.V24);
            seed.Frames.Add(new ID3v2Frame(seed)
            {
                FrameID = "SIGN",
                Data = [1, 2, 3],
            });
            seed.SaveTags();
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);

            MetadataOperationPlan strict =
                await service.PreviewId3VersionEditsAsync(
                    new Dictionary<string, Id3VersionEdit>
                    {
                        [mediaPath] = new(ID3v2Version.V23),
                    },
                    "Strict downgrade");

            Assert.False(strict.CanApply);
            Assert.Contains(
                Assert.Single(strict.Files).Issues,
                issue => issue.Code == "id3-version.lossy" &&
                    issue.Severity == OperationIssueSeverity.Blocker &&
                    issue.Message.Contains("SIGN"));

            MetadataOperationPlan permissive =
                await service.PreviewId3VersionEditsAsync(
                    new Dictionary<string, Id3VersionEdit>
                    {
                        [mediaPath] = new(
                            ID3v2Version.V23,
                            DropUnsupportedFrames: true),
                    },
                    "Lossy downgrade");

            Assert.True(permissive.CanApply);
            Assert.Contains(
                Assert.Single(permissive.Files).Issues,
                issue => issue.Code == "id3-version.lossy" &&
                    issue.Severity == OperationIssueSeverity.Warning);
            await service.ApplyAsync(permissive);
            var applied = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath));
            Assert.Equal(3, applied.Version);
            Assert.DoesNotContain(
                applied.Frames, frame => frame.FrameID == "SIGN");
            Assert.Equal("TestTitle", applied.Title);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Id3VersionPreview_RejectsNonId3Format()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(),
            "mlm-id3-unsupported-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);

            MetadataOperationPlan plan =
                await service.PreviewId3VersionEditsAsync(
                    new Dictionary<string, Id3VersionEdit>
                    {
                        [media.Path] = new(ID3v2Version.V24),
                    },
                    "Unsupported conversion");

            Assert.Contains(
                Assert.Single(plan.Files).Issues,
                issue => issue.Code == "id3-version.unsupported" &&
                    issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(plan.CanApply);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Id3v1Workbench_ConvertsEditsRemovesAndUndoes()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-id3v1-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var seed = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            seed.SetField(TagFields.Title, new string('T', 35));
            seed.Save();
            var settings = new AppSettings(statePath);
            var history = new EditHistoryService(
                settings, new OperationJournalService());
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                history: history);

            MetadataOperationPlan toV1 =
                await service.PreviewTagLayerConversionsAsync(
                    new Dictionary<string, TagLayerConversionEdit>
                    {
                        [mediaPath] = new(
                            TagLayerKind.Id3v2,
                            TagLayerKind.Id3v1),
                    },
                    "Create compatible ID3v1");

            Assert.True(toV1.CanApply);
            Assert.Contains(
                Assert.Single(toV1.Files).Issues,
                issue => issue.Severity == OperationIssueSeverity.Warning &&
                    issue.Message.Contains("Title"));
            await service.ApplyAsync(toV1);

            var withV1 = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath));
            Assert.Equal(2, withV1.Tags.Count());
            Assert.Equal(new string('T', 30), withV1.Tags.Last().Title);
            IMetadataWriter legacy = Assert.IsAssignableFrom<IMetadataWriter>(
                withV1.Tags.Last());
            legacy.SetField(TagFields.Title, "Legacy edited title");
            legacy.Save();

            MetadataOperationPlan toV2 =
                await service.PreviewTagLayerConversionsAsync(
                    new Dictionary<string, TagLayerConversionEdit>
                    {
                        [mediaPath] = new(
                            TagLayerKind.Id3v1,
                            TagLayerKind.Id3v2),
                    },
                    "Import ID3v1");
            await service.ApplyAsync(toV2);
            Assert.Equal(
                "Legacy edited title",
                MediaFile.GetFile(mediaPath).Tags.First().Title);

            MetadataOperationPlan encoding =
                await service.PreviewId3VersionEditsAsync(
                    new Dictionary<string, Id3VersionEdit>
                    {
                        [mediaPath] = new(
                            ID3v2Version.V23,
                            TextEncodingPolicy:
                                ID3TextEncodingPolicy.Utf16),
                    },
                    "Use UTF-16");
            Assert.True(encoding.CanApply);
            await service.ApplyAsync(encoding);
            var encoded = Assert.IsType<MP3File>(
                MediaFile.GetFile(mediaPath));
            TextFrame title = Assert.IsType<TextFrame>(
                encoded.Frames.Single(frame =>
                    frame.FrameID == "TIT2"));
            Assert.Equal(
                (byte)ID3v2Util.ID3Encoding.MarkedUnicode,
                title.Data[0]);

            byte[] beforeRemoval =
                await File.ReadAllBytesAsync(mediaPath);
            MetadataOperationPlan removal =
                await service.PreviewTagLayerEditsAsync(
                    new Dictionary<string, IReadOnlyList<TagLayerEdit>>
                    {
                        [mediaPath] =
                        [
                            new(
                                TagLayerKind.Id3v1,
                                TagLayerEditMode.Remove),
                        ],
                    },
                    "Remove ID3v1");
            await service.ApplyAsync(removal);
            Assert.Single(MediaFile.GetFile(mediaPath).Tags);

            Assert.Equal(1, await history.UndoLatestAsync());
            Assert.Equal(
                beforeRemoval,
                await File.ReadAllBytesAsync(mediaPath));
            Assert.Equal(
                "Legacy edited title",
                MediaFile.GetFile(mediaPath).Tags.Last().Title);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Apply_RoundTripsOrderedVorbisValuesWithoutJoiningThem()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-values-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var source = Assert.IsType<FLACFile>(
                MediaFile.GetFile(mediaPath, readOnly: false));
            Assert.IsAssignableFrom<IMultiValueMetadataWriter>(source)
                .SetFieldValues(TagFields.Artist, ["First artist", "Second artist"]);
            source.Save();

            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Copy artists",
                new CopyFieldOperation(
                    MetadataFieldKey.Known(TagFields.Artist),
                    MetadataFieldKey.Known(TagFields.Composer)));

            MetadataOperationPlan plan = await service.PreviewAsync([mediaPath], recipe);
            Assert.True(plan.CanApply);
            Assert.Equal(["First artist", "Second artist"],
                Assert.Single(plan.Files).Differences
                    .Single(change => change.Field.KnownField == TagFields.Composer)
                    .After);

            await service.ApplyAsync(plan);

            MediaDocument reloaded = await _documents.LoadAsync(mediaPath);
            Assert.Equal(["First artist", "Second artist"],
                reloaded.Values(MetadataFieldKey.Known(TagFields.Composer)));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ValueEditor_RoundTripsOrderedCustomValues()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-custom-values-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            MetadataFieldKey custom = MetadataFieldKey.Custom("DJ_SET");
            var edits = new Dictionary<string, IReadOnlyList<MetadataValueEdit>>
            {
                [mediaPath] =
                [
                    new(custom, ["Warmup", "Peak"]),
                ],
            };

            MetadataOperationPlan plan = await service.PreviewValueEditsAsync(
                edits, "Custom values");
            Assert.True(plan.CanApply);
            await service.ApplyAsync(plan);

            MediaDocument reloaded = await _documents.LoadAsync(mediaPath);
            Assert.Equal(["Warmup", "Peak"], reloaded.Values(custom));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkPreview_StagesApplyAndPersistentUndo()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-artwork-plan-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var journals = new OperationJournalService();
            var history = new EditHistoryService(settings, journals);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                history: history);
            byte[] originalFile = await File.ReadAllBytesAsync(mediaPath);
            byte[] imageBytes = CreatePngBytes(48, 48);
            var edits = new Dictionary<string, ArtworkValueEdit>
            {
                [mediaPath] = new(
                    ArtworkValueEditMode.ReplaceFrontCover,
                    new(
                        ID3v2Util.APICType.FrontCover,
                        "image/png",
                        imageBytes,
                        "Cover Art Archive")),
            };

            MetadataOperationPlan plan =
                await service.PreviewArtworkEditsAsync(
                    edits, "Import release artwork");

            MetadataFilePlan filePlan = Assert.Single(plan.Files);
            ArtworkSetDifference difference =
                Assert.IsType<ArtworkSetDifference>(filePlan.ArtworkDifference);
            Assert.Empty(difference.Before);
            ArtworkDescriptor after = Assert.Single(difference.After);
            Assert.Equal(ID3v2Util.APICType.FrontCover, after.Type);
            Assert.Equal("image/jpeg", after.MimeType);
            Assert.True(plan.CanApply);
            Assert.Equal(originalFile, await File.ReadAllBytesAsync(mediaPath));

            MetadataApplyResult result = await service.ApplyAsync(plan);

            Assert.Equal(1, result.ChangedFiles);
            MediaDocument applied = await _documents.LoadAsync(mediaPath);
            ArtworkModel cover = Assert.Single(applied.Artwork);
            Assert.Contains(
                "Front", cover.Category, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("image/jpeg", cover.ImageType);
            Assert.True(history.CanUndo);

            int restored = await history.UndoLatestAsync();

            Assert.Equal(1, restored);
            Assert.Equal(originalFile, await File.ReadAllBytesAsync(mediaPath));
            Assert.Empty((await _documents.LoadAsync(mediaPath)).Artwork);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkPreview_RemovesFrontCoverAndPreservesOtherRoles()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-artwork-remove-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            byte[] front = CreatePngBytes(48, 48);
            byte[] back = CreatePngBytes(40, 40);
            IMediaFile media = MediaFile.GetFile(mediaPath);
            IArtworkWriter writer = media as IArtworkWriter ??
                media.Tags.OfType<IArtworkWriter>().First();
            writer.SetImages(
            [
                new(
                    ID3v2Util.APICType.FrontCover,
                    "image/png",
                    "front",
                    front),
                new(
                    ID3v2Util.APICType.BackCover,
                    "image/png",
                    "back",
                    back),
            ]);
            media.SaveTags();
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            var edits = new Dictionary<string, ArtworkValueEdit>
            {
                [mediaPath] = new(
                    ArtworkValueEditMode.RemoveFrontCover),
            };

            MetadataOperationPlan plan =
                await service.PreviewArtworkEditsAsync(
                    edits, "Remove front cover");

            ArtworkSetDifference difference = Assert.IsType<ArtworkSetDifference>(
                Assert.Single(plan.Files).ArtworkDifference);
            Assert.Equal(2, difference.Before.Length);
            ArtworkDescriptor remaining = Assert.Single(difference.After);
            Assert.Equal(ID3v2Util.APICType.BackCover, remaining.Type);
            Assert.True(plan.CanApply);

            await service.ApplyAsync(plan);

            ArtworkModel stored = Assert.Single(
                (await _documents.LoadAsync(mediaPath)).Artwork);
            Assert.Contains(
                "Back", stored.Category, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkPreview_RequiresImageForReplacement()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(),
            "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);

            MetadataOperationPlan plan =
                await service.PreviewArtworkEditsAsync(
                    new Dictionary<string, ArtworkValueEdit>
                    {
                        [media.Path] = new(
                            ArtworkValueEditMode.ReplaceFrontCover),
                    },
                    "Invalid replacement");

            Assert.Contains(
                Assert.Single(plan.Files).Issues,
                issue => issue.Code == "artwork.image-required" &&
                    issue.Severity == OperationIssueSeverity.Blocker);
            Assert.False(plan.CanApply);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var image = new Image<Rgba32>(
            width, height, new Rgba32(24, 96, 192));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
