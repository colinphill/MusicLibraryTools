using System.Net;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AcoustIdLookupServiceTests
{
    private const string RecordedResponse =
        """
        {
          "status": "ok",
          "results": [
            {
              "id": "9ff43b6a-4f16-427c-93c2-92307ca505e0",
              "score": 0.97,
              "recordings": [
                { "id": "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff" },
                { "id": "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff" },
                { "id": "fe31a507-835a-4f3f-90b0-c4eecd909a4d" }
              ]
            },
            {
              "id": "1191ad2f-b5c4-4c58-b21c-9247f47d146e",
              "score": 0.63,
              "recordings": [
                { "id": "3b9ba545-1efc-4baa-86de-4bd6c48f1b2e" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Response_PreservesAllCandidatesScoresAndRecordingIds()
    {
        AcoustIdCandidate[] candidates =
            [.. AcoustIdLookupService.ParseResponse(RecordedResponse)];

        Assert.Equal(2, candidates.Length);
        Assert.Equal(
            Guid.Parse("9ff43b6a-4f16-427c-93c2-92307ca505e0"),
            candidates[0].AcoustId);
        Assert.Equal(0.97, candidates[0].Score);
        Assert.Equal(
            [
                Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff"),
                Guid.Parse("fe31a507-835a-4f3f-90b0-c4eecd909a4d"),
            ],
            candidates[0].MusicBrainzRecordingIds);
        Assert.Equal(0.63, candidates[1].Score);
    }

    [Fact]
    public void Request_UsesLookupOnlyAndEscapesFingerprint()
    {
        var fingerprint = new AudioFingerprint(
            @"C:\Music\track.flac",
            "AQAD+value/with=symbols",
            TimeSpan.FromSeconds(123.6),
            124);

        Uri uri = AcoustIdLookupService.BuildLookupUri("client key", fingerprint);

        Assert.Equal("/v2/lookup", uri.AbsolutePath);
        Assert.Contains("client=client%20key", uri.Query);
        Assert.Contains("meta=recordingids", uri.Query);
        Assert.Contains("duration=124", uri.Query);
        Assert.Contains("fingerprint=AQAD%2Bvalue%2Fwith%3Dsymbols", uri.Query);
        Assert.DoesNotContain("submit", uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lookup_ReportsProgressAndUsesPersonalClientKey()
    {
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            settings.SetPreference(AcoustIdLookupService.ClientKeyPreference, "test-client");
            var transport = new RecordingTransport(
                new(HttpStatusCode.OK, RecordedResponse));
            var service = new AcoustIdLookupService(transport, settings);
            var progress = new RecordingProgress();
            var fingerprint = new AudioFingerprint(
                "track.flac", "AQAD", TimeSpan.FromSeconds(42), 42);

            AcoustIdLookupResult result =
                await service.LookupAsync(fingerprint, progress);

            Assert.Equal(2, result.Candidates.Length);
            Assert.NotNull(transport.Uri);
            Assert.Contains("client=test-client", transport.Uri.Query);
            Assert.Equal([0, 1], progress.Items.Select(item => item.Completed));
            Assert.All(progress.Items, item => Assert.Equal(1, item.Total));
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Lookup_RequiresClientKeyBeforeNetworkAccess()
    {
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            var transport = new RecordingTransport(
                new(HttpStatusCode.OK, RecordedResponse));
            var service = new AcoustIdLookupService(transport, settings);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.LookupAsync(new(
                    "track.flac", "AQAD", TimeSpan.FromSeconds(42), 42)));

            Assert.Null(transport.Uri);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Lookup_PropagatesCancellationToTransport()
    {
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            settings.SetPreference(AcoustIdLookupService.ClientKeyPreference, "test-client");
            var transport = new CancellingTransport();
            var service = new AcoustIdLookupService(transport, settings);
            using var cancellation = new CancellationTokenSource();

            Task lookup = service.LookupAsync(
                new("track.flac", "AQAD", TimeSpan.FromSeconds(42), 42),
                ct: cancellation.Token);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Fact]
    public async Task Lookup_RetriesRateLimitResponse()
    {
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            settings.SetPreference(AcoustIdLookupService.ClientKeyPreference, "test-client");
            var transport = new QueueTransport(
                new(HttpStatusCode.TooManyRequests, "", TimeSpan.Zero),
                new(HttpStatusCode.OK, """{"status":"ok","results":[]}"""));
            var service = new AcoustIdLookupService(transport, settings);

            AcoustIdLookupResult result = await service.LookupAsync(
                new("track.flac", "AQAD", TimeSpan.FromSeconds(42), 42));

            Assert.Empty(result.Candidates);
            Assert.Equal(2, transport.CallCount);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Theory]
    [InlineData("""{"status":"error","error":{"message":"bad fingerprint"}}""")]
    [InlineData("""{"status":"ok","results":[{"id":"not-a-guid","score":1.0}]}""")]
    [InlineData("not-json")]
    public void Response_RejectsProviderAndSchemaErrors(string response)
    {
        Assert.Throws<InvalidDataException>(() =>
            AcoustIdLookupService.ParseResponse(response));
    }

    [Fact]
    public async Task Discovery_ContinuesAfterPerFileFailureAndReportsBatchProgress()
    {
        string first = Path.GetFullPath("first.flac");
        string second = Path.GetFullPath("second.flac");
        var fingerprints = new FakeFingerprintService(second);
        var lookup = new FakeLookupService();
        var service = new AcoustIdDiscoveryService(fingerprints, lookup);
        var progress = new RecordingProgress();

        AcoustIdDiscoveryResult result =
            await service.DiscoverAsync([first, second], progress);

        Assert.Equal(2, result.Files.Length);
        Assert.NotNull(result.Files[0].Fingerprint);
        Assert.Single(result.Files[0].Lookup!.Candidates);
        Assert.Null(result.Files[1].Fingerprint);
        Assert.Contains(result.Files[1].Issues,
            issue => issue.Code == "acoustid.fingerprint");
        Assert.Equal(1, result.FingerprintedFileCount);
        Assert.Equal(1, result.MatchedFileCount);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(4, progress.Items[^1].Completed);
        Assert.Equal(4, progress.Items[^1].Total);
    }

    private sealed class RecordingTransport(AcoustIdHttpResult result)
        : IAcoustIdHttpTransport
    {
        public Uri? Uri { get; private set; }

        public Task<AcoustIdHttpResult> GetAsync(
            Uri uri,
            CancellationToken ct = default)
        {
            Uri = uri;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellingTransport : IAcoustIdHttpTransport
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AcoustIdHttpResult> GetAsync(
            Uri uri,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }
    }

    private sealed class QueueTransport(params AcoustIdHttpResult[] results)
        : IAcoustIdHttpTransport
    {
        private readonly Queue<AcoustIdHttpResult> _results = new(results);
        public int CallCount { get; private set; }

        public Task<AcoustIdHttpResult> GetAsync(
            Uri uri,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }

    private sealed class FakeFingerprintService(string failingPath)
        : IAudioFingerprintService
    {
        public Task<AudioFingerprint> GenerateAsync(
            string path,
            CancellationToken ct = default)
        {
            if (path.Equals(failingPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported test codec.");
            return Task.FromResult(new AudioFingerprint(
                path, "AQAD", TimeSpan.FromSeconds(42), 42));
        }
    }

    private sealed class FakeLookupService : IAcoustIdLookupService
    {
        public Task<AcoustIdLookupResult> LookupAsync(
            AudioFingerprint fingerprint,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new AcoustIdLookupResult(
                fingerprint,
                [new(
                    Guid.Parse("9ff43b6a-4f16-427c-93c2-92307ca505e0"),
                    0.91,
                    [Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff")])],
                DateTimeOffset.UtcNow));
    }
}
