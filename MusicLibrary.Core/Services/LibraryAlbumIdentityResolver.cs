using System.Text;
using System.Text.RegularExpressions;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>Builds exact album identities from the effective root profile.</summary>
public static class LibraryAlbumIdentityResolver
{
    private static readonly Regex FormatSuffix = new(
        @" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DiscSuffix = new(
        @" \(Disc \d+\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Year = new(
        @"\b\d{4}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Key(TrackRecord record, LibraryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(configuration);
        LibraryIndexLocation? root = LibraryRootPermissionPolicy.MostSpecific(
            record.Path, configuration.IndexLocations);
        LibraryProfile profile = root is null
            ? configuration.ActiveProfile
            : configuration.GetEffectiveProfile(root);
        return Key(record, profile.AlbumIdentity);
    }

    public static string Key(TrackRecord record, LibraryAlbumIdentityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(policy);
        string artist = policy.UseAlbumArtist
            ? record.EffectiveAlbumArtist
            : record.Artist ?? string.Empty;
        string album = record.Album ?? string.Empty;
        if (policy.StripFormatSuffixes)
            album = FormatSuffix.Replace(album, string.Empty);
        if (policy.StripDiscSuffixes)
            album = DiscSuffix.Replace(album, string.Empty);
        string year = policy.IncludeReleaseYear
            ? (record.Year?.ToString() ?? Year.Match(record.ReleaseDate ?? string.Empty).Value)
            : string.Empty;
        return Normalize(artist) + "\0" + Normalize(album) + "\0" + Normalize(year);
    }

    public static string Key(MetadataCacheEntry entry, LibraryAlbumIdentityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(policy);
        string artist = policy.UseAlbumArtist && entry.HasAlbumArtist
            ? entry.AlbumArtist
            : entry.Artist;
        string album = entry.Album;
        if (policy.StripFormatSuffixes)
            album = FormatSuffix.Replace(album, string.Empty);
        if (policy.StripDiscSuffixes)
            album = DiscSuffix.Replace(album, string.Empty);
        string year = policy.IncludeReleaseYear
            ? entry.Year?.ToString() ?? Year.Match(entry.ReleaseDate ?? string.Empty).Value
            : string.Empty;
        return Normalize(artist) + "\0" + Normalize(album) + "\0" + Normalize(year);
    }

    /// <summary>
    /// Assigns a one-based ordinal to each distinct disc/track slot in every exact album. Multiple
    /// representations of a slot receive the same number, and unequal disc lengths are handled
    /// without assuming a fixed track total.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ContinuousTrackNumbers<T>(
        IEnumerable<T> records,
        Func<T, string> albumKey,
        Func<T, string> path,
        Func<T, int?> discNumber,
        Func<T, int?> trackNumber)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(albumKey);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(discNumber);
        ArgumentNullException.ThrowIfNull(trackNumber);
        var result = new Dictionary<string, int>(PathComparer);
        foreach (IGrouping<string, T> album in records.GroupBy(albumKey,
                     StringComparer.Ordinal))
        {
            var slots = album
                .Where(record => trackNumber(record) is > 0)
                .Select(record => (Disc: discNumber(record) is > 0
                        ? discNumber(record)!.Value
                        : 1,
                    Track: trackNumber(record)!.Value))
                .Distinct()
                .OrderBy(slot => slot.Disc)
                .ThenBy(slot => slot.Track)
                .Select((slot, index) => (slot, Number: index + 1))
                .ToDictionary(item => item.slot, item => item.Number);
            foreach (T record in album)
            {
                if (trackNumber(record) is not > 0)
                    continue;
                var slot = (Disc: discNumber(record) is > 0
                        ? discNumber(record)!.Value
                        : 1,
                    Track: trackNumber(record)!.Value);
                result[path(record)] = slots[slot];
            }
        }
        return result;
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
                result.Append(' ');
            result.Append(char.ToUpperInvariant(character));
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
