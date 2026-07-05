using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Finds redundant tracks — the same recording present more than once — by bucketing on
/// artist / stripped-album / track number / version-stripped title. This reimplements the
/// CheckRedundancies bucketing against the cache (cross-platform; no iTunes dependency).
/// </summary>
public static class DuplicateFinder
{
    // Strip trailing version/qualifier suffixes like " (Remastered)", " (Live)", " [2009]".
    private static readonly Regex VersionSuffix =
        new(@"[ \t]*[\(\[][^)\]]*[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<TrackRecord> records, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var groups = records
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .GroupBy(BuildKey)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                g.Key,
                g.OrderByDescending(r => r.SampleRate)
                 .ThenByDescending(r => r.BitsPerSample)
                 .ToList()))
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return groups;
    }

    private static string BuildKey(TrackRecord r)
    {
        var artist = Normalize(r.EffectiveAlbumArtist);
        var album = Normalize(r.StrippedAlbum ?? r.Album ?? "");
        var title = Normalize(BaseTitle(r.Title ?? ""));
        var track = r.TrackNumber?.ToString() ?? "?";
        return $"{artist}|{album}|{track}|{title}";
    }

    private static string BaseTitle(string title) => VersionSuffix.Replace(title, "").Trim();

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();
}
