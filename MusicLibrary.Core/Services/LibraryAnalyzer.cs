using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Pure analysis functions over a flat list of <see cref="TrackRecord"/> (built from the cache).
/// These reimplement the reporting checks that live tangled inside AnalyzeMetadata/FindNonLossless,
/// returning typed findings instead of writing to the console.
/// </summary>
public static class LibraryAnalyzer
{
    /// <summary>Files that are lossy (candidates that should perhaps be lossless).</summary>
    public static AnalysisReport Lossless(IReadOnlyList<TrackRecord> records)
    {
        var findings = records
            .Where(r => r.CodecType == CodecType.Lossy)
            .OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new AnalysisFinding(r.Path, $"{r.CodecName} (lossy)", "Lossy codec"))
            .ToList();
        return new AnalysisReport("Lossy files", findings);
    }

    /// <summary>
    /// Albums whose track/disc totals are inconsistent: disagreeing TrackTotal within a disc,
    /// disagreeing DiscTotal across the album, or a track/disc number that exceeds its stated total.
    /// </summary>
    public static AnalysisReport InconsistentTotals(IReadOnlyList<TrackRecord> records)
    {
        var findings = new List<AnalysisFinding>();

        var albums = records
            .Where(r => !string.IsNullOrWhiteSpace(r.Album))
            .GroupBy(r => (
                Artist: r.EffectiveAlbumArtist.Trim().ToUpperInvariant(),
                Album: (r.StrippedAlbum ?? r.Album!).Trim().ToUpperInvariant()));

        foreach (var album in albums)
        {
            var first = album.First();
            var label = $"{first.EffectiveAlbumArtist} — {first.StrippedAlbum ?? first.Album}";

            // Track totals are per disc, so compare them within each disc rather than across a whole
            // multi-disc release.
            foreach (var disc in album.GroupBy(r => r.DiscNumber ?? 1))
            {
                var totals = disc.Where(r => r.TrackTotal is > 0)
                    .Select(r => r.TrackTotal!.Value)
                    .Distinct()
                    .ToList();
                if (totals.Count > 1)
                {
                    foreach (var r in disc)
                        findings.Add(new AnalysisFinding(r.Path,
                            $"{label}: disagreeing total tracks ({string.Join("/", totals)})",
                            "Disagreeing track totals"));
                    continue;
                }

                foreach (var r in disc)
                    if (r.TrackNumber is int tn && r.TrackTotal is int tt && tn > tt)
                        findings.Add(new AnalysisFinding(r.Path,
                            $"track {tn} exceeds total {tt} on '{r.Album}'",
                            "Track number exceeds total"));
            }

            var discTotals = album.Where(r => r.DiscTotal is > 0)
                .Select(r => r.DiscTotal!.Value)
                .Distinct()
                .ToList();
            if (discTotals.Count > 1)
                foreach (var r in album)
                    findings.Add(new AnalysisFinding(r.Path,
                        $"{label}: disagreeing total discs ({string.Join("/", discTotals)})",
                        "Disagreeing disc totals"));

            foreach (var r in album)
                if (r.DiscNumber is int dn && r.DiscTotal is int dt && dn > dt)
                    findings.Add(new AnalysisFinding(r.Path,
                        $"disc {dn} exceeds total {dt} on '{r.Album}'",
                        "Disc number exceeds total"));
        }

        return new AnalysisReport("Inconsistent track/disc totals", findings);
    }

    /// <summary>
    /// A broader tag-hygiene pass (mirrors AnalyzeMetadata's "incon"): the album-level total-track
    /// disagreements from <see cref="InconsistentTotals"/>, plus per-file missing/zero track number or
    /// total and non-breaking-space characters in the path.
    /// </summary>
    public static AnalysisReport Inconsistencies(IReadOnlyList<TrackRecord> records)
    {
        var findings = new List<AnalysisFinding>(InconsistentTotals(records).Findings);

        foreach (var r in records.OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (r.TrackTotal is null)
                findings.Add(new AnalysisFinding(r.Path, "missing total-tracks", "Missing track total"));
            else if (r.TrackTotal == 0)
                findings.Add(new AnalysisFinding(r.Path, "zero total-tracks", "Zero track total"));

            if ((r.TrackNumber ?? 0) == 0)
                findings.Add(new AnalysisFinding(r.Path, "missing/zero track number", "Missing or zero track number"));

            if (r.Path.Contains('\u00A0'))
                findings.Add(new AnalysisFinding(r.Path, "path contains a non-breaking space", "Non-breaking space in path"));
        }

        return new AnalysisReport("Inconsistencies", findings);
    }

    /// <summary>
    /// Artist name variants that are near-duplicates of each other (e.g. "Beatles" vs "The Beatles"),
    /// flagged by normalized fuzzy distance so they can be reconciled.
    /// </summary>
    public static AnalysisReport SimilarArtists(
        IReadOnlyList<TrackRecord> records,
        double threshold = 0.15,
        CancellationToken ct = default)
    {
        var artists = records
            .Select(r => r.EffectiveAlbumArtist)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var findings = new List<AnalysisFinding>();
        var seen = new HashSet<string>();

        for (int i = 0; i < artists.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < artists.Count; j++)
            {
                var a = artists[i];
                var b = artists[j];
                var d = a.FuzzyDistance(b);
                if (d > 0 && d <= threshold && seen.Add(a + "\0" + b))
                {
                    // Anchor the finding on a representative file for each variant.
                    var pathA = records.First(r => r.EffectiveAlbumArtist == a).Path;
                    findings.Add(new AnalysisFinding(pathA, $"'{a}' ≈ '{b}' (distance {d:F2})",
                        "Similar artist names"));
                }
            }
        }

        return new AnalysisReport("Similar artist names", findings);
    }

    /// <summary>Run every analyzer.</summary>
    public static IReadOnlyList<AnalysisReport> RunAll(
        IReadOnlyList<TrackRecord> records,
        CancellationToken ct = default) =>
    [
        Lossless(records),
        InconsistentTotals(records),
        SimilarArtists(records, ct: ct),
    ];
}
