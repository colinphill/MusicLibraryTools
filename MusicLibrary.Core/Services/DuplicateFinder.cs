using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

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
        => Find(records, BuildKey, ct);

    public static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration,
        CancellationToken ct = default) =>
        Find(records, record => BuildKey(record, configuration), ct);

    private static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        Func<TrackRecord, DuplicateKey> keyFor,
        CancellationToken ct)
    {
        var buckets = new Dictionary<DuplicateKey, List<TrackRecord>>();
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(record.Title))
                continue;
            var key = keyFor(record);
            if (!buckets.TryGetValue(key, out var bucket))
                buckets[key] = bucket = [];
            bucket.Add(record);
        }

        var groups = buckets
            .Where(pair => pair.Value.Count > 1)
            .Select(pair =>
            {
                ct.ThrowIfCancellationRequested();
                return new DuplicateGroup(
                    pair.Key.Display,
                    pair.Value.OrderByDescending(r => r.SampleRate)
                        .ThenByDescending(r => r.BitsPerSample)
                        .ToList());
            })
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return groups;
    }

    private static DuplicateKey BuildKey(TrackRecord r) => new(
        Normalize(r.EffectiveAlbumArtist),
        Normalize(r.StrippedAlbum ?? r.Album ?? ""),
        r.TrackNumber,
        Normalize(BaseTitle(r.Title ?? "")));

    private static DuplicateKey BuildKey(
        TrackRecord record,
        LibraryConfiguration configuration) => new(
        Normalize(record.EffectiveAlbumArtist),
        LibraryAlbumIdentityResolver.Key(record, configuration),
        record.TrackNumber,
        Normalize(BaseTitle(record.Title ?? "")));

    private readonly record struct DuplicateKey(string Artist, string Album, int? Track, string Title)
    {
        public string Display => $"{Artist} | {Album} | {Track?.ToString() ?? "?"} | {Title}";
    }

    private static string BaseTitle(string title) => VersionSuffix.Replace(title, "").Trim();

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();
}
