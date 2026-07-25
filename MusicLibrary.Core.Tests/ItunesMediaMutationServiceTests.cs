using System.Security.Cryptography;
using System.Xml.Linq;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ItunesMediaMutationServiceTests
{
    [Fact]
    public async Task CompletedRefreshUpdatesItlAndPreservesTrackIdentity()
    {
        using var fixture = CreateFixture();
        ulong persistentId = ItlLibrary.Load(fixture.LibraryPath).Tracks.Single().PersistentId;
        var service = new ItunesMediaMutationService(
            new CommandLineAppSettings(fixture.ConfigurationPath));

        BatchWriteResult write = await new TagWriteService(itunes: service).ApplyAsync(
            [fixture.MediaPath], [new TagEdit(TagFields.Title, "Synchronized title")]);
        Assert.Equal(1, write.SavedCount);

        ItlTrack track = ItlLibrary.Load(fixture.LibraryPath).Tracks.Single();
        Assert.Equal("Synchronized title", track.Title);
        Assert.Equal(persistentId, track.PersistentId);
    }

    [Fact]
    public async Task AppliedSessionWithoutCompletionRestoresMediaAndItl()
    {
        using var fixture = CreateFixture();
        byte[] originalMedia = await File.ReadAllBytesAsync(fixture.MediaPath);
        byte[] originalLibraryHash = SHA256.HashData(
            await File.ReadAllBytesAsync(fixture.LibraryPath));
        var service = new ItunesMediaMutationService(
            new CommandLineAppSettings(fixture.ConfigurationPath));

        await using (IItunesMediaMutationSession session =
                     await service.BeginAsync([fixture.MediaPath], backupFiles: true))
        {
            BatchWriteResult write = await new TagWriteService().ApplyAsync(
                [fixture.MediaPath], [new TagEdit(TagFields.Title, "Rolled back title")]);
            Assert.Equal(1, write.SavedCount);
            await session.CommitAsync([ItunesMediaMutation.Refresh(fixture.MediaPath)]);
        }

        Assert.Equal(originalMedia, await File.ReadAllBytesAsync(fixture.MediaPath));
        Assert.Equal(originalLibraryHash,
            SHA256.HashData(await File.ReadAllBytesAsync(fixture.LibraryPath)));
    }

    [Fact]
    public async Task ReconciliationRefreshesChangedTrackedFilesAndReportsUntrackedFiles()
    {
        using var fixture = CreateFixture();
        string untracked = Path.Combine(fixture.MediaFolder, "untracked.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), untracked);
        BatchWriteResult write = await new TagWriteService().ApplyAsync(
            [fixture.MediaPath], [new TagEdit(TagFields.Title, "Externally changed")]);
        Assert.Equal(1, write.SavedCount);

        var service = new ItunesMediaMutationService(
            new CommandLineAppSettings(fixture.ConfigurationPath));
        ItunesMediaReconciliationResult result = await service.ReconcileAsync(
        [
            Snapshot(fixture.MediaPath),
            Snapshot(untracked),
        ], [fixture.MediaFolder]);

        Assert.True(result.Applied, result.Error);
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal("Externally changed",
            ItlLibrary.Load(fixture.LibraryPath).Tracks.Single().Title);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == ItunesMediaReconciliationIssueKind.UntrackedFile &&
            PathComparer.Equals(issue.Path, untracked));
    }

    [Fact]
    public async Task MirroredAddImportsOnlyWhenReferenceSourceIsTracked()
    {
        using var fixture = CreateFixture();
        string trackedOutput =
            Path.Combine(fixture.MediaFolder, "tracked-output.mp3");
        string untrackedSource =
            Path.Combine(fixture.MediaFolder, "untracked-source.mp3");
        string untrackedOutput =
            Path.Combine(fixture.MediaFolder, "untracked-output.mp3");
        File.Copy(
            MediaFixtures.Path_("sample.mp3"),
            trackedOutput);
        File.Copy(
            MediaFixtures.Path_("sample.mp3"),
            untrackedSource);
        File.Copy(
            MediaFixtures.Path_("sample.mp3"),
            untrackedOutput);
        var service = new ItunesMediaMutationService(
            new CommandLineAppSettings(
                fixture.ConfigurationPath));

        await using (IItunesMediaMutationSession session =
                     await service.BeginAsync(
                         [
                             trackedOutput,
                             untrackedOutput,
                         ],
                         backupFiles: false,
                         TestContext.Current.CancellationToken))
        {
            ItunesMediaMutationResult result =
                await session.CommitAsync(
                    [
                        ItunesMediaMutation.Add(
                            trackedOutput,
                            fixture.MediaPath),
                        ItunesMediaMutation.Add(
                            untrackedOutput,
                            untrackedSource),
                    ],
                    TestContext.Current.CancellationToken);
            await session.CompleteAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ImportedTracks);
        }

        ItlTrack[] tracks =
            [.. ItlLibrary.Load(fixture.LibraryPath).Tracks];
        Assert.Equal(2, tracks.Length);
        Assert.Contains(
            tracks,
            track => PathComparer.Equals(
                track.Location,
                trackedOutput));
        Assert.DoesNotContain(
            tracks,
            track => PathComparer.Equals(
                track.Location,
                untrackedOutput));
    }

    private static ItunesMediaIndexedFile Snapshot(string path)
    {
        var file = new FileInfo(path);
        return new(path, file.Length, file.LastWriteTimeUtc);
    }

    private static Fixture CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ml-itl-" + Guid.NewGuid().ToString("N"));
        string mediaFolder = Path.Combine(root, "iTunes Media");
        Directory.CreateDirectory(mediaFolder);
        string mediaPath = Path.Combine(mediaFolder, "track.mp3");
        File.Copy(MediaFixtures.Path_("sample.mp3"), mediaPath);
        string libraryPath = Path.Combine(root, "iTunes Library.itl");
        File.WriteAllBytes(libraryPath, SyntheticItunesLibrary.CreateFile(mediaFolder));

        ItlDocument document = ItlDocument.Load(libraryPath);
        ItlRecord track = document.Tracks.Single();
        document.RefreshLocalTrack(track, mediaPath, new ItlLocalTrackMetadata
        {
            Title = "TestTitle",
            Artist = "TestArtist",
            AlbumArtist = "TestArtist",
            Album = "TestAlbum",
            Genre = "Rock",
            TrackNumber = 3,
            TrackCount = 3,
            Year = 2021,
            Duration = TimeSpan.FromSeconds(1),
            BitRateKbps = 128,
        }, new FileInfo(mediaPath).Length, File.GetLastWriteTimeUtc(mediaPath));
        document.Save(libraryPath);

        string configurationPath = Path.Combine(root, "LibraryConfiguration.xml");
        new XDocument(
            new XElement("LibraryConfiguration",
                new XElement("DatabaseFile", Path.Combine(root, "cache.db")),
                new XElement("ItunesLibrary", libraryPath),
                new XElement("IndexTarget", mediaFolder)))
            .Save(configurationPath);
        return new(root, mediaFolder, mediaPath, libraryPath, configurationPath);
    }

    private sealed record Fixture(
        string Root,
        string MediaFolder,
        string MediaPath,
        string LibraryPath,
        string ConfigurationPath) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best effort; a failed assertion should remain the primary test failure.
            }
        }
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
