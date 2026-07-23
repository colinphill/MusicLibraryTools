using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record AcoustIdCandidate(
    Guid AcoustId,
    double Score,
    ImmutableArray<Guid> MusicBrainzRecordingIds);

public sealed record AcoustIdLookupResult(
    AudioFingerprint Source,
    ImmutableArray<AcoustIdCandidate> Candidates,
    DateTimeOffset RetrievedAtUtc);

public interface IAcoustIdLookupService
{
    Task<AcoustIdLookupResult> LookupAsync(
        AudioFingerprint fingerprint,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record AcoustIdHttpResult(
    HttpStatusCode StatusCode,
    string Content,
    TimeSpan? RetryAfter = null);

public interface IAcoustIdHttpTransport
{
    Task<AcoustIdHttpResult> GetAsync(
        Uri uri,
        CancellationToken ct = default);
}

public sealed class AcoustIdHttpTransport : IAcoustIdHttpTransport, IDisposable
{
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public AcoustIdHttpTransport()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MusicLibraryManager", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "(+https://github.com/colinphill/MusicLibraryTools)"));
    }

    public async Task<AcoustIdHttpResult> GetAsync(
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

/// <summary>
/// Performs lookup only. It never submits fingerprints or metadata.
/// </summary>
public sealed class AcoustIdLookupService(
    IAcoustIdHttpTransport transport,
    IAppSettings settings) : IAcoustIdLookupService
{
    public const string ClientKeyPreference = "providers.acoustid.clientKey";
    private static readonly TimeSpan MinimumRequestInterval =
        TimeSpan.FromMilliseconds(334);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public async Task<AcoustIdLookupResult> LookupAsync(
        AudioFingerprint fingerprint,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        string? configuredKey = settings.GetPreference(ClientKeyPreference);
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException(
                "AcoustID lookup requires an application client key in personal settings.");

        progress?.Report(new(
            OperationPhase.LoadingConfiguration,
            0,
            1,
            fingerprint.Path,
            $"Looking up AcoustID candidates for {Path.GetFileName(fingerprint.Path)}"));

        Uri requestUri = BuildLookupUri(configuredKey.Trim(), fingerprint);
        AcoustIdHttpResult response = await SendWithRetryAsync(requestUri, ct)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"AcoustID lookup failed with HTTP {(int)response.StatusCode}.");
        ImmutableArray<AcoustIdCandidate> candidates = ParseResponse(response.Content);
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            fingerprint.Path,
            $"Found {candidates.Length:N0} AcoustID candidate(s)"));
        return new(fingerprint, candidates, DateTimeOffset.UtcNow);
    }

    private async Task<AcoustIdHttpResult> SendWithRetryAsync(
        Uri uri,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            AcoustIdHttpResult result;
            try
            {
                TimeSpan elapsed = DateTimeOffset.UtcNow - _lastRequestUtc;
                TimeSpan wait = MinimumRequestInterval - elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                _lastRequestUtc = DateTimeOffset.UtcNow;
                result = await transport.GetAsync(uri, ct).ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }

            bool transient = result.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)result.StatusCode >= 500;
            if (!transient || attempt == 2)
                return result;
            TimeSpan retryDelay = result.RetryAfter ??
                TimeSpan.FromMilliseconds(500 * (1 << attempt));
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("AcoustID retry loop ended unexpectedly.");
    }

    public static Uri BuildLookupUri(
        string clientKey,
        AudioFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);
        ArgumentNullException.ThrowIfNull(fingerprint);
        string query = string.Join("&",
        [
            "client=" + Uri.EscapeDataString(clientKey),
            "format=json",
            "meta=recordingids",
            "duration=" + fingerprint.LookupDurationSeconds.ToString(
                CultureInfo.InvariantCulture),
            "fingerprint=" + Uri.EscapeDataString(fingerprint.Fingerprint),
        ]);
        return new Uri("https://api.acoustid.org/v2/lookup?" + query);
    }

    public static ImmutableArray<AcoustIdCandidate> ParseResponse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            string? status = root.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString()
                : null;
            if (!string.Equals(status, "ok", StringComparison.Ordinal))
            {
                string detail = TryReadError(root) ?? "The provider returned an error.";
                throw new InvalidDataException($"AcoustID lookup failed: {detail}");
            }
            if (!root.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = ImmutableArray.CreateBuilder<AcoustIdCandidate>();
            foreach (JsonElement result in results.EnumerateArray())
            {
                Guid id = ReadGuid(result, "id", "AcoustID result");
                if (!result.TryGetProperty("score", out JsonElement scoreElement) ||
                    !scoreElement.TryGetDouble(out double score) ||
                    !double.IsFinite(score))
                    throw new InvalidDataException(
                        $"AcoustID result '{id}' has no valid confidence score.");
                var recordingIds = ImmutableArray.CreateBuilder<Guid>();
                if (result.TryGetProperty("recordings", out JsonElement recordings) &&
                    recordings.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement recording in recordings.EnumerateArray())
                        recordingIds.Add(ReadGuid(
                            recording, "id", $"AcoustID result '{id}' recording"));
                }
                candidates.Add(new(
                    id,
                    score,
                    recordingIds.Distinct().ToImmutableArray()));
            }
            return candidates.ToImmutable();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "AcoustID returned malformed JSON.", error);
        }
    }

    private static Guid ReadGuid(
        JsonElement element,
        string property,
        string context)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out Guid id))
            throw new InvalidDataException($"{context} has no valid '{property}'.");
        return id;
    }

    private static string? TryReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error))
            return null;
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        return error.ValueKind == JsonValueKind.Object &&
               error.TryGetProperty("message", out JsonElement message)
            ? message.GetString()
            : null;
    }
}
