using System.Security.Cryptography;
using System.Text.Json;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ReviewedChangeBatchServiceTests
{
    [Fact]
    public async Task CopyExecutorEmitsReversibleCreatedRecordAndUndoRemovesOutput()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        string stage = temp.File("stage.flac", "encoded audio");
        string output = Path.Combine(library, "output.flac");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory("library.MusicLibraryManager-recovery", "copy"),
            stage,
            output);
        var coordinator = new FileMutationCoordinator();
        FileMutationSummary applied = await new FileMutationPlanExecutor(
            coordinator).ApplyAsync(
                mutation,
                ct: TestContext.Current.CancellationToken);
        string journalText = await File.ReadAllTextAsync(
            applied.JournalPath!,
            TestContext.Current.CancellationToken);
        Assert.Contains("CREATE_REVERSIBLE\t1\t", journalText);

        var journals = new OperationJournalService(coordinator);
        var run = new OperationJournalSummary(
            "MusicLibraryManager",
            OperationJournalKind.Other,
            OperationJournalState.Completed,
            mutation.RecoveryRoot,
            applied.JournalPath,
            DateTimeOffset.UtcNow,
            1);
        OperationFileEntry entry = Assert.Single(
            (await journals.BrowseAsync(
                run,
                TestContext.Current.CancellationToken)).Entries);
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run,
            [entry],
            TestContext.Current.CancellationToken);
        await journals.ApplyRestoreAsync(
            restore,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(OperationEntryKind.Created, entry.Kind);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task BatchCommitsParticipantsInDeterministicOrderAndCanUndoTogether()
    {
        using var temp = new TempDirectory();
        string firstRoot = temp.Directory("a");
        string secondRoot = temp.Directory("b");
        FileMutationPlan first = Plan(
            firstRoot,
            temp.Directory("a.MusicLibraryManager-recovery", "one"),
            temp.File("one.stage", "one"),
            Path.Combine(firstRoot, "one.flac"));
        FileMutationPlan second = Plan(
            secondRoot,
            temp.Directory("b.MusicLibraryManager-recovery", "two"),
            temp.File("two.stage", "two"),
            Path.Combine(secondRoot, "two.flac"));
        var coordinator = new FileMutationCoordinator();
        var journals = new OperationJournalService(coordinator);
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals);
        ReviewedChangeBatchPlan plan = service.CreatePlan([second, first]);

        ReviewedChangeBatchResult result = await service.ApplyAsync(
            plan,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(first.DestinationRoot, plan.Participants[0].DestinationRoot);
        Assert.Equal(2, result.JournalPaths.Length);
        Assert.True(File.Exists(first.Actions[0].DestinationPath));
        Assert.True(File.Exists(second.Actions[0].DestinationPath));
        Assert.Contains(
            "COMMIT\t",
            (await File.ReadAllTextAsync(
                result.CoordinatorManifestPath,
                TestContext.Current.CancellationToken)));

        var restorePlans = new List<OperationRestorePlan>();
        foreach (string journal in result.JournalPaths)
        {
            var run = new OperationJournalSummary(
                "MusicLibraryManager",
                OperationJournalKind.Other,
                OperationJournalState.Completed,
                Path.GetDirectoryName(journal)!,
                journal,
                DateTimeOffset.UtcNow,
                1);
            OperationBrowseResult browse = await journals.BrowseAsync(
                run,
                TestContext.Current.CancellationToken);
            restorePlans.Add(await journals.PreviewRestoreAsync(
                run,
                browse.Entries,
                TestContext.Current.CancellationToken));
        }
        await journals.ApplyRestoreBatchAsync(
            await journals.PreviewRestoreBatchAsync(
                restorePlans,
                TestContext.Current.CancellationToken),
            ct: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(first.Actions[0].DestinationPath));
        Assert.False(File.Exists(second.Actions[0].DestinationPath));
    }

    [Fact]
    public async Task LaterParticipantFailureRollsBackEarlierCommittedParticipant()
    {
        using var temp = new TempDirectory();
        string firstRoot = temp.Directory("a");
        string secondRoot = temp.Directory("b");
        FileMutationPlan first = Plan(
            firstRoot,
            temp.Directory("a.MusicLibraryManager-recovery", "one"),
            temp.File("one.stage", "one"),
            Path.Combine(firstRoot, "one.flac"));
        FileMutationPlan second = Plan(
            secondRoot,
            temp.Directory("b.MusicLibraryManager-recovery", "two"),
            temp.File("two.stage", "two"),
            Path.Combine(secondRoot, "two.flac"));
        var coordinator = new FileMutationCoordinator();
        var executor = new FailOnSecondExecutor(
            new FileMutationPlanExecutor(coordinator));
        var service = new ReviewedChangeBatchService(
            executor,
            new OperationJournalService(coordinator));
        ReviewedChangeBatchPlan plan = service.CreatePlan([first, second]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(
                plan,
                ct: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(first.Actions[0].DestinationPath));
        Assert.False(File.Exists(second.Actions[0].DestinationPath));
        Assert.Contains(
            "ROLLED_BACK\t",
            await File.ReadAllTextAsync(
            plan.CoordinatorManifestPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartupReconciliationRollsBackParticipantCommittedBeforeCoordinatorDecision()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory(
                "library.MusicLibraryManager-recovery",
                "interrupted"),
            temp.File("interrupted.stage", "encoded"),
            Path.Combine(library, "interrupted.flac"));
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var journals =
            new OperationJournalService(coordinator);
        FileMutationSummary participant =
            await new FileMutationPlanExecutor(coordinator)
                .ApplyAsync(
                    mutation,
                    ct: TestContext.Current
                        .CancellationToken);
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals,
            settings);
        ReviewedChangeBatchPlan plan =
            service.CreatePlan([mutation]);
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                plan.CoordinatorManifestPath)!);
        await File.WriteAllLinesAsync(
            plan.CoordinatorManifestPath,
            [
                $"BEGIN\t2\t{plan.Id:N}\t0",
                $"PARTICIPANT\t0\t{participant.JournalPath}",
                $"APPLIED\t0\t{participant.JournalPath}",
            ],
            TestContext.Current.CancellationToken);
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    plan.CoordinatorManifestPath,
                }));

        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(1, reconciled.RolledBack);
        Assert.Empty(reconciled.BlockedManifests);
        Assert.False(
            File.Exists(
                mutation.Actions[0].DestinationPath));
        Assert.Null(
            settings.GetPreference(
                ReviewedChangeBatchService
                    .PendingManifestPreference));
    }

    [Fact]
    public async Task StartupReconciliationRetainsBlockedManifestWhenParticipantVolumeIsUnavailable()
    {
        using var temp = new TempDirectory();
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            new OperationJournalService(coordinator),
            settings);
        string manifest = temp.File(
            "reviewed-change-v2-unavailable.tsv",
            string.Join(
                Environment.NewLine,
                "BEGIN\t2\t00000000000000000000000000000001\t0",
                "PARTICIPANT\t0\tZ:\\unavailable\\journal.tsv",
                "APPLIED\t0\tZ:\\unavailable\\journal.tsv"));
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    manifest,
                    manifest,
                }));

        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(0, reconciled.RolledBack);
        Assert.Equal(0, reconciled.Committed);
        Assert.Equal(
            manifest,
            Assert.Single(
                reconciled.BlockedManifests));
        Assert.Contains(
            manifest,
            JsonSerializer.Deserialize<string[]>(
                settings.GetPreference(
                    ReviewedChangeBatchService
                        .PendingManifestPreference)!)!);
    }

    [Fact]
    public async Task StartupReconciliationRetainsMissingCoordinatorManifestPointerAsBlocked()
    {
        using var temp = new TempDirectory();
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            new OperationJournalService(coordinator),
            settings);
        string manifest = Path.Combine(
            temp.Path,
            "offline-volume",
            "reviewed-change-v2-offline.tsv");
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    manifest,
                }));

        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(0, reconciled.RolledBack);
        Assert.Equal(0, reconciled.Committed);
        Assert.Equal(
            manifest,
            Assert.Single(
                reconciled.BlockedManifests));
        Assert.Contains(
            manifest,
            JsonSerializer.Deserialize<string[]>(
                settings.GetPreference(
                    ReviewedChangeBatchService
                        .PendingManifestPreference)!)!);
    }

    [Fact]
    public async Task ApplyRefusesNewBatchWhilePendingCoordinatorManifestIsUnavailable()
    {
        using var temp = new TempDirectory();
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            new OperationJournalService(coordinator),
            settings);
        string blockedManifest = Path.Combine(
            temp.Path,
            "offline-volume",
            "reviewed-change-v2-offline.tsv");
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    blockedManifest,
                }));
        string library = temp.Directory("new-library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory(
                "new-library.MusicLibraryManager-recovery",
                "new"),
            temp.File("new.stage", "new output"),
            Path.Combine(library, "new.flac"));
        ReviewedChangeBatchPlan plan =
            service.CreatePlan([mutation]);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    plan,
                    ct: TestContext.Current
                        .CancellationToken));

        Assert.Contains(
            "previous reviewed change",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(
                mutation.Actions[0].DestinationPath));
        Assert.False(
            File.Exists(
                plan.CoordinatorManifestPath));
        Assert.Contains(
            blockedManifest,
            JsonSerializer.Deserialize<string[]>(
                settings.GetPreference(
                    ReviewedChangeBatchService
                        .PendingManifestPreference)!)!);
    }

    [Fact]
    public async Task ReconciliationDoesNotRestoreTwoOfThreeCommittedParticipants()
    {
        using var temp = new TempDirectory();
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var executor =
            new FileMutationPlanExecutor(coordinator);
        var journals =
            new OperationJournalService(coordinator);
        var service = new ReviewedChangeBatchService(
            executor,
            journals,
            settings);
        FileMutationPlan[] participants =
        [
            Plan(
                temp.Directory("a"),
                temp.Directory(
                    "a.MusicLibraryManager-recovery",
                    "one"),
                temp.File("one.stage", "one"),
                Path.Combine(temp.Path, "a", "one.flac")),
            Plan(
                temp.Directory("b"),
                temp.Directory(
                    "b.MusicLibraryManager-recovery",
                    "two"),
                temp.File("two.stage", "two"),
                Path.Combine(temp.Path, "b", "two.flac")),
            Plan(
                temp.Directory("c"),
                temp.Directory(
                    "c.MusicLibraryManager-recovery",
                    "three"),
                temp.File("three.stage", "three"),
                Path.Combine(temp.Path, "c", "three.flac")),
        ];
        var applied = new List<FileMutationSummary>();
        foreach (FileMutationPlan participant in
                 participants)
        {
            applied.Add(
                await executor.ApplyAsync(
                    participant,
                    ct: TestContext.Current
                        .CancellationToken));
        }

        string unavailableJournal =
            applied[0].JournalPath!;
        string[] retainedLines =
        [
            .. (await File.ReadAllLinesAsync(
                    unavailableJournal,
                    TestContext.Current
                        .CancellationToken))
                .Where(line =>
                    !line.StartsWith(
                        "CREATE_REVERSIBLE\t",
                        StringComparison.Ordinal)),
        ];
        await File.WriteAllLinesAsync(
            unavailableJournal,
            retainedLines,
            TestContext.Current.CancellationToken);
        ReviewedChangeBatchPlan plan =
            service.CreatePlan(participants);
        await File.WriteAllLinesAsync(
            plan.CoordinatorManifestPath,
            [
                $"BEGIN\t2\t{plan.Id:N}\t0",
                .. applied.Select(
                    (result, index) =>
                        $"PARTICIPANT\t{index}\t{result.JournalPath}"),
                .. applied.Select(
                    (result, index) =>
                        $"APPLIED\t{index}\t{result.JournalPath}"),
            ],
            TestContext.Current.CancellationToken);
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    plan.CoordinatorManifestPath,
                }));

        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(0, reconciled.RolledBack);
        Assert.Equal(
            plan.CoordinatorManifestPath,
            Assert.Single(
                reconciled.BlockedManifests));
        Assert.All(
            participants,
            participant => Assert.True(
                File.Exists(
                    participant.Actions[0]
                        .DestinationPath)));
        Assert.Contains(
            plan.CoordinatorManifestPath,
            JsonSerializer.Deserialize<string[]>(
                settings.GetPreference(
                    ReviewedChangeBatchService
                        .PendingManifestPreference)!)!);
    }

    [Fact]
    public async Task StartupReconciliationKeepsCommittedDecisionAndOnlyClearsPendingMarker()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory(
                "library.MusicLibraryManager-recovery",
                "committed"),
            temp.File("committed.stage", "encoded"),
            Path.Combine(library, "committed.flac"));
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        FileMutationSummary participant =
            await new FileMutationPlanExecutor(coordinator)
                .ApplyAsync(
                    mutation,
                    ct: TestContext.Current
                        .CancellationToken);
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            new OperationJournalService(coordinator),
            settings);
        ReviewedChangeBatchPlan plan =
            service.CreatePlan([mutation]);
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                plan.CoordinatorManifestPath)!);
        await File.WriteAllLinesAsync(
            plan.CoordinatorManifestPath,
            [
                $"BEGIN\t2\t{plan.Id:N}\t0",
                $"PARTICIPANT\t0\t{participant.JournalPath}",
                $"APPLIED\t0\t{participant.JournalPath}",
                $"COMMIT\t{plan.Id:N}",
            ],
            TestContext.Current.CancellationToken);
        settings.SetPreference(
            ReviewedChangeBatchService
                .PendingManifestPreference,
            JsonSerializer.Serialize(
                new[]
                {
                    plan.CoordinatorManifestPath,
                }));

        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled.Examined);
        Assert.Equal(1, reconciled.Committed);
        Assert.True(
            File.Exists(
                mutation.Actions[0].DestinationPath));
        Assert.Null(
            settings.GetPreference(
                ReviewedChangeBatchService
                    .PendingManifestPreference));
    }

    [Fact]
    public async Task PostCommitSettingsAndProgressFailuresCannotRollBackDecidedBatch()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory(
                "library.MusicLibraryManager-recovery",
                "post-commit"),
            temp.File("post-commit.stage", "encoded"),
            Path.Combine(library, "post-commit.flac"));
        var settings = new MemorySettings
        {
            ThrowWhenClearingPendingManifest = true,
        };
        var coordinator = new FileMutationCoordinator();
        var service = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            new OperationJournalService(coordinator),
            settings);

        ReviewedChangeBatchResult result =
            await service.ApplyAsync(
                service.CreatePlan([mutation]),
                new ThrowOnCompletedProgress(),
                TestContext.Current.CancellationToken);

        Assert.True(
            File.Exists(
                mutation.Actions[0].DestinationPath));
        Assert.Contains(
            "COMMIT\t",
            await File.ReadAllTextAsync(
                result.CoordinatorManifestPath,
                TestContext.Current.CancellationToken));
        Assert.NotNull(
            settings.GetPreference(
                ReviewedChangeBatchService
                    .PendingManifestPreference));

        settings.ThrowWhenClearingPendingManifest = false;
        ReviewedChangeReconciliationResult reconciled =
            await service.ReconcilePendingAsync(
                TestContext.Current.CancellationToken);
        Assert.Equal(1, reconciled.Committed);
        Assert.True(
            File.Exists(
                mutation.Actions[0].DestinationPath));
    }

    [Fact]
    public async Task ReviewedHistoryUndoesCreatedOutputsAsOneBatchAndRetainsSemanticRedo()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory("library.MusicLibraryManager-recovery", "history"),
            temp.File("history.stage", "encoded"),
            Path.Combine(library, "history.flac"));
        var coordinator = new FileMutationCoordinator();
        var journals = new OperationJournalService(coordinator);
        var batchService = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals);
        ReviewedChangeBatchResult applied = await batchService.ApplyAsync(
            batchService.CreatePlan([mutation]),
            ct: TestContext.Current.CancellationToken);
        AudioTranscodeRequest redo = RedoRequest(mutation.Actions[0].SourcePath);
        AudioTranscodeRequest secondRedo = redo with
        {
            Settings = redo.Settings with
            {
                FormatId = AudioTranscodeFormatIds.Mp3,
                RateMode = AudioTranscodeRateMode.VariableQuality,
                Quality = 2,
            },
        };
        var settings = new MemorySettings();
        var history = new ReviewedChangeHistoryService(settings, journals);
        history.Record(new(
            Guid.NewGuid(),
            ReviewedChangeKindIds.AudioTranscode,
            DateTimeOffset.UtcNow,
            applied.JournalPaths,
            [mutation.Actions[0].SourcePath],
            [mutation.Actions[0].DestinationPath],
            applied.CoordinatorManifestPath,
            redo,
            [redo, secondRedo]));

        ReviewedChangeUndoResult undone = await history.UndoLatestAsync(
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, undone.RestoredFiles);
        Assert.False(File.Exists(mutation.Actions[0].DestinationPath));
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        AudioTranscodeRequest storedRedo =
            Assert.Single(history.RedoEntries).RedoRequest;
        Assert.Equal(redo.Settings, storedRedo.Settings);
        Assert.Equal(redo.Destination, storedRedo.Destination);
        Assert.Equal(redo.SourcePaths.ToArray(), storedRedo.SourcePaths.ToArray());
        AssertRedoRequests(
            Assert.Single(history.RedoEntries)
                .EffectiveRedoRequests,
            redo,
            secondRedo);
        var restarted = new ReviewedChangeHistoryService(settings, journals);
        Assert.True(restarted.CanRedo);
        AudioTranscodeRequest restartedRedo =
            Assert.Single(restarted.RedoEntries).RedoRequest;
        Assert.Equal(redo.Settings, restartedRedo.Settings);
        Assert.Equal(redo.Destination, restartedRedo.Destination);
        Assert.Equal(redo.SourcePaths.ToArray(), restartedRedo.SourcePaths.ToArray());
        AssertRedoRequests(
            Assert.Single(restarted.RedoEntries)
                .EffectiveRedoRequests,
            redo,
            secondRedo);
    }

    [Fact]
    public async Task DurableHistorySpoolSurvivesSettingsFailureAfterCommit()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory(
                "library.MusicLibraryManager-recovery",
                "history-spool"),
            temp.File("history-spool.stage", "encoded"),
            Path.Combine(library, "history-spool.flac"));
        var coordinator = new FileMutationCoordinator();
        var journals =
            new OperationJournalService(coordinator);
        var batch = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals);
        ReviewedChangeBatchResult applied =
            await batch.ApplyAsync(
                batch.CreatePlan([mutation]),
                ct: TestContext.Current
                    .CancellationToken);
        string spool =
            temp.Directory("durable-history");
        var failingSettings = new MemorySettings
        {
            ThrowWhenSettingReviewedHistory = true,
        };
        var history = new ReviewedChangeHistoryService(
            failingSettings,
            journals,
            durableDirectory: spool);
        AudioTranscodeRequest redo =
            RedoRequest(
                mutation.Actions[0].SourcePath);

        history.Record(new(
            Guid.NewGuid(),
            ReviewedChangeKindIds.AudioTranscode,
            DateTimeOffset.UtcNow,
            applied.JournalPaths,
            [mutation.Actions[0].SourcePath],
            [mutation.Actions[0].DestinationPath],
            applied.CoordinatorManifestPath,
            redo));

        Assert.True(history.CanUndo);
        Assert.Single(
            Directory.EnumerateFiles(
                spool,
                "*.json"));

        var restarted = new ReviewedChangeHistoryService(
            new MemorySettings(),
            journals,
            durableDirectory: spool);
        Assert.True(restarted.CanUndo);
        Assert.Empty(
            Directory.EnumerateFiles(
                spool,
                "*.json"));
    }

    [Fact]
    public async Task ReviewedHistoryRefusesWholeUndoWhenCreatedOutputChanged()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        FileMutationPlan mutation = Plan(
            library,
            temp.Directory("library.MusicLibraryManager-recovery", "stale"),
            temp.File("stale.stage", "encoded"),
            Path.Combine(library, "stale.flac"));
        var coordinator = new FileMutationCoordinator();
        var journals = new OperationJournalService(coordinator);
        var batchService = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals);
        ReviewedChangeBatchResult applied = await batchService.ApplyAsync(
            batchService.CreatePlan([mutation]),
            ct: TestContext.Current.CancellationToken);
        var history = new ReviewedChangeHistoryService(
            new MemorySettings(),
            journals);
        history.Record(new(
            Guid.NewGuid(),
            ReviewedChangeKindIds.AudioTranscode,
            DateTimeOffset.UtcNow,
            applied.JournalPaths,
            [mutation.Actions[0].SourcePath],
            [mutation.Actions[0].DestinationPath],
            applied.CoordinatorManifestPath,
            RedoRequest(mutation.Actions[0].SourcePath)));
        File.WriteAllText(
            mutation.Actions[0].DestinationPath,
            "externally changed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => history.UndoLatestAsync(
                ct: TestContext.Current.CancellationToken));

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(
            "externally changed",
            File.ReadAllText(mutation.Actions[0].DestinationPath));
    }

    [Fact]
    public async Task ReviewedTranscodeBatchCommitsMetadataAndOutputAsOneUndoUnit()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        string source = temp.File(
            Path.Combine("library", "source.flac"),
            "original");
        string metadataStage = temp.File(
            "metadata-stage.flac",
            "metadata-updated");
        string outputStage = temp.File(
            "output-stage.flac",
            "encoded");
        string output = Path.Combine(
            library,
            "output.flac");
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var journals =
            new OperationJournalService(coordinator);
        var batch = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals,
            settings);
        var reindex = new RecordingReindex(source);
        var history =
            new ReviewedChangeHistoryService(
                settings,
                journals,
                reindex);
        var service = new AudioTranscodeService(
            settings,
            new UnusedCapabilityService(),
            new AudioTranscodeAdapter(
                settings,
                new ManagedProcessRunner()),
            new TranscodeMetadataProjectionService(),
            new TranscodeWorkScheduler(
                settings,
                processorCount: 2),
            batch,
            history,
            new UnusedDecodedVerifier(),
            reindex: reindex);
        AudioTranscodeRequest request =
            RedoRequest(source);
        var item = new AudioTranscodePlanItem(
            Guid.NewGuid(),
            source,
            output,
            Snapshot(source),
            OperationPathSnapshot.Missing(output),
            Hash(source),
            request.Settings,
            []);
        var plan = new AudioTranscodePlan(
            Guid.NewGuid(),
            request,
            [item],
            [],
            DateTimeOffset.UtcNow,
            1);
        var staged = new AudioTranscodeStageResult(
            plan,
            [
                new(
                    item,
                    AudioTranscodeStageState.Ready,
                    outputStage,
                    Hash(outputStage),
                    new FileInfo(outputStage).Length),
            ]);
        var metadataParticipant = new FileMutationPlan(
            "MusicLibraryManager",
            library,
            temp.Directory("metadata-recovery"),
            [
                new(
                    FileMutationKind.Replace,
                    metadataStage,
                    source,
                    Snapshot(metadataStage),
                    Snapshot(source)),
            ],
            [],
            DateTimeOffset.UtcNow,
            RecoveryPayloadPolicy:
                RecoveryPayloadPolicy.AdaptiveReverseDelta);

        AudioTranscodeApplyResult applied =
            await service.ApplyReviewedBatchAsync(
                [staged],
                [metadataParticipant],
                new HashSet<Guid> { item.Id },
                ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, applied.ChangedFiles);
        Assert.Equal(
            "metadata-updated",
            File.ReadAllText(source));
        Assert.Equal(
            "encoded",
            File.ReadAllText(output));
        Assert.Single(history.Entries);
        Assert.Equal(
            [output],
            reindex.ReindexedPaths);
        Assert.Empty(reindex.RemovedPaths);

        ReviewedChangeUndoResult undone =
            await history.UndoLatestAsync(
                ct: TestContext.Current
                    .CancellationToken);

        Assert.Equal(2, undone.RestoredFiles);
        Assert.Equal(
            "original",
            File.ReadAllText(source));
        Assert.Equal(
            [output, source],
            reindex.ReindexedPaths);
        Assert.Equal(
            [output],
            reindex.RemovedPaths);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ReviewedTranscodeReplacementQuarantinesAndExactlyRestoresDifferentExtensionSource()
    {
        using var temp = new TempDirectory();
        string library = temp.Directory("library");
        string source = temp.File(
            Path.Combine("library", "source.mp3"),
            "original mp3 bytes");
        string outputStage = temp.File(
            "output-stage.flac",
            "encoded flac bytes");
        string output = Path.Combine(
            library,
            "source.flac");
        var settings = new MemorySettings();
        var coordinator = new FileMutationCoordinator();
        var journals =
            new OperationJournalService(coordinator);
        var batch = new ReviewedChangeBatchService(
            new FileMutationPlanExecutor(coordinator),
            journals,
            settings);
        var reindex = new RecordingReindex(source);
        var history =
            new ReviewedChangeHistoryService(
                settings,
                journals,
                reindex);
        var service = new AudioTranscodeService(
            settings,
            new UnusedCapabilityService(),
            new AudioTranscodeAdapter(
                settings,
                new ManagedProcessRunner()),
            new TranscodeMetadataProjectionService(),
            new TranscodeWorkScheduler(
                settings,
                processorCount: 2),
            batch,
            history,
            new UnusedDecodedVerifier(),
            reindex: reindex);
        var request = new AudioTranscodeRequest(
            [source],
            new(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Automatic,
                AudioTranscodeRateMode.Lossless),
            new(
                AudioTranscodeDestinationMode.ReplaceOriginal,
                null,
                true,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));
        var item = new AudioTranscodePlanItem(
            Guid.NewGuid(),
            source,
            output,
            Snapshot(source),
            OperationPathSnapshot.Missing(output),
            Hash(source),
            request.Settings,
            []);
        var plan = new AudioTranscodePlan(
            Guid.NewGuid(),
            request,
            [item],
            [],
            DateTimeOffset.UtcNow,
            1);
        var staged = new AudioTranscodeStageResult(
            plan,
            [
                new(
                    item,
                    AudioTranscodeStageState.Ready,
                    outputStage,
                    Hash(outputStage),
                    new FileInfo(outputStage).Length),
            ]);

        AudioTranscodeApplyResult applied =
            await service.ApplyReviewedBatchAsync(
                [staged],
                [],
                new HashSet<Guid> { item.Id },
                ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, applied.ChangedFiles);
        Assert.False(File.Exists(source));
        Assert.Equal(
            "encoded flac bytes",
            File.ReadAllText(output));
        Assert.Equal([output], reindex.ReindexedPaths);
        Assert.Equal([source], reindex.RemovedPaths);
        Assert.Single(history.Entries);

        ReviewedChangeUndoResult undone =
            await history.UndoLatestAsync(
                ct: TestContext.Current
                    .CancellationToken);

        Assert.Equal(2, undone.RestoredFiles);
        Assert.Equal(
            "original mp3 bytes",
            File.ReadAllText(source));
        Assert.False(File.Exists(output));
        Assert.Equal(
            [output, source],
            reindex.ReindexedPaths);
        Assert.Equal(
            [source, output],
            reindex.RemovedPaths);
        Assert.True(history.CanRedo);
    }

    private static FileMutationPlan Plan(
        string destinationRoot,
        string recoveryRoot,
        string stage,
        string destination) =>
        new(
            "MusicLibraryManager.Transcode",
            destinationRoot,
            recoveryRoot,
            [
                new(
                    FileMutationKind.Copy,
                    stage,
                    destination,
                    Snapshot(stage),
                    OperationPathSnapshot.Missing(destination)),
            ],
            [],
            DateTimeOffset.UtcNow,
            RetainRecovery: true);

    private static OperationPathSnapshot Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = Path.GetFullPath(path),
        };
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    private static AudioTranscodeRequest RedoRequest(string source) =>
        new(
            [source],
            new(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Automatic,
                AudioTranscodeRateMode.Lossless),
            new(
                AudioTranscodeDestinationMode.Alongside,
                null,
                true,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));

    private static void AssertRedoRequests(
        IReadOnlyList<AudioTranscodeRequest> actual,
        params AudioTranscodeRequest[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                expected[index].Settings,
                actual[index].Settings);
            Assert.Equal(
                expected[index].Destination,
                actual[index].Destination);
            Assert.Equal(
                expected[index].SourcePaths.ToArray(),
                actual[index].SourcePaths.ToArray());
        }
    }

    private sealed class FailOnSecondExecutor(
        IFileMutationPlanExecutor inner) : IFileMutationPlanExecutor
    {
        private int _calls;

        public Task<FileMutationSummary> ApplyAsync(
            FileMutationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
                throw new InvalidOperationException("Injected participant failure.");
            return inner.ApplyAsync(plan, progress, ct);
        }
    }

    private sealed class MemorySettings : IAppSettings
    {
        private readonly Dictionary<string, string> _preferences =
            new(StringComparer.Ordinal);

        public string? ConfigPath => null;
        public bool ThrowWhenClearingPendingManifest
        {
            get;
            set;
        }
        public bool ThrowWhenSettingReviewedHistory
        {
            get;
            set;
        }
        public LibraryConfiguration? Configuration => null;
        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);
        public event EventHandler? ConfigurationChanged
        {
            add
            {
            }
            remove
            {
            }
        }
        public void LoadConfig(string path) =>
            throw new NotSupportedException();
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs()
        {
        }
        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (ThrowWhenClearingPendingManifest &&
                value is null &&
                key == ReviewedChangeBatchService
                    .PendingManifestPreference)
                throw new IOException(
                    "Injected settings persistence failure.");
            if (ThrowWhenSettingReviewedHistory &&
                key == ReviewedChangeHistoryService.Preference)
                throw new IOException(
                    "Injected history persistence failure.");
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }

    private sealed class ThrowOnCompletedProgress :
        IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            if (value.Phase == OperationPhase.Completed)
                throw new InvalidOperationException(
                    "Injected progress callback failure.");
        }
    }

    private sealed class UnusedCapabilityService :
        IAudioTranscodeCapabilityService
    {
        public Task<AudioTranscodeCapabilitySnapshot> GetAsync(
            bool forceRefresh = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Invalidate()
        {
        }
    }

    private sealed class UnusedDecodedVerifier :
        IDecodedAudioVerificationService
    {
        public Task<AnalysisReport> VerifyAsync(
            string ffmpegExecutable,
            IReadOnlyList<DecodedAudioPair> pairs,
            IProgress<DecodedAudioProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReindex(
        string indexedSource) : IReindexService
    {
        public List<string> ReindexedPaths { get; } = [];
        public List<string> RemovedPaths { get; } = [];

        public Task<bool> IsIndexedFileAsync(
            string path,
            CancellationToken ct = default) =>
            Task.FromResult(
                StringComparer.OrdinalIgnoreCase.Equals(
                    path,
                    indexedSource));

        public Task ReindexFileAsync(
            string path,
            CancellationToken ct = default)
        {
            ReindexedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task RemoveIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            RemovedPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "reviewed-change-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);

        public string Directory(params string[] parts)
        {
            string path = parts.Aggregate(Path, System.IO.Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(string relative, string content)
        {
            string path = System.IO.Path.Combine(Path, relative);
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
