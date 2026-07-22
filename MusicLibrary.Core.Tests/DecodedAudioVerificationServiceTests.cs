using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DecodedAudioVerificationServiceTests
{
    [Fact]
    public async Task VerificationHashesEachUniquePathOnceAndReportsOnlyDifferingPairs()
    {
        var ffmpeg = new StubFfmpeg(new Dictionary<string, string>
        {
            ["a.flac"] = "same", ["b.flac"] = "same", ["c.flac"] = "different",
        });
        var service = new DecodedAudioVerificationService(ffmpeg);

        var report = await service.VerifyAsync("ffmpeg",
        [
            new("a.flac", "b.flac", "matching pair"),
            new("a.flac", "c.flac", "drifting pair"),
        ]);

        Assert.Equal(3, ffmpeg.Calls.Count);
        Assert.All(ffmpeg.Calls.GroupBy(path => path), group => Assert.Single(group));
        var finding = Assert.Single(report.Findings);
        Assert.Equal("Decoded-audio drift", finding.Problem);
        Assert.Contains("c.flac", finding.Description);
    }

    [Fact]
    public async Task VerificationSetupAndHashDispatchDoNotRunOnTheCallerContext()
    {
        var ffmpeg = new StubFfmpeg(new Dictionary<string, string>
        {
            ["a.flac"] = "same", ["b.flac"] = "same",
        });
        var service = new DecodedAudioVerificationService(ffmpeg);
        SynchronizationContext? previous = SynchronizationContext.Current;
        var callerContext = new SynchronizationContext();
        Task<AnalysisReport> task;
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            task = service.VerifyAsync("ffmpeg", [new("a.flac", "b.flac", "pair")]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await task;

        Assert.All(ffmpeg.SynchronizationContexts, Assert.Null);
    }

    private sealed class StubFfmpeg(IReadOnlyDictionary<string, string> hashes) : IFfmpegRunner
    {
        public List<string> Calls { get; } = [];
        public List<SynchronizationContext?> SynchronizationContexts { get; } = [];
        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input,
            CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add(input);
                SynchronizationContexts.Add(SynchronizationContext.Current);
            }
            return Task.FromResult(hashes[input]);
        }
        public Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ConvertAlacToFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeriveCdFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input,
            string output, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
