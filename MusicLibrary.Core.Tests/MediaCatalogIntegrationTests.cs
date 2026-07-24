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
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public Task CommitAsync(
            IReadOnlyList<MediaCatalogMutation> mutations,
            CancellationToken ct = default)
        {
            Mutations.AddRange(mutations);
            return Task.CompletedTask;
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
