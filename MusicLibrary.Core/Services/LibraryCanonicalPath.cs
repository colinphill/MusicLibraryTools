using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibraryTools;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MusicLibrary.Core.Services;

/// <summary>Projects a cached track into the naming convention selected by its IndexTarget.</summary>
internal static class LibraryCanonicalPath
{
    private static readonly Regex ExistingDiscTrackPrefix = new(
        @"^(?<disc>[1-9]\d*)-(?<track>\d{2,}) ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Initial(LibraryIndexLocation target, MetadataCacheEntry entry,
        string source, int lengthLimit, int discLimit)
    {
        string extension = Path.GetExtension(source);
        if (target.UseItunesCanonicalNaming)
        {
            string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Target));
            int? discNumber = EffectiveDiscNumber(entry, source);
            string destination = Path.GetFileName(targetRoot).Equals(
                    "Music", StringComparison.OrdinalIgnoreCase)
                ? ItlMediaOrganization.CanonicalMusicFolderPath(targetRoot,
                    entry.AlbumArtist, entry.Artist, entry.Album, entry.TrackNumber, entry.Title,
                    entry.Compilation, extension, discNumber)
                : ItlMediaOrganization.CanonicalMusicPath(targetRoot,
                    entry.AlbumArtist, entry.Artist, entry.Album, entry.TrackNumber, entry.Title,
                    entry.Compilation, extension, discNumber);
            return destination.Normalize();
        }

        return Path.Combine(target.Target,
            entry.FormatPath(lengthLimit, discLimit) + extension).Normalize();
    }

    public static string Collision(LibraryIndexLocation target, MetadataCacheEntry entry,
        string source, int lengthLimit, int discLimit, int collisionNumber)
    {
        if (target.UseItunesCanonicalNaming)
        {
            string initial = Initial(target, entry, source, lengthLimit, discLimit);
            return Path.Combine(Path.GetDirectoryName(initial)!,
                $"{Path.GetFileNameWithoutExtension(initial)} {collisionNumber}" +
                Path.GetExtension(initial)).Normalize();
        }

        return Path.Combine(target.Target,
            entry.FormatPath(lengthLimit, discLimit) + $"_{collisionNumber}" +
            Path.GetExtension(source)).Normalize();
    }

    private static int? EffectiveDiscNumber(MetadataCacheEntry entry, string source)
    {
        if (entry.DiscNumber is > 0)
            return entry.DiscNumber;
        if (entry.TrackNumber is not > 0)
            return null;

        Match match = ExistingDiscTrackPrefix.Match(Path.GetFileNameWithoutExtension(source));
        return match.Success &&
               int.TryParse(match.Groups["track"].Value, NumberStyles.None,
                   CultureInfo.InvariantCulture, out int track) &&
               track == entry.TrackNumber &&
               int.TryParse(match.Groups["disc"].Value, NumberStyles.None,
                   CultureInfo.InvariantCulture, out int disc)
            ? disc
            : null;
    }
}
