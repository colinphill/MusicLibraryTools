using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record MusicBrainzTrackCandidate(
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

public interface IMusicBrainzMetadataProvider
{
    Task<MusicBrainzReleaseResult> ResolveRecordingAsync(
        Guid recordingId,
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
    IMusicBrainzHttpTransport transport) : IMusicBrainzMetadataProvider
{
    private const int PageSize = 100;
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
            int total = ReadInt(root, "release-count") ?? 0;
            int offset = ReadInt(root, "release-offset") ?? 0;
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
                    Guid? recordingId = TryReadGuid(recording, "id");
                    if (recordingId is null)
                        continue;
                    tracks.Add(new(
                        mediumPosition,
                        ReadInt(track, "position") ?? 0,
                        ReadString(track, "number") ?? "",
                        ReadString(track, "title") ?? "",
                        ReadInt(track, "length"),
                        recordingId.Value,
                        ReadString(recording, "title") ?? "",
                        ReadArtistCredit(track)));
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
}

public sealed record MusicBrainzReleasePage(
    int Total,
    int Offset,
    ImmutableArray<MusicBrainzReleaseCandidate> Releases);
