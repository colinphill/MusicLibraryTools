using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
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
                        "Remove comment",
                        new RemoveFieldOperation(
                            MetadataFieldKey.Known(TagFields.Comment)),
                        Enabled: false),
                ]);

            store.Save(recipe);
            var restarted = new OperationRecipeStore(new AppSettings(statePath));

            OperationRecipe loaded = Assert.Single(restarted.Recipes);
            Assert.Equal("Cleanup", loaded.Name);
            Assert.Equal(["Trim title", "Remove comment"],
                loaded.Steps.Select(step => step.Name));
            Assert.False(loaded.Steps[1].Enabled);
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

            MetadataOperationPlan plan = await service.PreviewAsync([media.Path], recipe);

            MetadataFilePlan file = Assert.Single(plan.Files);
            MetadataFieldDifference difference = Assert.Single(file.Differences);
            Assert.Equal(TagFields.Title, difference.Field.KnownField);
            Assert.Equal(["TestTitle"], difference.Before);
            Assert.Equal(["A New Title"], difference.After);
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
}
