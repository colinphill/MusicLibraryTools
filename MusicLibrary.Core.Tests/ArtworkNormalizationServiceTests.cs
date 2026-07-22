using System.Security.Cryptography;
using iTunes.Binary;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicFileUtilities;
using MusicLibraryTools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkNormalizationServiceTests
{
    [Fact]
    public async Task EmptyReviewedPlanCompletesWithoutCreatingRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library);
        var reindex = new RecordingReindexService();

        ArtworkNormalizationResult result = await new ArtworkNormalizationService(reindex).ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.UpdatedFileCount);
        Assert.Null(result.JournalPath);
        Assert.Empty(reindex.Batches);
        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    [Fact]
    public async Task ApplyRejectsChangedLibraryBeforeCreatingRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library);
        await File.AppendAllTextAsync(library, "changed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtworkNormalizationService().ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    [Fact]
    public async Task ApplyRejectsBlockedPlan()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1], TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library) with
        {
            Issues = [new("playlist-ambiguous", OperationIssueSeverity.Blocker, "Ambiguous")],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtworkNormalizationService().ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyRefreshesCacheFromSavedMaterializedArtwork()
    {
        using var fixture = CreateMediaPlan();
        var reindex = new RecordingReindexService();

        ArtworkNormalizationResult result = await new ArtworkNormalizationService(reindex).ApplyAsync(
            fixture.Plan, ct: TestContext.Current.CancellationToken);

        Assert.Null(result.CacheError);
        Assert.Equal([fixture.MediaPath], result.UpdatedPaths);
        var refreshed = Assert.Single(Assert.Single(reindex.Batches));
        Assert.Equal(fixture.MediaPath, refreshed.Path);
        Assert.Equal(
            fixture.ProposedArtwork,
            Assert.Single(refreshed.File.Tags.SelectMany(tag => tag.GetImageMetadata())).Data);
        Assert.False(reindex.ReceivedToken.CanBeCanceled);
    }

    [Fact]
    public async Task CacheFailureDoesNotRollbackCommittedArtwork()
    {
        using var fixture = CreateMediaPlan();
        var reindex = new RecordingReindexService { Error = "cache unavailable" };

        ArtworkNormalizationResult result = await new ArtworkNormalizationService(reindex).ApplyAsync(
            fixture.Plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal("cache unavailable", result.CacheError);
        Assert.Equal(1, result.UpdatedFileCount);
        Assert.Equal(
            fixture.ProposedArtwork,
            Assert.Single(MediaFile.GetFile(fixture.MediaPath).Tags
                .SelectMany(tag => tag.GetImageMetadata())).Data);
    }

    [Fact]
    public async Task ApplyMakesNormalizedArtworkImmediatelyAvailableFromLibraryCache()
    {
        using var fixture = CreateMediaPlan();
        string config = Path.Combine(fixture.Workspace.Path, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = Path.Combine(fixture.Workspace.Path, "cache.db"),
            IndexTargets = [new IndexTargetEntry { Target = Path.GetDirectoryName(fixture.MediaPath)! }],
        }.Save(config);
        var settings = new AppSettings(Path.Combine(fixture.Workspace.Path, "settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        await library.IndexAsync(ct: TestContext.Current.CancellationToken);

        ArtworkNormalizationResult result = await new ArtworkNormalizationService(library).ApplyAsync(
            fixture.Plan, ct: TestContext.Current.CancellationToken);

        Assert.Null(result.CacheError);
        Assert.Equal(fixture.ProposedArtwork, await library.GetFirstImageAsync(
            fixture.MediaPath, TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(await library.GetArtworkAuditFilesAsync(
            TestContext.Current.CancellationToken)).ArtworkScanned);
    }

    [Fact]
    public async Task CatalogOnlyPolicyBlocksPreviewAndApplyWithoutMutatingFiles()
    {
        using var fixture = CreateMediaPlan();
        string configPath = Path.Combine(fixture.Workspace.Path, "catalog-only.xml");
        var editable = EditableLibraryConfig.CreateNew();
        editable.DatabaseFile = Path.Combine(fixture.Workspace.Path, "catalog-only.db");
        editable.ItunesLibraryPath = fixture.Plan.LibraryPath;
        editable.IndexTargets.Add(editable.CreateIndexTarget(
            Path.GetDirectoryName(fixture.MediaPath)!));
        editable.Save(configPath);
        var settings = new AppSettings(Path.Combine(fixture.Workspace.Path, "settings.json"));
        settings.LoadConfig(configPath);
        var reindex = new RecordingReindexService();
        var service = new ArtworkNormalizationService(
            settings, reindex, new FileMutationCoordinator());
        byte[] mediaBefore = await File.ReadAllBytesAsync(
            fixture.MediaPath, TestContext.Current.CancellationToken);
        byte[] libraryBefore = await File.ReadAllBytesAsync(
            fixture.Plan.LibraryPath, TestContext.Current.CancellationToken);

        ArtworkNormalizationPlan plan = await service.PreviewAsync(
            new("####!####", fixture.Plan.LibraryPath),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(settings.Configuration!.LibraryId, plan.LibraryId);
        Assert.Equal(settings.Configuration.PolicySnapshot.Fingerprint, plan.PolicyFingerprint);
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "artwork-policy-denied" &&
            issue.Severity == OperationIssueSeverity.Blocker);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan with { Issues = [] },
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("no root", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mediaBefore, await File.ReadAllBytesAsync(
            fixture.MediaPath, TestContext.Current.CancellationToken));
        Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(
            fixture.Plan.LibraryPath, TestContext.Current.CancellationToken));
        Assert.Empty(reindex.Batches);
        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    [Fact]
    public async Task ApplyRejectsPolicyChangedAfterArtworkPreview()
    {
        using var fixture = CreateMediaPlan();
        string configPath = Path.Combine(fixture.Workspace.Path, "policy-change.xml");
        var editable = new EditableLibraryConfig
        {
            ActiveProfileId = LibraryProfilePresets.PreserveLayoutAndTagsId,
            DatabaseFile = Path.Combine(fixture.Workspace.Path, "policy-change.db"),
            ItunesLibraryPath = fixture.Plan.LibraryPath,
        };
        editable.IndexTargets.Add(editable.CreateIndexTarget(
            Path.GetDirectoryName(fixture.MediaPath)!));
        editable.Save(configPath);
        var settings = new AppSettings(Path.Combine(fixture.Workspace.Path, "settings.json"));
        settings.LoadConfig(configPath);
        var service = new ArtworkNormalizationService(
            settings, new RecordingReindexService(), new FileMutationCoordinator());
        ArtworkNormalizationPlan plan = await service.PreviewAsync(
            new("####!####", fixture.Plan.LibraryPath),
            ct: TestContext.Current.CancellationToken);
        Assert.True(plan.CanApply);
        Assert.Single(plan.Items);

        EditableLibraryConfig changed = EditableLibraryConfig.Load(configPath);
        changed.ActiveProfileId = LibraryProfilePresets.CatalogOnlyId;
        IndexTargetEntry root = Assert.Single(changed.IndexTargets);
        root.ProfileId = LibraryProfilePresets.CatalogOnlyId;
        root.Permissions = LibraryRootPermissions.None;
        root.Organize = false;
        changed.Save(configPath);
        settings.LoadConfig(configPath);
        byte[] mediaBefore = await File.ReadAllBytesAsync(
            fixture.MediaPath, TestContext.Current.CancellationToken);
        byte[] libraryBefore = await File.ReadAllBytesAsync(
            fixture.Plan.LibraryPath, TestContext.Current.CancellationToken);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("policy changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mediaBefore, await File.ReadAllBytesAsync(
            fixture.MediaPath, TestContext.Current.CancellationToken));
        Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(
            fixture.Plan.LibraryPath, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    private static ArtworkNormalizationPlan CreatePlan(string library)
    {
        var info = new FileInfo(library);
        var snapshot = new OperationPathSnapshot(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = library,
        };
        return new(new("Artwork"), library, snapshot,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library))), [], 0, 0, 0, [],
            Path.Combine(Path.GetDirectoryName(library)!, "recovery"), DateTimeOffset.UtcNow);
    }

    private static MediaPlanFixture CreateMediaPlan()
    {
        var workspace = new TempDirectory();
        string mediaFolder = Path.Combine(workspace.Path, "media");
        Directory.CreateDirectory(mediaFolder);
        string mediaPath = Path.Combine(mediaFolder, "track.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), mediaPath);

        byte[] currentArtwork = CreateImage("png", 64, 64, Color.Blue);
        var media = MediaFile.GetFile(mediaPath);
        (media as IArtworkWriter ?? media.Tags.First() as IArtworkWriter)!
            .SetFrontCover(currentArtwork, "image/png");
        media.SaveTags();

        string library = Path.Combine(workspace.Path, "iTunes Library.itl");
        File.WriteAllBytes(library, SyntheticItunesLibrary.CreateFile(mediaFolder));
        ItlDocument document = ItlDocument.Load(library);
        document.SetTrackString(document.Tracks.Single(), ItlDataType.Location, mediaPath);
        ItlFileEditor.SaveValidated(document, library);
        byte[] proposedArtwork = CreateImage("jpeg", 48, 48, Color.Red);
        var mediaInfo = new FileInfo(mediaPath);
        var libraryInfo = new FileInfo(library);
        var item = new ArtworkNormalizationItem(
            [1],
            mediaPath,
            new(true, false, mediaInfo.Length, mediaInfo.LastWriteTimeUtc) { Path = mediaPath },
            new("image/png", 64, 64, currentArtwork.LongLength, Hash(currentArtwork)),
            new("image/jpeg", 48, 48, proposedArtwork.LongLength, Hash(proposedArtwork)),
            [.. proposedArtwork]);
        var plan = new ArtworkNormalizationPlan(
            new("####!####", library),
            library,
            new(true, false, libraryInfo.Length, libraryInfo.LastWriteTimeUtc) { Path = library },
            Hash(File.ReadAllBytes(library)),
            [item],
            1,
            1,
            0,
            [],
            Path.Combine(workspace.Path, "recovery"),
            DateTimeOffset.UtcNow);
        return new(workspace, mediaPath, proposedArtwork, plan);
    }

    private static byte[] CreateImage(string format, int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(context => context.BackgroundColor(color));
        using var stream = new MemoryStream();
        if (format == "png")
            image.Save(stream, new PngEncoder());
        else
            image.Save(stream, new JpegEncoder { Quality = 80 });
        return stream.ToArray();
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed record MediaPlanFixture(
        TempDirectory Workspace,
        string MediaPath,
        byte[] ProposedArtwork,
        ArtworkNormalizationPlan Plan) : IDisposable
    {
        public void Dispose() => Workspace.Dispose();
    }

    private sealed class RecordingReindexService : IReindexService
    {
        public string? Error { get; init; }
        public CancellationToken ReceivedToken { get; private set; }
        public List<IReadOnlyList<(string Path, IMediaFile File)>> Batches { get; } = [];

        public Task ReindexFileAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReindexFilesAsync(
            IReadOnlyList<(string Path, IMediaFile File)> files,
            CancellationToken ct = default)
        {
            ReceivedToken = ct;
            Batches.Add(files);
            if (Error is not null)
                throw new InvalidOperationException(Error);
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ArtworkNormalizationTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
