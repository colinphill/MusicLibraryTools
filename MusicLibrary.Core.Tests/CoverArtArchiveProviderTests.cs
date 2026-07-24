using System.Net;
using System.Text;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class CoverArtArchiveProviderTests
{
    private static readonly Guid ReleaseId =
        Guid.Parse("76df3287-6cda-33eb-8e9a-044b5e15ffdd");
    private const string RecordedResponse =
        """
        {
          "release": "https://musicbrainz.org/release/76df3287-6cda-33eb-8e9a-044b5e15ffdd",
          "images": [
            {
              "types": ["Front", "Booklet"],
              "front": true,
              "back": false,
              "image": "https://coverartarchive.org/release/76df3287-6cda-33eb-8e9a-044b5e15ffdd/829521842.jpg",
              "comment": "Primary scan",
              "approved": true,
              "id": "829521842",
              "thumbnails": {
                "250": "https://coverartarchive.org/release/76df3287-6cda-33eb-8e9a-044b5e15ffdd/829521842-250.jpg",
                "500": "https://coverartarchive.org/release/76df3287-6cda-33eb-8e9a-044b5e15ffdd/829521842-500.jpg"
              }
            }
          ]
        }
        """;

    [Fact]
    public void RecordedResponse_PreservesImageRolesAndUrls()
    {
        CoverArtArchiveCandidate candidate = Assert.Single(
            CoverArtArchiveProvider.ParseRelease(
                ReleaseId, Encoding.UTF8.GetBytes(RecordedResponse)));

        Assert.Equal("829521842", candidate.Id);
        Assert.True(candidate.IsFront);
        Assert.False(candidate.IsBack);
        Assert.True(candidate.Approved);
        Assert.Equal(["Front", "Booklet"], candidate.Types);
        Assert.EndsWith("-250.jpg", candidate.ThumbnailUri!.AbsoluteUri);
        Assert.Equal("Primary scan", candidate.Comment);
    }

    [Fact]
    public async Task MissingRelease_ReturnsEmptyArtworkResult()
    {
        var provider = new CoverArtArchiveProvider(
            new RecordingTransport(new(
                HttpStatusCode.NotFound, [])),
            new MemoryCache());

        CoverArtArchiveResult result =
            await provider.GetReleaseArtworkAsync(ReleaseId);

        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task Download_UsesBoundedCacheAfterFirstRequest()
    {
        byte[] image = [1, 2, 3, 4];
        var transport = new RecordingTransport(new(
            HttpStatusCode.OK, image, "image/jpeg"));
        string root = Path.Combine(
            Path.GetTempPath(), "mlm-caa-tests", Guid.NewGuid().ToString("N"));
        var cache = new ArtworkDownloadCache(root, 1024);
        var provider = new CoverArtArchiveProvider(transport, cache);
        CoverArtArchiveCandidate candidate = Candidate();

        CoverArtDownload first =
            await provider.DownloadAsync(candidate, thumbnail: true);
        CoverArtDownload second =
            await provider.DownloadAsync(candidate, thumbnail: true);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(image, second.Data);
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task Lookup_ReportsProgressAndObservesCancellation()
    {
        var progress = new RecordingProgress();
        var provider = new CoverArtArchiveProvider(
            new RecordingTransport(new(
                HttpStatusCode.OK,
                Encoding.UTF8.GetBytes(RecordedResponse),
                "application/json")),
            new MemoryCache());

        CoverArtArchiveResult result =
            await provider.GetReleaseArtworkAsync(ReleaseId, progress);

        Assert.Single(result.Images);
        Assert.Equal(1, progress.Items[^1].Completed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetReleaseArtworkAsync(
                ReleaseId, ct: cancellation.Token));
    }

    [Fact]
    public async Task ManifestCache_SupportsExplicitOfflineMode()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-caa-manifest-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "settings.json");
        string databasePath = Path.Combine(root, "providers.db");
        try
        {
            Directory.CreateDirectory(root);
            var settings = new AppSettings(statePath);
            var onlineTransport = new RecordingTransport(new(
                HttpStatusCode.OK,
                Encoding.UTF8.GetBytes(RecordedResponse),
                "application/json"));
            var online = new CoverArtArchiveProvider(
                onlineTransport,
                new MemoryCache(),
                new MusicBrainzReleaseCache(databasePath),
                new ProviderNetworkPolicy(settings));

            CoverArtArchiveResult downloaded =
                await online.GetReleaseArtworkAsync(ReleaseId);

            Assert.Single(downloaded.Images);
            Assert.Equal(1, onlineTransport.RequestCount);
            settings.SetPreference(
                ProviderNetworkPolicy.OfflinePreferenceKey,
                bool.TrueString);
            var offlineTransport = new RecordingTransport(new(
                HttpStatusCode.ServiceUnavailable, []));
            var progress = new RecordingProgress();
            var offline = new CoverArtArchiveProvider(
                offlineTransport,
                new MemoryCache(),
                new MusicBrainzReleaseCache(databasePath),
                new ProviderNetworkPolicy(settings));

            CoverArtArchiveResult cached =
                await offline.GetReleaseArtworkAsync(
                    ReleaseId, progress);

            Assert.Single(cached.Images);
            Assert.Equal(0, offlineTransport.RequestCount);
            Assert.Contains(
                "Offline mode",
                progress.Items[^1].Message!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MalformedRecordedManifest_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            CoverArtArchiveProvider.ParseRelease(
                ReleaseId,
                Encoding.UTF8.GetBytes("not-json")));
    }

    private static CoverArtArchiveCandidate Candidate() => new(
        ReleaseId,
        "829521842",
        new("https://coverartarchive.org/original.jpg"),
        new("https://coverartarchive.org/250.jpg"),
        ["Front"],
        true,
        false,
        true,
        null);

    private sealed class RecordingTransport(CoverArtArchiveHttpResult result)
        : ICoverArtArchiveHttpTransport
    {
        public int RequestCount { get; private set; }

        public Task<CoverArtArchiveHttpResult> GetAsync(
            Uri uri,
            string accept,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryCache : IArtworkDownloadCache
    {
        public Task<CoverArtDownload?> ReadAsync(
            Uri uri,
            CancellationToken ct = default) =>
            Task.FromResult<CoverArtDownload?>(null);

        public Task WriteAsync(
            Uri uri,
            CoverArtDownload value,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
