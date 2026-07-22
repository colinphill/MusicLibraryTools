using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Pure analysis functions over a flat list of <see cref="TrackRecord"/> (built from the cache).
/// These reimplement the reporting checks that live tangled inside AnalyzeMetadata/FindNonLossless,
/// returning typed findings instead of writing to the console.
/// </summary>
public static class LibraryAnalyzer
{
    /// <summary>
    /// Basic per-file metadata hygiene corresponding to AnalyzeMetadata's historical basecheck.
    /// </summary>
    public static AnalysisReport BasicMetadata(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var findings = new List<AnalysisFinding>();
        foreach (TrackRecord record in records.OrderBy(
                     record => record.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (record.TrackTotal == 0)
                findings.Add(new(record.Path, "0 TrackTotal", "Zero track total",
                    LibraryHealthRuleIds.MissingTrackTotal));
            else if (record.TrackTotal is null)
                findings.Add(new(record.Path, "Missing TrackTotal", "Missing track total",
                    LibraryHealthRuleIds.MissingTrackTotal));
            if ((record.TrackNumber ?? 0) == 0)
                findings.Add(new(record.Path, "0/Missing TrackNumber",
                    "Missing or zero track number", LibraryHealthRuleIds.MissingTrackTotal));
            if (record.DiscNumber is not null || record.DiscTotal is not null)
                findings.Add(new(record.Path,
                    $"({record.DiscNumber}/{record.DiscTotal}) Disc", "Disc metadata present",
                    LibraryHealthRuleIds.DiscMetadata));
            if (record.Path.Contains('\u00A0'))
                findings.Add(new(record.Path, "Contains nbsp", "Non-breaking space in path",
                    LibraryHealthRuleIds.NormalizeWhitespace));
        }
        return new("Basic metadata check", findings);
    }

    public static AnalysisReport BasicMetadata(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy) =>
        LibraryHealthPolicyService.Default.ApplyToReport(BasicMetadata(records), policy);

    /// <summary>
    /// Basic metadata hygiene plus album-folder track-total disagreements.
    /// </summary>
    public static AnalysisReport MetadataInconsistencies(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var findings = new List<AnalysisFinding>();
        foreach (var folder in records.GroupBy(
                     record => Path.GetDirectoryName(record.Path) ?? "",
                     PathComparer))
        {
            if (folder.Select(record => record.Album).Distinct().Take(2).Count() != 1)
                continue;
            if (folder.Select(record => record.TrackTotal).Distinct().Take(2).Count() > 1)
            {
                string path = folder.Key;
                findings.Add(new(path, "Multiple Track Totals",
                    "Album folder contains multiple track totals",
                    LibraryHealthRuleIds.MissingTrackTotal));
            }
        }
        findings.AddRange(BasicMetadata(records).Findings);
        return new("Metadata inconsistencies", findings);
    }

    public static AnalysisReport MetadataInconsistencies(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy) =>
        LibraryHealthPolicyService.Default.ApplyToReport(
            MetadataInconsistencies(records), policy);

    /// <summary>Low-resolution files stored anywhere beneath a HiRes directory.</summary>
    public static AnalysisReport LowResolutionInHighResolutionTree(
        IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var findings = records
            .Where(record => ContainsPathSegment(record.Path, "hires") &&
                record.SampleRate <= 48_000 && record.BitsPerSample <= 16)
            .OrderBy(record => record.Path, PathComparer)
            .Select(record => new AnalysisFinding(record.Path,
                $"({record.SampleRate}/{record.BitsPerSample})",
                "Low-resolution file in HiRes tree"))
            .ToList();
        return new("Low-resolution files in HiRes tree", findings);
    }

    /// <summary>Files whose sample rate or sample width exceeds compact-disc resolution.</summary>
    public static AnalysisReport HighResolutionAudio(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var findings = records
            .Where(record => record.SampleRate > 44_100 || record.BitsPerSample > 16)
            .OrderBy(record => record.Path, PathComparer)
            .Select(record => new AnalysisFinding(record.Path,
                $"({record.SampleRate}/{record.BitsPerSample})",
                "High-resolution audio"))
            .ToList();
        return new("High-resolution audio", findings);
    }

    /// <summary>
    /// Compare albums in a selected HiRes branch with the standard-resolution library using the
    /// historical progressively tightened fuzzy artist/album match.
    /// </summary>
    public static ResolutionComparisonReport CompareResolutionAlbums(
        IReadOnlyList<TrackRecord> records,
        params string[] highResolutionPathSegments)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (highResolutionPathSegments.Length == 0)
            throw new ArgumentException("At least one path segment is required.",
                nameof(highResolutionPathSegments));

        TrackRecord[] highFiles = records.Where(record =>
            ContainsPathSequence(record.Path, highResolutionPathSegments)).ToArray();
        TrackRecord[] standardFiles = records.Where(record =>
            !ContainsPathSegment(record.Path, "hires")).ToArray();
        var highAlbums = highFiles.Select(record => new ResolutionAlbum(
                record.EffectiveAlbumArtist,
                record.StrippedAlbum ?? record.Album ?? "",
                Path.GetDirectoryName(record.Path) ?? ""))
            .Distinct()
            .ToArray();
        var standardAlbums = standardFiles.Select(record => new ResolutionAlbum(
                record.EffectiveAlbumArtist,
                record.Album ?? "",
                Path.GetDirectoryName(record.Path) ?? ""))
            .Distinct()
            .ToArray();

        var findings = new List<ResolutionComparisonFinding>();
        int matched = 0, missing = 0, ambiguous = 0;
        foreach (ResolutionAlbum album in highAlbums)
        {
            double threshold = 0.5;
            ResolutionAlbum[] possibilities = MatchingAlbums(standardAlbums, album, threshold);
            while (possibilities.Length > 1 && threshold >= 0.1)
            {
                threshold -= 0.1;
                possibilities = MatchingAlbums(standardAlbums, album, threshold);
            }

            if (possibilities.Length == 0)
            {
                missing++;
                findings.Add(new(ResolutionComparisonKind.Missing, album, null, threshold));
                continue;
            }
            if (possibilities.Length > 1)
            {
                ambiguous++;
                findings.Add(new(ResolutionComparisonKind.Ambiguous, album, null, threshold,
                    possibilities));
                continue;
            }

            matched++;
            ResolutionAlbum standard = possibilities[0];
            int highTrackCount = highFiles.Count(record =>
                StringComparer.Ordinal.Equals(record.StrippedAlbum ?? record.Album ?? "", album.Album) &&
                (StringComparer.Ordinal.Equals(record.Artist, album.Artist) ||
                 StringComparer.Ordinal.Equals(record.AlbumArtist, album.Artist)));
            int standardTrackCount = standardFiles.Count(record =>
                StringComparer.Ordinal.Equals(record.Album ?? "", standard.Album) &&
                (StringComparer.Ordinal.Equals(record.Artist, standard.Artist) ||
                 StringComparer.Ordinal.Equals(record.AlbumArtist, standard.Artist)));
            if (highTrackCount < standardTrackCount)
            {
                findings.Add(new(ResolutionComparisonKind.TrackCountMismatch, album, standard,
                    threshold, HighTrackCount: highTrackCount,
                    StandardTrackCount: standardTrackCount));
                continue;
            }

            int artistDistance = album.Artist.EditDistance(standard.Artist);
            int albumDistance = album.Album.EditDistance(standard.Album);
            if (artistDistance != 0 || albumDistance != 0)
            {
                findings.Add(new(ResolutionComparisonKind.MetadataDifference, album, standard,
                    threshold, ArtistDistance: artistDistance, AlbumDistance: albumDistance));
            }
        }

        return new(highAlbums.Length, matched, missing, ambiguous, findings);
    }

    /// <summary>Files that are lossy (candidates that should perhaps be lossless).</summary>
    public static AnalysisReport Lossless(IReadOnlyList<TrackRecord> records)
    {
        var findings = records
            .Where(r => r.CodecType == CodecType.Lossy)
            .OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new AnalysisFinding(r.Path, $"{r.CodecName} (lossy)", "Lossy codec",
                LibraryHealthRuleIds.LossyFile))
            .ToList();
        return new AnalysisReport("Lossy files", findings);
    }

    public static AnalysisReport Lossless(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy) =>
        LibraryHealthPolicyService.Default.ApplyToReport(Lossless(records), policy);

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
                            "Disagreeing track totals",
                            LibraryHealthRuleIds.MissingTrackTotal));
                    continue;
                }

                foreach (var r in disc)
                    if (r.TrackNumber is int tn && r.TrackTotal is int tt && tn > tt)
                        findings.Add(new AnalysisFinding(r.Path,
                            $"track {tn} exceeds total {tt} on '{r.Album}'",
                            "Track number exceeds total",
                            LibraryHealthRuleIds.MissingTrackTotal));
            }

            var discTotals = album.Where(r => r.DiscTotal is > 0)
                .Select(r => r.DiscTotal!.Value)
                .Distinct()
                .ToList();
            if (discTotals.Count > 1)
                foreach (var r in album)
                    findings.Add(new AnalysisFinding(r.Path,
                        $"{label}: disagreeing total discs ({string.Join("/", discTotals)})",
                        "Disagreeing disc totals", LibraryHealthRuleIds.DiscMetadata));

            foreach (var r in album)
                if (r.DiscNumber is int dn && r.DiscTotal is int dt && dn > dt)
                    findings.Add(new AnalysisFinding(r.Path,
                        $"disc {dn} exceeds total {dt} on '{r.Album}'",
                        "Disc number exceeds total", LibraryHealthRuleIds.DiscMetadata));
        }

        return new AnalysisReport("Inconsistent track/disc totals", findings);
    }

    public static AnalysisReport InconsistentTotals(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy) =>
        LibraryHealthPolicyService.Default.ApplyToReport(InconsistentTotals(records), policy);

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
                findings.Add(new AnalysisFinding(r.Path, "missing total-tracks",
                    "Missing track total", LibraryHealthRuleIds.MissingTrackTotal));
            else if (r.TrackTotal == 0)
                findings.Add(new AnalysisFinding(r.Path, "zero total-tracks",
                    "Zero track total", LibraryHealthRuleIds.MissingTrackTotal));

            if ((r.TrackNumber ?? 0) == 0)
                findings.Add(new AnalysisFinding(r.Path, "missing/zero track number",
                    "Missing or zero track number", LibraryHealthRuleIds.MissingTrackTotal));

            if (r.Path.Contains('\u00A0'))
                findings.Add(new AnalysisFinding(r.Path, "path contains a non-breaking space",
                    "Non-breaking space in path", LibraryHealthRuleIds.NormalizeWhitespace));
        }

        return new AnalysisReport("Inconsistencies", findings);
    }

    public static AnalysisReport Inconsistencies(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy) =>
        LibraryHealthPolicyService.Default.ApplyToReport(Inconsistencies(records), policy);

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

    public static IReadOnlyList<AnalysisReport> RunAll(
        IReadOnlyList<TrackRecord> records,
        LibraryHealthPolicy policy,
        CancellationToken ct = default) =>
    [
        Lossless(records, policy),
        InconsistentTotals(records, policy),
        LibraryHealthPolicyService.Default.ApplyToReport(
            SimilarArtists(records, ct: ct), policy),
    ];

    private static ResolutionAlbum[] MatchingAlbums(
        IEnumerable<ResolutionAlbum> candidates,
        ResolutionAlbum album,
        double threshold) =>
        candidates.Where(candidate =>
            IsFuzzy(candidate.Artist, album.Artist, threshold) &&
            IsFuzzy(candidate.Album, album.Album, threshold)).ToArray();

    private static bool IsFuzzy(string left, string right, double threshold)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return false;
        double length = Math.Max(left.Length, right.Length);
        int distance = left.ToLowerInvariant().EditDistance(right.ToLowerInvariant());
        return distance / length < threshold;
    }

    private static bool ContainsPathSegment(string path, string segment) =>
        PathSegments(path).Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static bool ContainsPathSequence(string path, IReadOnlyList<string> sequence)
    {
        string[] segments = PathSegments(path);
        for (int start = 0; start <= segments.Length - sequence.Count; start++)
        {
            bool match = true;
            for (int offset = 0; offset < sequence.Count; offset++)
            {
                if (!segments[start + offset].Equals(
                        sequence[offset], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    private static string[] PathSegments(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
