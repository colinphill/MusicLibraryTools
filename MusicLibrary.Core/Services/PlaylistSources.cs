using System.Globalization;

namespace MusicLibrary.Core.Services;

public sealed record PlaylistSourceRequest(string Location, bool Recursive = false);

public sealed record PlaylistTrackReference(
    string Path,
    string? DisplayText = null,
    int? DurationSeconds = null);

public sealed record PlaylistDocument(
    string Id,
    string Name,
    string SourcePath,
    IReadOnlyList<PlaylistTrackReference> Tracks);

/// <summary>Reads playlists without coupling the indexed library to an external media catalog.</summary>
public interface IPlaylistSource
{
    string Id { get; }
    string DisplayName { get; }
    bool CanRead(string path);

    Task<IReadOnlyList<PlaylistDocument>> LoadAsync(
        PlaylistSourceRequest request,
        CancellationToken ct = default);
}

/// <summary>Reads UTF-8 M3U and M3U8 files from a file or directory.</summary>
public sealed class M3uPlaylistSource : IPlaylistSource
{
    private static readonly string[] Extensions = [".m3u", ".m3u8"];

    public string Id => "m3u";
    public string DisplayName => "M3U playlists";

    public bool CanRead(string path) =>
        Directory.Exists(path) || Extensions.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PlaylistDocument>> LoadAsync(
        PlaylistSourceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Location);

        string fullPath = Path.GetFullPath(request.Location);
        string[] files;
        if (File.Exists(fullPath))
        {
            if (!CanRead(fullPath))
                throw new InvalidDataException("Expected an .m3u or .m3u8 playlist file.");
            files = [fullPath];
        }
        else if (Directory.Exists(fullPath))
        {
            SearchOption search = request.Recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            files = Directory.EnumerateFiles(fullPath, "*", search)
                .Where(CanRead)
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }
        else
        {
            throw new DirectoryNotFoundException(
                $"Playlist source does not exist: {fullPath}");
        }

        var documents = new List<PlaylistDocument>(files.Length);
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            documents.Add(await ReadAsync(file, ct).ConfigureAwait(false));
        }
        return documents;
    }

    private static async Task<PlaylistDocument> ReadAsync(
        string path,
        CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(path)!;
        var tracks = new List<PlaylistTrackReference>();
        string? display = null;
        int? duration = null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            string value = line.Trim();
            if (value.Length == 0)
                continue;
            if (value.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                ParseExtendedInfo(value, out duration, out display);
                continue;
            }
            if (value[0] == '#')
                continue;

            string trackPath = ResolveTrackPath(value, directory);
            tracks.Add(new(trackPath, display, duration));
            display = null;
            duration = null;
        }

        return new(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path),
            Path.GetFullPath(path),
            tracks);
    }

    private static void ParseExtendedInfo(
        string line,
        out int? duration,
        out string? display)
    {
        int comma = line.IndexOf(',');
        string rawDuration = comma < 0
            ? line[8..]
            : line[8..comma];
        duration = int.TryParse(rawDuration, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
        display = comma >= 0 && comma + 1 < line.Length
            ? line[(comma + 1)..].Trim()
            : null;
        if (string.IsNullOrWhiteSpace(display))
            display = null;
    }

    private static string ResolveTrackPath(string value, string playlistDirectory)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            return Path.GetFullPath(uri.LocalPath);
        string platformPath = value.Replace(Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        return Path.IsPathFullyQualified(platformPath)
            ? Path.GetFullPath(platformPath)
            : Path.GetFullPath(platformPath, playlistDirectory);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
