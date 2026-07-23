using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record CoverArtArchiveCandidate(
    Guid ReleaseId,
    string Id,
    Uri ImageUri,
    Uri? ThumbnailUri,
    ImmutableArray<string> Types,
    bool IsFront,
    bool IsBack,
    bool Approved,
    string? Comment);

public sealed record CoverArtArchiveResult(
    Guid ReleaseId,
    ImmutableArray<CoverArtArchiveCandidate> Images,
    DateTimeOffset RetrievedAtUtc);

public sealed record CoverArtDownload(
    byte[] Data,
    string ContentType,
    bool FromCache);

public interface ICoverArtArchiveProvider : IMetadataSourceProvider
{
    Task<CoverArtArchiveResult> GetReleaseArtworkAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<CoverArtDownload> DownloadAsync(
        CoverArtArchiveCandidate candidate,
        bool thumbnail = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record CoverArtArchiveHttpResult(
    HttpStatusCode StatusCode,
    byte[] Content,
    string? ContentType = null,
    TimeSpan? RetryAfter = null);

public interface ICoverArtArchiveHttpTransport
{
    Task<CoverArtArchiveHttpResult> GetAsync(
        Uri uri,
        string accept,
        CancellationToken ct = default);
}

public sealed class CoverArtArchiveHttpTransport :
    ICoverArtArchiveHttpTransport, IDisposable
{
    private readonly HttpClient _client = new(
        new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public CoverArtArchiveHttpTransport()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MusicLibraryManager", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "(+https://github.com/colinphill/MusicLibraryTools)"));
    }

    public async Task<CoverArtArchiveHttpResult> GetAsync(
        Uri uri,
        string accept,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using HttpResponseMessage response =
            await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        byte[] content =
            await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new(
            response.StatusCode,
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _client.Dispose();
}

public interface IArtworkDownloadCache
{
    Task<CoverArtDownload?> ReadAsync(
        Uri uri,
        CancellationToken ct = default);

    Task WriteAsync(
        Uri uri,
        CoverArtDownload value,
        CancellationToken ct = default);
}

public sealed class ArtworkDownloadCache : IArtworkDownloadCache
{
    private const long DefaultMaximumBytes = 128L * 1024 * 1024;
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArtworkDownloadCache() : this(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicLibraryTools",
            "cover-art-cache"),
        DefaultMaximumBytes)
    {
    }

    public ArtworkDownloadCache(string root, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _root = Path.GetFullPath(root);
        _maximumBytes = maximumBytes;
    }

    public async Task<CoverArtDownload?> ReadAsync(
        Uri uri,
        CancellationToken ct = default)
    {
        string path = PathFor(uri);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return null;
            byte[] stored = await File.ReadAllBytesAsync(path, ct)
                .ConfigureAwait(false);
            if (stored.Length < sizeof(int))
                return null;
            int length = BitConverter.ToInt32(stored, 0);
            if (length <= 0 || length > 256 ||
                stored.Length < sizeof(int) + length)
                return null;
            string mime = Encoding.UTF8.GetString(
                stored, sizeof(int), length);
            byte[] data = stored[(sizeof(int) + length)..];
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return new(data, mime, true);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        Uri uri,
        CoverArtDownload value,
        CancellationToken ct = default)
    {
        byte[] mime = Encoding.UTF8.GetBytes(value.ContentType);
        if (mime.Length is 0 or > 256 || value.Data.LongLength > _maximumBytes)
            return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            string path = PathFor(uri);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] stored = new byte[sizeof(int) + mime.Length + value.Data.Length];
            BitConverter.GetBytes(mime.Length).CopyTo(stored, 0);
            mime.CopyTo(stored, sizeof(int));
            value.Data.CopyTo(stored, sizeof(int) + mime.Length);
            try
            {
                await File.WriteAllBytesAsync(temporary, stored, ct)
                    .ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
            Prune();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(Uri uri)
    {
        string key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))
            .ToLowerInvariant();
        return Path.Combine(_root, key + ".cache");
    }

    private void Prune()
    {
        FileInfo[] files = Directory.EnumerateFiles(_root, "*.cache")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastAccessTimeUtc)
            .ToArray();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files.Reverse())
        {
            if (total <= _maximumBytes)
                break;
            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch { }
        }
    }
}

public sealed class CoverArtArchiveProvider(
    ICoverArtArchiveHttpTransport transport,
    IArtworkDownloadCache cache) : ICoverArtArchiveProvider
{
    public MetadataSourceDescriptor Descriptor { get; } = new(
        "cover-art-archive",
        "Cover Art Archive",
        MetadataSourceCapabilities.ReleaseArtwork);

    private readonly SemaphoreSlim _requestGate = new(2, 2);

    public async Task<CoverArtArchiveResult> GetReleaseArtworkAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (releaseId == Guid.Empty)
            throw new ArgumentException(
                "A MusicBrainz release ID is required.", nameof(releaseId));
        progress?.Report(new(
            OperationPhase.LoadingConfiguration,
            0,
            1,
            Message: "Loading Cover Art Archive image list"));
        CoverArtArchiveHttpResult response = await SendWithRetryAsync(
            BuildReleaseUri(releaseId), "application/json", ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            progress?.Report(new(
                OperationPhase.Completed,
                0,
                0,
                Message: "This release has no Cover Art Archive images"));
            return new(releaseId, [], DateTimeOffset.UtcNow);
        }
        EnsureSuccess(response, "artwork lookup");
        ImmutableArray<CoverArtArchiveCandidate> images =
            ParseRelease(releaseId, response.Content);
        progress?.Report(new(
            OperationPhase.Completed,
            images.Length,
            images.Length,
            Message: $"Found {images.Length:N0} Cover Art Archive image(s)"));
        return new(releaseId, images, DateTimeOffset.UtcNow);
    }

    public async Task<CoverArtDownload> DownloadAsync(
        CoverArtArchiveCandidate candidate,
        bool thumbnail = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Uri uri = thumbnail
            ? candidate.ThumbnailUri ?? candidate.ImageUri
            : candidate.ImageUri;
        CoverArtDownload? cached =
            await cache.ReadAsync(uri, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Report(new(
                OperationPhase.Completed, 1, 1,
                Message: "Loaded artwork from the local cache"));
            return cached;
        }
        progress?.Report(new(
            OperationPhase.Applying,
            0,
            1,
            Message: thumbnail
                ? "Downloading artwork thumbnail"
                : "Downloading full-resolution artwork"));
        CoverArtArchiveHttpResult response = await SendWithRetryAsync(
            uri, "image/*", ct).ConfigureAwait(false);
        EnsureSuccess(response, "image download");
        if (response.Content.Length == 0 ||
            response.Content.LongLength > 64L * 1024 * 1024 ||
            response.ContentType is null ||
            !response.ContentType.StartsWith(
                "image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Cover Art Archive returned an invalid image response.");
        var downloaded = new CoverArtDownload(
            response.Content, response.ContentType, false);
        await cache.WriteAsync(uri, downloaded, ct).ConfigureAwait(false);
        progress?.Report(new(
            OperationPhase.Completed, 1, 1,
            Message: $"Downloaded {response.Content.Length:N0} artwork bytes"));
        return downloaded;
    }

    public static Uri BuildReleaseUri(Guid releaseId) =>
        new($"https://coverartarchive.org/release/{releaseId:D}");

    public static ImmutableArray<CoverArtArchiveCandidate> ParseRelease(
        Guid releaseId,
        byte[] content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty(
                    "images", out JsonElement images) ||
                images.ValueKind != JsonValueKind.Array)
                return [];
            var result =
                ImmutableArray.CreateBuilder<CoverArtArchiveCandidate>();
            foreach (JsonElement image in images.EnumerateArray())
            {
                string? id = ReadString(image, "id");
                string? imageUrl = ReadString(image, "image");
                if (id is null || imageUrl is null ||
                    !Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri))
                    continue;
                Uri? thumbnail = null;
                if (image.TryGetProperty(
                        "thumbnails", out JsonElement thumbnails))
                {
                    string? thumbnailUrl =
                        ReadString(thumbnails, "250") ??
                        ReadString(thumbnails, "small");
                    if (thumbnailUrl is not null)
                        Uri.TryCreate(
                            thumbnailUrl, UriKind.Absolute, out thumbnail);
                }
                ImmutableArray<string> types =
                    image.TryGetProperty("types", out JsonElement typeValues) &&
                    typeValues.ValueKind == JsonValueKind.Array
                        ? [.. typeValues.EnumerateArray()
                            .Where(value => value.ValueKind ==
                                JsonValueKind.String)
                            .Select(value => value.GetString()!)
                            .Where(value => !string.IsNullOrWhiteSpace(value))]
                        : [];
                result.Add(new(
                    releaseId,
                    id,
                    imageUri,
                    thumbnail,
                    types,
                    ReadBoolean(image, "front"),
                    ReadBoolean(image, "back"),
                    ReadBoolean(image, "approved"),
                    ReadString(image, "comment")));
            }
            return result.ToImmutable();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "Cover Art Archive returned malformed JSON.", error);
        }
    }

    private async Task<CoverArtArchiveHttpResult> SendWithRetryAsync(
        Uri uri,
        string accept,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            CoverArtArchiveHttpResult result;
            try
            {
                result = await transport.GetAsync(uri, accept, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
            bool transient = result.StatusCode is
                    HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable ||
                (int)result.StatusCode >= 500;
            if (!transient || attempt == 2)
                return result;
            await Task.Delay(
                result.RetryAfter ??
                    TimeSpan.FromMilliseconds(500 * (1 << attempt)),
                ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            "Cover Art Archive retry loop ended unexpectedly.");
    }

    private static string? ReadString(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True;

    private static void EnsureSuccess(
        CoverArtArchiveHttpResult response,
        string operation)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"Cover Art Archive {operation} failed with HTTP " +
                $"{(int)response.StatusCode}.");
    }
}
