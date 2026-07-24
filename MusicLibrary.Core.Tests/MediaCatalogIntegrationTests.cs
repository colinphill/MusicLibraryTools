using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MediaCatalogIntegrationTests
{
    [Fact]
    public async Task FileMutationExecutorNotifiesConfiguredCatalogIntegration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"catalog-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.flac");
            string destination = Path.Combine(root, "library", "track.flac");
            await File.WriteAllTextAsync(source, "audio");
            var info = new FileInfo(source);
            var sourceSnapshot = new OperationPathSnapshot(
                true, false, info.Length, info.LastWriteTimeUtc) { Path = source };
            var integration = new RecordingIntegration();
            var executor = new FileMutationPlanExecutor(
                new FileMutationCoordinator(),
                catalogIntegrations: [integration]);
            var plan = new FileMutationPlan(
                "test",
                root,
                "",
                [new(FileMutationKind.Copy, source, destination,
                    sourceSnapshot, OperationPathSnapshot.Missing(destination))],
                [],
                DateTimeOffset.UtcNow,
                RetainRecovery: false);

            await executor.ApplyAsync(plan);

            Assert.True(File.Exists(destination));
            MediaCatalogMutation mutation = Assert.Single(integration.Session.Mutations);
            Assert.Equal(MediaCatalogMutationKind.Add, mutation.Kind);
            Assert.Equal(destination, mutation.CurrentPath);
            Assert.True(integration.Session.Completed);
            Assert.True(integration.Session.Disposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MoveMutationRelocatesConfiguredCatalogEntry()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"catalog-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source =
                Path.Combine(root, "source.flac");
            string destination =
                Path.Combine(root, "renamed.flac");
            await File.WriteAllTextAsync(
                source,
                "audio");
            var sourceInfo =
                new FileInfo(source);
            var integration =
                new RecordingIntegration();
            var executor =
                new FileMutationPlanExecutor(
                    new FileMutationCoordinator(),
                    catalogIntegrations:
                        [integration]);
            var plan = new FileMutationPlan(
                "test",
                root,
                Path.Combine(root, "recovery"),
                [new(
                    FileMutationKind.Move,
                    source,
                    destination,
                    new(
                        true,
                        false,
                        sourceInfo.Length,
                        sourceInfo.LastWriteTimeUtc)
                    {
                        Path = source,
                    },
                    OperationPathSnapshot.Missing(
                        destination))],
                [],
                DateTimeOffset.UtcNow);

            FileMutationSummary result =
                await executor.ApplyAsync(plan);

            Assert.Equal(1, result.Moved);
            MediaCatalogMutation mutation =
                Assert.Single(
                    integration.Session.Mutations);
            Assert.Equal(
                MediaCatalogMutationKind.Relocate,
                mutation.Kind);
            Assert.Equal(
                source,
                mutation.OriginalPath);
            Assert.Equal(
                destination,
                mutation.CurrentPath);
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    [Fact]
    public async Task CatalogFailureRollsBackCompletedFilesystemActions()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"catalog-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.flac");
            string destination = Path.Combine(
                root, "library", "track.flac");
            await File.WriteAllTextAsync(source, "audio");
            var sourceInfo = new FileInfo(source);
            var integration = new RecordingIntegration
            {
                Session =
                {
                    CommitError = new IOException(
                        "catalog commit failed"),
                },
            };
            string recovery = Path.Combine(root, "recovery");
            var executor = new FileMutationPlanExecutor(
                new FileMutationCoordinator(),
                catalogIntegrations: [integration]);
            var plan = new FileMutationPlan(
                "test",
                root,
                recovery,
                [new(
                    FileMutationKind.Copy,
                    source,
                    destination,
                    new(
                        true,
                        false,
                        sourceInfo.Length,
                        sourceInfo.LastWriteTimeUtc)
                    {
                        Path = source,
                    },
                    OperationPathSnapshot.Missing(
                        destination))],
                [],
                DateTimeOffset.UtcNow);

            IOException error =
                await Assert.ThrowsAsync<IOException>(
                    () => executor.ApplyAsync(plan));

            Assert.Contains("catalog commit failed", error.Message);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));
            Assert.Contains(
                "ROLLBACK\t",
                File.ReadAllText(
                    Path.Combine(recovery, "journal.tsv")));
            Assert.True(integration.Session.Disposed);
            Assert.False(integration.Session.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterFirstActionRollsBackTheWholePlan()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"mutation-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "first.flac");
            string second = Path.Combine(root, "second.flac");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            string firstDestination = Path.Combine(
                root, "target", "first.flac");
            string secondDestination = Path.Combine(
                root, "target", "second.flac");
            string recovery = Path.Combine(root, "recovery");
            using var cancellation = new CancellationTokenSource();
            var progress =
                new SynchronousProgress<OperationProgress>(update =>
                {
                    if (update.Phase == OperationPhase.Applying &&
                        update.Completed == 1)
                        cancellation.Cancel();
                });
            var executor = new FileMutationPlanExecutor(
                new FileMutationCoordinator());
            var plan = new FileMutationPlan(
                "test",
                root,
                recovery,
                [
                    Copy(first, firstDestination),
                    Copy(second, secondDestination),
                ],
                [],
                DateTimeOffset.UtcNow);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => executor.ApplyAsync(
                    plan,
                    progress,
                    cancellation.Token));

            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(File.Exists(firstDestination));
            Assert.False(File.Exists(secondDestination));
            Assert.Contains(
                "ROLLBACK\t",
                File.ReadAllText(
                    Path.Combine(recovery, "journal.tsv")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static FileMutationAction Copy(
            string source,
            string destination)
        {
            var info = new FileInfo(source);
            return new(
                FileMutationKind.Copy,
                source,
                destination,
                new(
                    true,
                    false,
                    info.Length,
                    info.LastWriteTimeUtc)
                {
                    Path = source,
                },
                OperationPathSnapshot.Missing(destination));
        }
    }

    private sealed class RecordingIntegration : IMediaCatalogIntegration
    {
        public string Id => "recording";
        public string DisplayName => "Recording catalog";
        public RecordingSession Session { get; } = new();

        public Task<IMediaCatalogMutationSession?> BeginAsync(
            IReadOnlyCollection<string> candidatePaths,
            bool backupFiles,
            CancellationToken ct = default) =>
            Task.FromResult<IMediaCatalogMutationSession?>(Session);
    }

    private sealed class RecordingSession : IMediaCatalogMutationSession
    {
        public bool Active => true;
        public List<MediaCatalogMutation> Mutations { get; } = [];
        public Exception? CommitError { get; set; }
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public Task CommitAsync(
            IReadOnlyList<MediaCatalogMutation> mutations,
            CancellationToken ct = default)
        {
            Mutations.AddRange(mutations);
            return CommitError is null
                ? Task.CompletedTask
                : Task.FromException(CommitError);
        }

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
}
