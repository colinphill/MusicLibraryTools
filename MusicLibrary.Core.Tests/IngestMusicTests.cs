using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class IngestMusicTests
{
    [Fact]
    public async Task Preview_NormalizesMultiDiscAlbumAndTrackOffsets()
    {
        using var tree = new TempTree();
        string source = tree.Dir("incoming");
        string config = tree.Config();
        string fixture = Path.Combine(AppContext.BaseDirectory, "TestFiles", "sample.flac");
        foreach (var item in new[] { ("d1t1", 1, 1), ("d1t2", 2, 1), ("d2t3", 3, 2), ("d2t4", 4, 2) })
        {
            string path = Path.Combine(source, item.Item1 + ".flac");
            File.Copy(fixture, path);
            WriteTags(path, "Album (Disc 9)", item.Item1, item.Item2, item.Item3);
        }

        var plan = await new IngestMusicService(new FakeFfmpeg()).PreviewAsync(new(source, config));

        Assert.Empty(plan.Conflicts);
        var tracks = Assert.Single(plan.Albums).Tracks.OrderBy(t => t.OriginalDiscNumber).ThenBy(t => t.TrackNumber).ToArray();
        Assert.Equal(new[] { 1, 2, 1, 2 }, tracks.Select(t => t.TrackNumber));
        Assert.All(tracks, t => Assert.Equal(2, t.TrackTotal));
        Assert.Equal(new[] { "Album (Disc 1)", "Album (Disc 1)", "Album (Disc 2)", "Album (Disc 2)" }, tracks.Select(t => t.Album));
        Assert.All(plan.Albums.Single().Outputs.Where(o => o.Kind == IngestOutputKind.CdFlac),
            o => Assert.StartsWith(tree.Path("cd"), o.DestinationPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_DeclinedDerivationStopsBeforeFfmpegOrFilesystemChanges()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        var fake = new FakeFfmpeg();
        var plan = ManualPlan(tree, [source], requireApproval: true);

        var result = await new IngestMusicService(fake).ApplyAsync(plan,
            [new IngestApprovalDecision("album", false)]);

        Assert.True(result.Cancelled);
        Assert.Equal(0, fake.PreflightCalls);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(tree.Path("aac")));
    }

    [Fact]
    public async Task Apply_ParallelizesIndependentTranscodesUpToCpuLimit()
    {
        using var tree = new TempTree();
        string first = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        string second = tree.FileFromFixture("incoming", "two.flac", "sample.flac");
        var fake = new FakeFfmpeg(delay: TimeSpan.FromMilliseconds(100));
        var plan = ManualPlan(tree, [first, second], requireApproval: false);

        var result = await new IngestMusicService(fake).ApplyAsync(plan, []);

        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Failed);
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), fake.MaxConcurrent);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Configuration_RejectsMissingDestination()
    {
        using var tree = new TempTree();
        string path = tree.Path("bad.xml");
        File.WriteAllText(path, "<IngestMusicConfiguration><FfmpegPath>ffmpeg</FfmpegPath></IngestMusicConfiguration>");
        Assert.Throws<InvalidDataException>(() => IngestMusicConfiguration.Load(path));
    }

    [Fact]
    public void Configuration_SaveRoundTripsAllEditableValues()
    {
        using var tree = new TempTree();
        string path = tree.Path("saved.xml");
        var expected = new IngestMusicConfiguration
        {
            FfmpegPath = "custom-ffmpeg", AacDestination = tree.Path("aac-out"),
            CdDestination = tree.Path("cd-out"), PairedCdDestination = tree.Path("paired-out"),
            HighResolutionDestination = tree.Path("hires-out"), LengthLimit = 180,
            DiscNumLengthLimit = 160, AacEncoder = "libfdk_aac", AacBitrateKbps = 256,
        };

        expected.Save(path);
        var actual = IngestMusicConfiguration.Load(path);

        Assert.Equal(expected, actual);
    }

    private static IngestPlan ManualPlan(TempTree tree, IReadOnlyList<string> sources, bool requireApproval)
    {
        var config = IngestMusicConfiguration.Load(tree.Config());
        var tracks = sources.Select((path, i) => new IngestTrackPlan
        {
            Identity = $"track{i}", SourcePath = path, Title = $"Track {i + 1}", Artist = "Artist",
            AlbumArtist = "Artist", Album = "Album", TrackNumber = i + 1, TrackTotal = sources.Count,
            OriginalDiscNumber = 1, SampleRate = 96000, BitsPerSample = 24, Channels = 2,
            DurationInSeconds = 0, IsAlac = false, IsHighResolution = true,
        }).ToList();
        var outputs = tracks.SelectMany(t => new[]
        {
            new IngestOutputPlan { Identity = t.Identity, Kind = IngestOutputKind.CdFlac, Metadata = t,
                SourcePath = t.SourcePath, DestinationPath = tree.Path("paired", t.Identity + ".flac"), DeriveCd = true },
            new IngestOutputPlan { Identity = t.Identity, Kind = IngestOutputKind.Aac, Metadata = t,
                SourcePath = t.SourcePath, DestinationPath = tree.Path("aac", t.Identity + ".m4a") },
        }).ToList();
        var snapshots = sources.Select(p => { var f = new FileInfo(p); return new IngestFileSnapshot(p, f.Length, f.LastWriteTimeUtc); }).ToList();
        var album = new IngestAlbumPlan { Key = "album", Display = "Artist — Album", Tracks = tracks, Outputs = outputs,
            Sources = snapshots, HasHighResolution = true };
        return new IngestPlan
        {
            Request = new IngestRequest(tree.Path("incoming"), tree.Path("config.xml")), Configuration = config,
            Albums = [album], Actions = [], Conflicts = [], IgnoredFiles = [],
            RequiredApprovals = requireApproval ? [new IngestApprovalItem("album", album.Display, tracks.Select(t => t.Title).ToList())] : [],
        };
    }

    private static void WriteTags(string path, string album, string title, int track, int disc)
    {
        var media = MediaFile.GetFile(path);
        var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
        writer.SetField(TagFields.Album, album);
        writer.SetField(TagFields.Title, title);
        writer.SetField(TagFields.TrackNumber, track.ToString());
        writer.SetField(TagFields.DiscNumber, disc.ToString());
        writer.Save();
    }

    private sealed class FakeFfmpeg(TimeSpan? delay = null) : IFfmpegRunner
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        public int PreflightCalls { get; private set; }
        public Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default) { PreflightCalls++; return Task.CompletedTask; }
        public Task ConvertAlacToFlacAsync(string executable, string input, string output, CancellationToken ct = default) => Copy("sample.flac", output, ct);
        public Task DeriveCdFlacAsync(string executable, string input, string output, CancellationToken ct = default) => Copy("sample.flac", output, ct);
        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input, string output, CancellationToken ct = default) => Copy("sample_aac.m4a", output, ct);
        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input, CancellationToken ct = default) => Task.FromResult("SHA256=test");
        private async Task Copy(string fixture, string output, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, active);
            try
            {
                if (delay is { } d) await Task.Delay(d, ct);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "TestFiles", fixture), output);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ingest-tests-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(_root);
        public string Path(params string[] parts) => System.IO.Path.Combine([_root, .. parts]);
        public string Dir(params string[] parts) { string p = Path(parts); Directory.CreateDirectory(p); return p; }
        public string FileFromFixture(string dir, string name, string fixture)
        {
            string path = System.IO.Path.Combine(Dir(dir), name);
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "TestFiles", fixture), path);
            return path;
        }
        public string Config()
        {
            string path = Path("config.xml");
            if (!File.Exists(path)) File.WriteAllText(path, $"""
                <IngestMusicConfiguration>
                  <FfmpegPath>ffmpeg</FfmpegPath>
                  <AacDestination>{Path("aac")}</AacDestination>
                  <CdDestination>{Path("cd")}</CdDestination>
                  <PairedCdDestination>{Path("paired")}</PairedCdDestination>
                  <HighResolutionDestination>{Path("hires")}</HighResolutionDestination>
                  <LengthLimit>255</LengthLimit><DiscNumLengthLimit>255</DiscNumLengthLimit>
                  <AacEncoder>libfdk_aac</AacEncoder><AacBitrateKbps>256</AacBitrateKbps>
                </IngestMusicConfiguration>
                """);
            return path;
        }
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } try { Directory.Delete(_root + ".IngestMusic-quarantine", true); } catch { } }
    }
}
