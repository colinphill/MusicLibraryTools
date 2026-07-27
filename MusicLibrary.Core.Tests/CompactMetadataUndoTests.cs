using System.Security.Cryptography;
using System.Text.Json;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class CompactMetadataUndoTests
{
    private const FileAttributes StandardAttributeMask =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Normal |
        FileAttributes.Temporary |
        FileAttributes.Offline |
        FileAttributes.NotContentIndexed;

    [Fact]
    public async Task BeneficialReplacementUsesCompactJournalAndRestoresExactFile()
    {
        using var workspace = new TempDirectory();
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "compact.flac", 4L * 1024 * 1024);
        var executor = new FileMutationPlanExecutor(new FileMutationCoordinator());

        FileMutationSummary result = await executor.ApplyAsync(
            fixture.Plan, ct: TestContext.Current.CancellationToken);

        RecoveryStorageSummary storage = Assert.IsType<RecoveryStorageSummary>(
            result.RecoveryStorage);
        Assert.Equal(fixture.OriginalLength, storage.OriginalBytes);
        Assert.Equal(0, storage.FullOriginalCount);
        Assert.Equal(1, storage.ReverseDeltaCount);
        Assert.InRange(storage.RetainedBytes, 1, fixture.OriginalLength - 1);
        string journal = await File.ReadAllTextAsync(
            result.JournalPath!, TestContext.Current.CancellationToken);
        Assert.Contains("PLAN_COMPACT_REPLACE\t1\t", journal);
        Assert.Contains("DELTA_READY\t1\t", journal);
        Assert.Contains("COMPACT_REPLACE\t1\t", journal);
        Assert.DoesNotContain("QUARANTINE\tREPLACE\t", journal);
        Assert.Equal(fixture.PostEditHash, await HashFileAsync(fixture.Destination));

        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(
            journals, fixture.LibraryRoot);
        OperationBrowseResult browse = await journals.BrowseAsync(
            run, TestContext.Current.CancellationToken);
        OperationFileEntry entry = Assert.Single(browse.Entries);
        Assert.Equal(RecoveryPayloadKind.ReverseDelta, entry.PayloadKind);
        Assert.Equal(storage.RetainedBytes, entry.RetainedBytes);
        Assert.Equal(fixture.OriginalLength, entry.OriginalBytes);
        Assert.True(File.Exists(entry.DeltaPath));

        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, [entry], TestContext.Current.CancellationToken);
        OperationRestoreResult restored = await journals.ApplyRestoreAsync(
            restore, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, restored.RestoredCount);
        Assert.Equal(fixture.OriginalHash, await HashFileAsync(fixture.Destination));
        Assert.Equal(fixture.OriginalLastWriteTimeUtc,
            File.GetLastWriteTimeUtc(fixture.Destination));
        Assert.Equal(
            fixture.OriginalAttributes & StandardAttributeMask,
            File.GetAttributes(fixture.Destination) & StandardAttributeMask);
        Assert.False(File.Exists(entry.DeltaPath));
        Assert.Equal("CONSUMED\tRESTORE",
            (await File.ReadAllLinesAsync(
                restore.RestoreJournalPath, TestContext.Current.CancellationToken))[^1]);
    }

    [Fact]
    public async Task LargeTitleLikeEditRetainsAtMostFivePercentOrOneMiB()
    {
        using var workspace = new TempDirectory();
        const long originalLength = 64L * 1024 * 1024;
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "large-title.flac", originalLength);

        FileMutationSummary result = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                fixture.Plan, ct: TestContext.Current.CancellationToken);

        RecoveryStorageSummary storage = Assert.IsType<RecoveryStorageSummary>(
            result.RecoveryStorage);
        long limit = Math.Min(originalLength / 20, 1024 * 1024);
        Assert.Equal(1, storage.ReverseDeltaCount);
        Assert.True(storage.RetainedBytes <= limit,
            $"Retained {storage.RetainedBytes:N0} bytes; limit is {limit:N0}.");
        Assert.Equal(fixture.PostEditHash, await HashFileAsync(fixture.Destination));
    }

    [Fact]
    public async Task IncompressibleReplacementFallsBackToLegacyFullPayloadAndRestores()
    {
        using var workspace = new TempDirectory();
        string library = workspace.Directory("library");
        string destination = Path.Combine(library, "random.flac");
        string stage = Path.Combine(library, ".random.flac.stage");
        const int length = 3 * 1024 * 1024;
        await WriteRandomFileAsync(
            destination, length, TestContext.Current.CancellationToken);
        DateTime originalTimestamp = SetStableTimestamp(destination);
        FileAttributes originalAttributes = File.GetAttributes(destination);
        string originalHash = await HashFileAsync(destination);
        await WriteRandomFileAsync(stage, length, TestContext.Current.CancellationToken);
        string postEditHash = await HashFileAsync(stage);
        Assert.NotEqual(originalHash, postEditHash);
        FileMutationPlan plan = CreatePlan(
            workspace, library, [Replace(stage, destination)]);

        FileMutationSummary result = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken);

        RecoveryStorageSummary storage = Assert.IsType<RecoveryStorageSummary>(
            result.RecoveryStorage);
        Assert.Equal(1, storage.FullOriginalCount);
        Assert.Equal(0, storage.ReverseDeltaCount);
        Assert.Equal(length, storage.RetainedBytes);
        string journal = await File.ReadAllTextAsync(
            result.JournalPath!, TestContext.Current.CancellationToken);
        Assert.Contains("QUARANTINE\tREPLACE\t", journal);
        Assert.DoesNotContain(
            journal.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("COMPACT_REPLACE\t", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(
            plan.RecoveryRoot, "*.mldelta", SearchOption.AllDirectories));

        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(journals, library);
        OperationFileEntry entry = Assert.Single(
            (await journals.BrowseAsync(run, TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(RecoveryPayloadKind.FullOriginal, entry.PayloadKind);
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, [entry], TestContext.Current.CancellationToken);
        await journals.ApplyRestoreAsync(
            restore, ct: TestContext.Current.CancellationToken);

        Assert.Equal(originalHash, await HashFileAsync(destination));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(destination));
        Assert.Equal(
            originalAttributes & StandardAttributeMask,
            File.GetAttributes(destination) & StandardAttributeMask);
    }

    [Fact]
    public async Task MixedCompactAndFullFallbackBatchRestoresBothExactly()
    {
        using var workspace = new TempDirectory();
        string library = workspace.Directory("library");
        MutationFiles compact = await CreateTitleEditFilesAsync(
            library, "compact.flac", 3L * 1024 * 1024, seed: 29);
        string fullDestination = Path.Combine(library, "full.flac");
        string fullStage = Path.Combine(library, ".full.flac.stage");
        const int fullLength = 3 * 1024 * 1024;
        await WriteRandomFileAsync(
            fullDestination, fullLength, TestContext.Current.CancellationToken);
        string fullOriginalHash = await HashFileAsync(fullDestination);
        await WriteRandomFileAsync(
            fullStage, fullLength, TestContext.Current.CancellationToken);
        FileMutationPlan mutation = CreatePlan(
            workspace,
            library,
            [
                Replace(compact.Stage, compact.Destination),
                Replace(fullStage, fullDestination),
            ]);

        FileMutationSummary applied = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                mutation, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, applied.RecoveryStorage?.ReverseDeltaCount);
        Assert.Equal(1, applied.RecoveryStorage?.FullOriginalCount);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(journals, library);
        OperationBrowseResult browse = await journals.BrowseAsync(
            run, TestContext.Current.CancellationToken);
        Assert.Equal(
            [RecoveryPayloadKind.FullOriginal, RecoveryPayloadKind.ReverseDelta],
            browse.Entries.Select(entry => entry.PayloadKind).Order());
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, browse.Entries, TestContext.Current.CancellationToken);
        OperationRestoreBatchPlan batch = await journals.PreviewRestoreBatchAsync(
            [restore], TestContext.Current.CancellationToken);
        OperationRestoreBatchResult restored = await journals.ApplyRestoreBatchAsync(
            batch, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, restored.RestoredCount);
        Assert.Equal(compact.OriginalHash, await HashFileAsync(compact.Destination));
        Assert.Equal(fullOriginalHash, await HashFileAsync(fullDestination));
    }

    [Fact]
    public async Task SameLengthAndTimestampExternalChangeRefusesUndoAndRetainsRecovery()
    {
        using var workspace = new TempDirectory();
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "externally-changed.flac", 4L * 1024 * 1024);
        FileMutationSummary applied = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                fixture.Plan, ct: TestContext.Current.CancellationToken);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(
            journals, fixture.LibraryRoot);
        OperationFileEntry entry = Assert.Single(
            (await journals.BrowseAsync(run, TestContext.Current.CancellationToken)).Entries);
        string settingsPath = Path.Combine(workspace.Path, "settings.json");
        var history = new EditHistoryService(new AppSettings(settingsPath), journals);
        var historyEntry = new EditHistoryEntry(
            Guid.NewGuid(),
            "External modification refusal",
            DateTimeOffset.UtcNow,
            [applied.JournalPath!],
            [fixture.Destination],
            null);
        history.Record(historyEntry);
        DateTime reviewedTimestamp = File.GetLastWriteTimeUtc(fixture.Destination);
        await ChangeByteWithoutChangingLengthAsync(
            fixture.Destination, 8193, reviewedTimestamp);
        string externallyChangedHash = await HashFileAsync(fixture.Destination);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => history.UndoLatestAsync(
                    ct: TestContext.Current.CancellationToken));

        Assert.Contains("Undo was refused", error.Message);
        Assert.Equal(externallyChangedHash, await HashFileAsync(fixture.Destination));
        Assert.True(File.Exists(entry.DeltaPath));
        Assert.True(history.CanUndo);
        Assert.Equal(historyEntry.Id, Assert.Single(history.Entries).Id);
        var restarted = new EditHistoryService(
            new AppSettings(settingsPath),
            new OperationJournalService(new FileMutationCoordinator()));
        Assert.True(restarted.CanUndo);
        Assert.Equal(historyEntry.Id, Assert.Single(restarted.Entries).Id);
        Assert.True(File.Exists(entry.DeltaPath));
    }

    [Fact]
    public async Task BatchPrevalidationWithOneStaleCompactBaseChangesNoFile()
    {
        using var workspace = new TempDirectory();
        string library = workspace.Directory("library");
        MutationFiles first = await CreateTitleEditFilesAsync(
            library, "first.flac", 3L * 1024 * 1024, seed: 17);
        MutationFiles second = await CreateTitleEditFilesAsync(
            library, "second.flac", 3L * 1024 * 1024, seed: 43);
        FileMutationPlan plan = CreatePlan(
            workspace,
            library,
            [
                Replace(first.Stage, first.Destination),
                Replace(second.Stage, second.Destination),
            ]);
        FileMutationSummary result = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.RecoveryStorage?.ReverseDeltaCount);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(journals, library);
        OperationBrowseResult browse = await journals.BrowseAsync(
            run, TestContext.Current.CancellationToken);
        Assert.Equal(2, browse.Entries.Count);
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, browse.Entries, TestContext.Current.CancellationToken);
        OperationRestoreBatchPlan batch = await journals.PreviewRestoreBatchAsync(
            [restore], TestContext.Current.CancellationToken);
        DateTime secondReviewedTimestamp = File.GetLastWriteTimeUtc(second.Destination);
        await ChangeByteWithoutChangingLengthAsync(
            second.Destination, 16_777, secondReviewedTimestamp);
        string staleSecondHash = await HashFileAsync(second.Destination);
        string firstPostEditHash = await HashFileAsync(first.Destination);
        string[] deltaPaths = browse.Entries.Select(entry => entry.DeltaPath!).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => journals.ApplyRestoreBatchAsync(
                batch, ct: TestContext.Current.CancellationToken));

        Assert.Equal(firstPostEditHash, await HashFileAsync(first.Destination));
        Assert.Equal(staleSecondHash, await HashFileAsync(second.Destination));
        Assert.All(deltaPaths, path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(restore.RestoreJournalPath));
    }

    [Fact]
    public async Task CatalogFailureRollsCompactInstallBackExactly()
    {
        using var workspace = new TempDirectory();
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "catalog-failure.flac", 4L * 1024 * 1024);
        var integration = new FailingCatalogIntegration();
        var executor = new FileMutationPlanExecutor(
            new FileMutationCoordinator(),
            catalogIntegrations: [integration]);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => executor.ApplyAsync(
                fixture.Plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("catalog commit failed", error.Message);
        Assert.Equal(fixture.OriginalHash, await HashFileAsync(fixture.Destination));
        Assert.Equal(
            fixture.OriginalLastWriteTimeUtc,
            File.GetLastWriteTimeUtc(fixture.Destination));
        Assert.Equal(
            fixture.OriginalAttributes & StandardAttributeMask,
            File.GetAttributes(fixture.Destination) & StandardAttributeMask);
        Assert.Contains(
            "COMPACT_REPLACE\t",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.Plan.RecoveryRoot, "journal.tsv"),
                TestContext.Current.CancellationToken));
        Assert.Contains(
            "ROLLBACK\t",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.Plan.RecoveryRoot, "journal.tsv"),
                TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Plan.RecoveryRoot, "*.mldelta", SearchOption.AllDirectories));
        Assert.True(integration.Session.Disposed);
        Assert.False(integration.Session.Completed);
    }

    [Fact]
    public async Task FailedCompactRollbackIsInterruptedAndRecoveryRemainsRestorable()
    {
        using var workspace = new TempDirectory();
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "rollback-failure.flac", 4L * 1024 * 1024);
        var executor = new FileMutationPlanExecutor(
            new FileMutationCoordinator(),
            catalogIntegrations: [new FailingCatalogIntegration()],
            reverseDelta: new RestoreFailingReverseDeltaService());

        await Assert.ThrowsAsync<AggregateException>(
            () => executor.ApplyAsync(
                fixture.Plan, ct: TestContext.Current.CancellationToken));

        string journalPath = Path.Combine(fixture.Plan.RecoveryRoot, "journal.tsv");
        Assert.Equal(
            "ROLLBACK_FAILED",
            (await File.ReadAllLinesAsync(
                journalPath,
                TestContext.Current.CancellationToken))[^1].Split('\t')[0]);
        Assert.Equal(fixture.PostEditHash, await HashFileAsync(fixture.Destination));
        string deltaPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Plan.RecoveryRoot, "*.mldelta", SearchOption.AllDirectories));

        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(
            journals, fixture.LibraryRoot);
        Assert.Equal(OperationJournalState.Interrupted, run.State);
        OperationFileEntry entry = Assert.Single(
            (await journals.BrowseAsync(
                run, TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(deltaPath, entry.DeltaPath);
        Assert.Equal(RecoveryPayloadKind.ReverseDelta, entry.PayloadKind);

        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, [entry], TestContext.Current.CancellationToken);
        await journals.ApplyRestoreAsync(
            restore, ct: TestContext.Current.CancellationToken);

        Assert.Equal(fixture.OriginalHash, await HashFileAsync(fixture.Destination));
        Assert.False(File.Exists(deltaPath));
    }

    [Fact]
    public async Task InterruptedDeltaReadyRecordDistinguishesUnappliedAndInstalledReplacement()
    {
        using var workspace = new TempDirectory();
        string library = workspace.Directory("library");
        MutationFiles files = await CreateTitleEditFilesAsync(
            library, "interrupted.flac", 4L * 1024 * 1024, seed: 71);
        string container = library + ".MusicLibraryManager-recovery";
        string run = Path.Combine(
            container,
            "20260724-130000000-" + Guid.NewGuid().ToString("N"));
        workspace.TrackExternalDirectory(container);
        string deltaPath = Path.Combine(run, "deltas", "interrupted.mldelta");
        Directory.CreateDirectory(Path.GetDirectoryName(deltaPath)!);
        ReverseDeltaDescriptor descriptor = await new ReverseDeltaService().CreateFileAsync(
            files.Destination,
            files.Stage,
            deltaPath,
            TestContext.Current.CancellationToken);
        string fields = string.Join(
            '\t',
            descriptor.FormatVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            files.Destination,
            deltaPath,
            descriptor.OriginalLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            descriptor.PostEditLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            descriptor.OriginalSha256,
            descriptor.PostEditSha256,
            descriptor.RetainedBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            descriptor.OriginalLastWriteTimeUtc.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)descriptor.OriginalAttributes).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            descriptor.PayloadSha256);
        string journalPath = Path.Combine(run, "journal.tsv");
        await File.WriteAllLinesAsync(
            journalPath,
            [
                "BEGIN\tcrash-test",
                $"PLAN_COMPACT_REPLACE\t1\t{files.Destination}",
                $"DELTA_READY\t{fields}",
            ],
            TestContext.Current.CancellationToken);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary summary = await DiscoverOnlyRunAsync(journals, library);

        OperationBrowseResult beforeInstall = await journals.BrowseAsync(
            summary, TestContext.Current.CancellationToken);

        Assert.Equal(OperationJournalState.RolledBack, summary.State);
        Assert.Empty(beforeInstall.Entries);
        Assert.Equal(files.OriginalHash, await HashFileAsync(files.Destination));
        Assert.True(File.Exists(deltaPath));
        OperationPurgePlan safePurge = await journals.PreviewPurgeAsync(
            [summary],
            90,
            summary.CreatedAtUtc.AddDays(91),
            TestContext.Current.CancellationToken);
        Assert.Single(safePurge.Runs);
        Assert.Equal(0, safePurge.ProtectedInterruptedCount);

        File.Move(files.Stage, files.Destination, overwrite: true);
        summary = await DiscoverOnlyRunAsync(journals, library);
        Assert.Equal(OperationJournalState.Interrupted, summary.State);
        OperationBrowseResult afterInstall = await journals.BrowseAsync(
            summary, TestContext.Current.CancellationToken);
        OperationFileEntry entry = Assert.Single(afterInstall.Entries);
        Assert.Equal(OperationEntryKind.Quarantined, entry.Kind);
        Assert.Equal(RecoveryPayloadKind.ReverseDelta, entry.PayloadKind);
        Assert.Equal(files.PostEditHash, await HashFileAsync(files.Destination));

        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            summary, [entry], TestContext.Current.CancellationToken);
        await journals.ApplyRestoreAsync(
            restore, ct: TestContext.Current.CancellationToken);

        Assert.Equal(files.OriginalHash, await HashFileAsync(files.Destination));
        Assert.False(File.Exists(deltaPath));
    }

    [Fact]
    public async Task ForwardJournalAfterCompactReplaceBeforeCommitIsInterruptedAndRestorable()
    {
        using var workspace = new TempDirectory();
        MutationFixture fixture = await CreateTitleEditFixtureAsync(
            workspace, "forward-crash.flac", 4L * 1024 * 1024);
        FileMutationSummary applied = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                fixture.Plan, ct: TestContext.Current.CancellationToken);
        string[] interruptedLines =
        [
            .. (await File.ReadAllLinesAsync(
                applied.JournalPath!,
                TestContext.Current.CancellationToken))
            .Where(line => !line.StartsWith("COMMIT\t", StringComparison.Ordinal)),
        ];
        await File.WriteAllLinesAsync(
            applied.JournalPath!,
            interruptedLines,
            TestContext.Current.CancellationToken);
        Assert.Contains(
            interruptedLines,
            line => line.StartsWith("COMPACT_REPLACE\t", StringComparison.Ordinal));
        var journals = new OperationJournalService(new FileMutationCoordinator());

        OperationJournalSummary run = await DiscoverOnlyRunAsync(
            journals, fixture.LibraryRoot);

        Assert.Equal(OperationJournalState.Interrupted, run.State);
        OperationFileEntry entry = Assert.Single(
            (await journals.BrowseAsync(run, TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(RecoveryPayloadKind.ReverseDelta, entry.PayloadKind);
        Assert.Equal(fixture.PostEditHash, await HashFileAsync(fixture.Destination));
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, [entry], TestContext.Current.CancellationToken);
        OperationRestoreResult result = await journals.ApplyRestoreAsync(
            restore, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(fixture.OriginalHash, await HashFileAsync(fixture.Destination));
        Assert.Equal(
            fixture.OriginalLastWriteTimeUtc,
            File.GetLastWriteTimeUtc(fixture.Destination));
        Assert.Equal(
            fixture.OriginalAttributes & StandardAttributeMask,
            File.GetAttributes(fixture.Destination) & StandardAttributeMask);
        Assert.False(File.Exists(entry.DeltaPath));
    }

    [Fact]
    public async Task PartialMixedPayloadUndoAcrossJournalsRollsBackAndCanRetry()
    {
        using var workspace = new TempDirectory();
        string library = workspace.Directory("library");
        MutationFiles compact = await CreateTitleEditFilesAsync(
            library, "mixed-compact.flac", 3L * 1024 * 1024, seed: 61);
        string fullDestination = Path.Combine(library, "mixed-full.flac");
        string fullStage = Path.Combine(library, ".mixed-full.flac.stage");
        const int fullLength = 3 * 1024 * 1024;
        await WriteRandomFileAsync(
            fullDestination, fullLength, TestContext.Current.CancellationToken);
        string fullOriginalHash = await HashFileAsync(fullDestination);
        await WriteRandomFileAsync(
            fullStage, fullLength, TestContext.Current.CancellationToken);
        string fullPostEditHash = await HashFileAsync(fullStage);
        FileMutationPlan mutation = CreatePlan(
            workspace,
            library,
            [
                Replace(compact.Stage, compact.Destination),
                Replace(fullStage, fullDestination),
            ]);
        FileMutationSummary applied = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                mutation, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, applied.RecoveryStorage?.ReverseDeltaCount);
        Assert.Equal(1, applied.RecoveryStorage?.FullOriginalCount);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(journals, library);
        OperationBrowseResult browse = await journals.BrowseAsync(
            run, TestContext.Current.CancellationToken);
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, browse.Entries, TestContext.Current.CancellationToken);
        OperationRestoreAction compactAction = Assert.Single(
            restore.Actions,
            action => action.PayloadKind == RecoveryPayloadKind.ReverseDelta);
        OperationRestoreAction fullAction = Assert.Single(
            restore.Actions,
            action => action.PayloadKind == RecoveryPayloadKind.FullOriginal);
        string prepared = PreparedPath(compactAction);
        await new ReverseDeltaService().RestoreFileAsync(
            compactAction.SourcePath,
            compactAction.DestinationPath,
            prepared,
            TestContext.Current.CancellationToken);
        string compactJournal = restore.RestoreJournalPath;
        string fullJournal = Path.Combine(
            Path.GetDirectoryName(compactJournal)!,
            "restore-full.tsv");
        WriteInterruptedRestoreJournal(
            compactJournal,
            [(compactAction, prepared)]);
        Directory.CreateDirectory(Path.GetDirectoryName(fullJournal)!);
        File.WriteAllLines(
            fullJournal,
            [
                "BEGIN\tRESTORE_BATCH",
                $"PLAN_RESTORE\t{fullAction.SourcePath}\t" +
                $"{fullAction.DestinationPath}\t" +
                $"{fullAction.CollisionBackupPath}",
            ]);
        ApplyPreparedCompactRestore(compactAction, prepared);
        AppendRestoreJournal(
            compactJournal,
            $"RESTORE_COMPACT\t{compactAction.SourcePath}\t" +
            $"{compactAction.DestinationPath}\t" +
            $"{compactAction.CollisionBackupPath}");
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullAction.CollisionBackupPath)!);
        File.Move(
            fullAction.DestinationPath,
            fullAction.CollisionBackupPath);
        File.Move(fullAction.SourcePath, fullAction.DestinationPath);
        AppendRestoreJournal(
            fullJournal,
            $"RESTORE\t{fullAction.SourcePath}\t" +
            $"{fullAction.DestinationPath}\t{fullAction.CollisionBackupPath}");
        string settingsPath = Path.Combine(workspace.Path, "mixed-settings.json");
        var historyEntry = new EditHistoryEntry(
            Guid.NewGuid(),
            "Interrupted mixed undo",
            DateTimeOffset.UtcNow,
            [applied.JournalPath!],
            [compact.Destination, fullDestination],
            null);
        PersistPendingHistory(
            settingsPath,
            historyEntry,
            [compactJournal, fullJournal]);

        var restarted = new EditHistoryService(
            new AppSettings(settingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(restarted.CanUndo);
        Assert.Equal(compact.PostEditHash, await HashFileAsync(compact.Destination));
        Assert.Equal(fullPostEditHash, await HashFileAsync(fullDestination));
        Assert.True(File.Exists(compactAction.SourcePath));
        Assert.True(File.Exists(fullAction.SourcePath));
        Assert.False(File.Exists(compactAction.CollisionBackupPath));
        Assert.False(File.Exists(fullAction.CollisionBackupPath));
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(compactJournal).Last());
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(fullJournal).Last());

        int restored = await restarted.UndoLatestAsync(
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, restored);
        Assert.False(restarted.CanUndo);
        Assert.Equal(compact.OriginalHash, await HashFileAsync(compact.Destination));
        Assert.Equal(fullOriginalHash, await HashFileAsync(fullDestination));
        Assert.False(File.Exists(compactAction.SourcePath));
        Assert.False(File.Exists(fullAction.SourcePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterBeginDeletesPartialOrCompletePreparedFileAndRetainsUndo(
        bool completelyPrepared)
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 1);
        OperationRestoreAction action = Assert.Single(scenario.Restore.Actions);
        string prepared = PreparedPath(action);
        WriteInterruptedRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            [(action, prepared)]);
        if (completelyPrepared)
        {
            await new ReverseDeltaService().RestoreFileAsync(
                action.SourcePath,
                action.DestinationPath,
                prepared,
                TestContext.Current.CancellationToken);
        }
        else
        {
            await File.WriteAllBytesAsync(
                prepared,
                CreateDeterministicBytes(4097, 211),
                TestContext.Current.CancellationToken);
        }
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [scenario.Restore.RestoreJournalPath]);

        var restarted = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(restarted.CanUndo);
        Assert.Equal(
            scenario.HistoryEntry.Id,
            Assert.Single(restarted.Entries).Id);
        Assert.Equal(
            scenario.Files[0].PostEditHash,
            await HashFileAsync(action.DestinationPath));
        Assert.True(File.Exists(action.SourcePath));
        Assert.False(File.Exists(prepared));
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());

        var restartedAgain = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));
        Assert.True(restartedAgain.CanUndo);
        Assert.False(File.Exists(prepared));
        Assert.Single(
            File.ReadLines(scenario.Restore.RestoreJournalPath),
            line => line == "ROLLBACK\tRESTORE_BATCH");
    }

    [Fact]
    public async Task RestartAfterPartialMultiJournalRestoreRollsBackAndUndoStillSucceeds()
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 2);
        OperationRestoreAction[] actions = [.. scenario.Restore.Actions];
        Assert.Equal(2, actions.Length);
        string firstPrepared = PreparedPath(actions[0]);
        string secondPrepared = PreparedPath(actions[1]);
        await new ReverseDeltaService().RestoreFileAsync(
            actions[0].SourcePath,
            actions[0].DestinationPath,
            firstPrepared,
            TestContext.Current.CancellationToken);
        await new ReverseDeltaService().RestoreFileAsync(
            actions[1].SourcePath,
            actions[1].DestinationPath,
            secondPrepared,
            TestContext.Current.CancellationToken);
        string firstJournal = scenario.Restore.RestoreJournalPath;
        string secondJournal = Path.Combine(
            Path.GetDirectoryName(firstJournal)!,
            "restore-second.tsv");
        WriteInterruptedRestoreJournal(
            firstJournal,
            [(actions[0], firstPrepared)]);
        WriteInterruptedRestoreJournal(
            secondJournal,
            [(actions[1], secondPrepared)]);
        Directory.CreateDirectory(
            Path.GetDirectoryName(actions[0].CollisionBackupPath)!);
        File.Move(
            actions[0].DestinationPath,
            actions[0].CollisionBackupPath);
        File.Move(firstPrepared, actions[0].DestinationPath);
        AppendRestoreJournal(
            firstJournal,
            $"RESTORE_COMPACT\t{actions[0].SourcePath}\t" +
            $"{actions[0].DestinationPath}\t" +
            $"{actions[0].CollisionBackupPath}");
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [firstJournal, secondJournal]);

        var restarted = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(restarted.CanUndo);
        for (int index = 0; index < actions.Length; index++)
        {
            Assert.Equal(
                scenario.Files[index].PostEditHash,
                await HashFileAsync(actions[index].DestinationPath));
            Assert.True(File.Exists(actions[index].SourcePath));
            Assert.False(File.Exists(actions[index].CollisionBackupPath));
        }
        Assert.False(File.Exists(firstPrepared));
        Assert.False(File.Exists(secondPrepared));
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(firstJournal).Last());
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(secondJournal).Last());

        int restored = await restarted.UndoLatestAsync(
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, restored);
        Assert.False(restarted.CanUndo);
        for (int index = 0; index < actions.Length; index++)
        {
            Assert.Equal(
                scenario.Files[index].OriginalHash,
                await HashFileAsync(actions[index].DestinationPath));
            Assert.False(File.Exists(actions[index].SourcePath));
        }
    }

    [Fact]
    public async Task RestartAfterAppliedWithoutCommitRollsBackAndRetainsUndo()
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 1);
        OperationRestoreAction action = Assert.Single(scenario.Restore.Actions);
        string prepared = PreparedPath(action);
        await new ReverseDeltaService().RestoreFileAsync(
            action.SourcePath,
            action.DestinationPath,
            prepared,
            TestContext.Current.CancellationToken);
        WriteInterruptedRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            [(action, prepared)]);
        ApplyPreparedCompactRestore(action, prepared);
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            $"RESTORE_COMPACT\t{action.SourcePath}\t" +
            $"{action.DestinationPath}\t{action.CollisionBackupPath}");
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            "APPLIED\tRESTORE_BATCH");
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [scenario.Restore.RestoreJournalPath]);

        var restarted = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(restarted.CanUndo);
        Assert.Equal(
            scenario.Files[0].PostEditHash,
            await HashFileAsync(action.DestinationPath));
        Assert.True(File.Exists(action.SourcePath));
        Assert.False(File.Exists(action.CollisionBackupPath));
        Assert.False(File.Exists(prepared));
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());
    }

    [Fact]
    public async Task RestartAfterCommitFinishesCleanupConsumesHistoryAndIsIdempotent()
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 1);
        OperationRestoreAction action = Assert.Single(scenario.Restore.Actions);
        string prepared = PreparedPath(action);
        await new ReverseDeltaService().RestoreFileAsync(
            action.SourcePath,
            action.DestinationPath,
            prepared,
            TestContext.Current.CancellationToken);
        WriteInterruptedRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            [(action, prepared)]);
        ApplyPreparedCompactRestore(action, prepared);
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            $"RESTORE_COMPACT\t{action.SourcePath}\t" +
            $"{action.DestinationPath}\t{action.CollisionBackupPath}");
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            "APPLIED\tRESTORE_BATCH");
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            "COMMIT\tRESTORE_BATCH");
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [scenario.Restore.RestoreJournalPath]);

        var restarted = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.False(restarted.CanUndo);
        Assert.Equal(
            scenario.HistoryEntry.Id,
            Assert.Single(restarted.RedoEntries).Id);
        Assert.Equal(
            scenario.Files[0].OriginalHash,
            await HashFileAsync(action.DestinationPath));
        Assert.False(File.Exists(action.SourcePath));
        Assert.False(File.Exists(action.CollisionBackupPath));
        Assert.False(File.Exists(prepared));
        Assert.Equal(
            "CONSUMED\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());

        var restartedAgain = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));
        Assert.False(restartedAgain.CanUndo);
        Assert.Equal(
            scenario.HistoryEntry.Id,
            Assert.Single(restartedAgain.RedoEntries).Id);
        Assert.Single(
            File.ReadLines(scenario.Restore.RestoreJournalPath),
            line => line == "CONSUMED\tRESTORE_BATCH");
    }

    [Fact]
    public async Task CleanupFailureAfterCommitConsumesUndoAndRetainsRepairTransition()
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 1);
        OperationRestoreAction action = Assert.Single(scenario.Restore.Actions);
        string prepared = PreparedPath(action);
        await new ReverseDeltaService().RestoreFileAsync(
            action.SourcePath,
            action.DestinationPath,
            prepared,
            TestContext.Current.CancellationToken);
        WriteInterruptedRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            [(action, prepared)]);
        ApplyPreparedCompactRestore(action, prepared);
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            $"RESTORE_COMPACT\t{action.SourcePath}\t" +
            $"{action.DestinationPath}\t{action.CollisionBackupPath}");
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            "APPLIED\tRESTORE_BATCH");
        AppendRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            "COMMIT\tRESTORE_BATCH");
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [scenario.Restore.RestoreJournalPath]);

        string heldCollision = action.CollisionBackupPath + ".held";
        File.Move(action.CollisionBackupPath, heldCollision);
        Directory.CreateDirectory(action.CollisionBackupPath);

        var blockedRestart = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.False(blockedRestart.CanUndo);
        Assert.Equal(
            scenario.HistoryEntry.Id,
            Assert.Single(blockedRestart.RedoEntries).Id);
        Assert.Contains(
            blockedRestart.LastUndoIssues,
            issue => issue.Code ==
                "restore.recovery-cleanup-failed");
        Assert.True(File.Exists(action.SourcePath));
        Assert.True(Directory.Exists(action.CollisionBackupPath));
        Assert.Equal(
            scenario.Files[0].OriginalHash,
            await HashFileAsync(action.DestinationPath));
        Assert.Equal(
            "COMMIT\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());
        Assert.DoesNotContain(
            "CONSUMED\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath));

        Directory.Delete(action.CollisionBackupPath);
        File.Move(heldCollision, action.CollisionBackupPath);

        var retriedRestart = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.False(retriedRestart.CanUndo);
        Assert.Equal(
            scenario.HistoryEntry.Id,
            Assert.Single(retriedRestart.RedoEntries).Id);
        Assert.False(File.Exists(action.SourcePath));
        Assert.False(File.Exists(action.CollisionBackupPath));
        Assert.Equal(
            "CONSUMED\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());
        Assert.Single(
            File.ReadLines(scenario.Restore.RestoreJournalPath),
            line => line == "CONSUMED\tRESTORE_BATCH");
    }

    [Fact]
    public async Task PreparedCleanupFailureIsNotMarkedRolledBackAndCanRetry()
    {
        using var workspace = new TempDirectory();
        RestartScenario scenario = await CreateRestartScenarioAsync(
            workspace, fileCount: 1);
        OperationRestoreAction action = Assert.Single(scenario.Restore.Actions);
        string prepared = PreparedPath(action);
        WriteInterruptedRestoreJournal(
            scenario.Restore.RestoreJournalPath,
            [(action, prepared)]);
        Directory.CreateDirectory(prepared);
        PersistPendingHistory(
            scenario.SettingsPath,
            scenario.HistoryEntry,
            [scenario.Restore.RestoreJournalPath]);

        var blockedRestart = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(blockedRestart.CanUndo);
        Assert.Empty(blockedRestart.RedoEntries);
        Assert.True(Directory.Exists(prepared));
        Assert.True(File.Exists(action.SourcePath));
        Assert.Equal(
            scenario.Files[0].PostEditHash,
            await HashFileAsync(action.DestinationPath));
        Assert.Equal(
            "ROLLBACK_FAILED\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());
        Assert.DoesNotContain(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath));
        Assert.DoesNotContain(
            "CONSUMED\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath));

        Directory.Delete(prepared);

        var retriedRestart = new EditHistoryService(
            new AppSettings(scenario.SettingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        Assert.True(retriedRestart.CanUndo);
        Assert.Empty(retriedRestart.RedoEntries);
        Assert.False(File.Exists(prepared));
        Assert.True(File.Exists(action.SourcePath));
        Assert.Equal(
            scenario.Files[0].PostEditHash,
            await HashFileAsync(action.DestinationPath));
        Assert.Equal(
            "ROLLBACK\tRESTORE_BATCH",
            File.ReadLines(scenario.Restore.RestoreJournalPath).Last());
    }

    [Theory]
    [InlineData("APPLIED\tRESTORE_BATCH", false)]
    [InlineData("COMMIT\tRESTORE_BATCH", true)]
    [InlineData("APPLIED\tRESTORE_BATCH\nROLLBACK\tRESTORE_BATCH", false)]
    [InlineData("", false)]
    public void RestartReconcilesPreparedHistoryTransitionFromDurableRestoreMarker(
        string journalText,
        bool shouldConsume)
    {
        using var workspace = new TempDirectory();
        string settingsPath = Path.Combine(workspace.Path, "settings.json");
        string restoreJournal = Path.Combine(workspace.Path, "restore.tsv");
        if (journalText.Length > 0)
            File.WriteAllText(
                restoreJournal,
                journalText.Replace("\n", Environment.NewLine, StringComparison.Ordinal) +
                Environment.NewLine);
        var entry = new EditHistoryEntry(
            Guid.NewGuid(),
            "Restart transition",
            DateTimeOffset.UtcNow,
            [],
            [],
            null);
        var settings = new AppSettings(settingsPath);
        settings.SetPreference(
            "manager.workbench.history.v1",
            JsonSerializer.Serialize(new
            {
                Undo = new[] { entry },
                Redo = Array.Empty<EditHistoryEntry>(),
                PendingTransition = new
                {
                    EntryId = entry.Id,
                    Stage = 0,
                    RestoreJournalPaths = new[] { restoreJournal },
                },
            }));

        var restarted = new EditHistoryService(
            new AppSettings(settingsPath),
            new OperationJournalService(new FileMutationCoordinator()));

        if (shouldConsume)
        {
            Assert.Empty(restarted.Entries);
            Assert.Equal(entry.Id, Assert.Single(restarted.RedoEntries).Id);
        }
        else
        {
            Assert.Equal(entry.Id, Assert.Single(restarted.Entries).Id);
            Assert.Empty(restarted.RedoEntries);
        }
    }

    private static async Task<RestartScenario> CreateRestartScenarioAsync(
        TempDirectory workspace,
        int fileCount)
    {
        string library = workspace.Directory("library");
        var files = new List<MutationFiles>(fileCount);
        for (int index = 0; index < fileCount; index++)
        {
            files.Add(await CreateTitleEditFilesAsync(
                library,
                $"restart-{index + 1}.flac",
                3L * 1024 * 1024,
                seed: 101 + index * 37));
        }
        FileMutationPlan mutation = CreatePlan(
            workspace,
            library,
            files.Select(file => Replace(file.Stage, file.Destination)).ToArray());
        FileMutationSummary applied = await new FileMutationPlanExecutor(
            new FileMutationCoordinator()).ApplyAsync(
                mutation, ct: TestContext.Current.CancellationToken);
        var journals = new OperationJournalService(new FileMutationCoordinator());
        OperationJournalSummary run = await DiscoverOnlyRunAsync(journals, library);
        OperationBrowseResult browse = await journals.BrowseAsync(
            run, TestContext.Current.CancellationToken);
        Assert.Equal(fileCount, browse.Entries.Count);
        OperationRestorePlan restore = await journals.PreviewRestoreAsync(
            run, browse.Entries, TestContext.Current.CancellationToken);
        string settingsPath = Path.Combine(workspace.Path, "restart-settings.json");
        var historyEntry = new EditHistoryEntry(
            Guid.NewGuid(),
            "Interrupted compact undo",
            DateTimeOffset.UtcNow,
            [applied.JournalPath!],
            [.. files.Select(file => file.Destination)],
            null);
        return new(
            library,
            [.. files],
            applied,
            restore,
            settingsPath,
            historyEntry);
    }

    private static string PreparedPath(OperationRestoreAction action) =>
        Path.Combine(
            Path.GetDirectoryName(action.DestinationPath)!,
            $".{Path.GetFileName(action.DestinationPath)}." +
            $"{Guid.NewGuid():N}.undo-restore");

    private static void WriteInterruptedRestoreJournal(
        string path,
        IReadOnlyList<(OperationRestoreAction Action, string Prepared)> actions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(
            path,
            [
                "BEGIN\tRESTORE_BATCH",
                .. actions.Select(item =>
                    $"PLAN_RESTORE_COMPACT\t{item.Action.SourcePath}\t" +
                    $"{item.Action.DestinationPath}\t" +
                    $"{item.Action.CollisionBackupPath}\t{item.Prepared}"),
            ]);
    }

    private static void AppendRestoreJournal(string path, string line) =>
        File.AppendAllText(path, line + Environment.NewLine);

    private static void ApplyPreparedCompactRestore(
        OperationRestoreAction action,
        string prepared)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(action.CollisionBackupPath)!);
        File.Move(action.DestinationPath, action.CollisionBackupPath);
        File.Move(prepared, action.DestinationPath);
    }

    private static void PersistPendingHistory(
        string settingsPath,
        EditHistoryEntry entry,
        IReadOnlyList<string> restoreJournalPaths)
    {
        var settings = new AppSettings(settingsPath);
        settings.SetPreference(
            "manager.workbench.history.v1",
            JsonSerializer.Serialize(new
            {
                Undo = new[] { entry },
                Redo = Array.Empty<EditHistoryEntry>(),
                PendingTransition = new
                {
                    EntryId = entry.Id,
                    Stage = 0,
                    RestoreJournalPaths = restoreJournalPaths,
                },
            }));
    }

    private static byte[] CreateDeterministicBytes(int length, int seed)
    {
        byte[] result = new byte[length];
        new Random(seed).NextBytes(result);
        return result;
    }

    private static async Task<MutationFixture> CreateTitleEditFixtureAsync(
        TempDirectory workspace,
        string fileName,
        long length)
    {
        string library = workspace.Directory("library");
        MutationFiles files = await CreateTitleEditFilesAsync(
            library, fileName, length, seed: 113);
        return new(
            library,
            files.Destination,
            files.Stage,
            CreatePlan(workspace, library, [Replace(files.Stage, files.Destination)]),
            files.OriginalLength,
            files.OriginalHash,
            files.PostEditHash,
            files.OriginalLastWriteTimeUtc,
            files.OriginalAttributes);
    }

    private static async Task<MutationFiles> CreateTitleEditFilesAsync(
        string library,
        string fileName,
        long length,
        int seed)
    {
        string destination = Path.Combine(library, fileName);
        string stage = Path.Combine(library, $".{fileName}.{Guid.NewGuid():N}.stage");
        await WritePatternFileAsync(
            destination, length, seed, TestContext.Current.CancellationToken);
        DateTime originalTimestamp = SetStableTimestamp(destination);
        File.SetAttributes(destination, FileAttributes.Archive);
        FileAttributes originalAttributes = File.GetAttributes(destination);
        string originalHash = await HashFileAsync(destination);
        File.Copy(destination, stage);
        await using (var stream = new FileStream(
            stage, FileMode.Open, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            stream.Position = 257;
            await stream.WriteAsync(
                "A compact metadata title edit"u8.ToArray(),
                TestContext.Current.CancellationToken);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
            stream.Flush(flushToDisk: true);
        }
        return new(
            destination,
            stage,
            new FileInfo(destination).Length,
            originalHash,
            await HashFileAsync(stage),
            originalTimestamp,
            originalAttributes);
    }

    private static FileMutationPlan CreatePlan(
        TempDirectory workspace,
        string library,
        IReadOnlyList<FileMutationAction> actions)
    {
        string container = library + ".MusicLibraryManager-recovery";
        string run = Path.Combine(
            container,
            "20260724-120000000-" + Guid.NewGuid().ToString("N"));
        workspace.TrackExternalDirectory(container);
        return new(
            "MusicLibraryManager",
            library,
            run,
            actions,
            [],
            DateTimeOffset.UtcNow,
            RetainRecovery: true,
            RecoveryPayloadPolicy: RecoveryPayloadPolicy.AdaptiveReverseDelta);
    }

    private static FileMutationAction Replace(string stage, string destination) =>
        new(
            FileMutationKind.Replace,
            stage,
            destination,
            Snapshot(stage),
            Snapshot(destination));

    private static OperationPathSnapshot Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = Path.GetFullPath(path),
        };
    }

    private static async Task<OperationJournalSummary> DiscoverOnlyRunAsync(
        OperationJournalService service,
        string library)
    {
        OperationJournalDiscoveryResult discovered = await service.DiscoverAsync(
            [library], TestContext.Current.CancellationToken);
        Assert.Empty(discovered.Warnings);
        return Assert.Single(discovered.Runs);
    }

    private static DateTime SetStableTimestamp(string path)
    {
        DateTime requested = DateTime.UtcNow.AddHours(-6);
        File.SetLastWriteTimeUtc(path, requested);
        return File.GetLastWriteTimeUtc(path);
    }

    private static async Task ChangeByteWithoutChangingLengthAsync(
        string path,
        long offset,
        DateTime timestamp)
    {
        await using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            stream.Position = offset;
            int current = stream.ReadByte();
            Assert.NotEqual(-1, current);
            stream.Position = offset;
            stream.WriteByte((byte)(current ^ 0x5a));
            stream.Flush(flushToDisk: true);
        }
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    private static async Task WritePatternFileAsync(
        string path,
        long length,
        int seed,
        CancellationToken ct)
    {
        byte[] block = new byte[64 * 1024];
        new Random(seed).NextBytes(block);
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            block.Length, FileOptions.Asynchronous);
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(block.Length, remaining);
            await stream.WriteAsync(block.AsMemory(0, count), ct);
            remaining -= count;
        }
    }

    private static async Task WriteRandomFileAsync(
        string path,
        int length,
        CancellationToken ct)
    {
        byte[] block = new byte[64 * 1024];
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            block.Length, FileOptions.Asynchronous);
        int remaining = length;
        while (remaining > 0)
        {
            RandomNumberGenerator.Fill(block);
            int count = Math.Min(block.Length, remaining);
            await stream.WriteAsync(block.AsMemory(0, count), ct);
            remaining -= count;
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(
            stream, TestContext.Current.CancellationToken));
    }

    private sealed record MutationFiles(
        string Destination,
        string Stage,
        long OriginalLength,
        string OriginalHash,
        string PostEditHash,
        DateTime OriginalLastWriteTimeUtc,
        FileAttributes OriginalAttributes);

    private sealed record MutationFixture(
        string LibraryRoot,
        string Destination,
        string Stage,
        FileMutationPlan Plan,
        long OriginalLength,
        string OriginalHash,
        string PostEditHash,
        DateTime OriginalLastWriteTimeUtc,
        FileAttributes OriginalAttributes);

    private sealed record RestartScenario(
        string LibraryRoot,
        IReadOnlyList<MutationFiles> Files,
        FileMutationSummary Applied,
        OperationRestorePlan Restore,
        string SettingsPath,
        EditHistoryEntry HistoryEntry);

    private sealed class FailingCatalogIntegration : IMediaCatalogIntegration
    {
        public string Id => "failing-test";
        public string DisplayName => "Failing test catalog";
        public FailingCatalogSession Session { get; } = new();

        public Task<IMediaCatalogMutationSession?> BeginAsync(
            IReadOnlyCollection<string> candidatePaths,
            bool backupFiles,
            CancellationToken ct = default) =>
            Task.FromResult<IMediaCatalogMutationSession?>(Session);
    }

    private sealed class FailingCatalogSession : IMediaCatalogMutationSession
    {
        public bool Active => true;
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public Task CommitAsync(
            IReadOnlyList<MediaCatalogMutation> mutations,
            CancellationToken ct = default) =>
            Task.FromException(new IOException("catalog commit failed"));

        public Task CompleteAsync(CancellationToken ct = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RestoreFailingReverseDeltaService : IReverseDeltaService
    {
        private readonly ReverseDeltaService _inner = new();

        public Task<ReverseDeltaDescriptor> CreateAsync(
            Stream original,
            Stream postEdit,
            Stream deltaOutput,
            ReverseDeltaFileMetadata originalMetadata,
            CancellationToken ct = default) =>
            _inner.CreateAsync(original, postEdit, deltaOutput, originalMetadata, ct);

        public Task<ReverseDeltaDescriptor> CreateFileAsync(
            string originalPath,
            string postEditPath,
            string deltaPath,
            CancellationToken ct = default) =>
            _inner.CreateFileAsync(originalPath, postEditPath, deltaPath, ct);

        public Task<ReverseDeltaDescriptor> InspectAsync(
            Stream delta,
            CancellationToken ct = default) =>
            _inner.InspectAsync(delta, ct);

        public Task<ReverseDeltaDescriptor> InspectFileAsync(
            string deltaPath,
            CancellationToken ct = default) =>
            _inner.InspectFileAsync(deltaPath, ct);

        public Task<ReverseDeltaDescriptor> ValidateBaseAsync(
            Stream delta,
            Stream postEdit,
            CancellationToken ct = default) =>
            _inner.ValidateBaseAsync(delta, postEdit, ct);

        public Task<ReverseDeltaDescriptor> ValidateBaseFileAsync(
            string deltaPath,
            string postEditPath,
            CancellationToken ct = default) =>
            _inner.ValidateBaseFileAsync(deltaPath, postEditPath, ct);

        public Task<ReverseDeltaDescriptor> ValidateAsync(
            Stream delta,
            Stream postEdit,
            CancellationToken ct = default) =>
            _inner.ValidateAsync(delta, postEdit, ct);

        public Task<ReverseDeltaDescriptor> ValidateFileAsync(
            string deltaPath,
            string postEditPath,
            CancellationToken ct = default) =>
            _inner.ValidateFileAsync(deltaPath, postEditPath, ct);

        public Task<ReverseDeltaDescriptor> RestoreAsync(
            Stream delta,
            Stream postEdit,
            Stream originalOutput,
            CancellationToken ct = default) =>
            _inner.RestoreAsync(delta, postEdit, originalOutput, ct);

        public Task<ReverseDeltaDescriptor> RestoreFileAsync(
            string deltaPath,
            string postEditPath,
            string originalOutputPath,
            CancellationToken ct = default) =>
            Task.FromException<ReverseDeltaDescriptor>(
                new IOException("forced compact rollback failure"));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly List<string> _externalDirectories = [];

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "compact-metadata-undo-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Directory(params string[] parts)
        {
            string path = parts.Aggregate(Path, System.IO.Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void TrackExternalDirectory(string path)
        {
            if (!path.StartsWith(
                System.IO.Path.TrimEndingDirectorySeparator(Path) +
                System.IO.Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                _externalDirectories.Add(path);
            }
        }

        public void Dispose()
        {
            foreach (string directory in _externalDirectories.Append(Path)
                         .OrderByDescending(value => value.Length))
            {
                if (!System.IO.Directory.Exists(directory))
                    continue;
                foreach (string file in System.IO.Directory.EnumerateFiles(
                    directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch { }
                }
                try { System.IO.Directory.Delete(directory, recursive: true); }
                catch { }
            }
        }
    }
}
