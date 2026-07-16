using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>Projects a cached track into the naming convention selected by its IndexTarget.</summary>
internal static class LibraryCanonicalPath
{
    public static string Initial(LibraryIndexLocation target, MetadataCacheEntry entry,
        string source, int lengthLimit, int discLimit)
    {
        string extension = Path.GetExtension(source);
        if (target.UseItunesCanonicalNaming)
        {
            string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Target));
            string destination = Path.GetFileName(targetRoot).Equals(
                    "Music", StringComparison.OrdinalIgnoreCase)
                ? ItlMediaOrganization.CanonicalMusicFolderPath(targetRoot,
                    entry.AlbumArtist, entry.Artist, entry.Album, entry.TrackNumber, entry.Title,
                    entry.Compilation, extension)
                : ItlMediaOrganization.CanonicalMusicPath(targetRoot,
                    entry.AlbumArtist, entry.Artist, entry.Album, entry.TrackNumber, entry.Title,
                    entry.Compilation, extension);
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
}
