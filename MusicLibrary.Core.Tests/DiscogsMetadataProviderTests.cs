using System.Net;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DiscogsMetadataProviderTests
{
    [Fact]
    public async Task SearchUsesStoredCredentialAndParsesCandidates()
    {
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, SearchJson));
        var secrets = new SessionSecretStore();
        await secrets.WriteAsync(
            DiscogsMetadataProvider.TokenSecretKey,
            "test-token");
        var provider = new DiscogsMetadataProvider(transport, secrets);
        var reports = new List<OperationProgress>();

        DiscogsReleaseSearchResult result =
            await provider.SearchReleasesAsync(
                new(
                    Artist: "Massive Attack",
                    Album: "Mezzanine",
                    Barcode: "724384559925"),
                new SynchronousProgress<OperationProgress>(reports.Add));

        DiscogsReleaseCandidate release = Assert.Single(result.Releases);
        Assert.Equal(12345, release.ReleaseId);
        Assert.Equal("Massive Attack", release.ArtistCredit);
        Assert.Equal("Mezzanine", release.Title);
        Assert.Equal(1998, release.Year);
        Assert.Equal(["Virgin"], release.Labels);
        Assert.Equal(["7243 8 45599 2 5"], release.CatalogNumbers);
        Assert.Equal("test-token", transport.Token);
        Assert.Contains("type=release", transport.Uri!.Query);
        Assert.Contains("artist=Massive%20Attack", transport.Uri.Query);
        Assert.Contains("release_title=Mezzanine", transport.Uri.Query);
        Assert.Equal(OperationPhase.Completed, reports[^1].Phase);
    }

    [Fact]
    public async Task ReleaseLookupParsesEditionAndTrackDetails()
    {
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, ReleaseJson));
        var secrets = new SessionSecretStore();
        await secrets.WriteAsync(
            DiscogsMetadataProvider.TokenSecretKey,
            "test-token");
        var provider = new DiscogsMetadataProvider(transport, secrets);

        DiscogsReleaseCandidate release =
            await provider.GetReleaseAsync(12345);

        Assert.Equal(12345, release.ReleaseId);
        Assert.Equal(678, release.MasterId);
        Assert.Equal("Massive Attack", release.ArtistCredit);
        Assert.Equal("1998-04-20", release.Released);
        Assert.Equal(["Virgin"], release.Labels);
        Assert.Equal(["7243 8 45599 2 5"], release.CatalogNumbers);
        Assert.Equal(["1 CD (Album)"], release.Formats);
        Assert.Equal(["724384559925"], release.Barcodes);
        Assert.Equal(2, release.Tracks.Length);
        Assert.Equal("1", release.Tracks[0].Position);
        Assert.Equal("Angel", release.Tracks[0].Title);
        Assert.Equal("6:18", release.Tracks[0].Duration);
        Assert.Equal("Massive Attack", release.Tracks[0].ArtistCredit);
        Assert.Equal(
            "https://api.discogs.com/releases/12345",
            transport.Uri!.ToString());
    }

    [Fact]
    public async Task ReleaseIdSearchUsesAuthoritativeReleaseEndpoint()
    {
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, ReleaseJson));
        var secrets = new SessionSecretStore();
        await secrets.WriteAsync(
            DiscogsMetadataProvider.TokenSecretKey,
            "test-token",
            TestContext.Current.CancellationToken);
        var provider = new DiscogsMetadataProvider(transport, secrets);

        DiscogsReleaseSearchResult result =
            await provider.SearchReleasesAsync(
                new(ReleaseId: 12345),
                ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Releases);
        Assert.Equal(
            "https://api.discogs.com/releases/12345",
            transport.Uri!.ToString());
    }

    [Fact]
    public async Task MissingTokenIsReportedWithoutNetworkRequest()
    {
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, SearchJson));
        var provider = new DiscogsMetadataProvider(
            transport,
            new SessionSecretStore());

        await Assert.ThrowsAsync<DiscogsCredentialRequiredException>(
            () => provider.SearchReleasesAsync(
                new(Album: "Mezzanine")));

        Assert.Null(transport.Uri);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotHideBehindExpiredCache()
    {
        var cache = new MemoryCache();
        await cache.WriteAsync(
            SearchCacheKey(new(Album: "Mezzanine")),
            new DiscogsReleaseSearchResult(
                DiscogsMetadataProvider.ParseSearch(SearchJson),
                DateTimeOffset.UtcNow.AddDays(-40)),
            DateTimeOffset.UtcNow.AddDays(-40));
        var secrets = new SessionSecretStore();
        await secrets.WriteAsync(
            DiscogsMetadataProvider.TokenSecretKey,
            "expired-token");
        var provider = new DiscogsMetadataProvider(
            new RecordedTransport(
                new(HttpStatusCode.Unauthorized, "{}")),
            secrets,
            cache);

        await Assert.ThrowsAsync<DiscogsAuthenticationException>(
            () => provider.SearchReleasesAsync(
                new(Album: "Mezzanine")));
    }

    [Fact]
    public async Task OfflineModeUsesCacheWithoutReadingCredential()
    {
        var query = new DiscogsReleaseSearchQuery(Album: "Mezzanine");
        var cache = new MemoryCache();
        await cache.WriteAsync(
            SearchCacheKey(query),
            new DiscogsReleaseSearchResult(
                DiscogsMetadataProvider.ParseSearch(SearchJson),
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, "{}"));
        var provider = new DiscogsMetadataProvider(
            transport,
            new ThrowingSecretStore(),
            cache,
            new OfflinePolicy());

        DiscogsReleaseSearchResult result =
            await provider.SearchReleasesAsync(query);

        Assert.True(result.FromCache);
        Assert.True(result.OfflineFallback);
        Assert.Single(result.Releases);
        Assert.Null(transport.Uri);
    }

    [Fact]
    public async Task CancellationStopsBeforeCredentialOrNetworkAccess()
    {
        var transport = new RecordedTransport(
            new(HttpStatusCode.OK, SearchJson));
        var secrets = new ThrowingSecretStore();
        var provider = new DiscogsMetadataProvider(transport, secrets);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchReleasesAsync(
                new(Album: "Mezzanine"),
                ct: cancellation.Token));

        Assert.Null(transport.Uri);
    }

    [Fact]
    public void DescriptorAdvertisesCredentialedTypedCapabilities()
    {
        var provider = new DiscogsMetadataProvider(
            new RecordedTransport(new(HttpStatusCode.OK, "{}")),
            new SessionSecretStore());

        Assert.Equal("discogs", provider.Descriptor.Id);
        Assert.True(provider.Descriptor.RequiresCredential);
        Assert.True(provider.Descriptor.Capabilities.HasFlag(
            MetadataSourceCapabilities.ReleaseSearch));
        Assert.True(provider.Descriptor.Capabilities.HasFlag(
            MetadataSourceCapabilities.ReleaseDetails));
    }

    private static string SearchCacheKey(DiscogsReleaseSearchQuery query)
    {
        string canonical = string.Join(
            "\n",
            query.Artist?.Trim().ToUpperInvariant() ?? "",
            query.Album?.Trim().ToUpperInvariant() ?? "",
            query.Barcode?.Trim().ToUpperInvariant() ?? "",
            query.CatalogNumber?.Trim().ToUpperInvariant() ?? "",
            query.ReleaseId?.ToString() ?? "");
        return "discogs:search:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class RecordedTransport(DiscogsHttpResult response)
        : IDiscogsHttpTransport
    {
        public Uri? Uri { get; private set; }
        public string? Token { get; private set; }

        public Task<DiscogsHttpResult> GetAsync(
            Uri uri,
            string token,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Uri = uri;
            Token = token;
            return Task.FromResult(response);
        }
    }

    private sealed class MemoryCache : IMetadataSourceDataCache
    {
        private readonly Dictionary<string, Entry> _values = [];

        public Task<MusicBrainzCacheEntry<T>?> ReadAsync<T>(
            string key,
            TimeSpan maximumAge,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_values.TryGetValue(key, out Entry? entry))
                return Task.FromResult<MusicBrainzCacheEntry<T>?>(null);
            return Task.FromResult<MusicBrainzCacheEntry<T>?>(
                new(
                    (T)entry.Value,
                    entry.RetrievedAtUtc,
                    DateTimeOffset.UtcNow - entry.RetrievedAtUtc <=
                    maximumAge));
        }

        public Task WriteAsync<T>(
            string key,
            T value,
            DateTimeOffset retrievedAtUtc,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _values[key] = new(value!, retrievedAtUtc);
            return Task.CompletedTask;
        }

        private sealed record Entry(
            object Value,
            DateTimeOffset RetrievedAtUtc);
    }

    private sealed class OfflinePolicy : IProviderNetworkPolicy
    {
        public bool IsOffline => true;
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public SecretStoreKind Kind => SecretStoreKind.SessionOnly;
        public bool IsPersistent => false;

        public Task<string?> ReadAsync(
            string key,
            CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Credential store should not be read.");

        public Task WriteAsync(
            string key,
            string secret,
            CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task DeleteAsync(
            string key,
            CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private const string SearchJson =
        """
        {
          "pagination": { "page": 1, "pages": 1, "items": 1 },
          "results": [
            {
              "id": 12345,
              "type": "release",
              "master_id": 678,
              "title": "Massive Attack (2) - Mezzanine",
              "year": 1998,
              "country": "UK",
              "label": ["Virgin"],
              "catno": "7243 8 45599 2 5",
              "format": ["CD", "Album"],
              "genre": ["Electronic"],
              "style": ["Trip Hop"],
              "barcode": ["724384559925"],
              "uri": "/release/12345-Massive-Attack-Mezzanine",
              "thumb": "https://i.discogs.com/thumb.jpg",
              "cover_image": "https://i.discogs.com/cover.jpg"
            }
          ]
        }
        """;

    private const string ReleaseJson =
        """
        {
          "id": 12345,
          "master_id": 678,
          "title": "Mezzanine",
          "artists": [{ "name": "Massive Attack (2)" }],
          "year": 1998,
          "released": "1998-04-20",
          "country": "UK",
          "labels": [
            { "name": "Virgin", "catno": "7243 8 45599 2 5" }
          ],
          "formats": [
            { "name": "CD", "qty": "1", "descriptions": ["Album"] }
          ],
          "genres": ["Electronic"],
          "styles": ["Trip Hop"],
          "identifiers": [
            { "type": "Barcode", "value": "724384559925" }
          ],
          "uri": "https://www.discogs.com/release/12345",
          "images": [
            {
              "type": "primary",
              "uri": "https://i.discogs.com/cover.jpg"
            }
          ],
          "tracklist": [
            {
              "position": "1",
              "type_": "track",
              "title": "Angel",
              "duration": "6:18"
            },
            {
              "position": "2",
              "type_": "track",
              "title": "Risingson",
              "duration": "4:48"
            }
          ]
        }
        """;
}
