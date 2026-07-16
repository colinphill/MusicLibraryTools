using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class RepresentationRepairServiceTests
{
    [Fact]
    public async Task Preview_CombinesMetadataDerivationAndOrganizationWithoutChangingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"representation-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            LibraryConfiguration configuration = CreateConfiguration(root);

            var high = Track(Path.Combine(root, "hires", "01.flac"), "Canonical title", 96_000, 24);
            var cd = Track(Path.Combine(root, "cd", "01.flac"), "Old title", 44_100, 16);
            string organized = Path.Combine(root, "cd", "Artist", "Album", "01 Canonical title.flac");
            var service = new RepresentationRepairService(
                new StubOrganizer([new PlannedMove(cd.Path, organized)]));

            var preview = await service.PreviewAsync([high, cd], configuration);

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
        Directory.CreateDirectory(root);
        try
        {
            LibraryConfiguration configuration = CreateConfiguration(root);
            var high = Track(Path.Combine(root, "hires", "01.flac"), "Title", 192_000, 24);

            var preview = await new RepresentationRepairService(new StubOrganizer([]))
                .PreviewAsync([high], configuration);

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

    [Fact]
    public async Task Apply_DerivesOutputsBeforeOrganizingTheirSource()
    {
        string root = Path.Combine(Path.GetTempPath(), $"representation-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            LibraryConfiguration configuration = CreateConfiguration(root);
            string source = Path.Combine(root, "hires", "01.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(MediaFixtures.Path_("sample.flac"), source);
            var record = TrackFromFile(source, "Title", 96_000, 24);
            string organized = Path.Combine(root, "hires", "Artist", "Album", "01 Title.flac");
            var order = new List<string>();
            var organizer = new ApplyingOrganizer(
                [new PlannedMove(source, organized,
                    Snapshot(source), OperationPathSnapshot.Missing(organized))],
                order);
            var ffmpeg = new RecordingFfmpeg(order);
            var reindex = new RecordingReindex();
            var service = new RepresentationRepairService(
                organizer, ffmpeg, reindex: reindex);

            RepresentationRepairPreview preview =
                await service.PreviewAsync([record], configuration);
            RepresentationRepairApplyResult result =
                await service.ApplyAsync(preview.FileActions, configuration);

            Assert.Equal(3, result.Applied);
            Assert.Equal(
                ["DeriveCdFlac", "DeriveAac", "Organize"],
                order);
            Assert.True(File.Exists(organized));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "paired"), "*.flac",
                SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "aac"), "*.m4a",
                SearchOption.AllDirectories));
            Assert.StartsWith(Path.Combine(root, "paired"), ffmpeg.AacInput!,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, reindex.Paths.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_RejectsStaleSourcesAndOccupiedDestinationsBeforeFfmpeg()
    {
        string root = Path.Combine(Path.GetTempPath(), $"representation-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            LibraryConfiguration configuration = CreateConfiguration(root);
            string source = Path.Combine(root, "hires", "01.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(MediaFixtures.Path_("sample.flac"), source);
            var record = TrackFromFile(source, "Title", 96_000, 24);
            var ffmpeg = new RecordingFfmpeg([]);
            var service = new RepresentationRepairService(new StubOrganizer([]), ffmpeg);
            RepresentationRepairPreview preview =
                await service.PreviewAsync([record], configuration);
            var derivations = preview.FileActions
                .Where(action => action.Kind != RepresentationRepairKind.Organize)
                .ToList();

            File.AppendAllText(source, "changed");
            Directory.CreateDirectory(Path.GetDirectoryName(derivations[1].DestinationPath)!);
            File.WriteAllText(derivations[1].DestinationPath, "occupied");
            RepresentationRepairAction collision = derivations[1] with
            {
                ExpectedSource = Snapshot(source),
            };

            RepresentationRepairApplyResult result =
                await service.ApplyAsync([derivations[0], collision], configuration);

            Assert.Equal(2, result.Failed);
            Assert.Empty(ffmpeg.Order);
            Assert.Contains(result.Results,
                item => item.Error!.Contains("Source changed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Results,
                item => item.Error!.Contains("Destination", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static TrackRecord TrackFromFile(
        string path, string title, uint sampleRate, uint bitsPerSample)
    {
        FileInfo info = new(path);
        return Track(path, title, sampleRate, bitsPerSample) with
        {
            Length = info.Length,
            LastWriteTime = info.LastWriteTimeUtc,
        };
    }

    private static OperationPathSnapshot Snapshot(string path)
    {
        FileInfo info = new(path);
        return new(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = Path.GetFullPath(path),
        };
    }

    private static LibraryConfiguration CreateConfiguration(string root)
    {
        string path = Path.Combine(root, "library.xml");
        new EditableLibraryConfig
        {
            IndexTargets =
            [
                new() { Target = Path.Combine(root, "cd"), IngestRole = LibraryIngestRole.Cd },
                new() { Target = Path.Combine(root, "paired"), IngestRole = LibraryIngestRole.CdFallback },
                new() { Target = Path.Combine(root, "hires"), IngestRole = LibraryIngestRole.HiRes },
                new() { Target = Path.Combine(root, "aac"), IngestRole = LibraryIngestRole.AacFallback },
            ],
        }.Save(path);
        return new LibraryConfiguration(path);
    }

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

    private sealed class ApplyingOrganizer(
        IReadOnlyList<PlannedMove> moves,
        List<string> order) : ILibraryOrganizer
    {
        public Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default) =>
            Task.FromResult(moves);

        public Task<OrganizeResult> ApplyMovesAsync(
            IReadOnlyList<PlannedMove> selected,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            order.Add("Organize");
            int completed = 0;
            foreach (PlannedMove move in selected)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                File.Move(move.Source, move.Destination);
                progress?.Report(++completed);
            }
            return Task.FromResult(new OrganizeResult(selected.Count, []));
        }
    }

    private sealed class RecordingFfmpeg(List<string> order) : IFfmpegRunner
    {
        public List<string> Order => order;
        public string? AacInput { get; private set; }

        public Task PreflightAsync(
            string executable, string requiredEncoder, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ConvertAlacToFlacAsync(
            string executable, string input, string output, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeriveCdFlacAsync(
            string executable, string input, string output, CancellationToken ct = default)
        {
            order.Add("DeriveCdFlac");
            File.Copy(MediaFixtures.Path_("sample.flac"), output);
            return Task.CompletedTask;
        }

        public Task EncodeAacAsync(
            string executable,
            string encoder,
            int bitrateKbps,
            string input,
            string output,
            CancellationToken ct = default)
        {
            order.Add("DeriveAac");
            AacInput = input;
            File.Copy(MediaFixtures.Path_("sample_aac.m4a"), output);
            return Task.CompletedTask;
        }

        public Task<string> ComputeDecodedAudioHashAsync(
            string executable, string input, CancellationToken ct = default) =>
            Task.FromResult("SHA256=test");
    }

    private sealed class RecordingReindex : IReindexService
    {
        public List<string> Paths { get; } = [];
        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }
}
