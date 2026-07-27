using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ReviewedFileOperationServiceTests
{
    [Fact]
    public async Task CopyPreviewResolvesClaimedNamesAndAppliesReviewedPlan()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine("source-a", "song.flac"),
            "first");
        string second = temp.File(
            Path.Combine("source-b", "song.flac"),
            "second");
        string destination = temp.Directory("copies");
        var reindex = new RecordingReindexService();
        ReviewedFileOperationService service =
            CreateService(temp, reindex: reindex);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Copy,
                destination,
                CollisionPolicy:
                    ReviewedFileCollisionPolicy.Suffix));

        Assert.True(plan.CanApply);
        Assert.Equal(2, plan.MutationPlan.Actions.Count);
        Assert.Equal(
            Path.Combine(destination, "song.flac"),
            plan.Items[0].DestinationPath);
        Assert.Equal(
            Path.Combine(destination, "song_2.flac"),
            plan.Items[1].DestinationPath);

        FileMutationSummary result =
            await service.ApplyAsync(plan);
        Assert.Empty(
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion);

        Assert.Equal(2, result.Copied);
        Assert.Equal("first", File.ReadAllText(
            Path.Combine(destination, "song.flac")));
        Assert.Equal("second", File.ReadAllText(
            Path.Combine(destination, "song_2.flac")));
        Assert.NotNull(result.JournalPath);
        Assert.Equal(
            [
                $"reindex:{Path.Combine(destination, "song.flac")}",
                $"reindex:{Path.Combine(destination, "song_2.flac")}",
            ],
            reindex.Calls);
    }

    [Fact]
    public async Task RenameUsesMoveJournalAndCanBeRestored()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("album", "old.flac"),
            "audio");
        var reindex = new RecordingReindexService();
        ReviewedFileOperationService service =
            CreateService(temp, reindex: reindex);
        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Rename,
                FileNameTemplate:
                    "new{Extension}"));
        string destination =
            Assert.Single(plan.Items).DestinationPath!;

        FileMutationSummary result =
            await service.ApplyAsync(plan);
        Assert.Empty(
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion);

        Assert.Equal(1, result.Moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal(
            [
                $"reindex:{destination}",
                $"remove:{source}",
            ],
            reindex.Calls);
        Assert.Contains(
            $"MOVE\tFILE\t{source}\t{destination}",
            File.ReadAllLines(result.JournalPath!));

        string run = Path.GetDirectoryName(
            result.JournalPath!)!;
        var journal = new OperationJournalService();
        var summary = new OperationJournalSummary(
            "MusicLibraryManager",
            OperationJournalKind.Other,
            OperationJournalState.Completed,
            run,
            result.JournalPath,
            DateTimeOffset.UtcNow,
            1);
        OperationFileEntry entry = Assert.Single(
            (await journal.BrowseAsync(summary)).Entries);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
        OperationRestorePlan restore =
            await journal.PreviewRestoreAsync(
                summary,
                [entry]);

        await journal.ApplyRestoreAsync(restore);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task QuarantineMovesToReviewedFolderAndRemainsRecoverable()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("album", "remove.flac"),
            "audio");
        string quarantine =
            temp.Directory("reviewed-quarantine");
        var reindex = new RecordingReindexService();
        ReviewedFileOperationService service =
            CreateService(temp, reindex: reindex);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Quarantine,
                quarantine));
        FileMutationSummary result =
            await service.ApplyAsync(plan);
        Assert.Empty(
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion);

        Assert.Equal(1, result.Quarantined);
        Assert.False(File.Exists(source));
        Assert.Equal(
            "audio",
            File.ReadAllText(
                Path.Combine(
                    quarantine,
                    "remove.flac")));
        Assert.Contains(
            "QUARANTINE\tSTALE",
            File.ReadAllText(result.JournalPath!));
        Assert.Equal(
            [$"remove:{source}"],
            reindex.Calls);
    }

    [Fact]
    public async Task MoveKeepsUntrackedSourceAndDestinationSessionOnly()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("outside", "song.flac"),
            "audio");
        string destinationDirectory =
            temp.Directory("library");
        var reindex = new RecordingReindexService
        {
            IndexedResult = false,
        };
        ReviewedFileOperationService service =
            CreateService(temp, reindex: reindex);
        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destinationDirectory));
        string destination =
            Assert.Single(plan.Items).DestinationPath!;

        FileMutationSummary result =
            await service.ApplyAsync(plan);
        Assert.Null(result.PostCommitReconciliation);

        Assert.Equal(1, result.Moved);
        Assert.Empty(reindex.Calls);
        Assert.Equal(1, reindex.IsIndexedCalls);
    }

    [Fact]
    public async Task CommittedApplyReturnsBeforeBlockedCatalogRefresh()
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(
            temp.Root,
            "source.flac");
        string destination = Path.Combine(
            temp.Root,
            "destination.flac");
        var executor = new StubExecutor(
            new FileMutationSummary(
                0,
                0,
                0,
                0,
                "journal.tsv",
                [])
            {
                Moved = 1,
            });
        var reindex = new BlockingReindexService();
        ReviewedFileOperationService service =
            CreateService(
                temp,
                executor: executor,
                reindex: reindex);
        ReviewedFileOperationPlan plan = CreatePlan(
            temp,
            Mutation(
                FileMutationKind.Move,
                source,
                destination));
        using var cts =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

        Task<FileMutationSummary> applying =
            service.ApplyAsync(
                plan,
                ct: cts.Token);
        await reindex.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        FileMutationSummary result =
            await applying.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

        PostCommitReconciliationHandle reconciliation =
            Assert.IsType<PostCommitReconciliationHandle>(
                result.PostCommitReconciliation);
        Assert.False(reconciliation.Completion.IsCompleted);
        Assert.Equal(1, executor.ApplyCalls);
        cts.Cancel();
        reindex.Release.TrySetResult(true);
        Assert.Empty(
            await reconciliation.Completion.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.All(
            reindex.Tokens,
            token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task ApplyDeduplicatesCacheEffectsAndRemovesDeletedSources()
    {
        using var temp = new TempDirectory();
        string copied = Path.Combine(
            temp.Root,
            "copies",
            "song.flac");
        string moved = Path.Combine(
            temp.Root,
            "organized",
            "song.flac");
        string moveSource = Path.Combine(
            temp.Root,
            "source",
            "song.flac");
        string deleted = Path.Combine(
            temp.Root,
            "source",
            "delete.flac");
        var executor = new StubExecutor(
            new FileMutationSummary(
                2,
                0,
                0,
                1,
                "journal.tsv",
                [])
            {
                Moved = 2,
            });
        var reindex = new RecordingReindexService();
        ReviewedFileOperationService service =
            CreateService(
                temp,
                executor: executor,
                reindex: reindex);
        ReviewedFileOperationPlan plan = CreatePlan(
            temp,
            Mutation(
                FileMutationKind.Copy,
                "copy-source-a.flac",
                copied),
            Mutation(
                FileMutationKind.Copy,
                "copy-source-b.flac",
                copied),
            Mutation(
                FileMutationKind.Move,
                moveSource,
                moved),
            Mutation(
                FileMutationKind.Move,
                moveSource,
                moved),
            Mutation(
                FileMutationKind.Delete,
                deleted,
                deleted + ".recovery"));

        FileMutationSummary result =
            await service.ApplyAsync(plan);
        Assert.Empty(
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion);

        Assert.Equal(2, result.Copied);
        Assert.Equal(
            [
                $"reindex:{copied}",
                $"reindex:{moved}",
                $"remove:{moveSource}",
                $"remove:{deleted}",
            ],
            reindex.Calls);
    }

    [Fact]
    public async Task CacheFailuresBecomeWarningsAfterCommittedApply()
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(
            temp.Root,
            "source.flac");
        string destination = Path.Combine(
            temp.Root,
            "destination.flac");
        var existingIssue = new OperationIssue(
            "existing",
            OperationIssueSeverity.Information,
            "Existing executor issue.");
        var executor = new StubExecutor(
            new FileMutationSummary(
                0,
                0,
                0,
                0,
                "journal.tsv",
                [existingIssue])
            {
                Moved = 1,
            });
        var reindex = new RecordingReindexService();
        reindex.ReindexFailures.Add(destination);
        reindex.RemoveFailures.Add(source);
        ReviewedFileOperationService service =
            CreateService(
                temp,
                executor: executor,
                reindex: reindex);
        ReviewedFileOperationPlan plan = CreatePlan(
            temp,
            Mutation(
                FileMutationKind.Move,
                source,
                destination));

        FileMutationSummary result =
            await service.ApplyAsync(plan);
        IReadOnlyList<OperationIssue> postCommitIssues =
            await Assert.IsType<
                    PostCommitReconciliationHandle>(
                    result.PostCommitReconciliation)
                .Completion;

        Assert.Equal(1, executor.ApplyCalls);
        Assert.Equal(1, result.Moved);
        Assert.Equal("journal.tsv", result.JournalPath);
        Assert.Same(existingIssue, result.Issues[0]);
        OperationIssue[] warnings = postCommitIssues
            .Where(issue =>
                issue.Code ==
                "file-operation.catalog-refresh-failed")
            .ToArray();
        // Keep the old tracked source row when the destination cannot be
        // refreshed; otherwise a transient refresh failure would lose
        // catalog membership entirely.
        Assert.Single(warnings);
        Assert.All(
            warnings,
            issue => Assert.Equal(
                OperationIssueSeverity.Warning,
                issue.Severity));
        Assert.Equal(
            [destination],
            warnings.Select(issue => issue.Path));
        Assert.Equal(
            [
                $"reindex:{destination}",
            ],
            reindex.Calls);
    }

    [Fact]
    public async Task ExecutorFailureDoesNotStartCacheReconciliation()
    {
        using var temp = new TempDirectory();
        var executor = new StubExecutor(
            error: new InvalidOperationException(
                "Pre-commit validation failed."));
        var reindex = new RecordingReindexService();
        ReviewedFileOperationService service =
            CreateService(
                temp,
                executor: executor,
                reindex: reindex);
        ReviewedFileOperationPlan plan = CreatePlan(
            temp,
            Mutation(
                FileMutationKind.Copy,
                Path.Combine(temp.Root, "source.flac"),
                Path.Combine(temp.Root, "destination.flac")));

        InvalidOperationException error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => service.ApplyAsync(plan));

        Assert.Equal(
            "Pre-commit validation failed.",
            error.Message);
        Assert.Equal(1, executor.ApplyCalls);
        Assert.Empty(reindex.Calls);
    }

    [Fact]
    public async Task CollisionIsBlockingUnlessSuffixingWasReviewed()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("source", "song.flac"),
            "new");
        string destination =
            temp.Directory("destination");
        temp.File(
            Path.Combine("destination", "song.flac"),
            "existing");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan blocked =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination));
        ReviewedFileOperationPlan suffixed =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination,
                CollisionPolicy:
                    ReviewedFileCollisionPolicy.Suffix));

        Assert.False(blocked.CanApply);
        Assert.Contains(
            blocked.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.collision");
        Assert.True(suffixed.CanApply);
        Assert.EndsWith(
            "song_2.flac",
            Assert.Single(suffixed.Items)
                .DestinationPath);

        ReviewedFileOperationPlan destinationIsFile =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Copy,
                Path.Combine(
                    destination,
                    "song.flac")));
        Assert.False(destinationIsFile.CanApply);
        Assert.Contains(
            destinationIsFile.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.destination-file");
    }

    [Fact]
    public async Task ApplyRejectsStaleSourceBeforeMovingAnyFile()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine("source", "first.flac"),
            "first");
        string second = temp.File(
            Path.Combine("source", "second.flac"),
            "second");
        string destination =
            temp.Directory("destination");
        ReviewedFileOperationService service =
            CreateService(temp);
        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Move,
                destination));
        File.AppendAllText(second, " changed");

        InvalidOperationException error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => service.ApplyAsync(plan));

        Assert.Contains(
            "Stale plan",
            error.Message);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(
            Path.Combine(
                destination,
                "first.flac")));
    }

    [Fact]
    public async Task PreserveRelativeLayoutAndTemplateTokensAreDeterministic()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine(
                "source",
                "disc-1",
                "one.flac"),
            "one");
        string second = temp.File(
            Path.Combine(
                "source",
                "disc-2",
                "two.flac"),
            "two");
        string destination =
            temp.Directory("destination");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Copy,
                destination,
                "{Index}-{Name}{Extension}",
                PreserveRelativeLayout: true));

        Assert.Equal(
            Path.Combine(
                destination,
                "disc-1",
                "1-one.flac"),
            plan.Items[0].DestinationPath);
        Assert.Equal(
            Path.Combine(
                destination,
                "disc-2",
                "2-two.flac"),
            plan.Items[1].DestinationPath);
    }

    [Fact]
    public async Task ActiveLibraryPolicyBlocksMovingFromReadOnlyRoot()
    {
        using var temp = new TempDirectory();
        string libraryRoot =
            temp.Directory("library");
        string source = temp.File(
            Path.Combine(
                "library",
                "song.flac"),
            "audio");
        string destination =
            temp.Directory("outside");
        string configuration =
            temp.File(
                "library.xml",
                "<LibraryConfiguration>" +
                $"<IndexTarget Organize=\"false\">{System.Security.SecurityElement.Escape(libraryRoot)}</IndexTarget>" +
                "</LibraryConfiguration>");
        var settings = new AppSettings(
            Path.Combine(
                temp.Root,
                "settings.json"));
        settings.LoadConfig(configuration);
        ReviewedFileOperationService service =
            CreateService(
                temp,
                settings);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination));

        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.source-permission");
    }

    private static ReviewedFileOperationService CreateService(
        TempDirectory temp,
        AppSettings? settings = null,
        IFileMutationPlanExecutor? executor = null,
        IReindexService? reindex = null)
    {
        settings ??= new AppSettings(
            Path.Combine(
                temp.Root,
                "settings.json"));
        executor ??= new FileMutationPlanExecutor(
            new FileMutationCoordinator(),
            settings: settings);
        return new(executor, settings, reindex);
    }

    private static ReviewedFileOperationPlan CreatePlan(
        TempDirectory temp,
        params FileMutationAction[] actions) =>
        new(
            new(
                actions.Select(action => action.SourcePath).ToArray(),
                ReviewedFileOperationKind.Copy),
            [],
            new(
                "MusicLibraryManager",
                temp.Root,
                Path.Combine(temp.Root, "recovery"),
                actions,
                [],
                DateTimeOffset.UtcNow));

    private static FileMutationAction Mutation(
        FileMutationKind kind,
        string source,
        string destination) =>
        new(
            kind,
            source,
            destination,
            null,
            null);

    private sealed class StubExecutor(
        FileMutationSummary? result = null,
        Exception? error = null) :
        IFileMutationPlanExecutor
    {
        public int ApplyCalls { get; private set; }

        public Task<FileMutationSummary> ApplyAsync(
            FileMutationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            return error is null
                ? Task.FromResult(
                    result ??
                    new FileMutationSummary(
                        0,
                        0,
                        0,
                        0,
                        null,
                        []))
                : Task.FromException<FileMutationSummary>(
                    error);
        }
    }

    private sealed class RecordingReindexService :
        IReindexService
    {
        public bool IndexedResult { get; init; } =
            true;
        public int IsIndexedCalls { get; private set; }
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public HashSet<string> ReindexFailures { get; } =
            new(PathComparer);
        public HashSet<string> RemoveFailures { get; } =
            new(PathComparer);

        public Task<bool> IsIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            IsIndexedCalls++;
            return Task.FromResult(IndexedResult);
        }

        public Task ReindexFileAsync(
            string path,
            CancellationToken ct = default)
        {
            Calls.Add($"reindex:{path}");
            Tokens.Add(ct);
            return ReindexFailures.Contains(path)
                ? Task.FromException(
                    new IOException("Reindex failed."))
                : Task.CompletedTask;
        }

        public Task RemoveIndexedFileAsync(
            string path,
            CancellationToken ct = default)
        {
            Calls.Add($"remove:{path}");
            Tokens.Add(ct);
            return RemoveFailures.Contains(path)
                ? Task.FromException(
                    new IOException("Removal failed."))
                : Task.CompletedTask;
        }
    }

    private sealed class BlockingReindexService :
        IReindexService
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CancellationToken> Tokens { get; } = [];

        public Task<bool> IsIndexedFileAsync(
            string path,
            CancellationToken ct = default) =>
            Task.FromResult(true);

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

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"reviewed-file-operation-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] parts)
        {
            string path =
                parts.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(
            string relativePath,
            string content)
        {
            string path =
                Path.Combine(Root, relativePath);
            System.IO.Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(
                    Root,
                    recursive: true);
            }
            catch
            {
            }
        }
    }
}
