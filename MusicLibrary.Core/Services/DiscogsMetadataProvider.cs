using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record DiscogsReleaseSearchQuery(
    string? Artist = null,
    string? Album = null,
    string? Barcode = null,
    string? CatalogNumber = null,
    long? ReleaseId = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Artist) &&
        string.IsNullOrWhiteSpace(Album) &&
        string.IsNullOrWhiteSpace(Barcode) &&
        string.IsNullOrWhiteSpace(CatalogNumber) &&
        ReleaseId is null;
}

public sealed record DiscogsTrackCandidate(
    string Position,
    string Title,
    string? Duration,
    string ArtistCredit);

public sealed record DiscogsReleaseCandidate(
    long ReleaseId,
    long? MasterId,
    string Title,
    string ArtistCredit,
    int? Year,
    string? Released,
    string? Country,
    ImmutableArray<string> Labels,
    ImmutableArray<string> CatalogNumbers,
    ImmutableArray<string> Formats,
    ImmutableArray<string> Genres,
    ImmutableArray<string> Styles,
    ImmutableArray<string> Barcodes,
    Uri? WebUri,
    Uri? ThumbnailUri,
    Uri? CoverImageUri,
    ImmutableArray<DiscogsTrackCandidate> Tracks);

public sealed record DiscogsReleaseSearchResult(
    ImmutableArray<DiscogsReleaseCandidate> Releases,
    DateTimeOffset RetrievedAtUtc,
    bool FromCache = false,
    bool OfflineFallback = false);

public interface IDiscogsMetadataProvider : IMetadataSourceProvider
{
    Task<DiscogsReleaseSearchResult> SearchReleasesAsync(
        DiscogsReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<DiscogsReleaseCandidate> GetReleaseAsync(
        long releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<CoverArtDownload> DownloadPrimaryArtworkAsync(
        DiscogsReleaseCandidate release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record DiscogsHttpResult(
    HttpStatusCode StatusCode,
    string Content,
    TimeSpan? RetryAfter = null);

public interface IDiscogsHttpTransport
{
    Task<DiscogsHttpResult> GetAsync(
        Uri uri,
        string token,
        CancellationToken ct = default);
}

public sealed record DiscogsImageHttpResult(
    HttpStatusCode StatusCode,
    byte[] Content,
    string? ContentType = null,
    TimeSpan? RetryAfter = null);

public interface IDiscogsImageHttpTransport
{
    Task<DiscogsImageHttpResult> GetAsync(
        Uri uri,
        string token,
        CancellationToken ct = default);
}

public sealed class DiscogsHttpTransport : IDiscogsHttpTransport, IDisposable
{
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public DiscogsHttpTransport()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MusicLibraryManager", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "(+https://github.com/colinphill/MusicLibraryTools)"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<DiscogsHttpResult> GetAsync(
        Uri uri,
        string token,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Discogs", $"token={token}");
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        string content = await response.Content
            .ReadAsStringAsync(ct).ConfigureAwait(false);
        return new(
            response.StatusCode,
            content,
            response.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class DiscogsImageHttpTransport :
    IDiscogsImageHttpTransport, IDisposable
{
    private readonly HttpClient _client = new(
        new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public DiscogsImageHttpTransport()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MusicLibraryManager", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "(+https://github.com/colinphill/MusicLibraryTools)"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/*"));
    }

    public async Task<DiscogsImageHttpResult> GetAsync(
        Uri uri,
        string token,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Discogs", $"token={token}");
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        byte[] content = await response.Content
            .ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new(
            response.StatusCode,
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class DiscogsCredentialRequiredException(string message)
    : InvalidOperationException(message);

public sealed class DiscogsAuthenticationException(string message)
    : InvalidOperationException(message);

public sealed class DiscogsMetadataProvider(
    IDiscogsHttpTransport transport,
    ISecretStore secrets,
    IMetadataSourceDataCache? cache = null,
    IProviderNetworkPolicy? networkPolicy = null,
    IDiscogsImageHttpTransport? imageTransport = null,
    IArtworkDownloadCache? artworkCache = null) :
    IDiscogsMetadataProvider
{
    public const string TokenSecretKey = "discogs.personal-token";
    private const int MaximumResults = 100;
    private const int PageSize = 100;
    private static readonly TimeSpan CacheMaximumAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinimumRequestInterval =
        TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public MetadataSourceDescriptor Descriptor { get; } = new(
        "discogs",
        "Discogs",
        MetadataSourceCapabilities.ReleaseSearch |
        MetadataSourceCapabilities.ReleaseDetails |
        MetadataSourceCapabilities.ReleaseArtwork,
        RequiresCredential: true);

    public async Task<DiscogsReleaseSearchResult> SearchReleasesAsync(
        DiscogsReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty)
            throw new ArgumentException(
                "Provide at least one Discogs search field.", nameof(query));
        if (query.ReleaseId is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(query), "Discogs release IDs must be positive.");
        ct.ThrowIfCancellationRequested();
        if (query.ReleaseId is not null)
        {
            DiscogsReleaseCandidate release = await GetReleaseAsync(
                query.ReleaseId.Value, progress, ct).ConfigureAwait(false);
            return new([release], DateTimeOffset.UtcNow);
        }
        string cacheKey = "discogs:search:" + Hash(BuildCanonicalQuery(query));
        MusicBrainzCacheEntry<DiscogsReleaseSearchResult>? cached =
            await ReadCacheAsync<DiscogsReleaseSearchResult>(cacheKey, ct)
                .ConfigureAwait(false);
        if (cached?.IsFresh == true &&
            networkPolicy?.IsOffline != true)
            return Cached(cached.Value, progress, offline: false);
        if (networkPolicy?.IsOffline == true)
        {
            if (cached is not null)
                return Cached(cached.Value, progress, offline: true);
            throw new InvalidOperationException(
                "Offline mode is enabled and no cached Discogs search results are available.");
        }

        try
        {
            string token = await RequireTokenAsync(ct).ConfigureAwait(false);
            progress?.Report(new(
                OperationPhase.LoadingLibrary,
                Message: "Searching Discogs releases"));
            DiscogsHttpResult response = await SendWithRetryAsync(
                BuildSearchUri(query), token, ct).ConfigureAwait(false);
            EnsureSuccess(response, "search");
            DiscogsReleaseSearchResult result = new(
                ParseSearch(response.Content),
                DateTimeOffset.UtcNow);
            if (cache is not null)
                await cache.WriteAsync(
                    cacheKey, result, result.RetrievedAtUtc, ct)
                    .ConfigureAwait(false);
            progress?.Report(new(
                OperationPhase.Completed,
                result.Releases.Length,
                result.Releases.Length,
                Message:
                    $"Found {result.Releases.Length:N0} Discogs release(s)"));
            return result;
        }
        catch (Exception error) when (
            cached is not null &&
            error is not OperationCanceledException &&
            error is not DiscogsCredentialRequiredException &&
            error is not DiscogsAuthenticationException)
        {
            return Cached(cached.Value, progress, offline: false);
        }
    }

    public async Task<DiscogsReleaseCandidate> GetReleaseAsync(
        long releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (releaseId <= 0)
            throw new ArgumentOutOfRangeException(nameof(releaseId));
        string cacheKey = $"discogs:release:{releaseId}";
        MusicBrainzCacheEntry<DiscogsReleaseCandidate>? cached =
            await ReadCacheAsync<DiscogsReleaseCandidate>(cacheKey, ct)
                .ConfigureAwait(false);
        if (cached?.IsFresh == true &&
            networkPolicy?.IsOffline != true)
        {
            ReportCached(progress, "Discogs release details", offline: false);
            return cached.Value;
        }
        if (networkPolicy?.IsOffline == true)
        {
            if (cached is not null)
            {
                ReportCached(progress, "Discogs release details", offline: true);
                return cached.Value;
            }
            throw new InvalidOperationException(
                "Offline mode is enabled and no cached Discogs release details are available.");
        }

        try
        {
            string token = await RequireTokenAsync(ct).ConfigureAwait(false);
            progress?.Report(new(
                OperationPhase.LoadingLibrary,
                Message: "Loading complete Discogs release details"));
            DiscogsHttpResult response = await SendWithRetryAsync(
                new Uri($"https://api.discogs.com/releases/{releaseId}"),
                token,
                ct).ConfigureAwait(false);
            EnsureSuccess(response, "release lookup");
            DiscogsReleaseCandidate result =
                ParseRelease(response.Content);
            if (cache is not null)
                await cache.WriteAsync(
                    cacheKey, result, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
            progress?.Report(new(
                OperationPhase.Completed,
                1,
                1,
                Message: "Loaded Discogs release details"));
            return result;
        }
        catch (Exception error) when (
            cached is not null &&
            error is not OperationCanceledException &&
            error is not DiscogsCredentialRequiredException &&
            error is not DiscogsAuthenticationException)
        {
            ReportCached(
                progress,
                "Discogs is unavailable; using cached release details",
                offline: false);
            return cached.Value;
        }
    }

    public async Task<CoverArtDownload> DownloadPrimaryArtworkAsync(
        DiscogsReleaseCandidate release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        Uri uri = release.CoverImageUri ??
            throw new InvalidOperationException(
                "The selected Discogs release has no primary image.");
        if (artworkCache is not null)
        {
            CoverArtDownload? cached =
                await artworkCache.ReadAsync(uri, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                progress?.Report(new(
                    OperationPhase.Completed,
                    1,
                    1,
                    Message: "Loaded Discogs artwork from cache"));
                return cached with { FromCache = true };
            }
        }
        if (networkPolicy?.IsOffline == true)
            throw new InvalidOperationException(
                "Offline mode is enabled and this Discogs image is not cached.");
        if (imageTransport is null)
            throw new InvalidOperationException(
                "Discogs artwork transport is unavailable.");
        string token = await RequireTokenAsync(ct).ConfigureAwait(false);
        progress?.Report(new(
            OperationPhase.LoadingLibrary,
            Message: "Downloading Discogs release artwork"));
        DiscogsImageHttpResult response =
            await SendImageWithRetryAsync(uri, token, ct)
                .ConfigureAwait(false);
        if (response.StatusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new DiscogsAuthenticationException(
                "Discogs rejected the stored personal access token.");
        if (!response.StatusCode.IsSuccess())
            throw new HttpRequestException(
                "Discogs artwork download failed with HTTP " +
                $"{(int)response.StatusCode}.");
        if (response.Content.Length == 0)
            throw new InvalidDataException(
                "Discogs returned an empty artwork file.");
        var result = new CoverArtDownload(
            response.Content,
            response.ContentType ?? "application/octet-stream",
            FromCache: false);
        if (artworkCache is not null)
            await artworkCache.WriteAsync(uri, result, ct)
                .ConfigureAwait(false);
        progress?.Report(new(
            OperationPhase.Completed,
            response.Content.Length,
            response.Content.Length,
            Message:
                $"Downloaded {response.Content.Length:N0} bytes of Discogs artwork"));
        return result;
    }

    private async Task<string> RequireTokenAsync(CancellationToken ct)
    {
        string? token = await secrets.ReadAsync(TokenSecretKey, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new DiscogsCredentialRequiredException(
                "A Discogs personal access token is required. Add it in Settings.");
        return token;
    }

    private async Task<DiscogsHttpResult> SendWithRetryAsync(
        Uri uri,
        string token,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            DiscogsHttpResult result;
            try
            {
                TimeSpan wait = MinimumRequestInterval -
                    (DateTimeOffset.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                result = await transport.GetAsync(uri, token, ct)
                    .ConfigureAwait(false);
                _lastRequestUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                _requestGate.Release();
            }
            if (result.StatusCode != HttpStatusCode.TooManyRequests &&
                (int)result.StatusCode < 500)
                return result;
            if (attempt < 2)
            {
                TimeSpan delay = result.RetryAfter ??
                    TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException(
            "Discogs retry loop ended unexpectedly.");
    }

    private async Task<DiscogsImageHttpResult> SendImageWithRetryAsync(
        Uri uri,
        string token,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            DiscogsImageHttpResult result;
            try
            {
                TimeSpan wait = MinimumRequestInterval -
                    (DateTimeOffset.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                result = await imageTransport!.GetAsync(uri, token, ct)
                    .ConfigureAwait(false);
                _lastRequestUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                _requestGate.Release();
            }
            if (result.StatusCode != HttpStatusCode.TooManyRequests &&
                (int)result.StatusCode < 500)
                return result;
            if (attempt < 2)
            {
                TimeSpan delay = result.RetryAfter ??
                    TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException(
            "Discogs artwork retry loop ended unexpectedly.");
    }

    private static void EnsureSuccess(
        DiscogsHttpResult response,
        string operation)
    {
        if (response.StatusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new DiscogsAuthenticationException(
                "Discogs rejected the stored personal access token.");
        if (!response.StatusCode.IsSuccess())
            throw new HttpRequestException(
                $"Discogs {operation} failed with HTTP " +
                $"{(int)response.StatusCode}.");
    }

    private static Uri BuildSearchUri(DiscogsReleaseSearchQuery query)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("type", "release"),
            new("per_page", PageSize.ToString()),
            new("page", "1"),
        };
        Add("artist", query.Artist);
        Add("release_title", query.Album);
        Add("barcode", query.Barcode);
        Add("catno", query.CatalogNumber);
        if (query.ReleaseId is not null)
            Add("release_id", query.ReleaseId.Value.ToString());
        string encoded = string.Join("&", values.Select(value =>
            $"{Uri.EscapeDataString(value.Key)}=" +
            Uri.EscapeDataString(value.Value)));
        return new($"https://api.discogs.com/database/search?{encoded}");

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(new(key, value.Trim()));
        }
    }

    private static string BuildCanonicalQuery(DiscogsReleaseSearchQuery query) =>
        string.Join(
            "\n",
            query.Artist?.Trim().ToUpperInvariant() ?? "",
            query.Album?.Trim().ToUpperInvariant() ?? "",
            query.Barcode?.Trim().ToUpperInvariant() ?? "",
            query.CatalogNumber?.Trim().ToUpperInvariant() ?? "",
            query.ReleaseId?.ToString() ?? "");

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static ImmutableArray<DiscogsReleaseCandidate> ParseSearch(
        string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty(
                    "results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
                return [];
            return results.EnumerateArray()
                .Where(item =>
                    ReadString(item, "type")?.Equals(
                        "release", StringComparison.OrdinalIgnoreCase) != false)
                .Take(MaximumResults)
                .Select(ParseSearchItem)
                .Where(item => item.ReleaseId > 0)
                .ToImmutableArray();
        }
        catch (Exception error) when (
            error is JsonException or
                InvalidOperationException or
                FormatException)
        {
            throw new InvalidDataException(
                "Discogs returned malformed search JSON.", error);
        }
    }

    public static DiscogsReleaseCandidate ParseRelease(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement item = document.RootElement;
            long id = ReadInt64(item, "id") ??
                throw new InvalidDataException(
                    "Discogs release JSON does not contain an ID.");
            string title = ReadString(item, "title") ?? $"Release {id}";
            string artists = JoinNames(item, "artists");
            ImmutableArray<string> labels = ReadObjectStrings(
                item, "labels", "name");
            ImmutableArray<string> catalogNumbers = ReadObjectStrings(
                item, "labels", "catno");
            ImmutableArray<DiscogsTrackCandidate> tracks =
                ParseTracks(item, artists);
            return new(
                id,
                ReadInt64(item, "master_id"),
                title,
                artists,
                ReadInt32(item, "year"),
                ReadString(item, "released"),
                ReadString(item, "country"),
                labels,
                catalogNumbers,
                ReadFormats(item),
                ReadStrings(item, "genres"),
                ReadStrings(item, "styles"),
                ReadIdentifiers(item, "Barcode"),
                ReadUri(item, "uri"),
                null,
                ReadImageUri(item),
                tracks);
        }
        catch (Exception error) when (
            error is JsonException or
                InvalidOperationException or
                FormatException)
        {
            throw new InvalidDataException(
                "Discogs returned malformed release JSON.", error);
        }
    }

    private static DiscogsReleaseCandidate ParseSearchItem(JsonElement item)
    {
        long id = ReadInt64(item, "id") ?? 0;
        string combinedTitle =
            ReadString(item, "title") ?? $"Release {id}";
        (string artists, string title) = SplitTitle(combinedTitle);
        return new(
            id,
            ReadInt64(item, "master_id"),
            title,
            artists,
            ReadInt32(item, "year"),
            null,
            ReadString(item, "country"),
            ReadStrings(item, "label"),
            Single(ReadString(item, "catno")),
            ReadStrings(item, "format"),
            ReadStrings(item, "genre"),
            ReadStrings(item, "style"),
            ReadStrings(item, "barcode"),
            ReadUri(item, "uri"),
            ReadUri(item, "thumb"),
            ReadUri(item, "cover_image"),
            []);
    }

    private static ImmutableArray<DiscogsTrackCandidate> ParseTracks(
        JsonElement item,
        string releaseArtists)
    {
        if (!item.TryGetProperty("tracklist", out JsonElement tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
            return [];
        return tracks.EnumerateArray()
            .Where(track =>
                ReadString(track, "type_")?.Equals(
                    "track", StringComparison.OrdinalIgnoreCase) != false)
            .Select(track => new DiscogsTrackCandidate(
                ReadString(track, "position") ?? "",
                ReadString(track, "title") ?? "",
                ReadString(track, "duration"),
                JoinNames(track, "artists") is { Length: > 0 } artists
                    ? artists
                    : releaseArtists))
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ReadFormats(JsonElement item)
    {
        if (!item.TryGetProperty("formats", out JsonElement formats) ||
            formats.ValueKind != JsonValueKind.Array)
            return [];
        return formats.EnumerateArray()
            .Select(format =>
            {
                string name = ReadString(format, "name") ?? "";
                string? quantity = ReadString(format, "qty");
                string details = string.Join(
                    ", ", ReadStrings(format, "descriptions"));
                return string.Join(
                    " ",
                    new[] { quantity, name }
                        .Where(value => !string.IsNullOrWhiteSpace(value))) +
                    (details.Length == 0 ? "" : $" ({details})");
            })
            .Where(value => value.Length > 0)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ReadIdentifiers(
        JsonElement item,
        string type)
    {
        if (!item.TryGetProperty("identifiers", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Where(value => string.Equals(
                ReadString(value, "type"),
                type,
                StringComparison.OrdinalIgnoreCase))
            .Select(value => ReadString(value, "value"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ReadObjectStrings(
        JsonElement item,
        string property,
        string child)
    {
        if (!item.TryGetProperty(property, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Select(value => ReadString(value, child))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ReadStrings(
        JsonElement item,
        string property)
    {
        if (!item.TryGetProperty(property, out JsonElement value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return Single(value.GetString());
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)
            .ToImmutableArray();
    }

    private static string JoinNames(JsonElement item, string property) =>
        string.Join(
            ", ",
            ReadObjectStrings(item, property, "name")
                .Select(RemoveDiscogsDisambiguator));

    private static string RemoveDiscogsDisambiguator(string value)
    {
        int suffix = value.LastIndexOf(" (", StringComparison.Ordinal);
        return suffix > 0 && value.EndsWith(')') &&
               int.TryParse(
                   value.AsSpan(suffix + 2, value.Length - suffix - 3),
                   out _)
            ? value[..suffix]
            : value;
    }

    private static (string Artist, string Title) SplitTitle(string value)
    {
        int separator = value.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (RemoveDiscogsDisambiguator(value[..separator]),
                value[(separator + 3)..])
            : ("", value);
    }

    private static ImmutableArray<string> Single(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string? ReadString(
        JsonElement item,
        string property) =>
        item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt64(out long result)
            ? result
            : null;

    private static Uri? ReadUri(JsonElement item, string property)
    {
        string? value = ReadString(item, property);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute))
            return absolute;
        return Uri.TryCreate(
            new Uri("https://www.discogs.com"), value, out Uri? relative)
            ? relative
            : null;
    }

    private static Uri? ReadImageUri(JsonElement item)
    {
        if (!item.TryGetProperty("images", out JsonElement images) ||
            images.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement? primary = images.EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault(image => string.Equals(
                ReadString(image!.Value, "type"),
                "primary",
                StringComparison.OrdinalIgnoreCase));
        JsonElement? selected = primary ?? images.EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault();
        return selected is null ? null : ReadUri(selected.Value, "uri");
    }

    private async Task<MusicBrainzCacheEntry<T>?> ReadCacheAsync<T>(
        string key,
        CancellationToken ct) =>
        cache is null
            ? null
            : await cache.ReadAsync<T>(key, CacheMaximumAge, ct)
                .ConfigureAwait(false);

    private static DiscogsReleaseSearchResult Cached(
        DiscogsReleaseSearchResult value,
        IProgress<OperationProgress>? progress,
        bool offline)
    {
        ReportCached(
            progress,
            offline
                ? "Offline mode; using cached Discogs search results"
                : "Using cached Discogs search results",
            offline);
        return value with
        {
            FromCache = true,
            OfflineFallback = offline,
        };
    }

    private static void ReportCached(
        IProgress<OperationProgress>? progress,
        string message,
        bool offline) =>
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            Message: message +
                (offline ? " (network disabled)" : "")));
}

internal static class HttpStatusCodeExtensions
{
    public static bool IsSuccess(this HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and <= 299;
}
