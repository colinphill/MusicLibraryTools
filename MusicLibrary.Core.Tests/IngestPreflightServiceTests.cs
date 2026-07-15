using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class IngestPreflightServiceTests
{
    [Fact]
    public async Task Check_ValidConfigurationPassesBeforeMediaEnumeration()
    {
        using var tree = new TempTree();
        string source = tree.Dir("source");
        string config = tree.Config();
        var ffmpeg = new StubFfmpeg();

        var result = await new IngestPreflightService(ffmpeg)
            .CheckAsync(new IngestRequest(source, config));

        Assert.True(result.CanProceed);
        Assert.Equal(1, ffmpeg.PreflightCalls);
        Assert.Contains(result.Checks, check => check.Name == "Path isolation" &&
            check.Severity == IngestPreflightSeverity.Pass);
    }

    [Fact]
    public async Task Check_OverlappingSourceIsBlockingButFfmpegFailureIsWarning()
    {
        using var tree = new TempTree();
        string source = tree.Dir("aac", "incoming");
        string config = tree.Config();

        var result = await new IngestPreflightService(new StubFfmpeg(fail: true))
            .CheckAsync(new IngestRequest(source, config));

        Assert.False(result.CanProceed);
        Assert.Contains(result.Checks, check => check.Name == "Path isolation" &&
            check.Severity == IngestPreflightSeverity.Error);
        Assert.Contains(result.Checks, check => check.Name == "ffmpeg" &&
            check.Severity == IngestPreflightSeverity.Warning);
    }

    private sealed class StubFfmpeg(bool fail = false) : IFfmpegRunner
    {
        public int PreflightCalls { get; private set; }
        public Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default)
        {
            PreflightCalls++;
            return fail ? Task.FromException(new InvalidOperationException("encoder missing")) : Task.CompletedTask;
        }
        public Task ConvertAlacToFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeriveCdFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input,
            string output, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"preflight-{Guid.NewGuid():N}");
        public TempTree() => Directory.CreateDirectory(_root);
        public string Dir(params string[] parts)
        {
            string path = Path.Combine([_root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }
        public string Config()
        {
            string path = Path.Combine(_root, "ingest.xml");
            new IngestMusicConfiguration
            {
                FfmpegPath = "ffmpeg",
                AacDestination = Dir("aac"),
                CdDestination = Dir("cd"),
                PairedCdDestination = Dir("paired"),
                HighResolutionDestination = Dir("hires"),
            }.Save(path);
            return path;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
