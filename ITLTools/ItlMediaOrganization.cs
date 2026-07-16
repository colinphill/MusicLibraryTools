namespace iTunes.Binary;

/// <summary>iTunes' Windows media-folder naming rules for ordinary music tracks.</summary>
public static class ItlMediaOrganization
{
    public const int ComponentLengthLimit = 40;
    private static readonly HashSet<char> ReplacedCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static string CanonicalMusicPath(string mediaFolder, string? albumArtist, string? artist,
        string album, int trackNumber, string title, bool compilation, string extension = ".m4a")
    {
        if (trackNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(trackNumber));
        return CanonicalMusicPath(mediaFolder, albumArtist, artist, album,
            (int?)trackNumber, title, compilation, extension);
    }

    /// <summary>
    /// Returns the native iTunes music path when a cached track has no track number. iTunes omits
    /// the numeric prefix in that case rather than making the file impossible to organize.
    /// </summary>
    public static string CanonicalMusicPath(string mediaFolder, string? albumArtist, string? artist,
        string album, int? trackNumber, string title, bool compilation, string extension = ".m4a")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFolder);
        return CanonicalMusicFolderPath(Path.Combine(Path.GetFullPath(mediaFolder), "Music"),
            albumArtist, artist, album, trackNumber, title, compilation, extension);
    }

    /// <summary>
    /// Returns the native iTunes path when the caller already has the Music subdirectory rather
    /// than the parent iTunes Media folder.
    /// </summary>
    public static string CanonicalMusicFolderPath(string musicFolder, string? albumArtist,
        string? artist, string album, int? trackNumber, string title, bool compilation,
        string extension = ".m4a")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(musicFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(album);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (trackNumber <= 0)
            trackNumber = null;
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("An extension is required.", nameof(extension));
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        if (extension.Length >= ComponentLengthLimit)
            throw new ArgumentException("The extension leaves no room for an iTunes filename.", nameof(extension));

        string effectiveAlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? artist?.Trim() ?? string.Empty : albumArtist;
        if (!compilation)
            ArgumentException.ThrowIfNullOrWhiteSpace(effectiveAlbumArtist, nameof(albumArtist));

        string artistFolder = compilation ? "Compilations" : Component(effectiveAlbumArtist);
        string albumFolder = Component(album);
        // Native iTunes' 40-character filename limit includes the extension. Collision suffixes
        // are appended later and may make an otherwise truncated filename longer than 40.
        string stem = trackNumber is null ? title : $"{trackNumber:D2} {title}";
        string fileName = Component(stem, ComponentLengthLimit - extension.Length) + extension;
        return Path.Combine(Path.GetFullPath(musicFolder), artistFolder, albumFolder, fileName);
    }

    private static string Component(string value, int limit = ComponentLengthLimit)
    {
        char[] characters = value.Trim().Select(character =>
            character < ' ' || ReplacedCharacters.Contains(character) ? '_' : character).ToArray();
        string result = new string(characters).TrimEnd(' ', '.');
        if (result.Length > limit)
            result = result[..limit].TrimEnd(' ', '.');
        return string.IsNullOrEmpty(result) ? "Unknown" : result;
    }
}

public sealed record ItlAacTrackImport
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string AlbumArtist { get; init; }
    public required string Album { get; init; }
    public string? Genre { get; init; }
    public required int TrackNumber { get; init; }
    public required int TrackCount { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int BitRate { get; init; }
    public required int ArtworkCount { get; init; }
    public bool Compilation { get; init; }
}
