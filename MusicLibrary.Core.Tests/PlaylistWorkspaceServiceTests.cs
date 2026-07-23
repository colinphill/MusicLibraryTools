using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class PlaylistWorkspaceServiceTests
{
    [Fact]
    public async Task PreviewPreservesOrderAndBuildsRecoverableM3u8()
    {
        using var temp = new TempDirectory();
        string output = Path.Combine(temp.Path, "ordered.m3u8");
        await File.WriteAllTextAsync(
            output,
            "old",
            TestContext.Current.CancellationToken);
        string first = Path.Combine(temp.Path, "music", "two.flac");
        string second = Path.Combine(temp.Path, "music", "one.flac");
        var documents = new FakeDocuments(
            Document(first, "Artist", "Two", "Album", 122),
            Document(second, "Artist", "One", "Album", 62));
        var executor = new RecordingExecutor();
        var service = CreateService(documents, executor);
        var configuration = new PlaylistWorkspaceConfiguration(
            "Ordered",
            "m3u8",
            output,
            PlaylistPathStyle.Relative,
            PlaylistWorkspaceEncoding.Utf8,
            PlaylistLineEnding.Lf,
            IncludeExtendedInfo: true);
        var progress = new List<OperationProgress>();

        PlaylistWorkspacePlan plan = await service.PreviewAsync(
            new([first, second, first], configuration),
            new SynchronousProgress<OperationProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        PlaylistWorkspaceFilePlan file = Assert.Single(plan.Files);
        Assert.Equal(output, file.DestinationPath);
        Assert.Equal(3, file.TrackCount);
        FileMutationAction action =
            Assert.Single(plan.MutationPlan.Actions);
        Assert.Equal(FileMutationKind.ReplaceGenerated, action.Kind);
        Assert.True(action.ExpectedDestination!.Exists);
        Assert.True(plan.MutationPlan.RetainRecovery);
        string text = Encoding.UTF8.GetString(action.Content.AsSpan());
        Assert.StartsWith("#EXTM3U\n", text);
        Assert.Contains("#EXTINF:122,Artist - Two\n", text);
        Assert.True(
            text.IndexOf("two.flac", StringComparison.Ordinal) <
            text.IndexOf("one.flac", StringComparison.Ordinal));
        Assert.Equal(
            2,
            text.Split(
                "two.flac",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(OperationPhase.Completed, progress[^1].Phase);

        PlaylistWorkspaceResult result = await service.ApplyAsync(
            plan,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.PlaylistCount);
        Assert.Equal(3, result.TrackReferenceCount);
        Assert.Same(plan.MutationPlan, executor.Plan);
    }

    [Fact]
    public async Task GroupedPreviewCreatesOneSanitizedPlaylistPerValue()
    {
        using var temp = new TempDirectory();
        string first = Path.Combine(temp.Path, "one.flac");
        string second = Path.Combine(temp.Path, "two.flac");
        string third = Path.Combine(temp.Path, "three.flac");
        var service = CreateService(
            new FakeDocuments(
                Document(first, album: "Rock/Pop"),
                Document(second, album: "Jazz"),
                Document(third, album: "")),
            new RecordingExecutor());
        var configuration = new PlaylistWorkspaceConfiguration(
            "Albums",
            "wpl",
            Path.Combine(temp.Path, "playlists"),
            OnePlaylistPerGroup: true,
            GroupByField: MetadataFieldKey.Known(TagFields.Album),
            GroupFileNameTemplate: "{Name} - {Group}");

        PlaylistWorkspacePlan plan = await service.PreviewAsync(
            new([first, second, third], configuration),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Equal(3, plan.Files.Count);
        Assert.Contains(plan.Files, file =>
            Path.GetFileName(file.DestinationPath) ==
            "Albums - Rock_Pop.wpl");
        Assert.Contains(plan.Files, file => file.Group == "Missing");
        Assert.All(plan.Files, file => Assert.Equal(1, file.TrackCount));
        Assert.All(plan.MutationPlan.Actions, action =>
            Assert.Equal(FileMutationKind.Write, action.Kind));
    }

    [Fact]
    public async Task InvalidConfigurationReturnsReviewBlockers()
    {
        var service = CreateService(
            new FakeDocuments(),
            new RecordingExecutor());
        var configuration = new PlaylistWorkspaceConfiguration(
            "",
            "m3u8",
            "\0",
            Encoding: PlaylistWorkspaceEncoding.Utf16LittleEndian,
            OnePlaylistPerGroup: true);

        PlaylistWorkspacePlan plan = await service.PreviewAsync(
            new([], configuration),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Empty(plan.Files);
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "playlist-workspace-sources-empty");
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "playlist-workspace-output-invalid");
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "playlist-workspace-group-required");
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "playlist-workspace-m3u8-encoding");
    }

    [Fact]
    public async Task PreviewObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(
            new FakeDocuments(Document("source.flac")),
            new RecordingExecutor());
        var configuration = new PlaylistWorkspaceConfiguration(
            "Playlist",
            "m3u",
            "playlist.m3u");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewAsync(
                new(["source.flac"], configuration),
                ct: cancellation.Token));
    }

    [Fact]
    public void ServiceRegistrationIncludesPlaylistWorkspace()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<PlaylistWorkspaceService>(
            provider.GetRequiredService<IPlaylistWorkspaceService>());
    }

    private static PlaylistWorkspaceService CreateService(
        IMetadataDocumentService documents,
        IFileMutationPlanExecutor executor) =>
        new(
            documents,
            executor,
            [new M3uPlaylistWriter(), new WplPlaylistWriter()]);

    private static MediaDocument Document(
        string path,
        string artist = "Artist",
        string title = "Title",
        string album = "Album",
        uint duration = 180)
    {
        ImmutableArray<MetadataValueSet> fields =
        [
            new(
                MetadataFieldKey.Known(TagFields.Artist),
                [artist]),
            new(
                MetadataFieldKey.Known(TagFields.Title),
                [title]),
            new(
                MetadataFieldKey.Known(TagFields.Album),
                string.IsNullOrWhiteSpace(album) ? [] : [album]),
        ];
        return new(
            Path.GetFullPath(path),
            [new("Test", fields, true, true, true, true)],
            [],
            new CodecModel
            {
                CodecName = "FLAC",
                DurationInSeconds = duration,
            },
            new(
                Path.GetFullPath(path),
                123,
                DateTime.UnixEpoch,
                "hash"),
            true);
    }

    private sealed class FakeDocuments(
        params MediaDocument[] documents) :
        IMetadataDocumentService
    {
        private readonly Dictionary<string, MediaDocument> _documents =
            documents.ToDictionary(
                document => document.Path,
                PathComparer);

        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _documents[Path.GetFullPath(path)]);
        }
    }

    private sealed class RecordingExecutor : IFileMutationPlanExecutor
    {
        public FileMutationPlan? Plan { get; private set; }

        public Task<FileMutationSummary> ApplyAsync(
            FileMutationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Plan = plan;
            return Task.FromResult(new FileMutationSummary(
                0,
                plan.Actions.Count,
                0,
                0,
                null,
                []));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-playlist-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
