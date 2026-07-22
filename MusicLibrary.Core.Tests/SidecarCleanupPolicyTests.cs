using MusicLibraryTools;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class SidecarCleanupPolicyTests
{
    [Theory]
    [InlineData(LibrarySidecarDisposition.Quarantine)]
    [InlineData(LibrarySidecarDisposition.Delete)]
    public async Task CleanupAppliesMatchingRuleAndPreservesUnknownFiles(
        LibrarySidecarDisposition disposition)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "sidecar-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string quarantineBase = root + ".IngestMusic-quarantine";
        try
        {
            string cue = Path.Combine(root, "album.cue");
            string unknown = Path.Combine(root, "vendor-private.bin");
            File.WriteAllText(cue, "cue");
            File.WriteAllText(unknown, "private");
            LibraryProfile profile = LibraryProfilePresets.Create(
                LibraryProfilePreset.Custom, "sidecar-cleanup", "Sidecar cleanup") with
            {
                Ingest = new(true, LibrarySourceDisposition.Preserve, false, []),
                Sidecars = new(
                    LibrarySidecarDisposition.Preserve,
                    [new("cue", "Cue sheet", true, ["*.cue"], disposition)]),
            };
            var configuration = new IngestMusicConfiguration
            {
                FfmpegPath = "ffmpeg",
                AacDestination = "",
                CdDestination = "",
                PairedCdDestination = "",
                HighResolutionDestination = "",
                RemoveNonMusicAfterIngest = true,
                ConfiguredSourceDisposition = LibrarySourceDisposition.Preserve,
                Profile = profile,
            };
            IngestFileSnapshot[] snapshots = [Snapshot(cue), Snapshot(unknown)];
            var plan = new IngestPlan
            {
                Request = new IngestRequest(root),
                Configuration = configuration,
                Albums = [],
                Files = [],
                RequiredApprovals = [],
                Conflicts = [],
                IgnoredFiles = snapshots.Select(item => item.Path).ToArray(),
                IgnoredFileSnapshots = snapshots,
            };

            Assert.True(plan.CanApply);
            IngestResult result = await new IngestMusicService(new UnusedFfmpeg())
                .ApplyAsync(plan, []);

            Assert.False(result.Cancelled);
            Assert.False(File.Exists(cue));
            Assert.True(File.Exists(unknown));
            string[] quarantined = Directory.Exists(quarantineBase)
                ? Directory.GetFiles(quarantineBase, "album.cue", SearchOption.AllDirectories)
                : [];
            if (disposition == LibrarySidecarDisposition.Quarantine)
                Assert.Single(quarantined);
            else
                Assert.Empty(quarantined);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(quarantineBase, recursive: true); } catch { }
        }
    }

    private static IngestFileSnapshot Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new(path, info.Length, info.LastWriteTimeUtc);
    }

    private sealed class UnusedFfmpeg : IFfmpegRunner
    {
        private static Task Unexpected() =>
            Task.FromException(new InvalidOperationException("FFmpeg should not be used."));

        public Task PreflightAsync(string executable, string requiredEncoder,
            CancellationToken ct = default) => Unexpected();
        public Task ConvertAlacToFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => Unexpected();
        public Task DeriveCdFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => Unexpected();
        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps,
            string input, string output, CancellationToken ct = default) => Unexpected();
        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input,
            CancellationToken ct = default) => Task.FromException<string>(
                new InvalidOperationException("FFmpeg should not be used."));
    }
}
