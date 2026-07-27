using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OperationJournalServiceTests
{
    [Fact]
    public async Task DiscoveryFindsSiblingIngestRunsAndFlagsAnUnfinishedGroup()
    {
        using var temp = new TempDirectory();
        string source = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-120000000");
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            "BEGIN\talbum-one",
            $"QUARANTINE\talbum-one\t{Path.Combine(source, "one.flac")}\t{Path.Combine(run, "one.flac")}",
            "COMMIT\talbum-one",
            "BEGIN\talbum-two",
        ]);

        var result = await new OperationJournalService().DiscoverAsync([source]);

        var summary = Assert.Single(result.Runs);
        Assert.Equal("IngestMusic", summary.ToolName);
        Assert.Equal(OperationJournalKind.Ingest, summary.Kind);
        Assert.Equal(OperationJournalState.Interrupted, summary.State);
        Assert.Equal(1, summary.AffectedItemCount);
        Assert.Equal(Path.Combine(run, "journal.tsv"), summary.JournalPath);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task DiscoveryClassifiesJournalLessOrganizeAndSyncQuarantines()
    {
        using var temp = new TempDirectory();
        string incoming = temp.Directory("incoming");
        string mirror = temp.Directory("mirror");
        temp.Directory("incoming.SortDownloads-quarantine", "20260715-120000000");
        temp.Directory("mirror.CrossSyncMusic-quarantine", "20260715-130000000");

        var result = await new OperationJournalService().DiscoverAsync([incoming, mirror]);

        Assert.Equal(2, result.Runs.Count);
        Assert.Contains(result.Runs, run =>
            run.ToolName == "SortDownloads" && run.Kind == OperationJournalKind.Organize &&
            run.State == OperationJournalState.Unknown && run.JournalPath is null);
        Assert.Contains(result.Runs, run =>
            run.ToolName == "CrossSyncMusic" && run.Kind == OperationJournalKind.Sync &&
            run.State == OperationJournalState.Unknown);
    }

    [Fact]
    public async Task DiscoveryGroupsReviewedChangeParticipantsAndBrowsesThemAsOneTransaction()
    {
        using var temp = new TempDirectory();
        string firstRoot = temp.Directory("first");
        string secondRoot = temp.Directory("second");
        string firstRun = temp.Directory(
            "first.MusicLibraryManager-recovery",
            "20260101-000000000");
        string secondRun = temp.Directory(
            "second.MusicLibraryManager-recovery",
            "20260101-000000000");
        string firstOutput =
            Path.Combine(firstRoot, "one.flac");
        string secondOutput =
            Path.Combine(secondRoot, "two.flac");
        File.WriteAllText(firstOutput, "one");
        File.WriteAllText(secondOutput, "two");
        string firstJournal =
            WriteCreatedJournal(
                firstRun,
                firstOutput);
        string secondJournal =
            WriteCreatedJournal(
                secondRun,
                secondOutput);
        Guid id = Guid.NewGuid();
        DateTimeOffset created =
            DateTimeOffset.UtcNow.AddDays(-100);
        string manifest = Path.Combine(
            firstRun,
            $"reviewed-change-v2-{id:N}.tsv");
        File.WriteAllLines(
            manifest,
            [
                $"BEGIN\t2\t{id:N}\t" +
                created.UtcDateTime.Ticks,
                $"PARTICIPANT\t0\t{firstJournal}",
                $"PARTICIPANT\t1\t{secondJournal}",
                $"APPLIED\t0\t{firstJournal}",
                $"APPLIED\t1\t{secondJournal}",
                $"COMMIT\t{id:N}",
            ]);
        var service =
            new OperationJournalService();

        OperationJournalDiscoveryResult discovery =
            await service.DiscoverAsync(
                [firstRoot, secondRoot]);

        OperationJournalSummary summary =
            Assert.Single(discovery.Runs);
        Assert.Equal(
            OperationJournalKind.ReviewedChange,
            summary.Kind);
        Assert.Equal(
            OperationJournalState.Completed,
            summary.State);
        Assert.Equal(manifest, summary.JournalPath);
        Assert.Equal(2, summary.AffectedItemCount);
        ReviewedChangeTransactionSummary transaction =
            Assert.IsType<
                ReviewedChangeTransactionSummary>(
                summary.ReviewedChangeTransaction);
        Assert.Equal(id, transaction.Id);
        Assert.Equal(2, transaction.ParticipantCount);
        Assert.Equal(2, transaction.AppliedParticipantCount);

        OperationBrowseResult browse =
            await service.BrowseAsync(summary);
        Assert.Equal(2, browse.Entries.Count);
        Assert.All(
            browse.Entries,
            entry => Assert.Equal(
                OperationEntryKind.Created,
                entry.Kind));
        OperationRestorePlan restore =
            await service.PreviewRestoreAsync(
                summary,
                browse.Entries);
        Assert.Equal(2, restore.Actions.Count);

        OperationPurgePlan purge =
            await service.PreviewPurgeAsync(
                [summary],
                30,
                DateTimeOffset.UtcNow);
        Assert.Equal(2, purge.Runs.Count);
        Assert.Contains(
            purge.Runs,
            run => run.Run.RunPath == firstRun);
        Assert.Contains(
            purge.Runs,
            run => run.Run.RunPath == secondRun);
    }

    [Theory]
    [InlineData(
        "ROLLED_BACK",
        OperationJournalState.RolledBack)]
    [InlineData(
        "ROLLBACK_FAILED",
        OperationJournalState.Interrupted)]
    [InlineData(
        null,
        OperationJournalState.Interrupted)]
    public async Task ReviewedCoordinatorTerminalControlsGroupedState(
        string? terminal,
        OperationJournalState expected)
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("library");
        string run = temp.Directory(
            "library.MusicLibraryManager-recovery",
            "20260101-000000000");
        string output =
            Path.Combine(root, "track.flac");
        File.WriteAllText(output, "audio");
        string journal =
            WriteCreatedJournal(run, output);
        Guid id = Guid.NewGuid();
        var lines = new List<string>
        {
            $"BEGIN\t2\t{id:N}\t" +
            DateTime.UtcNow.Ticks,
            $"PARTICIPANT\t0\t{journal}",
            $"APPLIED\t0\t{journal}",
        };
        if (terminal is not null)
            lines.Add(
                $"{terminal}\t{id:N}");
        File.WriteAllLines(
            Path.Combine(
                run,
                $"reviewed-change-v2-{id:N}.tsv"),
            lines);

        OperationJournalSummary summary =
            Assert.Single(
                (await new OperationJournalService()
                    .DiscoverAsync([root]))
                .Runs);

        Assert.Equal(expected, summary.State);
        Assert.NotNull(
            summary.ReviewedChangeTransaction);
    }

    [Theory]
    [InlineData("COMMIT", OperationJournalState.Completed)]
    [InlineData("ROLLED_BACK", OperationJournalState.RolledBack)]
    [InlineData(null, OperationJournalState.Interrupted)]
    public async Task DiscoveryReadsUpdateCarCardTerminalState(
        string? terminal,
        OperationJournalState expected)
    {
        using var temp = new TempDirectory();
        string device = temp.Directory("card");
        string run = temp.Directory("card.UpdateCarCard-recovery", "20260715-140000000");
        var lines = new List<string> { "MOVE\tZmlyc3Q=\tc2Vjb25k", "CREATE\tdGhpcmQ=\t" };
        if (terminal is not null) lines.Add(terminal);
        File.WriteAllLines(Path.Combine(run, "journal.tsv"), lines);

        var result = await new OperationJournalService().DiscoverAsync([device]);

        var summary = Assert.Single(result.Runs);
        Assert.Equal(OperationJournalKind.Device, summary.Kind);
        Assert.Equal(expected, summary.State);
        Assert.Equal(2, summary.AffectedItemCount);
    }

    [Fact]
    public async Task BrowseIngestMapsJournalAndPhysicalFoldersToTheOriginalHierarchy()
    {
        using var temp = new TempDirectory();
        string source = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-150000000");
        string albumDirectory = Path.Combine(run, "Artist", "Album");
        Directory.CreateDirectory(albumDirectory);
        string quarantined = Path.Combine(albumDirectory, "one.flac");
        File.WriteAllText(quarantined, "audio");
        string original = Path.Combine(source, "Artist", "Album", "one.flac");
        string stagedDelete = Path.Combine(albumDirectory, "two.flac");
        File.WriteAllText(stagedDelete, "audio");
        string stagedOriginal = Path.Combine(source, "Artist", "Album", "two.flac");
        string journal = Path.Combine(run, "journal.tsv");
        File.WriteAllLines(journal,
        [
            "BEGIN\talbum",
            $"QUARANTINE\talbum\t{original}\t{quarantined}",
            $"PLAN_DELETE\talbum\t{stagedOriginal}",
            "COMMIT\talbum",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([source])).Runs);

        var browse = await service.BrowseAsync(summary);

        Assert.Equal(source, browse.OriginalRoot);
        var file = Assert.Single(browse.Entries, entry => entry.OriginalPath == original);
        Assert.Equal(original, file.OriginalPath);
        Assert.Equal(Path.Combine("Artist", "Album", "one.flac"), file.RelativePath);
        Assert.Equal(OperationEntryKind.Quarantined, file.Kind);
        Assert.True(file.Exists);
        var recoveredPlan = Assert.Single(browse.Entries, entry => entry.OriginalPath == stagedOriginal);
        Assert.Equal(stagedDelete, recoveredPlan.CurrentPath);
        Assert.Equal(OperationEntryKind.Quarantined, recoveredPlan.Kind);
        Assert.Contains(browse.Entries, entry => entry.IsDirectory && entry.RelativePath == "Artist");
        Assert.Contains(browse.Entries, entry => entry.IsDirectory &&
            entry.RelativePath == Path.Combine("Artist", "Album"));
    }

    [Fact]
    public async Task BrowseFolderOnlySyncQuarantineReconstructsOriginalPaths()
    {
        using var temp = new TempDirectory();
        string target = temp.Directory("mirror");
        string run = temp.Directory("mirror.CrossSyncMusic-quarantine", "20260715-160000000");
        string current = Path.Combine(run, "Artist", "song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "audio");
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([target])).Runs);

        var browse = await service.BrowseAsync(summary);

        var file = Assert.Single(browse.Entries, entry => !entry.IsDirectory);
        Assert.Equal(Path.Combine(target, "Artist", "song.flac"), file.OriginalPath);
        Assert.Equal(current, file.CurrentPath);
        Assert.Equal(OperationEntryKind.Quarantined, file.Kind);
    }

    [Fact]
    public async Task BrowseOrganizeJournalShowsSourceToDestinationMove()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("music");
        string run = temp.Directory("music.OrganizeFiles-recovery", "20260715-170000000");
        string source = Path.Combine(root, "old.flac");
        string destination = Path.Combine(root, "Artist", "Album", "01 Song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "audio");
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            "BEGIN\tORGANIZE",
            $"PLAN_MOVE\tORGANIZE\t{source}\t{destination}",
            $"MOVE\tORGANIZE\t{source}\t{destination}",
            "COMMIT\tORGANIZE",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([root])).Runs);

        var entry = Assert.Single((await service.BrowseAsync(summary)).Entries);

        Assert.Equal(source, entry.OriginalPath);
        Assert.Equal(destination, entry.CurrentPath);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
        Assert.True(entry.Exists);
    }

    [Fact]
    public async Task BrowseDeviceJournalPreservesBackupMoveWhenAReplacementWasCreated()
    {
        using var temp = new TempDirectory();
        string device = temp.Directory("card");
        string run = temp.Directory("card.UpdateCarCard-recovery", "20260715-180000000");
        string original = Path.Combine(device, "syncdb.xml");
        string backup = Path.Combine(run, "data", "backup-syncdb.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(original, "new");
        File.WriteAllText(backup, "old");
        static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            $"MOVE\t{Encode(original)}\t{Encode(backup)}",
            $"CREATE\t{Encode(original)}\t",
            "COMMIT",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([device])).Runs);

        var entry = Assert.Single((await service.BrowseAsync(summary)).Entries);

        Assert.Equal(original, entry.OriginalPath);
        Assert.Equal(backup, entry.CurrentPath);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
    }

    [Fact]
    public async Task RestorePreservesDestinationCollisionAndMovesQuarantineBack()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-190000000");
        string source = Path.Combine(run, "song.flac");
        string destination = Path.Combine(root, "song.flac");
        File.WriteAllText(source, "quarantined original");
        File.WriteAllText(destination, "new collision");
        var summary = Summary("IngestMusic", OperationJournalKind.Ingest, run);
        var entry = Entry(source, destination, OperationEntryKind.Quarantined);
        var reindex = new RecordingReindexService(
            source);
        var service = new OperationJournalService(
            reindex: reindex);

        var plan = await service.PreviewRestoreAsync(summary, [entry]);
        var result = await service.ApplyRestoreAsync(plan);
        IReadOnlyList<OperationIssue> postCommitIssues =
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion;

        Assert.Equal(1, plan.CollisionCount);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal("quarantined original", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
        var action = Assert.Single(plan.Actions);
        Assert.Equal("new collision", File.ReadAllText(action.CollisionBackupPath));
        Assert.Equal("CONSUMED\tRESTORE", File.ReadLines(plan.RestoreJournalPath).Last());
        Assert.Equal([destination], reindex.ReindexedPaths);
        Assert.Equal([source], reindex.RemovedPaths);
        Assert.All(
            reindex.Tokens,
            token => Assert.False(token.CanBeCanceled));
        Assert.Empty(result.Issues);
        Assert.Empty(postCommitIssues);
    }

    [Fact]
    public async Task CommittedRestoreReportsCacheFailuresAsWarnings()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory(
            "incoming.IngestMusic-quarantine",
            "20260715-193000000");
        string source = Path.Combine(run, "song.flac");
        string destination = Path.Combine(root, "song.flac");
        File.WriteAllText(source, "quarantined original");
        var reindex = new RecordingReindexService(
            source)
        {
            ReindexError =
                new IOException("refresh failed"),
            RemoveError =
                new IOException("remove failed"),
        };
        var service = new OperationJournalService(
            reindex: reindex);
        OperationRestorePlan plan =
            await service.PreviewRestoreAsync(
                Summary(
                    "IngestMusic",
                    OperationJournalKind.Ingest,
                    run),
                [
                    Entry(
                        source,
                        destination,
                        OperationEntryKind.Quarantined),
                ]);

        OperationRestoreResult result =
            await service.ApplyRestoreAsync(plan);
        IReadOnlyList<OperationIssue> postCommitIssues =
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion;

        Assert.Equal("quarantined original", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
        // A failed destination refresh deliberately retains the old tracked
        // source membership instead of removing the last known catalog row.
        Assert.Single(postCommitIssues);
        Assert.All(
            postCommitIssues,
            issue =>
            {
                Assert.Equal(
                    "restore.catalog-refresh-failed",
                    issue.Code);
                Assert.Equal(
                    OperationIssueSeverity.Warning,
                    issue.Severity);
            });
    }

    [Fact]
    public async Task DirectRestorePreservesUntrackedCatalogMembership()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory(
            "incoming.IngestMusic-quarantine",
            "20260715-194000000");
        string source = Path.Combine(run, "session-only.flac");
        string destination =
            Path.Combine(root, "session-only.flac");
        File.WriteAllText(source, "session audio");
        var reindex = new RecordingReindexService();
        var service = new OperationJournalService(
            reindex: reindex);
        OperationRestorePlan plan =
            await service.PreviewRestoreAsync(
                Summary(
                    "IngestMusic",
                    OperationJournalKind.Ingest,
                    run),
                [
                    Entry(
                        source,
                        destination,
                        OperationEntryKind.Quarantined),
                ]);

        OperationRestoreResult result =
            await service.ApplyRestoreAsync(plan);

        Assert.Equal("session audio", File.ReadAllText(destination));
        Assert.Equal(
            OperationRestoreTransitionState.Consumed,
            result.TransitionState);
        Assert.Null(result.PostCommitReconciliation);
        Assert.Empty(result.Issues);
        Assert.Empty(reindex.ReindexedPaths);
        Assert.Empty(reindex.RemovedPaths);
        Assert.Equal(
            [source, destination],
            reindex.IndexedQueries);
    }

    [Fact]
    public async Task DirectoryRestoreReconcilesTrackedDescendantsWithoutScanningDirectory()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory(
            "incoming.IngestMusic-quarantine",
            "20260715-195000000");
        string sourceDirectory =
            temp.Directory(
                "incoming.IngestMusic-quarantine",
                "20260715-195000000",
                "album");
        string destinationDirectory =
            Path.Combine(root, "album");
        string trackedSource =
            Path.Combine(sourceDirectory, "tracked.flac");
        string untrackedSource =
            Path.Combine(sourceDirectory, "session-only.flac");
        File.WriteAllText(trackedSource, "tracked");
        File.WriteAllText(untrackedSource, "session");
        var reindex = new RecordingReindexService(
            trackedSource);
        var service = new OperationJournalService(
            reindex: reindex);
        var entry = new OperationFileEntry(
            destinationDirectory,
            sourceDirectory,
            "album",
            OperationEntryKind.Quarantined,
            true,
            true);
        OperationRestorePlan plan =
            await service.PreviewRestoreAsync(
                Summary(
                    "IngestMusic",
                    OperationJournalKind.Ingest,
                    run),
                [entry]);

        OperationRestoreResult result =
            await service.ApplyRestoreAsync(plan);
        Assert.Empty(
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion);

        string trackedDestination =
            Path.Combine(destinationDirectory, "tracked.flac");
        Assert.Equal(
            [trackedDestination],
            reindex.ReindexedPaths);
        Assert.Equal(
            [trackedSource],
            reindex.RemovedPaths);
        Assert.DoesNotContain(
            destinationDirectory,
            reindex.ReindexedPaths);
        Assert.True(File.Exists(
            Path.Combine(
                destinationDirectory,
                "session-only.flac")));
    }

    [Fact]
    public async Task CommittedRestoreReturnsBeforeBlockedCatalogWorkAndIgnoresLaterCancellation()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory(
            "incoming.IngestMusic-quarantine",
            "20260715-196000000");
        string source = Path.Combine(run, "song.flac");
        string destination = Path.Combine(root, "song.flac");
        File.WriteAllText(source, "audio");
        var reindex = new BlockingReindexService(source);
        var service = new OperationJournalService(
            reindex: reindex);
        OperationRestorePlan plan =
            await service.PreviewRestoreAsync(
                Summary(
                    "IngestMusic",
                    OperationJournalKind.Ingest,
                    run),
                [
                    Entry(
                        source,
                        destination,
                        OperationEntryKind.Quarantined),
                ]);
        using var cts =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

        Task<OperationRestoreResult> applying =
            service.ApplyRestoreAsync(
                plan,
                ct: cts.Token);
        await reindex.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        OperationRestoreResult result =
            await applying.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

        Assert.Equal(
            OperationRestoreTransitionState.Committed,
            result.TransitionState);
        Assert.False(
            Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion.IsCompleted);
        Assert.Equal("audio", File.ReadAllText(destination));
        cts.Cancel();
        reindex.Release.TrySetResult(true);
        Assert.Empty(
            await result.PostCommitReconciliation!
                .Completion.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
        Assert.Equal(
            "CONSUMED\tRESTORE",
            File.ReadLines(
                    plan.RestoreJournalPath)
                .Last());
        Assert.All(
            reindex.Tokens,
            token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task RestartReconciliationReturnsCatalogWarningsAndRetriesFromCommit()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory(
            "incoming.IngestMusic-quarantine",
            "20260715-197000000");
        string source = Path.Combine(run, "song.flac");
        string destination = Path.Combine(root, "song.flac");
        File.WriteAllText(source, "audio");
        var firstReindex = new RecordingReindexService(source)
        {
            ReindexError = new IOException("index busy"),
        };
        var firstService = new OperationJournalService(
            reindex: firstReindex);
        OperationRestorePlan plan =
            await firstService.PreviewRestoreAsync(
                Summary(
                    "IngestMusic",
                    OperationJournalKind.Ingest,
                    run),
                [
                    Entry(
                        source,
                        destination,
                        OperationEntryKind.Quarantined),
                ]);
        OperationRestoreResult applied =
            await firstService.ApplyRestoreAsync(plan);
        OperationIssue firstIssue = Assert.Single(
            await applied.PostCommitReconciliation!
                .Completion);
        Assert.Equal(
            "restore.catalog-refresh-failed",
            firstIssue.Code);
        Assert.Equal(
            "COMMIT\tRESTORE",
            File.ReadLines(plan.RestoreJournalPath).Last());

        var retryFailure = new RecordingReindexService(source)
        {
            ReindexError = new IOException("still busy"),
        };
        var restartService = new OperationJournalService(
            reindex: retryFailure);
        OperationRestoreReconciliationResult failedRetry =
            await restartService
                .ReconcileRestoreBatchDetailedAsync(
                    [plan.RestoreJournalPath],
                    ct: TestContext.Current
                        .CancellationToken);

        Assert.Equal(
            OperationRestoreTransitionState.Committed,
            failedRetry.State);
        Assert.Single(failedRetry.Issues);
        Assert.Equal(
            "COMMIT\tRESTORE",
            File.ReadLines(plan.RestoreJournalPath).Last());

        var successfulReindex =
            new RecordingReindexService(source);
        var finalService = new OperationJournalService(
            reindex: successfulReindex);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        OperationRestoreReconciliationResult final =
            await finalService
                .ReconcileRestoreBatchDetailedAsync(
                    [plan.RestoreJournalPath],
                    ct: canceled.Token);

        Assert.Equal(
            OperationRestoreTransitionState.Consumed,
            final.State);
        Assert.Empty(final.Issues);
        Assert.Equal([destination],
            successfulReindex.ReindexedPaths);
        Assert.Equal([source],
            successfulReindex.RemovedPaths);
    }

    [Fact]
    public async Task RestoreRejectsAChangedSourceBeforeMovingAnything()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-200000000");
        string source = Path.Combine(run, "song.flac");
        string destination = Path.Combine(root, "song.flac");
        File.WriteAllText(source, "original");
        var reindex = new RecordingReindexService(
            source);
        var service = new OperationJournalService(
            reindex: reindex);
        var plan = await service.PreviewRestoreAsync(
            Summary("IngestMusic", OperationJournalKind.Ingest, run),
            [Entry(source, destination, OperationEntryKind.Quarantined)]);
        File.AppendAllText(source, " changed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRestoreAsync(plan));

        Assert.Contains("changed since preview", error.Message);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(plan.RestoreJournalPath));
        Assert.Empty(reindex.ReindexedPaths);
        Assert.Empty(reindex.RemovedPaths);
    }

    [Fact]
    public async Task RestoreRollsBackEarlierMovesWhenALaterActionFails()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-210000000");
        string firstSource = Path.Combine(run, "a.flac");
        string secondSource = Path.Combine(run, "b.flac");
        File.WriteAllText(firstSource, "first");
        File.WriteAllText(secondSource, "second");
        string firstDestination = Path.Combine(root, "a.flac");
        string blocker = Path.Combine(root, "z-blocker");
        File.WriteAllText(blocker, "not a directory");
        string secondDestination = Path.Combine(blocker, "b.flac");
        var service = new OperationJournalService();
        var plan = await service.PreviewRestoreAsync(
            Summary("IngestMusic", OperationJournalKind.Ingest, run),
            [
                Entry(firstSource, firstDestination, OperationEntryKind.Quarantined),
                Entry(secondSource, secondDestination, OperationEntryKind.Quarantined),
            ]);

        await Assert.ThrowsAnyAsync<IOException>(() => service.ApplyRestoreAsync(plan));

        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        Assert.False(File.Exists(firstDestination));
        Assert.Equal("ROLLBACK\tRESTORE", File.ReadLines(plan.RestoreJournalPath).Last());
    }

    [Fact]
    public async Task RestoreRemovesAnUnchangedReversibleCreatedOutput()
    {
        using var temp = new TempDirectory();
        string run = temp.Directory("transcode-recovery");
        string output = Path.Combine(temp.Path, "song.flac");
        File.WriteAllText(output, "generated audio");
        var info = new FileInfo(output);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output)));
        string journal = Path.Combine(run, "journal.tsv");
        File.WriteAllLines(journal,
        [
            "BEGIN\ttranscode",
            $"CREATE_REVERSIBLE\t1\t{output}\t{info.Length}\t{hash}\t" +
            $"{info.LastWriteTimeUtc.Ticks}\t{(int)info.Attributes}",
            "COMMIT\ttranscode",
        ]);
        var summary = new OperationJournalSummary(
            "MusicLibraryManager",
            OperationJournalKind.Other,
            OperationJournalState.Completed,
            run,
            journal,
            DateTimeOffset.UtcNow,
            1);
        var reindex = new RecordingReindexService(
            output);
        var service = new OperationJournalService(
            reindex: reindex);

        OperationFileEntry entry = Assert.Single(
            (await service.BrowseAsync(summary)).Entries);
        OperationRestorePlan plan = await service.PreviewRestoreAsync(summary, [entry]);
        OperationRestoreResult result = await service.ApplyRestoreAsync(plan);
        IReadOnlyList<OperationIssue> postCommitIssues =
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion;

        Assert.Equal(OperationEntryKind.Created, entry.Kind);
        Assert.Equal(
            OperationRestoreDisposition.RemoveCreatedOutput,
            Assert.Single(plan.Actions).Disposition);
        Assert.Equal(1, result.RestoredCount);
        Assert.False(File.Exists(output));
        Assert.Equal("CONSUMED\tRESTORE", File.ReadLines(plan.RestoreJournalPath).Last());
        Assert.Empty(reindex.ReindexedPaths);
        Assert.Equal([output], reindex.RemovedPaths);
        Assert.All(
            reindex.Tokens,
            token => Assert.False(token.CanBeCanceled));
        Assert.Empty(postCommitIssues);
    }

    [Fact]
    public async Task EditHistorySurfacesRestoreCacheWarningsAndResetsThemPerAttempt()
    {
        using var temp = new TempDirectory();
        string run = temp.Directory("history-recovery");
        string output = Path.Combine(
            temp.Path,
            "history-output.flac");
        File.WriteAllText(output, "generated audio");
        string journal = WriteCreatedJournal(run, output);
        var reindex = new RecordingReindexService(
            output)
        {
            RemoveError =
                new IOException("catalog removal failed"),
        };
        var journals = new OperationJournalService(
            reindex: reindex);
        var history = new EditHistoryService(
            new AppSettings(
                Path.Combine(temp.Path, "settings.json")),
            journals);
        history.Record(new(
            Guid.NewGuid(),
            "Generated output",
            DateTimeOffset.UtcNow,
            [journal],
            [output],
            null));

        int restored = await history.UndoLatestAsync(
            ct: TestContext.Current.CancellationToken);
        _ = await history.LastUndoReconciliation;

        Assert.Equal(1, restored);
        Assert.True(history.ReconcilesInternalCatalogOnUndo);
        OperationIssue warning =
            Assert.Single(history.LastUndoIssues);
        Assert.Equal(
            "restore.catalog-refresh-failed",
            warning.Code);
        Assert.Equal(output, warning.Path);
        Assert.Equal([output], reindex.RemovedPaths);

        reindex.RemoveError = null;
        Assert.Equal(
            0,
            await history.UndoLatestAsync(
                ct: TestContext.Current.CancellationToken));
        Assert.Empty(history.LastUndoIssues);
        IEditHistoryService legacy =
            new LegacyEditHistoryService();
        Assert.False(
            legacy.ReconcilesInternalCatalogOnUndo);
        Assert.Empty(legacy.LastUndoIssues);
    }

    [Fact]
    public async Task RestoreRefusesToRemoveAReversibleOutputWhoseHashChanged()
    {
        using var temp = new TempDirectory();
        string run = temp.Directory("transcode-recovery");
        string output = Path.Combine(temp.Path, "song.flac");
        File.WriteAllText(output, "AAAA");
        var info = new FileInfo(output);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output)));
        string journal = Path.Combine(run, "journal.tsv");
        File.WriteAllLines(journal,
        [
            "BEGIN\ttranscode",
            $"CREATE_REVERSIBLE\t1\t{output}\t{info.Length}\t{hash}\t" +
            $"{info.LastWriteTimeUtc.Ticks}\t{(int)info.Attributes}",
            "COMMIT\ttranscode",
        ]);
        var summary = new OperationJournalSummary(
            "MusicLibraryManager",
            OperationJournalKind.Other,
            OperationJournalState.Completed,
            run,
            journal,
            DateTimeOffset.UtcNow,
            1);
        var service = new OperationJournalService();
        OperationFileEntry entry = Assert.Single(
            (await service.BrowseAsync(summary)).Entries);
        OperationRestorePlan plan = await service.PreviewRestoreAsync(summary, [entry]);
        File.WriteAllText(output, "BBBB");
        File.SetLastWriteTimeUtc(output, plan.Actions[0].SourceSnapshot.LastWriteTimeUtc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyRestoreAsync(plan));

        Assert.Contains("generated output changed", error.Message);
        Assert.Equal("BBBB", File.ReadAllText(output));
        Assert.False(File.Exists(plan.RestoreJournalPath));
    }

    [Fact]
    public async Task PurgePreviewFiltersByAgeProtectsInterruptedRunsAndCountsRestoreBackups()
    {
        using var temp = new TempDirectory();
        string container = temp.Directory("incoming.IngestMusic-quarantine");
        string eligiblePath = temp.Directory("incoming.IngestMusic-quarantine", "20260101-000000000");
        string interruptedPath = temp.Directory("incoming.IngestMusic-quarantine", "20260102-000000000");
        string newerPath = temp.Directory("incoming.IngestMusic-quarantine", "20260710-000000000");
        File.WriteAllText(Path.Combine(eligiblePath, "song.flac"), "audio");
        string restore = Path.Combine(eligiblePath, ".MusicLibrary.App-restore", "one", "collisions");
        Directory.CreateDirectory(restore);
        File.WriteAllText(Path.Combine(restore, "existing.flac"), "collision");
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        OperationJournalSummary Make(string path, OperationJournalState state, DateTimeOffset created) =>
            new("IngestMusic", OperationJournalKind.Ingest, state, path, null, created, null);
        var service = new OperationJournalService();

        var plan = await service.PreviewPurgeAsync(
            [
                Make(eligiblePath, OperationJournalState.Completed, now.AddDays(-100)),
                Make(interruptedPath, OperationJournalState.Interrupted, now.AddDays(-100)),
                Make(newerPath, OperationJournalState.Completed, now.AddDays(-5)),
            ], 30, now);

        var run = Assert.Single(plan.Runs);
        Assert.Equal(eligiblePath, run.Run.RunPath);
        Assert.Equal(2, plan.FileCount);
        Assert.Equal(1, plan.RestoreBackupFileCount);
        Assert.Equal(1, plan.ProtectedInterruptedCount);
        Assert.Equal(1, plan.NewerCount);
        Assert.StartsWith(Path.Combine(container, ".MusicLibrary.App-purge-staging"), run.StagingPath);
    }

    [Fact]
    public async Task PurgeRejectsAChangedManifestBeforeMovingAnyRun()
    {
        using var temp = new TempDirectory();
        string first = temp.Directory("incoming.IngestMusic-quarantine", "20260101-000000000");
        string second = temp.Directory("incoming.IngestMusic-quarantine", "20260102-000000000");
        File.WriteAllText(Path.Combine(first, "a.flac"), "a");
        File.WriteAllText(Path.Combine(second, "b.flac"), "b");
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        OperationJournalSummary Make(string path) =>
            new("IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
                path, null, old, null);
        var service = new OperationJournalService();
        var plan = await service.PreviewPurgeAsync([Make(first), Make(second)], 30);
        File.WriteAllText(Path.Combine(second, "new.flac"), "changed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPurgeAsync(plan));

        Assert.Contains("changed since purge preview", error.Message);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.All(plan.Runs, run => Assert.False(Directory.Exists(run.StagingPath)));
    }

    [Fact]
    public async Task PurgeDeletesTheReviewedRunAndDiscoveryIgnoresItsStagingContainer()
    {
        using var temp = new TempDirectory();
        string source = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260101-000000000");
        File.WriteAllText(Path.Combine(run, "song.flac"), "audio");
        var summary = new OperationJournalSummary(
            "IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
            run, null, DateTimeOffset.UtcNow.AddDays(-100), null);
        var service = new OperationJournalService();
        var plan = await service.PreviewPurgeAsync([summary], 30);

        var result = await service.ApplyPurgeAsync(plan);

        Assert.Equal(1, result.RunsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(Directory.Exists(run));
        Assert.Empty((await service.DiscoverAsync([source])).Runs);
    }

    private static OperationJournalSummary Summary(
        string tool,
        OperationJournalKind kind,
        string run) =>
        new(tool, kind, OperationJournalState.Completed, run, null, DateTimeOffset.UtcNow, null);

    private static OperationFileEntry Entry(
        string source,
        string destination,
        OperationEntryKind kind) =>
        new(destination, source, Path.GetFileName(destination), kind, true, false);

    private static string WriteCreatedJournal(
        string run,
        string output)
    {
        var info = new FileInfo(output);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256
                .HashData(
                    File.ReadAllBytes(output)));
        string journal =
            Path.Combine(run, "journal.tsv");
        File.WriteAllLines(
            journal,
            [
                "BEGIN\ttranscode",
                $"CREATE_REVERSIBLE\t1\t{output}\t" +
                $"{info.Length}\t{hash}\t" +
                $"{info.LastWriteTimeUtc.Ticks}\t" +
                $"{(int)info.Attributes}",
                "COMMIT\ttranscode",
            ]);
        return journal;
    }

    private sealed class RecordingReindexService(
        params string[] indexedPaths) :
        IReindexService
    {
        public Exception? ReindexError { get; set; }
        public Exception? RemoveError { get; set; }
        public List<string> IndexedQueries { get; } = [];
        public List<string> ReindexedPaths { get; } = [];
        public List<string> RemovedPaths { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        private readonly HashSet<string> _indexedPaths =
            indexedPaths.ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        public Task<bool> IsIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            IndexedQueries.Add(path);
            return Task.FromResult(
                _indexedPaths.Contains(path));
        }

        public Task ReindexFileAsync(
            string path,
            CancellationToken ct = default)
        {
            ReindexedPaths.Add(path);
            Tokens.Add(ct);
            return ReindexError is null
                ? Task.CompletedTask
                : Task.FromException(ReindexError);
        }

        public Task RemoveIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            RemovedPaths.Add(path);
            Tokens.Add(ct);
            return RemoveError is null
                ? Task.CompletedTask
                : Task.FromException(RemoveError);
        }
    }

    private sealed class BlockingReindexService(
        params string[] indexedPaths) :
        IReindexService
    {
        private readonly HashSet<string> _indexedPaths =
            indexedPaths.ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CancellationToken> Tokens { get; } = [];

        public Task<bool> IsIndexedFileAsync(
            string path,
            CancellationToken ct = default) =>
            Task.FromResult(_indexedPaths.Contains(path));

        public async Task ReindexFileAsync(
            string path,
            CancellationToken ct = default)
        {
            Tokens.Add(ct);
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
        }

        public Task RemoveIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return Task.CompletedTask;
        }
    }

    private sealed class LegacyEditHistoryService :
        IEditHistoryService
    {
        public IReadOnlyList<EditHistoryEntry> Entries => [];
        public IReadOnlyList<EditHistoryEntry> RedoEntries => [];
        public bool CanUndo => false;
        public bool CanRedo => false;

        public void Record(EditHistoryEntry entry)
        {
        }

        public Task<int> UndoLatestAsync(
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "operation-journal-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);

        public string Directory(params string[] parts)
        {
            string path = parts.Aggregate(Path, System.IO.Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
