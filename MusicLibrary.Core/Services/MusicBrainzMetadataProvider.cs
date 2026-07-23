using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record MusicBrainzTrackCandidate(
    Guid TrackId,
    int MediumPosition,
    int TrackPosition,
    string Number,
    string Title,
    int? LengthMilliseconds,
    Guid RecordingId,
    string RecordingTitle,
    string ArtistCredit);

public sealed record MusicBrainzReleaseCandidate(
    Guid ReleaseId,
    string Title,
    string ArtistCredit,
    string? Date,
    string? Country,
    string? Status,
    string? Barcode,
    Guid? ReleaseGroupId,
    string? ReleaseGroupTitle,
    string? PrimaryType,
    string? Label,
    string? CatalogNumber,
    ImmutableArray<string> Formats,
    ImmutableArray<MusicBrainzTrackCandidate> Tracks);

public sealed record MusicBrainzReleaseResult(
    Guid RecordingId,
    ImmutableArray<MusicBrainzReleaseCandidate> Releases,
    DateTimeOffset RetrievedAtUtc);

public sealed record MusicBrainzReleaseSearchQuery(
    string? Artist = null,
    string? Album = null,
    string? Barcode = null,
    string? CatalogNumber = null,
    Guid? ReleaseId = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Artist) &&
        string.IsNullOrWhiteSpace(Album) &&
        string.IsNullOrWhiteSpace(Barcode) &&
        string.IsNullOrWhiteSpace(CatalogNumber) &&
        ReleaseId is null;
}

public sealed record MusicBrainzReleaseSearchResult(
    ImmutableArray<MusicBrainzReleaseCandidate> Releases,
    DateTimeOffset RetrievedAtUtc);

public interface IMusicBrainzMetadataProvider : IMetadataSourceProvider
{
    Task<MusicBrainzReleaseResult> ResolveRecordingAsync(
        Guid recordingId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<MusicBrainzReleaseSearchResult> SearchReleasesAsync(
        MusicBrainzReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<MusicBrainzReleaseCandidate> GetReleaseAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record MusicBrainzHttpResult(
    HttpStatusCode StatusCode,
    string Content,
    TimeSpan? RetryAfter = null);

public interface IMusicBrainzHttpTransport
{
    Task<MusicBrainzHttpResult> GetAsync(
        Uri uri,
        CancellationToken ct = default);
}

public sealed class MusicBrainzHttpTransport :
    IMusicBrainzHttpTransport, IDisposable
{
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public MusicBrainzHttpTransport()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MusicLibraryManager", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "(+https://github.com/colinphill/MusicLibraryTools)"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<MusicBrainzHttpResult> GetAsync(
        Uri uri,
        CancellationToken ct = default)
    {
        using HttpResponseMessage response =
            await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        string content =
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new(
            response.StatusCode,
            content,
            response.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class MusicBrainzMetadataProvider(
    IMusicBrainzHttpTransport transport,
    IMusicBrainzReleaseCache? cache = null) : IMusicBrainzMetadataProvider
{
    public MetadataSourceDescriptor Descriptor { get; } = new(
        "musicbrainz",
        "MusicBrainz",
        MetadataSourceCapabilities.RecordingReleaseLookup |
        MetadataSourceCapabilities.ReleaseSearch |
        MetadataSourceCapabilities.ReleaseDetails);

    private const int PageSize = 100;
    private const int MaximumSearchResults = 250;
    private static readonly TimeSpan CacheMaximumAge =
        TimeSpan.FromDays(30);
    private static readonly TimeSpan MinimumRequestInterval =
        TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public async Task<MusicBrainzReleaseResult> ResolveRecordingAsync(
        Guid recordingId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (recordingId == Guid.Empty)
            throw new ArgumentException(
                "A MusicBrainz recording ID is required.", nameof(recordingId));
        string cacheKey = $"recording:{recordingId:D}";
        MusicBrainzCacheEntry<MusicBrainzReleaseResult>? cached =
            await ReadCacheAsync<MusicBrainzReleaseResult>(cacheKey, ct)
                .ConfigureAwait(false);
        if (cached?.IsFresh == true)
        {
            ReportCached(
                progress,
                cached.Value.Releases.Length,
                "MusicBrainz release editions");
            return cached.Value;
        }
        try
        {
            MusicBrainzReleaseResult result =
                await ResolveRecordingOnlineAsync(recordingId, progress, ct)
                    .ConfigureAwait(false);
            await WriteCacheAsync(
                    cacheKey, result, result.RetrievedAtUtc, ct)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (
            error is not OperationCanceledException && cached is not null)
        {
            ReportStaleCache(
                progress,
                cached.Value.Releases.Length,
                "MusicBrainz is unavailable; using cached release editions");
            return cached.Value;
        }
    }

    private async Task<MusicBrainzReleaseResult> ResolveRecordingOnlineAsync(
        Guid recordingId,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var releases = ImmutableArray.CreateBuilder<MusicBrainzReleaseCandidate>();
        int offset = 0;
        int total = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.LoadingConfiguration,
                offset,
                total > 0 ? total : null,
                Message: $"Loading MusicBrainz releases {offset + 1:N0}" +
                    (total > 0 ? $" of {total:N0}" : "")));
            MusicBrainzHttpResult response = await SendWithRetryAsync(
                BuildBrowseUri(recordingId, offset), ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException(
                    $"MusicBrainz lookup failed with HTTP {(int)response.StatusCode}.");
            MusicBrainzReleasePage page = ParseReleasePage(response.Content);
            total = page.Total;
            releases.AddRange(page.Releases);
            offset += page.Releases.Length;
            if (page.Releases.Length == 0)
                break;
        }
        while (offset < total);

        progress?.Report(new(
            OperationPhase.Completed,
            releases.Count,
            Math.Max(total, releases.Count),
            Message: $"Loaded {releases.Count:N0} MusicBrainz release edition(s)"));
        return new(recordingId, releases.ToImmutable(), DateTimeOffset.UtcNow);
    }

    public async Task<MusicBrainzReleaseSearchResult> SearchReleasesAsync(
        MusicBrainzReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty)
            throw new ArgumentException(
                "Supply an artist, album, barcode, catalog number, or release ID.",
                nameof(query));
        string cacheKey = SearchCacheKey(query);
        MusicBrainzCacheEntry<MusicBrainzReleaseSearchResult>? cached =
            await ReadCacheAsync<MusicBrainzReleaseSearchResult>(cacheKey, ct)
                .ConfigureAwait(false);
        if (cached?.IsFresh == true)
        {
            ReportCached(
                progress,
                cached.Value.Releases.Length,
                "MusicBrainz search results");
            return cached.Value;
        }
        try
        {
            MusicBrainzReleaseSearchResult result =
                await SearchReleasesOnlineAsync(query, progress, ct)
                    .ConfigureAwait(false);
            await WriteCacheAsync(
                    cacheKey, result, result.RetrievedAtUtc, ct)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (
            error is not OperationCanceledException && cached is not null)
        {
            ReportStaleCache(
                progress,
                cached.Value.Releases.Length,
                "MusicBrainz is unavailable; using cached search results");
            return cached.Value;
        }
    }

    private async Task<MusicBrainzReleaseSearchResult>
        SearchReleasesOnlineAsync(
        MusicBrainzReleaseSearchQuery query,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var releases = ImmutableArray.CreateBuilder<MusicBrainzReleaseCandidate>();
        int offset = 0;
        int total = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.LoadingConfiguration,
                offset,
                total > 0 ? Math.Min(total, MaximumSearchResults) : null,
                Message: $"Searching MusicBrainz releases {offset + 1:N0}" +
                    (total > 0 ? $" of {total:N0}" : "")));
            MusicBrainzHttpResult response = await SendWithRetryAsync(
                BuildSearchUri(query, offset), ct).ConfigureAwait(false);
            EnsureSuccess(response, "search");
            MusicBrainzReleasePage page = ParseReleasePage(response.Content);
            total = page.Total;
            releases.AddRange(page.Releases.Take(
                MaximumSearchResults - releases.Count));
            offset += page.Releases.Length;
            if (page.Releases.Length == 0)
                break;
        }
        while (offset < Math.Min(total, MaximumSearchResults));
        progress?.Report(new(
            OperationPhase.Completed,
            releases.Count,
            releases.Count,
            Message: $"Found {releases.Count:N0} MusicBrainz release edition(s)" +
                (total > releases.Count ? "; refine the search to see other results" : "")));
        return new(releases.ToImmutable(), DateTimeOffset.UtcNow);
    }

    public async Task<MusicBrainzReleaseCandidate> GetReleaseAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (releaseId == Guid.Empty)
            throw new ArgumentException(
                "A MusicBrainz release ID is required.", nameof(releaseId));
        string cacheKey = $"release:{releaseId:D}";
        MusicBrainzCacheEntry<MusicBrainzReleaseCandidate>? cached =
            await ReadCacheAsync<MusicBrainzReleaseCandidate>(cacheKey, ct)
                .ConfigureAwait(false);
        if (cached?.IsFresh == true)
        {
            ReportCached(
                progress,
                cached.Value.Tracks.Length,
                "MusicBrainz release tracks");
            return cached.Value;
        }
        try
        {
            MusicBrainzReleaseCandidate result =
                await GetReleaseOnlineAsync(releaseId, progress, ct)
                    .ConfigureAwait(false);
            await WriteCacheAsync(
                    cacheKey, result, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (
            error is not OperationCanceledException && cached is not null)
        {
            ReportStaleCache(
                progress,
                cached.Value.Tracks.Length,
                "MusicBrainz is unavailable; using cached release details");
            return cached.Value;
        }
    }

    private async Task<MusicBrainzReleaseCandidate> GetReleaseOnlineAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new(
            OperationPhase.LoadingConfiguration,
            0,
            1,
            Message: "Loading complete MusicBrainz release details"));
        MusicBrainzHttpResult response = await SendWithRetryAsync(
            BuildReleaseUri(releaseId), ct).ConfigureAwait(false);
        EnsureSuccess(response, "release lookup");
        MusicBrainzReleaseCandidate release =
            ParseReleaseDocument(response.Content);
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            Message: $"Loaded {release.Tracks.Length:N0} release track(s)"));
        return release;
    }

    private async Task<MusicBrainzCacheEntry<T>?> ReadCacheAsync<T>(
        string key,
        CancellationToken ct)
    {
        if (cache is null)
            return null;
        try
        {
            return await cache.ReadAsync<T>(key, CacheMaximumAge, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync<T>(
        string key,
        T value,
        DateTimeOffset retrievedAtUtc,
        CancellationToken ct)
    {
        if (cache is null)
            return;
        try
        {
            await cache.WriteAsync(key, value, retrievedAtUtc, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is not OperationCanceledException)
        {
            // Cache failures do not invalidate a successful provider response.
        }
    }

    private static void ReportCached(
        IProgress<OperationProgress>? progress,
        int count,
        string description) =>
        progress?.Report(new(
            OperationPhase.Completed,
            count,
            count,
            Message: $"Loaded {count:N0} cached {description}"));

    private static void ReportStaleCache(
        IProgress<OperationProgress>? progress,
        int count,
        string message) =>
        progress?.Report(new(
            OperationPhase.Completed,
            count,
            count,
            Message: message));

    internal static string SearchCacheKey(
        MusicBrainzReleaseSearchQuery query)
    {
        string normalized = string.Join(
            "\n",
            Normalize(query.Artist),
            Normalize(query.Album),
            Normalize(query.Barcode),
            Normalize(query.CatalogNumber),
            query.ReleaseId?.ToString("D") ?? "");
        return "search:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToUpperInvariant() ?? "";

    public static Uri BuildBrowseUri(Guid recordingId, int offset = 0)
    {
        string includes =
            "artist-credits+labels+release-groups+media+recordings";
        return new Uri(
            "https://musicbrainz.org/ws/2/release" +
            $"?recording={recordingId:D}" +
            $"&inc={includes}" +
            $"&limit={PageSize}&offset={Math.Max(0, offset)}&fmt=json");
    }

    public static Uri BuildSearchUri(
        MusicBrainzReleaseSearchQuery query,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        var clauses = new List<string>();
        AddClause(clauses, "artist", query.Artist);
        AddClause(clauses, "release", query.Album);
        AddClause(clauses, "barcode", query.Barcode);
        AddClause(clauses, "catno", query.CatalogNumber);
        if (query.ReleaseId is not null)
            clauses.Add($"reid:{query.ReleaseId.Value:D}");
        if (clauses.Count == 0)
            throw new ArgumentException(
                "At least one release search field is required.", nameof(query));
        string encoded = Uri.EscapeDataString(string.Join(" AND ", clauses));
        return new Uri(
            "https://musicbrainz.org/ws/2/release" +
            $"?query={encoded}&limit={PageSize}" +
            $"&offset={Math.Max(0, offset)}&fmt=json");
    }

    public static Uri BuildReleaseUri(Guid releaseId)
    {
        string includes =
            "artist-credits+labels+release-groups+media+recordings";
        return new Uri(
            $"https://musicbrainz.org/ws/2/release/{releaseId:D}" +
            $"?inc={includes}&fmt=json");
    }

    private async Task<MusicBrainzHttpResult> SendWithRetryAsync(
        Uri uri,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            MusicBrainzHttpResult result;
            try
            {
                TimeSpan wait = MinimumRequestInterval -
                    (DateTimeOffset.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                _lastRequestUtc = DateTimeOffset.UtcNow;
                result = await transport.GetAsync(uri, ct).ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
            bool transient = result.StatusCode is
                    HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable ||
                (int)result.StatusCode >= 500;
            if (!transient || attempt == 2)
                return result;
            await Task.Delay(
                result.RetryAfter ??
                    TimeSpan.FromMilliseconds(500 * (1 << attempt)),
                ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            "MusicBrainz retry loop ended unexpectedly.");
    }

    public static MusicBrainzReleasePage ParseReleasePage(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            int total = ReadInt(root, "release-count") ??
                ReadInt(root, "count") ?? 0;
            int offset = ReadInt(root, "release-offset") ??
                ReadInt(root, "offset") ?? 0;
            if (!root.TryGetProperty("releases", out JsonElement items) ||
                items.ValueKind != JsonValueKind.Array)
                return new(total, offset, []);
            var releases = ImmutableArray.CreateBuilder<MusicBrainzReleaseCandidate>();
            foreach (JsonElement item in items.EnumerateArray())
                releases.Add(ParseRelease(item));
            return new(total, offset, releases.ToImmutable());
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "MusicBrainz returned malformed JSON.", error);
        }
    }

    public static MusicBrainzReleaseCandidate ParseReleaseDocument(
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return ParseRelease(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "MusicBrainz returned malformed JSON.", error);
        }
    }

    private static MusicBrainzReleaseCandidate ParseRelease(JsonElement item)
    {
        Guid id = ReadGuid(item, "id", "MusicBrainz release");
        string title = ReadString(item, "title") ?? "";
        Guid? groupId = null;
        string? groupTitle = null;
        string? primaryType = null;
        if (item.TryGetProperty("release-group", out JsonElement group) &&
            group.ValueKind == JsonValueKind.Object)
        {
            groupId = TryReadGuid(group, "id");
            groupTitle = ReadString(group, "title");
            primaryType = ReadString(group, "primary-type");
        }

        string? label = null;
        string? catalog = null;
        if (item.TryGetProperty("label-info", out JsonElement labels) &&
            labels.ValueKind == JsonValueKind.Array)
        {
            JsonElement first = labels.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                catalog = ReadString(first, "catalog-number");
                if (first.TryGetProperty("label", out JsonElement labelElement))
                    label = ReadString(labelElement, "name");
            }
        }

        var formats = ImmutableArray.CreateBuilder<string>();
        var tracks = ImmutableArray.CreateBuilder<MusicBrainzTrackCandidate>();
        if (item.TryGetProperty("media", out JsonElement media) &&
            media.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement medium in media.EnumerateArray())
            {
                int mediumPosition = ReadInt(medium, "position") ?? 0;
                string? format = ReadString(medium, "format");
                if (!string.IsNullOrWhiteSpace(format))
                    formats.Add(format);
                if (!medium.TryGetProperty("tracks", out JsonElement mediumTracks) ||
                    mediumTracks.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement track in mediumTracks.EnumerateArray())
                {
                    if (!track.TryGetProperty(
                            "recording", out JsonElement recording))
                        continue;
                    Guid? trackId = TryReadGuid(track, "id");
                    Guid? recordingId = TryReadGuid(recording, "id");
                    if (trackId is null || recordingId is null)
                        continue;
                    string artistCredit = ReadArtistCredit(track);
                    if (string.IsNullOrWhiteSpace(artistCredit))
                        artistCredit = ReadArtistCredit(recording);
                    tracks.Add(new(
                        trackId.Value,
                        mediumPosition,
                        ReadInt(track, "position") ?? 0,
                        ReadString(track, "number") ?? "",
                        ReadString(track, "title") ?? "",
                        ReadInt(track, "length"),
                        recordingId.Value,
                        ReadString(recording, "title") ?? "",
                        artistCredit));
                }
            }
        }
        return new(
            id,
            title,
            ReadArtistCredit(item),
            ReadString(item, "date"),
            ReadString(item, "country"),
            ReadString(item, "status"),
            ReadString(item, "barcode"),
            groupId,
            groupTitle,
            primaryType,
            label,
            catalog,
            formats.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            tracks.ToImmutable());
    }

    private static string ReadArtistCredit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out JsonElement credit) ||
            credit.ValueKind != JsonValueKind.Array)
            return "";
        return string.Concat(credit.EnumerateArray().Select(item =>
            (ReadString(item, "name") ??
             (item.TryGetProperty("artist", out JsonElement artist)
                 ? ReadString(artist, "name")
                 : null) ??
             "") +
            (ReadString(item, "joinphrase") ?? "")));
    }

    private static Guid ReadGuid(
        JsonElement element,
        string property,
        string context) =>
        TryReadGuid(element, property) ??
        throw new InvalidDataException($"{context} has no valid '{property}'.");

    private static Guid? TryReadGuid(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out Guid id)
            ? id
            : null;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static void AddClause(
        ICollection<string> clauses,
        string field,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        string escaped = value.Trim()
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        clauses.Add($"{field}:\"{escaped}\"");
    }

    private static void EnsureSuccess(
        MusicBrainzHttpResult response,
        string operation)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"MusicBrainz {operation} failed with HTTP " +
                $"{(int)response.StatusCode}.");
    }
}

public sealed record MusicBrainzReleasePage(
    int Total,
    int Offset,
    ImmutableArray<MusicBrainzReleaseCandidate> Releases);
