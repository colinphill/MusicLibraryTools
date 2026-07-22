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
        => Find(records, BuildKey, null, ct);

    public static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default)
        => Find(records, BuildKey, progress, ct);

    public static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration,
        CancellationToken ct = default) =>
        Find(records, record => BuildKey(record, configuration), null, ct);

    public static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default) =>
        Find(records, record => BuildKey(record, configuration), progress, ct);

    private static IReadOnlyList<DuplicateGroup> Find(
        IReadOnlyList<TrackRecord> records,
        Func<TrackRecord, DuplicateKey> keyFor,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct)
    {
        var buckets = new Dictionary<DuplicateKey, List<TrackRecord>>();
        progress?.Report(new(0, records.Count, "tracks", "Analyzing duplicate candidates"));
        for (int index = 0; index < records.Count; index++)
        {
            TrackRecord record = records[index];
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(record.Title))
            {
                var key = keyFor(record);
                if (!buckets.TryGetValue(key, out var bucket))
                    buckets[key] = bucket = [];
                bucket.Add(record);
            }
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == records.Count)
                progress?.Report(new(completed, records.Count, "tracks",
                    "Analyzing duplicate candidates", record.Path));
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
