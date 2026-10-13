#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using MusicFileUtilities;

namespace MusicLibraryTools;

/// <summary>Optional index enumeration source. File contents are still read through Target.</summary>
public sealed record EverythingHttpSettings(
    string Url,
    string? Root = null,
    string? Username = null,
    string? PasswordEnvironmentVariable = null)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException(
                "Everything HTTP URL must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        if (Root is not null && (string.IsNullOrWhiteSpace(Root) ||
            !(Regex.IsMatch(Root, @"^[A-Za-z]:[\\/]") || Root.StartsWith(@"\\", StringComparison.Ordinal))))
            throw new InvalidDataException("Everything HTTP root must be an absolute Windows drive or UNC path.");
        if (Username?.Contains(':') == true || Username?.Any(char.IsControl) == true)
            throw new InvalidDataException("Everything HTTP username cannot contain a colon or control character.");
        if (PasswordEnvironmentVariable is not null &&
            (string.IsNullOrWhiteSpace(PasswordEnvironmentVariable) ||
             PasswordEnvironmentVariable.Contains('=') || PasswordEnvironmentVariable.Contains('\0')))
            throw new InvalidDataException("Everything HTTP password environment variable name is invalid.");
    }
}

/// <summary>Enumerates the Everything index without listing or statting filesystem directories.</summary>
public static class EverythingHttpEnumerator
{
    public static IEnumerable<(string Name, DateTime Modified, long Size, MFEType FileType)> Enumerate(
        string target, EverythingHttpSettings settings, CancellationToken ct = default,
        HttpClient? client = null, int pageSize = 1000)
    {
        settings.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        string fullTarget = Path.GetFullPath(target);
        // A relative local root must be resolved before querying the absolute server index.
        // Non-Windows clients need an explicit Windows-to-local root mapping.
        string serverRoot = settings.Root ?? fullTarget;
        (settings with { Root = serverRoot }).Validate();
        string root = serverRoot.Replace('/', '\\').TrimEnd('\\') + "\\";
        // Regex mode treats punctuation in paths literally and scopes the query to descendants.
        string search = "^" + Regex.Escape(root);
        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : null;
        client ??= ownedClient!;
        string? password = settings.PasswordEnvironmentVariable is null ? null :
            Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable) ??
            throw new InvalidDataException("Everything HTTP password environment variable is not set.");
        AuthenticationHeaderValue? authorization = settings.Username is null && password is null
            ? null : new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                (settings.Username ?? "") + ":" + (password ?? ""))));
        long offset = 0;
        long? expectedTotal = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            ct.ThrowIfCancellationRequested();
            var uri = new UriBuilder(settings.Url)
            {
                Query = "json=1&path_column=1&size_column=1&date_modified_column=1" +
                    "&path=1&regex=1&sort=path&ascending=1&search=" + Uri.EscapeDataString(search) +
                    "&offset=" + offset.ToString(CultureInfo.InvariantCulture) +
                    "&count=" + pageSize.ToString(CultureInfo.InvariantCulture),
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
            request.Headers.Authorization = authorization;
            using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Everything HTTP enumeration failed (HTTP {(int)response.StatusCode}).");
            using var stream = response.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
            using var document = JsonDocument.ParseAsync(stream, cancellationToken: ct).GetAwaiter().GetResult();
            JsonElement page = document.RootElement;
            long total = ReadInteger(page, "totalResults");
            if (total < 0 || (expectedTotal.HasValue && total != expectedTotal.Value))
                throw new InvalidDataException("Everything HTTP result count changed during enumeration. Retry the scan.");
            expectedTotal = total;
            JsonElement results = page.GetProperty("results");
            int count = results.GetArrayLength();
            if (count > pageSize || offset + count > total || (count == 0 && offset < total))
                throw new InvalidDataException("Everything HTTP returned an incomplete or invalid result page.");
            foreach (JsonElement result in results.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                string type = result.GetProperty("type").GetString() ?? "";
                if (type == "folder")
                    continue;
                if (type != "file")
                    throw new InvalidDataException("Everything HTTP returned an unknown result type.");
                string name = result.GetProperty("name").GetString() ?? "";
                string parent = (result.GetProperty("path").GetString() ?? "").Replace('/', '\\');
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or "..")
                    throw new InvalidDataException("Everything HTTP returned an invalid filename.");
                string fullPath = parent.TrimEnd('\\') + "\\" + name;
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Everything HTTP returned a file outside the configured root.");
                string relative = fullPath[root.Length..];
                string[] parts = relative.Split('\\');
                if (parts.Any(part => part is ".." or "." or "" ||
                    part.EndsWith('.') || part.EndsWith(' ') ||
                    part.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character))))
                    throw new InvalidDataException("Everything HTTP returned an invalid relative path.");
                if (!seen.Add(fullPath))
                    throw new InvalidDataException("Everything HTTP returned duplicate results. Retry the scan.");
                if (parts.SkipLast(1).Any(part => part.Contains(".itlp", StringComparison.OrdinalIgnoreCase)) ||
                    !MediaFormatRegistry.Default.SupportsExtension(Path.GetExtension(name), MediaFormatCapabilities.LibraryIndex))
                    continue;
                long size = ReadInteger(result, "size");
                long modified = ReadInteger(result, "date_modified");
                if (size < 0 || modified <= 0)
                    throw new InvalidDataException("Everything HTTP returned an unknown file size or modified time.");
                yield return (Path.Combine(fullTarget, Path.Combine(parts)), DateTime.FromFileTimeUtc(modified), size, MFEType.MusicFile);
            }
            offset += count;
        } while (offset < expectedTotal);
    }

    private static long ReadInteger(JsonElement element, string property)
    {
        JsonElement value = element.GetProperty(property);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        throw new InvalidDataException($"Everything HTTP returned an invalid or missing {property}.");
    }
}
