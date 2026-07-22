using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record LibraryPathMetadata(
    string Artist,
    string? AlbumArtist,
    string Album,
    string Title,
    int? TrackNumber,
    int? DiscNumber,
    bool Compilation,
    string? ReleaseDate,
    string OriginalName,
    string Extension)
{
    private static readonly Regex ExistingDiscTrackPrefix = new(
        @"^(?<disc>[1-9]\d*)-(?<track>\d{2,}) ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string EffectiveAlbumArtist => string.IsNullOrWhiteSpace(AlbumArtist)
        ? Artist
        : AlbumArtist;

    public string? Genre { get; init; }
    public int? TrackTotal { get; init; }
    /// <summary>
    /// Album-context projection used by the continuous-numbering disc strategy. Callers that
    /// plan an album set this to the ordinal of the distinct disc/track slot.
    /// </summary>
    public int? FlattenedTrackNumber { get; init; }

    public static LibraryPathMetadata From(MetadataCacheEntry entry, string source) => new(
        entry.Artist,
        entry.HasAlbumArtist ? entry.AlbumArtist : null,
        entry.Album,
        entry.Title,
        entry.TrackNumber,
        EffectiveDiscNumber(entry, source),
        entry.Compilation,
        entry.ReleaseDate,
        Path.GetFileNameWithoutExtension(source),
        Path.GetExtension(source))
    {
        Genre = entry.Genre,
        TrackTotal = entry.TrackTotal,
    };

    private static int? EffectiveDiscNumber(MetadataCacheEntry entry, string source)
    {
        if (entry.DiscNumber is > 0)
            return entry.DiscNumber;
        if (entry.TrackNumber is not > 0)
            return null;
        Match match = ExistingDiscTrackPrefix.Match(
            Path.GetFileNameWithoutExtension(source));
        return match.Success &&
               int.TryParse(match.Groups["track"].Value, NumberStyles.None,
                   CultureInfo.InvariantCulture, out int track) &&
               track == entry.TrackNumber &&
               int.TryParse(match.Groups["disc"].Value, NumberStyles.None,
                   CultureInfo.InvariantCulture, out int disc)
            ? disc
            : null;
    }

    public static LibraryPathMetadata From(TrackRecord record, string extension) => new(
        record.Artist ?? "",
        record.AlbumArtist,
        record.Album ?? "Unknown Album",
        record.Title ?? "Untitled",
        record.TrackNumber,
        record.DiscNumber,
        false,
        record.ReleaseDate ?? record.Year?.ToString(CultureInfo.InvariantCulture),
        Path.GetFileNameWithoutExtension(record.Path),
        extension)
    {
        Genre = record.Genre,
        TrackTotal = record.TrackTotal,
    };

    public static LibraryPathMetadata From(IngestTrackPlan track, string extension) => new(
        track.Artist,
        track.AlbumArtist,
        track.Album,
        track.Title,
        track.HadTrackNumber ? track.TrackNumber : null,
        track.OriginalDiscNumber > 0 ? track.OriginalDiscNumber : null,
        track.Compilation,
        null,
        Path.GetFileNameWithoutExtension(track.SourcePath),
        extension)
    {
        TrackTotal = track.HadTrackNumber ? track.TrackTotal : null,
        // Ingest has already applied the profile's track-number projection while building the
        // album plan, so this value is exact even when discs have different lengths.
        FlattenedTrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
    };
}

public interface IPathLayoutResolver
{
    string Resolve(
        string root,
        LibraryProfile profile,
        LibraryPathMetadata metadata,
        int componentLengthLimit,
        int discAlbumLengthLimit);

    string ResolveCollision(
        string initialPath,
        string sourcePath,
        LibraryProfile profile,
        int collisionNumber);
}

/// <summary>
/// Applies one profile-controlled path policy for organization, ingest, repair, and export.
/// Optional template fragments use square brackets and are omitted when one of their tokens is
/// blank, for example <c>[{Year} - ]{Album}</c>.
/// </summary>
public sealed class LibraryPathLayoutResolver : IPathLayoutResolver
{
    private static readonly Regex Token = new(
        @"\{(?<name>[A-Za-z]+)(?::(?<format>[^}]+))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OptionalFragment = new(
        @"\[(?<content>[^\[\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FormatSuffix = new(
        @" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Year = new(
        @"\b(?<year>\d{4})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LibraryPathLayoutResolver Shared { get; } = new();

    public string Resolve(
        string root,
        LibraryProfile profile,
        LibraryPathMetadata metadata,
        int componentLengthLimit,
        int discAlbumLengthLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(metadata);
        if (componentLengthLimit <= 0 || discAlbumLengthLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(componentLengthLimit));

        LibraryNamingPolicy naming = profile.Naming;
        if (naming.UseItunesCanonicalNaming)
            return ResolveItunes(root, ApplyNamingFallbacks(metadata, naming));

        if (naming.LegacySanitization)
            return ResolveLegacy(root, metadata, componentLengthLimit, discAlbumLengthLimit);

        int effectiveComponentLimit = naming.ComponentLengthLimit is { } configuredLimit
            ? Math.Min(componentLengthLimit, configuredLimit)
            : componentLengthLimit;
        LibraryPathMetadata projected = ApplyDiscPolicy(
            ApplyNamingFallbacks(metadata, naming), profile.Disc);
        string directoryTemplate = naming.DirectoryTemplate;
        string fileTemplate = naming.FileNameTemplate;
        if (profile.Disc.Strategy == LibraryDiscStrategy.DiscFolder &&
            projected.DiscNumber is > 0 && !ContainsToken(directoryTemplate, "Disc"))
            directoryTemplate += "/Disc {Disc}";
        if (profile.Disc.Strategy == LibraryDiscStrategy.FileNamePrefix &&
            projected.DiscNumber is > 0 && !ContainsToken(fileTemplate, "Disc"))
            fileTemplate = "{Disc:00}-" + fileTemplate;

        string fileName = Render(fileTemplate, projected, naming);
        string[] directoryParts = RenderDirectoryParts(
            directoryTemplate, projected, naming, effectiveComponentLimit).ToArray();
        string safeFileName = SanitizeComponent(fileName, naming, effectiveComponentLimit);
        if (safeFileName.Length == 0)
            throw new InvalidDataException(
                $"Naming profile '{profile.Name}' produced an empty file name.");
        string resolved = Path.Combine(
            [Path.GetFullPath(root), .. directoryParts, safeFileName]).Normalize();
        if (naming.CompletePathLengthLimit is { } pathLimit && resolved.Length > pathLimit)
            throw new InvalidDataException(
                $"Naming profile '{profile.Name}' produced a {resolved.Length}-character path, " +
                $"exceeding its {pathLimit}-character complete-path limit: {resolved}");
        return resolved;
    }

    public string ResolveCollision(
        string initialPath,
        string sourcePath,
        LibraryProfile profile,
        int collisionNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(profile);
        string directory = Path.GetDirectoryName(initialPath)!;
        string stem = Path.GetFileNameWithoutExtension(initialPath);
        string extension = Path.GetExtension(initialPath);
        return profile.Naming.CollisionPolicy switch
        {
            LibraryPathCollisionPolicy.Stop => throw new InvalidDataException(
                $"Naming profile '{profile.Name}' maps more than one file to '{initialPath}'."),
            LibraryPathCollisionPolicy.PreserveExisting => Path.GetFullPath(sourcePath),
            LibraryPathCollisionPolicy.Hash => Path.Combine(directory,
                $"{stem}_{StableHash(sourcePath)}{extension}").Normalize(),
            LibraryPathCollisionPolicy.Suffix => Path.Combine(directory,
                profile.Naming.UseItunesCanonicalNaming
                    ? $"{stem} {collisionNumber}{extension}"
                    : $"{stem}_{collisionNumber}{extension}").Normalize(),
            _ => throw new ArgumentOutOfRangeException(nameof(profile.Naming.CollisionPolicy)),
        };
    }

    private static string ResolveItunes(string root, LibraryPathMetadata metadata)
    {
        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        int? discNumber = metadata.DiscNumber is > 0 ? metadata.DiscNumber : null;
        string destination = Path.GetFileName(targetRoot).Equals(
                "Music", StringComparison.OrdinalIgnoreCase)
            ? ItlMediaOrganization.CanonicalMusicFolderPath(targetRoot,
                metadata.AlbumArtist, metadata.Artist, metadata.Album, metadata.TrackNumber,
                metadata.Title, metadata.Compilation, metadata.Extension, discNumber)
            : ItlMediaOrganization.CanonicalMusicPath(targetRoot,
                metadata.AlbumArtist, metadata.Artist, metadata.Album, metadata.TrackNumber,
                metadata.Title, metadata.Compilation, metadata.Extension, discNumber);
        return destination.Normalize();
    }

    private static string ResolveLegacy(
        string root,
        LibraryPathMetadata metadata,
        int lengthLimit,
        int discLimit)
    {
        string artist = metadata.EffectiveAlbumArtist.LimitLength(lengthLimit).FixPath();
        string album = FormatSuffix.Replace(metadata.Album, "")
            .FormatDisc(lengthLimit, discLimit).FixPath();
        string title = metadata.Title.LimitLength(lengthLimit).FixPath();
        string name = (metadata.TrackNumber is int track ? $"{track:D2} " : "") + title;
        return Path.Combine(Path.GetFullPath(root), artist, album,
            name + metadata.Extension).Normalize();
    }

    private static LibraryPathMetadata ApplyDiscPolicy(
        LibraryPathMetadata metadata,
        LibraryDiscPolicy policy)
    {
        if (metadata.DiscNumber is not > 0)
            return metadata;
        if (policy.Strategy == LibraryDiscStrategy.FlattenContinuous &&
            metadata.TrackNumber is > 0)
        {
            if (metadata.FlattenedTrackNumber is not > 0)
                throw new InvalidDataException(
                    "The continuous disc-numbering strategy requires album context. " +
                    "Preview the complete album before resolving its paths.");
            return metadata with
            {
                TrackNumber = metadata.FlattenedTrackNumber,
                DiscNumber = null,
            };
        }
        if (policy.Strategy != LibraryDiscStrategy.AlbumSuffix)
            return metadata;
        string album = Regex.IsMatch(metadata.Album, @" \(Disc \d+\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? metadata.Album
            : $"{metadata.Album} (Disc {metadata.DiscNumber})";
        return metadata with { Album = album };
    }

    private static LibraryPathMetadata ApplyNamingFallbacks(
        LibraryPathMetadata metadata,
        LibraryNamingPolicy naming)
    {
        string artist = string.IsNullOrWhiteSpace(metadata.Artist)
            ? naming.MissingArtistFallback
            : metadata.Artist;
        string? albumArtist = string.IsNullOrWhiteSpace(metadata.AlbumArtist)
            ? null
            : metadata.AlbumArtist;
        string album = string.IsNullOrWhiteSpace(metadata.Album)
            ? naming.MissingAlbumFallback
            : metadata.Album;
        string title = string.IsNullOrWhiteSpace(metadata.Title)
            ? naming.MissingTitleFallback
            : metadata.Title;
        return metadata with
        {
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            Title = title,
        };
    }

    private static string Render(
        string template,
        LibraryPathMetadata metadata,
        LibraryNamingPolicy naming)
    {
        string withOptionals = OptionalFragment.Replace(template, match =>
        {
            string content = match.Groups["content"].Value;
            MatchCollection tokens = Token.Matches(content);
            return tokens.Count > 0 && tokens.Cast<Match>().Any(token =>
                    string.IsNullOrWhiteSpace(TokenValue(token, metadata, naming)))
                ? ""
                : content;
        });
        return Token.Replace(withOptionals, match => TokenValue(match, metadata, naming));
    }

    private static string TokenValue(
        Match token,
        LibraryPathMetadata metadata,
        LibraryNamingPolicy naming)
    {
        string name = token.Groups["name"].Value;
        string? format = token.Groups["format"].Success
            ? token.Groups["format"].Value
            : null;
        return name.ToUpperInvariant() switch
        {
            "ALBUMARTIST" => metadata.EffectiveAlbumArtist,
            "ARTIST" => metadata.Artist,
            "ALBUM" => naming.StripFormatSuffixes
                ? FormatSuffix.Replace(metadata.Album, "")
                : metadata.Album,
            "TITLE" => metadata.Title,
            "COMPILATION" => metadata.Compilation ? naming.CompilationValue : "",
            "YEAR" => Year.Match(metadata.ReleaseDate ?? "") is { Success: true } match
                ? match.Groups["year"].Value
                : "",
            "GENRE" => metadata.Genre ?? "",
            "DISC" => FormatNumber(metadata.DiscNumber, format, naming.DiscPadding),
            "TRACK" => FormatNumber(metadata.TrackNumber, format, naming.TrackPadding),
            "ORIGINALNAME" => metadata.OriginalName,
            "EXTENSION" => NormalizeExtension(metadata.Extension),
            _ => throw new InvalidDataException(
                $"Unknown naming-template token '{{{name}}}'."),
        };
    }

    private static string FormatNumber(int? value, string? format, int defaultPadding)
    {
        if (value is null)
            return "";
        string actualFormat = string.IsNullOrWhiteSpace(format)
            ? "D" + defaultPadding.ToString(CultureInfo.InvariantCulture)
            : format;
        try
        {
            return value.Value.ToString(actualFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException(
                $"Invalid numeric naming-template format '{actualFormat}'.", error);
        }
    }

    private static IEnumerable<string> RenderDirectoryParts(
        string template,
        LibraryPathMetadata metadata,
        LibraryNamingPolicy naming,
        int limit)
    {
        // Split the template before substituting tokens. A slash in a tag value such as AC/DC is
        // data to sanitize, never an instruction to create another directory.
        foreach (string componentTemplate in template.Split(['/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string sanitized = SanitizeComponent(
                Render(componentTemplate, metadata, naming), naming, limit);
            if (sanitized is "." or "..")
                throw new InvalidDataException("Naming templates cannot contain relative traversal.");
            if (sanitized.Length > 0)
                yield return sanitized;
        }
    }

    private static string SanitizeComponent(
        string value,
        LibraryNamingPolicy naming,
        int limit)
    {
        string normalized = NormalizeUnicode(value, naming.UnicodeNormalization);
        string result = naming.PreserveUnicode ? normalized : FoldToAscii(normalized);
        string replacement = naming.InvalidCharacterReplacement ?? "";
        result = result.Replace("/", replacement, StringComparison.Ordinal)
            .Replace("\\", replacement, StringComparison.Ordinal);
        foreach (char character in Path.GetInvalidFileNameChars()
                     .Concat(Path.GetInvalidPathChars()).Distinct())
            result = result.Replace(character.ToString(), replacement,
                StringComparison.Ordinal);
        result = result.Trim().TrimEnd('.');
        return result.LimitLength(limit);
    }

    private static string NormalizeUnicode(
        string value,
        LibraryUnicodeNormalization normalization) => normalization switch
        {
            LibraryUnicodeNormalization.None => value,
            LibraryUnicodeNormalization.FormC => value.Normalize(NormalizationForm.FormC),
            LibraryUnicodeNormalization.FormD => value.Normalize(NormalizationForm.FormD),
            LibraryUnicodeNormalization.FormKC => value.Normalize(NormalizationForm.FormKC),
            LibraryUnicodeNormalization.FormKD => value.Normalize(NormalizationForm.FormKD),
            _ => throw new ArgumentOutOfRangeException(nameof(normalization)),
        };

    private static string FoldToAscii(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                character <= 0x7f)
                result.Append(character);
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool ContainsToken(string template, string token) =>
        Regex.IsMatch(template, @"\{" + Regex.Escape(token) + @"(?::[^}]+)?\}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension)
            ? ""
            : extension.StartsWith('.') ? extension : "." + extension;

    private static string StableHash(string sourcePath) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath))))[..8].ToLowerInvariant();
}

/// <summary>Compatibility facade for existing organization callers.</summary>
internal static class LibraryCanonicalPath
{
    public static string Initial(
        LibraryIndexLocation target,
        LibraryProfile profile,
        MetadataCacheEntry entry,
        string source,
        int lengthLimit,
        int discLimit) => LibraryPathLayoutResolver.Shared.Resolve(
            target.Target, profile, LibraryPathMetadata.From(entry, source),
            lengthLimit, discLimit);

    public static string Collision(
        LibraryIndexLocation target,
        LibraryProfile profile,
        MetadataCacheEntry entry,
        string source,
        int lengthLimit,
        int discLimit,
        int collisionNumber)
    {
        string initial = Initial(target, profile, entry, source, lengthLimit, discLimit);
        return LibraryPathLayoutResolver.Shared.ResolveCollision(
            initial, source, profile, collisionNumber);
    }

    public static string Initial(
        LibraryIndexLocation target,
        MetadataCacheEntry entry,
        string source,
        int lengthLimit,
        int discLimit)
    {
        LibraryProfile profile = target.UseItunesCanonicalNaming
            ? LibraryProfilePresets.Create(LibraryProfilePreset.ItunesMedia)
            : LibraryProfilePresets.Create(LibraryProfilePreset.LegacyMusicLibraryTools);
        return Initial(target, profile, entry, source, lengthLimit, discLimit);
    }

    public static string Collision(
        LibraryIndexLocation target,
        MetadataCacheEntry entry,
        string source,
        int lengthLimit,
        int discLimit,
        int collisionNumber)
    {
        LibraryProfile profile = target.UseItunesCanonicalNaming
            ? LibraryProfilePresets.Create(LibraryProfilePreset.ItunesMedia)
            : LibraryProfilePresets.Create(LibraryProfilePreset.LegacyMusicLibraryTools);
        return Collision(target, profile, entry, source,
            lengthLimit, discLimit, collisionNumber);
    }
}
