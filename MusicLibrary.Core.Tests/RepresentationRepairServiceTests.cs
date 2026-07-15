using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class RepresentationRepairServiceTests
{
    [Fact]
    public async Task Preview_CombinesMetadataDerivationAndOrganizationWithoutChangingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"representation-repair-{Guid.NewGuid():N}");
        string configPath = Path.Combine(root, "ingest.xml");
        Directory.CreateDirectory(root);
        try
        {
            new IngestMusicConfiguration
            {
                FfmpegPath = "ffmpeg",
                AacDestination = Path.Combine(root, "aac"),
                CdDestination = Path.Combine(root, "cd"),
                PairedCdDestination = Path.Combine(root, "paired"),
                HighResolutionDestination = Path.Combine(root, "hires"),
            }.Save(configPath);

            var high = Track(Path.Combine(root, "hires", "01.flac"), "Canonical title", 96_000, 24);
            var cd = Track(Path.Combine(root, "cd", "01.flac"), "Old title", 44_100, 16);
            string organized = Path.Combine(root, "cd", "Artist", "Album", "01 Canonical title.flac");
            var service = new RepresentationRepairService(
                new StubOrganizer([new PlannedMove(cd.Path, organized)]));

            var preview = await service.PreviewAsync([high, cd], configPath);

            var titleCopy = Assert.Single(preview.MetadataCopies.Items,
                repair => repair.Path == cd.Path && repair.Field == TagFields.Title);
            Assert.Equal("Canonical title", titleCopy.After);
            var aac = Assert.Single(preview.FileActions,
                action => action.Kind == RepresentationRepairKind.DeriveAac);
            Assert.Equal(cd.Path, aac.SourcePath);
            Assert.EndsWith(".m4a", aac.DestinationPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(preview.FileActions,
                action => action.Kind == RepresentationRepairKind.Organize &&
                    action.DestinationPath == organized);
            Assert.DoesNotContain(preview.FileActions,
                action => action.Kind == RepresentationRepairKind.DeriveCdFlac);
            Assert.Empty(preview.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_HighResolutionOnlyPlansPairedCdAndAac()
    {
        string root = Path.Combine(Path.GetTempPath(), $"representation-repair-{Guid.NewGuid():N}");
        string configPath = Path.Combine(root, "ingest.xml");
        Directory.CreateDirectory(root);
        try
        {
            new IngestMusicConfiguration
            {
                FfmpegPath = "ffmpeg",
                AacDestination = Path.Combine(root, "aac"),
                CdDestination = Path.Combine(root, "cd"),
                PairedCdDestination = Path.Combine(root, "paired"),
                HighResolutionDestination = Path.Combine(root, "hires"),
            }.Save(configPath);
            var high = Track(Path.Combine(root, "hires", "01.flac"), "Title", 192_000, 24);

            var preview = await new RepresentationRepairService(new StubOrganizer([]))
                .PreviewAsync([high], configPath);

            Assert.Contains(preview.FileActions,
                action => action.Kind == RepresentationRepairKind.DeriveCdFlac &&
                    action.DestinationPath.StartsWith(Path.Combine(root, "paired"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(preview.FileActions,
                action => action.Kind == RepresentationRepairKind.DeriveAac &&
                    action.DestinationPath.StartsWith(Path.Combine(root, "aac"), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_MissingConfigurationAndOfflineOrganizerRetainsWarnings()
    {
        var preview = await new RepresentationRepairService(new ThrowingOrganizer())
            .PreviewAsync([Track("track.flac", "Title", 44_100, 16)], null);

        Assert.Equal(2, preview.Warnings.Count);
        Assert.Contains(preview.Warnings, warning => warning.Contains("configuration", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("offline", StringComparison.OrdinalIgnoreCase));
    }

    private static TrackRecord Track(string path, string title, uint sampleRate, uint bitsPerSample) => new()
    {
        Path = path,
        Artist = "Artist",
        AlbumArtist = "Artist",
        HasAlbumArtist = true,
        Album = "Album",
        StrippedAlbum = "Album",
        Title = title,
        ReleaseDate = "2024",
        TrackNumber = 1,
        TrackTotal = 1,
        DiscNumber = 1,
        DiscTotal = 1,
        CodecName = "FLAC",
        CodecType = CodecType.Lossless,
        SampleRate = sampleRate,
        BitsPerSample = bitsPerSample,
        Channels = 2,
        Length = 123,
        LastWriteTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private sealed class StubOrganizer(IReadOnlyList<PlannedMove> moves) : ILibraryOrganizer
    {
        public Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default) =>
            Task.FromResult(moves);
        public Task<OrganizeResult> ApplyMovesAsync(IReadOnlyList<PlannedMove> moves,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingOrganizer : ILibraryOrganizer
    {
        public Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default) =>
            throw new IOException("Library root is offline.");
        public Task<OrganizeResult> ApplyMovesAsync(IReadOnlyList<PlannedMove> moves,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
