using System.Net;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MusicBrainzMetadataProviderTests
{
    private const string RecordedReleasePage =
        """
        {
          "release-count": 2,
          "release-offset": 0,
          "releases": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Example Album",
              "date": "2001-02-03",
              "country": "US",
              "status": "Official",
              "barcode": "0123456789012",
              "artist-credit": [
                { "name": "Example Artist", "joinphrase": "" }
              ],
              "release-group": {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "title": "Example Album",
                "primary-type": "Album"
              },
              "label-info": [
                {
                  "catalog-number": "CAT-001",
                  "label": { "name": "Example Label" }
                }
              ],
              "media": [
                {
                  "position": 1,
                  "format": "CD",
                  "tracks": [
                    {
                      "id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                      "position": 1,
                      "number": "1",
                      "title": "Example Song",
                      "length": 241000,
                      "artist-credit": [{ "name": "Example Artist" }],
                      "recording": {
                        "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                        "title": "Example Song"
                      }
                    }
                  ]
                }
              ]
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "title": "Example Album",
              "date": "2002",
              "country": "GB",
              "status": "Official",
              "artist-credit": [{ "name": "Example Artist" }],
              "release-group": {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "title": "Example Album",
                "primary-type": "Album"
              },
              "media": [
                { "position": 1, "format": "Digital Media", "tracks": [] }
              ]
            }
          ]
        }
        """;
    private const string RecordedReleaseDocument =
        """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "title": "Example Album",
          "date": "2001-02-03",
          "country": "US",
          "status": "Official",
          "artist-credit": [{ "name": "Example Artist" }],
          "release-group": {
            "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "title": "Example Album",
            "primary-type": "Album"
          },
          "media": [{
            "position": 1,
            "format": "CD",
            "tracks": [{
              "id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "position": 1,
              "number": "1",
              "title": "Example Song",
              "length": 241000,
              "recording": {
                "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "title": "Example Song",
                "artist-credit": [{ "name": "Example Artist" }]
              }
            }]
          }]
        }
        """;

    [Fact]
    public void RecordedPage_PreservesEditionAndTrackDetails()
    {
        MusicBrainzReleasePage page =
            MusicBrainzMetadataProvider.ParseReleasePage(RecordedReleasePage);

        Assert.Equal(2, page.Total);
        MusicBrainzReleaseCandidate first = page.Releases[0];
        Assert.Equal("Example Album", first.Title);
        Assert.Equal("Example Artist", first.ArtistCredit);
        Assert.Equal("2001-02-03", first.Date);
        Assert.Equal("US", first.Country);
        Assert.Equal("Official", first.Status);
        Assert.Equal("0123456789012", first.Barcode);
        Assert.Equal("Example Label", first.Label);
        Assert.Equal("CAT-001", first.CatalogNumber);
        Assert.Equal(["CD"], first.Formats);
        MusicBrainzTrackCandidate track = Assert.Single(first.Tracks);
        Assert.Equal(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            track.TrackId);
        Assert.Equal(1, track.MediumPosition);
        Assert.Equal(1, track.TrackPosition);
        Assert.Equal(241000, track.LengthMilliseconds);
        Assert.Equal(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            track.RecordingId);
    }

    [Fact]
    public async Task Provider_ReportsProgressAndUsesRecordingBrowseEndpoint()
    {
        var transport = new RecordingTransport(new(
            HttpStatusCode.OK, RecordedReleasePage));
        var provider = new MusicBrainzMetadataProvider(transport);
        var progress = new RecordingProgress();
        Guid recordingId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        MusicBrainzReleaseResult result =
            await provider.ResolveRecordingAsync(recordingId, progress);

        Assert.Equal(2, result.Releases.Length);
        Assert.Contains($"recording={recordingId:D}", transport.Uri!.Query);
        Assert.Contains("recordings", transport.Uri.Query);
        Assert.Equal(2, progress.Items[^1].Completed);
        Assert.Equal(2, progress.Items[^1].Total);
    }

    [Fact]
    public void BrowseUri_RequestsJsonAndEditionIncludes()
    {
        Uri uri = MusicBrainzMetadataProvider.BuildBrowseUri(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal("musicbrainz.org", uri.Host);
        Assert.Equal("/ws/2/release", uri.AbsolutePath);
        Assert.Contains("fmt=json", uri.Query);
        Assert.Contains("release-groups", uri.Query);
        Assert.Contains("media", uri.Query);
        Assert.Contains("labels", uri.Query);
    }

    [Fact]
    public async Task Search_UsesTypedFieldsAndReturnsEditionSummaries()
    {
        var transport = new RecordingTransport(new(
            HttpStatusCode.OK, RecordedReleasePage));
        var provider = new MusicBrainzMetadataProvider(transport);
        var query = new MusicBrainzReleaseSearchQuery(
            Artist: "Example Artist",
            Album: "Example Album",
            Barcode: "0123456789012",
            CatalogNumber: "CAT-001",
            ReleaseId:
                Guid.Parse("11111111-1111-1111-1111-111111111111"));

        MusicBrainzReleaseSearchResult result =
            await provider.SearchReleasesAsync(query);

        Assert.Equal(2, result.Releases.Length);
        string decoded = Uri.UnescapeDataString(transport.Uri!.Query);
        Assert.Contains("artist:\"Example Artist\"", decoded);
        Assert.Contains("release:\"Example Album\"", decoded);
        Assert.Contains("barcode:\"0123456789012\"", decoded);
        Assert.Contains("catno:\"CAT-001\"", decoded);
        Assert.Contains(
            "reid:11111111-1111-1111-1111-111111111111", decoded);
    }

    [Fact]
    public void SearchPage_UsesSearchCountAndOffsetProperties()
    {
        string searchPage = RecordedReleasePage
            .Replace("\"release-count\"", "\"count\"",
                StringComparison.Ordinal)
            .Replace("\"release-offset\"", "\"offset\"",
                StringComparison.Ordinal);

        MusicBrainzReleasePage page =
            MusicBrainzMetadataProvider.ParseReleasePage(searchPage);

        Assert.Equal(2, page.Total);
        Assert.Equal(0, page.Offset);
        Assert.Equal(2, page.Releases.Length);
    }

    [Fact]
    public async Task ReleaseLookup_LoadsCompleteTrackDetails()
    {
        var transport = new RecordingTransport(new(
            HttpStatusCode.OK, RecordedReleaseDocument));
        var provider = new MusicBrainzMetadataProvider(transport);
        Guid releaseId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");

        MusicBrainzReleaseCandidate result =
            await provider.GetReleaseAsync(releaseId);

        Assert.Equal(releaseId, result.ReleaseId);
        Assert.Single(result.Tracks);
        Assert.Contains($"/release/{releaseId:D}", transport.Uri!.AbsolutePath);
        Assert.Contains("recordings", transport.Uri.Query);
    }

    [Fact]
    public async Task ReleaseCache_PersistsAcrossProviderRestarts()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-musicbrainz-cache-" + Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(root, "metadata.db");
        Guid releaseId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        try
        {
            var online = new RecordingTransport(new(
                HttpStatusCode.OK, RecordedReleaseDocument));
            var first = new MusicBrainzMetadataProvider(
                online,
                new MusicBrainzReleaseCache(databasePath));

            MusicBrainzReleaseCandidate downloaded =
                await first.GetReleaseAsync(releaseId);

            Assert.Equal(1, online.RequestCount);
            var offline = new RecordingTransport(new(
                HttpStatusCode.ServiceUnavailable, ""));
            var restarted = new MusicBrainzMetadataProvider(
                offline,
                new MusicBrainzReleaseCache(databasePath));
            var progress = new RecordingProgress();

            MusicBrainzReleaseCandidate cached =
                await restarted.GetReleaseAsync(releaseId, progress);

            Assert.Equal(downloaded.ReleaseId, cached.ReleaseId);
            Assert.Equal(downloaded.Title, cached.Title);
            Assert.Equal(
                downloaded.Tracks.Select(track => track.TrackId),
                cached.Tracks.Select(track => track.TrackId));
            Assert.Equal(0, offline.RequestCount);
            Assert.Contains(
                "cached",
                progress.Items[^1].Message!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Provider_UsesExpiredCacheWhenMusicBrainzIsOffline()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-musicbrainz-stale-" + Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(root, "metadata.db");
        Guid recordingId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        try
        {
            var cache = new MusicBrainzReleaseCache(databasePath);
            MusicBrainzReleasePage page =
                MusicBrainzMetadataProvider.ParseReleasePage(
                    RecordedReleasePage);
            var cachedResult = new MusicBrainzReleaseResult(
                recordingId,
                page.Releases,
                DateTimeOffset.UtcNow.AddDays(-60));
            await cache.WriteAsync(
                $"recording:{recordingId:D}",
                cachedResult,
                cachedResult.RetrievedAtUtc);
            var unavailable = new RecordingTransport(new(
                HttpStatusCode.BadGateway, ""));
            var provider = new MusicBrainzMetadataProvider(
                unavailable, cache);
            var progress = new RecordingProgress();

            MusicBrainzReleaseResult result =
                await provider.ResolveRecordingAsync(recordingId, progress);

            Assert.Equal(cachedResult.RecordingId, result.RecordingId);
            Assert.Equal(
                cachedResult.Releases.Select(release => release.ReleaseId),
                result.Releases.Select(release => release.ReleaseId));
            Assert.Equal(
                cachedResult.RetrievedAtUtc,
                result.RetrievedAtUtc,
                TimeSpan.FromMilliseconds(1));
            Assert.Equal(3, unavailable.RequestCount);
            Assert.Contains(
                "unavailable",
                progress.Items[^1].Message!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExplicitOfflineMode_UsesCacheWithoutNetwork()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-musicbrainz-offline-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "settings.json");
        string databasePath = Path.Combine(root, "metadata.db");
        Guid releaseId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        try
        {
            Directory.CreateDirectory(root);
            var settings = new AppSettings(statePath);
            var onlineTransport = new RecordingTransport(new(
                HttpStatusCode.OK, RecordedReleaseDocument));
            var online = new MusicBrainzMetadataProvider(
                onlineTransport,
                new MusicBrainzReleaseCache(databasePath),
                new ProviderNetworkPolicy(settings));
            await online.GetReleaseAsync(releaseId);
            settings.SetPreference(
                ProviderNetworkPolicy.OfflinePreferenceKey,
                bool.TrueString);
            var offlineTransport = new RecordingTransport(new(
                HttpStatusCode.ServiceUnavailable, ""));
            var progress = new RecordingProgress();
            var offline = new MusicBrainzMetadataProvider(
                offlineTransport,
                new MusicBrainzReleaseCache(databasePath),
                new ProviderNetworkPolicy(settings));

            MusicBrainzReleaseCandidate cached =
                await offline.GetReleaseAsync(releaseId, progress);

            Assert.Equal(releaseId, cached.ReleaseId);
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

    [Theory]
    [InlineData("not-json")]
    [InlineData(
        """{"release-count":1,"release-offset":0,"releases":[{"id":"bad"}]}""")]
    public void Page_RejectsMalformedProviderData(string content)
    {
        Assert.Throws<InvalidDataException>(() =>
            MusicBrainzMetadataProvider.ParseReleasePage(content));
    }

    private sealed class RecordingTransport(MusicBrainzHttpResult result)
        : IMusicBrainzHttpTransport
    {
        public Uri? Uri { get; private set; }
        public int RequestCount { get; private set; }

        public Task<MusicBrainzHttpResult> GetAsync(
            Uri uri,
            CancellationToken ct = default)
        {
            Uri = uri;
            RequestCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
