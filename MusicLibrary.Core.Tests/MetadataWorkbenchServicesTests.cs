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

    public static TheoryData<string> UnicodeWritableFixtures => new()
    {
        "sample.mp3",
        "sample.dsf",
        "sample.wav",
        "sample.aiff",
        "sample.aac",
        "sample.flac",
        "sample.ogg",
        "sample_alac.m4a",
        "sample_aac.m4a",
        "sample.wv",
        "sample.ape",
        "sample.mpc",
        "sample.tta",
        "sample.tak",
        "sample.ofr",
        "sample.ofs",
        "sample.off",
        "sample.wma",
        "sample.mka",
        "sample.webm",
    };

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

    [Theory]
    [MemberData(nameof(UnicodeWritableFixtures))]
    public async Task Workbench_RoundTripsUnicodePathAndMetadata(
        string fixture)
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-音楽-Δ-😀-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(
            session,
            "café-日本語-🎵" + Path.GetExtension(fixture));
        File.Copy(MediaFixtures.Path_(fixture), mediaPath);
        string statePath = Path.Combine(session, "設定.json");
        const string title = "Déjà vu — 日本語 — 🦊";
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);

            MetadataOperationPlan plan =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [mediaPath] =
                        [
                            new(
                                MetadataFieldKey.Known(TagFields.Title),
                                [title]),
                        ],
                    },
                    "Unicode round trip");

            MetadataFilePlan filePlan = Assert.Single(plan.Files);
            Assert.Equal(mediaPath, filePlan.Path);
            Assert.True(
                plan.CanApply,
                string.Join(
                    Environment.NewLine,
                    filePlan.Issues.Select(issue => issue.Message)));

            MetadataApplyResult result = await service.ApplyAsync(plan);

            Assert.Equal(1, result.ChangedFiles);
            MediaDocument reopened =
                await _documents.LoadAsync(mediaPath);
            Assert.Equal(mediaPath, reopened.Path);
            Assert.Equal(title, reopened.FirstValue(TagFields.Title));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
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
    public async Task Workbench_ScansLargeDirectoriesWithPeriodicProgress()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-workbench-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        const int fileCount = 2_048;
        try
        {
            for (int index = 0; index < fileCount; index++)
                File.Create(Path.Combine(session, $"{index:D5}.flac")).Dispose();
            var documents = new SyntheticDocuments();
            var service = new WorkbenchService(
                documents,
                MediaFormatRegistry.Default);
            var progress = new RecordingProgress();

            WorkbenchLoadResult result = await service.LoadAsync(
                new([session], Recursive: false),
                progress);

            Assert.Equal(fileCount, result.Documents.Length);
            Assert.Equal(fileCount, documents.LoadCount);
            Assert.Contains(progress.Items, item =>
                item.Phase == OperationPhase.Planning &&
                item.Completed == 256 &&
                item.Total is null);
            Assert.Contains(progress.Items, item =>
                item.Phase == OperationPhase.Planning &&
                item.Completed == fileCount &&
                item.Total == fileCount);
            OperationProgress completed = progress.Items[^1];
            Assert.Equal(OperationPhase.Completed, completed.Phase);
            Assert.Equal(fileCount, completed.Completed);
            Assert.Equal(fileCount, completed.Total);
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Workbench_CancelsDuringLargeDirectoryScan()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-workbench-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        try
        {
            for (int index = 0; index < 1_024; index++)
                File.Create(Path.Combine(session, $"{index:D5}.flac")).Dispose();
            using var cancellation = new CancellationTokenSource();
            var progress = new CallbackProgress(item =>
            {
                if (item.Phase == OperationPhase.Planning &&
                    item.Completed >= 256)
                    cancellation.Cancel();
            });
            var service = new WorkbenchService(
                new SyntheticDocuments(),
                MediaFormatRegistry.Default);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.LoadAsync(
                    new([session], Recursive: false),
                    progress,
                    cancellation.Token));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Workbench_ReportsOfflinePlaylistEntriesWithoutDroppingHealthyFiles()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-workbench-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string available = Path.Combine(session, "available.flac");
        string missing = Path.Combine(session, "offline.flac");
        string playlist = Path.Combine(session, "selection.m3u8");
        try
        {
            File.Create(available).Dispose();
            await File.WriteAllLinesAsync(
                playlist,
                ["available.flac", "offline.flac"]);
            var service = new WorkbenchService(
                new SyntheticDocuments(),
                MediaFormatRegistry.Default);

            WorkbenchLoadResult result = await service.LoadAsync(
                new([playlist], Recursive: false));

            Assert.Equal(
                [available],
                result.Documents.Select(document => document.Path));
            OperationIssue issue = Assert.Single(result.Issues);
            Assert.Equal("workbench.playlist-missing", issue.Code);
            Assert.Equal(missing, issue.Path);
            Assert.Equal(OperationIssueSeverity.Warning, issue.Severity);
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
    public async Task EveryTypedOperationHonorsEveryConditionFormAndNegation()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(),
            "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            MetadataFieldKey title =
                MetadataFieldKey.Known(TagFields.Title);
            MetadataFieldKey artist =
                MetadataFieldKey.Known(TagFields.Artist);
            MetadataFieldKey album =
                MetadataFieldKey.Known(TagFields.Album);
            MetadataFieldKey genre =
                MetadataFieldKey.Known(TagFields.Genre);
            MetadataFieldKey comment =
                MetadataFieldKey.Known(TagFields.Comment);
            MetadataFieldKey track =
                MetadataFieldKey.Known(TagFields.TrackNumber);
            MetadataFieldKey trackTotal =
                MetadataFieldKey.Known(TagFields.TotalTracks);
            MetadataFieldKey missing =
                MetadataFieldKey.Custom("MATRIX_MISSING");
            var info = new FileInfo(media.Path);
            var document = new MediaDocument(
                media.Path,
                [new(
                    "VorbisComment",
                    [
                        new(title, ["Zulu/One", "Alpha/Two"]),
                        new(artist, ["ConditionValue"]),
                        new(album, ["  padded value  "]),
                        new(genre, ["Rock", "rock"]),
                    ],
                    SupportsCustomFields: true,
                    IsWritable: true,
                    SupportsMultipleValues: true,
                    SupportsCustomMultipleValues: true)],
                [],
                null,
                new(
                    media.Path,
                    info.Length,
                    info.LastWriteTimeUtc,
                    "matrix"),
                true);
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                new StaticDocumentService(document),
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            var operations =
                new (string Name,
                    Func<MetadataCondition?, MetadataOperation> Create)[]
                {
                    ("assign", condition =>
                        new AssignFieldOperation(
                            comment, "Assigned", condition)),
                    ("remove", condition =>
                        new RemoveFieldOperation(album, condition)),
                    ("copy", condition =>
                        new CopyFieldOperation(
                            title, comment, When: condition)),
                    ("replace", condition =>
                        new ReplaceTextOperation(
                            album, "padded", "clean",
                            When: condition)),
                    ("case", condition =>
                        new ChangeCaseOperation(
                            album,
                            MetadataCaseMode.Upper,
                            condition)),
                    ("trim", condition =>
                        new TrimFieldOperation(
                            album,
                            NormalizeInternalWhitespace: true,
                            condition)),
                    ("sequence", condition =>
                        new SequenceNumberOperation(
                            track,
                            Start: 7,
                            TotalField: trackTotal,
                            When: condition)),
                    ("combine", condition =>
                        new CombineFieldsOperation(
                            title,
                            artist,
                            comment,
                            When: condition)),
                    ("split", condition =>
                        new SplitFieldOperation(
                            title, "/", When: condition)),
                    ("join", condition =>
                        new JoinFieldValuesOperation(
                            title, "; ", condition)),
                    ("deduplicate", condition =>
                        new DeduplicateFieldValuesOperation(
                            genre, When: condition)),
                    ("reorder", condition =>
                        new ReorderFieldValuesOperation(
                            title,
                            MetadataValueOrder.Ascending,
                            When: condition)),
                    ("extract-path", condition =>
                        new ExtractPathComponentOperation(
                            comment,
                            MetadataPathComponent.FileName,
                            When: condition)),
                };
            var conditions =
                new (string Name, MetadataCondition? Value, bool Matches)[]
                {
                    ("none", null, true),
                    ("always", new(Operator:
                        MetadataConditionOperator.Always), true),
                    ("not always", new(
                        Operator: MetadataConditionOperator.Always,
                        Negate: true), false),
                    ("present", new(
                        artist,
                        MetadataConditionOperator.Present), true),
                    ("present false", new(
                        missing,
                        MetadataConditionOperator.Present), false),
                    ("not present", new(
                        artist,
                        MetadataConditionOperator.Present,
                        Negate: true), false),
                    ("not present true", new(
                        missing,
                        MetadataConditionOperator.Present,
                        Negate: true), true),
                    ("missing", new(
                        missing,
                        MetadataConditionOperator.Missing), true),
                    ("missing false", new(
                        artist,
                        MetadataConditionOperator.Missing), false),
                    ("not missing", new(
                        missing,
                        MetadataConditionOperator.Missing,
                        Negate: true), false),
                    ("not missing true", new(
                        artist,
                        MetadataConditionOperator.Missing,
                        Negate: true), true),
                    ("equals", new(
                        artist,
                        MetadataConditionOperator.Equals,
                        "conditionvalue"), true),
                    ("equals false", new(
                        artist,
                        MetadataConditionOperator.Equals,
                        "different"), false),
                    ("not equals", new(
                        artist,
                        MetadataConditionOperator.Equals,
                        "conditionvalue",
                        Negate: true), false),
                    ("not equals true", new(
                        artist,
                        MetadataConditionOperator.Equals,
                        "different",
                        Negate: true), true),
                    ("contains", new(
                        artist,
                        MetadataConditionOperator.Contains,
                        "DITION"), true),
                    ("contains false", new(
                        artist,
                        MetadataConditionOperator.Contains,
                        "absent"), false),
                    ("not contains", new(
                        artist,
                        MetadataConditionOperator.Contains,
                        "DITION",
                        Negate: true), false),
                    ("not contains true", new(
                        artist,
                        MetadataConditionOperator.Contains,
                        "absent",
                        Negate: true), true),
                    ("regex", new(
                        artist,
                        MetadataConditionOperator.MatchesRegularExpression,
                        "^Condition"), true),
                    ("regex false", new(
                        artist,
                        MetadataConditionOperator.MatchesRegularExpression,
                        "^Absent"), false),
                    ("not regex", new(
                        artist,
                        MetadataConditionOperator.MatchesRegularExpression,
                        "^Condition",
                        Negate: true), false),
                    ("not regex true", new(
                        artist,
                        MetadataConditionOperator.MatchesRegularExpression,
                        "^Absent",
                        Negate: true), true),
                };

            foreach ((string operationName,
                         Func<MetadataCondition?, MetadataOperation> create)
                     in operations)
            foreach ((string conditionName,
                         MetadataCondition? condition,
                         bool matches)
                     in conditions)
            {
                MetadataOperationPlan plan = await service.PreviewAsync(
                    [media.Path],
                    OperationRecipe.Create(
                        $"{operationName}/{conditionName}",
                        create(condition)));
                MetadataFilePlan file = Assert.Single(plan.Files);
                Assert.True(
                    file.HasChanges == matches,
                    $"{operationName} with '{conditionName}' expected " +
                    $"HasChanges={matches}, got {file.HasChanges}. " +
                    string.Join("; ", file.Issues.Select(issue =>
                        $"{issue.Code}: {issue.Message}")));
                Assert.DoesNotContain(
                    file.Issues,
                    issue => issue.Code == "metadata.operation");
            }
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
    public async Task SequenceNumbering_PreviewsAndPersistsTrackDiscAndTotals()
    {
        string session = Path.Combine(
            Path.GetTempPath(), "mlm-sequence-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string firstPath = Path.Combine(session, "first.flac");
        string secondPath = Path.Combine(session, "second.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), firstPath);
        File.Copy(MediaFixtures.Path_("sample.flac"), secondPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Number tracks and discs",
                new SequenceNumberOperation(
                    MetadataFieldKey.Known(TagFields.TrackNumber),
                    Start: 3,
                    Step: 2,
                    PadWidth: 2,
                    TotalField:
                        MetadataFieldKey.Known(TagFields.TotalTracks)),
                new SequenceNumberOperation(
                    MetadataFieldKey.Known(TagFields.DiscNumber),
                    TotalField:
                        MetadataFieldKey.Known(TagFields.TotalDiscs)));

            MetadataOperationPlan plan =
                await service.PreviewAsync(
                    [firstPath, secondPath],
                    recipe);

            Assert.True(plan.CanApply);
            Assert.Equal(
                ["03", "05"],
                plan.Files.Select(file => Assert.Single(
                    file.Differences,
                    difference => difference.Field.KnownField ==
                        TagFields.TrackNumber).After.Single()));
            Assert.Equal(
                ["1", "2"],
                plan.Files.Select(file => Assert.Single(
                    file.Differences,
                    difference => difference.Field.KnownField ==
                        TagFields.DiscNumber).After.Single()));
            Assert.All(plan.Files, file =>
            {
                Assert.Equal(
                    ["2"],
                    Assert.Single(
                        file.Differences,
                        difference => difference.Field.KnownField ==
                            TagFields.TotalTracks).After);
                Assert.Equal(
                    ["2"],
                    Assert.Single(
                        file.Differences,
                        difference => difference.Field.KnownField ==
                            TagFields.TotalDiscs).After);
            });

            MetadataApplyResult result = await service.ApplyAsync(plan);

            Assert.Equal(2, result.ChangedFiles);
            Assert.Equal(
                ["03", "05"],
                new[] { firstPath, secondPath }.Select(path =>
                    MediaFile.GetFile(path).Tags.First()
                        .GetKnownMetadata()
                        .First(value =>
                            value.Key == TagFields.TrackNumber)
                        .Value));
            Assert.Equal(
                ["1", "2"],
                new[] { firstPath, secondPath }.Select(path =>
                    MediaFile.GetFile(path).Tags.First()
                        .GetKnownMetadata()
                        .First(value =>
                            value.Key == TagFields.DiscNumber)
                        .Value));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
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
    public async Task ValuePreview_DryRunsNativeWritersWithoutChangingFiles()
    {
        using var mp4 = MediaFixtures.Copy("sample_aac.m4a");
        using var flac = MediaFixtures.Copy("sample.flac");
        byte[] originalMp4 = await File.ReadAllBytesAsync(mp4.Path);
        byte[] originalFlac = await File.ReadAllBytesAsync(flac.Path);
        string statePath = Path.Combine(
            Path.GetTempPath(),
            "mlm-native-preview-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            TagFields unsupportedMp4 = Enum.GetValues<TagFields>()
                .First(field =>
                    field != TagFields.NullField &&
                    field is not TagFields.TrackNumber and
                        not TagFields.TotalTracks and
                        not TagFields.DiscNumber and
                        not TagFields.TotalDiscs &&
                    !MP4Util.ReverseTagMapping.ContainsKey(field));

            MetadataOperationPlan unsupported =
                await service.PreviewValueEditsAsync(
                    new Dictionary<string, IReadOnlyList<MetadataValueEdit>>
                    {
                        [mp4.Path] =
                        [
                            new(
                                MetadataFieldKey.Known(unsupportedMp4),
                                ["value"]),
                        ],
                        [flac.Path] =
                        [
                            new(
                                MetadataFieldKey.Custom("BAD=KEY"),
                                ["value"]),
                        ],
                    },
                    "Validate native fields");

            Assert.All(unsupported.Files, file =>
                Assert.Contains(file.Issues, issue =>
                    issue.Code == "metadata.native-unsupported" &&
                    issue.Severity == OperationIssueSeverity.Blocker));
            Assert.False(unsupported.CanApply);

            MetadataOperationPlan normalized =
                await service.PreviewValueEditsAsync(
                    new Dictionary<string, IReadOnlyList<MetadataValueEdit>>
                    {
                        [mp4.Path] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.TrackNumber),
                                ["003"]),
                        ],
                    },
                    "Preview numeric normalization");

            Assert.Contains(
                Assert.Single(normalized.Files).Issues,
                issue => issue.Code ==
                    "metadata.native-normalization" &&
                    issue.Severity == OperationIssueSeverity.Warning);
            Assert.True(normalized.CanApply);
            Assert.Equal(
                originalMp4,
                await File.ReadAllBytesAsync(mp4.Path));
            Assert.Equal(
                originalFlac,
                await File.ReadAllBytesAsync(flac.Path));
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
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
    public async Task Apply_RoundTripsOrderedApeV2KnownAndCustomValues()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-ape-values-" + Guid.NewGuid().ToString("N"));
        string recovery = session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath = Path.Combine(session, "track.ofr");
        File.Copy(MediaFixtures.Path_("sample.ofr"), mediaPath);
        string statePath = Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings);
            MetadataFieldKey custom =
                MetadataFieldKey.Custom("DJ_SET");

            MetadataOperationPlan plan =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [mediaPath] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Artist),
                                ["First artist", "Second artist"]),
                            new(custom, ["Warmup", "Peak"]),
                        ],
                    },
                    "Ordered APEv2 values");

            Assert.True(plan.CanApply);
            Assert.DoesNotContain(
                Assert.Single(plan.Files).Issues,
                issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);

            await service.ApplyAsync(plan);

            MediaDocument reloaded =
                await _documents.LoadAsync(mediaPath);
            Assert.Equal(
                ["First artist", "Second artist"],
                reloaded.Values(
                    MetadataFieldKey.Known(TagFields.Artist)));
            Assert.Equal(
                ["Warmup", "Peak"],
                reloaded.Values(custom));
        }
        finally
        {
            try { Directory.Delete(session, recursive: true); } catch { }
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Apply_RoundTripsId3v24ValuesAndBlocksLegacyVersions()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-id3-values-" + Guid.NewGuid().ToString("N"));
        string recovery =
            session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string v24Path = Path.Combine(session, "v24.mp3");
        string v23Path = Path.Combine(session, "v23.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), v24Path);
        File.Copy(MediaFixtures.Path_("sample.mp3"), v23Path);
        var v24 = Assert.IsType<MP3File>(
            MediaFile.GetFile(v24Path, readOnly: false));
        v24.ChangeVersion(ID3v2Version.V24);
        v24.SaveTags();
        string statePath =
            Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(
                    settings: settings),
                settings);
            IReadOnlyList<MetadataValueEdit> edits =
            [
                new(
                    MetadataFieldKey.Known(TagFields.Artist),
                    ["First artist", "Second artist"]),
                new(
                    MetadataFieldKey.Known(TagFields.Genre),
                    ["Rock", "Electronic"]),
            ];

            MetadataOperationPlan supported =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [v24Path] = edits,
                    },
                    "Ordered ID3v2.4 values");

            Assert.True(supported.CanApply);
            Assert.DoesNotContain(
                Assert.Single(supported.Files).Issues,
                issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
            await service.ApplyAsync(supported);

            MediaDocument reloaded =
                await _documents.LoadAsync(v24Path);
            Assert.Equal(
                ["First artist", "Second artist"],
                reloaded.Values(
                    MetadataFieldKey.Known(
                        TagFields.Artist)));
            Assert.Equal(
                ["Rock", "Electronic"],
                reloaded.Values(
                    MetadataFieldKey.Known(
                        TagFields.Genre)));

            MetadataOperationPlan legacy =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [v23Path] = edits,
                    },
                    "Unsupported legacy ID3 values");

            Assert.False(legacy.CanApply);
            Assert.Contains(
                Assert.Single(legacy.Files).Issues,
                issue =>
                    issue.Code ==
                        "metadata.native-unsupported" &&
                    issue.Severity ==
                        OperationIssueSeverity.Blocker);
        }
        finally
        {
            try
            {
                Directory.Delete(session, recursive: true);
            }
            catch
            {
            }
            try
            {
                Directory.Delete(recovery, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Apply_RoundTripsMp4AndAsfOrderedValues()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-mp4-asf-values-" +
            Guid.NewGuid().ToString("N"));
        string recovery =
            session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mp4Path = Path.Combine(session, "track.m4a");
        string asfPath = Path.Combine(session, "track.wma");
        File.Copy(
            MediaFixtures.Path_("sample_aac.m4a"),
            mp4Path);
        File.Copy(
            MediaFixtures.Path_("sample.wma"),
            asfPath);
        string statePath =
            Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(
                    settings: settings),
                settings);
            MetadataFieldKey custom =
                MetadataFieldKey.Custom("CUSTOM_ORDER");

            MetadataOperationPlan supported =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [mp4Path] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Artist),
                                ["First artist", "Second artist"]),
                            new(custom, ["first", "second"]),
                        ],
                        [asfPath] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Genre),
                                ["Rock", "Electronic"]),
                            new(custom, ["first", "second"]),
                        ],
                    },
                    "Ordered MP4 and ASF values");

            Assert.True(supported.CanApply);
            Assert.All(
                supported.Files,
                file => Assert.DoesNotContain(
                    file.Issues,
                    issue => issue.Severity ==
                        OperationIssueSeverity.Blocker));
            await service.ApplyAsync(supported);

            MediaDocument mp4 =
                await _documents.LoadAsync(mp4Path);
            Assert.Equal(
                ["First artist", "Second artist"],
                mp4.Values(
                    MetadataFieldKey.Known(
                        TagFields.Artist)));
            Assert.Equal(
                ["first", "second"],
                mp4.Values(custom));
            MediaDocument asf =
                await _documents.LoadAsync(asfPath);
            Assert.Equal(
                ["Rock", "Electronic"],
                asf.Values(
                    MetadataFieldKey.Known(
                        TagFields.Genre)));
            Assert.Equal(
                ["first", "second"],
                asf.Values(custom));

            MetadataOperationPlan unsupported =
                await service.PreviewValueEditsAsync(
                    new Dictionary<
                        string,
                        IReadOnlyList<MetadataValueEdit>>
                    {
                        [mp4Path] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.BPM),
                                ["120", "121"]),
                        ],
                        [asfPath] =
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Album),
                                ["First", "Second"]),
                        ],
                    },
                    "Unsupported native values");

            Assert.False(unsupported.CanApply);
            Assert.All(
                unsupported.Files,
                file => Assert.Contains(
                    file.Issues,
                    issue =>
                        issue.Code ==
                            "metadata.native-unsupported" &&
                        issue.Severity ==
                            OperationIssueSeverity.Blocker));
        }
        finally
        {
            try
            {
                Directory.Delete(session, recursive: true);
            }
            catch
            {
            }
            try
            {
                Directory.Delete(recovery, recursive: true);
            }
            catch
            {
            }
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
    public async Task ArtworkSetPreview_PreservesOrderRolesDescriptionsAndResize()
    {
        string session = Path.Combine(
            Path.GetTempPath(),
            "mlm-artwork-set-" +
            Guid.NewGuid().ToString("N"));
        string recovery =
            session + ".MusicLibraryManager-recovery";
        Directory.CreateDirectory(session);
        string mediaPath =
            Path.Combine(session, "track.flac");
        File.Copy(
            MediaFixtures.Path_("sample.flac"),
            mediaPath);
        string statePath =
            Path.Combine(session, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var service = new MetadataOperationService(
                _documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(
                    settings: settings),
                settings);
            var requests = new Dictionary<
                string,
                ArtworkSetPreviewRequest>
            {
                [mediaPath] = new(
                    [
                        new(
                            ID3v2Util.APICType.FrontCover,
                            "image/png",
                            CreatePngBytes(96, 72),
                            "front"),
                        new(
                            ID3v2Util.APICType.BackCover,
                            "image/png",
                            CreatePngBytes(80, 64),
                            "back"),
                    ],
                    MaxDimension: 48),
            };

            MetadataOperationPlan plan =
                await service.PreviewArtworkSetsAsync(
                    requests,
                    "Edit artwork set");

            MetadataFilePlan file = Assert.Single(plan.Files);
            ArtworkSetDifference difference =
                Assert.IsType<ArtworkSetDifference>(
                    file.ArtworkDifference);
            Assert.Equal(
                [
                    ID3v2Util.APICType.FrontCover,
                    ID3v2Util.APICType.BackCover,
                ],
                difference.After.Select(item => item.Type));
            Assert.Equal(
                ["front", "back"],
                file.ArtworkEdit!.Images.Select(
                    item => item.Description));
            Assert.All(
                file.ArtworkEdit.Images,
                item =>
                {
                    using Image image =
                        Image.Load(item.Data);
                    Assert.True(image.Width <= 48);
                    Assert.True(image.Height <= 48);
                });
            Assert.True(plan.CanApply);

            await service.ApplyAsync(plan);

            MediaDocument stored =
                await _documents.LoadAsync(mediaPath);
            Assert.Equal(2, stored.Artwork.Length);
            Assert.Contains(
                "Front",
                stored.Artwork[0].Category,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "Back",
                stored.Artwork[1].Category,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(session, recursive: true);
            }
            catch { }
            try
            {
                Directory.Delete(recovery, recursive: true);
            }
            catch { }
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

    private sealed class CallbackProgress(
        Action<OperationProgress> callback) :
        IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => callback(value);
    }

    private sealed class SyntheticDocuments : IMetadataDocumentService
    {
        public int LoadCount { get; private set; }

        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LoadCount++;
            string fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            return Task.FromResult(new MediaDocument(
                fullPath,
                [],
                [],
                null,
                new(
                    fullPath,
                    info.Length,
                    info.LastWriteTimeUtc,
                    ""),
                true));
        }
    }

    private sealed class StaticDocumentService(MediaDocument document) :
        IMetadataDocumentService
    {
        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(document);
        }
    }
}
