using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public enum LibraryRepresentation
{
    CdFlac,
    HighResolutionFlac,
    Purchased,
    GeneratedAac,
    Other,
}

/// <summary>Cache-only comparison of track counterparts across representations of the same album.</summary>
public static class RepresentationAnalyzer
{
    public static AnalysisReport Compare(IReadOnlyList<TrackRecord> records, CancellationToken ct = default)
        => Compare(records, Classify, AlbumKey, ct);

    public static AnalysisReport Compare(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration,
        CancellationToken ct = default) =>
        Compare(records, record => Classify(record, configuration),
            record => LibraryAlbumIdentityResolver.Key(record, configuration), ct);

    private static AnalysisReport Compare(
        IReadOnlyList<TrackRecord> records,
        Func<TrackRecord, LibraryRepresentation> classify,
        Func<TrackRecord, string> albumKey,
        CancellationToken ct)
    {
        var findings = new List<AnalysisFinding>();
        foreach (var album in records.GroupBy(albumKey))
        {
            ct.ThrowIfCancellationRequested();
            var represented = album.Select(record => (Record: record, Role: classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other)
                .ToList();
            var roles = represented.Select(item => item.Role).Distinct().Order().ToList();
            if (roles.Count < 2)
                continue;

            var counts = roles.ToDictionary(role => role,
                role => represented.Where(item => item.Role == role)
                    .Select(item => TrackKey(item.Record)).Distinct().Count());
            if (counts.Values.Distinct().Count() > 1)
            {
                var source = represented[0].Record;
                findings.Add(new(source.Path,
                    $"Album track counts differ: {string.Join(", ", counts.Select(pair =>
                        $"{Display(pair.Key)} {pair.Value:N0}"))}.",
                    "Representation track-count drift"));
            }

            foreach (var track in represented.GroupBy(item => TrackKey(item.Record)))
            {
                var byRole = track.GroupBy(item => item.Role).ToDictionary(group => group.Key, group => group.ToList());
                foreach (var duplicate in byRole.Where(pair => pair.Value.Count > 1))
                {
                    foreach (var item in duplicate.Value)
                        findings.Add(new(item.Record.Path,
                            $"{Display(duplicate.Key)} has {duplicate.Value.Count:N0} candidates for " +
                            $"{TrackDisplay(item.Record)}; counterpart matching is ambiguous.",
                            "Ambiguous representation counterpart"));
                }
                foreach (var missing in roles.Where(role => !byRole.ContainsKey(role)))
                {
                    var source = track.First().Record;
                    findings.Add(new(source.Path,
                        $"{TrackDisplay(source)} is present as {string.Join(", ", byRole.Keys.Select(Display))} " +
                        $"but missing from {Display(missing)}.",
                        "Missing representation counterpart"));
                }
                if (byRole.Count >= 2 && byRole.Values.All(candidates => candidates.Count == 1))
                    AddTrackDrift(byRole.Values.Select(candidates => candidates[0].Record).ToList(),
                        classify, findings);
            }
        }
        return new("Album representations", findings
            .OrderBy(finding => finding.Path, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>Paths whose artwork is worth hydrating: unique, matched counterparts only.</summary>
    public static IReadOnlyList<string> ArtworkCandidatePaths(IReadOnlyList<TrackRecord> records)
        => ArtworkCandidatePaths(records, Classify, AlbumKey);

    public static IReadOnlyList<string> ArtworkCandidatePaths(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration) =>
        ArtworkCandidatePaths(records, record => Classify(record, configuration),
            record => LibraryAlbumIdentityResolver.Key(record, configuration));

    private static IReadOnlyList<string> ArtworkCandidatePaths(
        IReadOnlyList<TrackRecord> records,
        Func<TrackRecord, LibraryRepresentation> classify,
        Func<TrackRecord, string> albumKey)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var album in records.GroupBy(albumKey))
        {
            var represented = album.Select(record => (Record: record, Role: classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other).ToList();
            if (represented.Select(item => item.Role).Distinct().Count() < 2)
                continue;
            foreach (var track in represented.GroupBy(item => TrackKey(item.Record)))
            {
                var byRole = track.GroupBy(item => item.Role).ToList();
                if (byRole.Count >= 2 && byRole.All(group => group.Count() == 1))
                    foreach (var item in track) paths.Add(item.Record.Path);
            }
        }
        return paths.ToList();
    }

    public static AnalysisReport CompareArtwork(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyDictionary<string, string> signatures)
        => CompareArtwork(records, signatures, Classify, AlbumKey);

    public static AnalysisReport CompareArtwork(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyDictionary<string, string> signatures,
        LibraryConfiguration configuration) =>
        CompareArtwork(records, signatures, record => Classify(record, configuration),
            record => LibraryAlbumIdentityResolver.Key(record, configuration));

    private static AnalysisReport CompareArtwork(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyDictionary<string, string> signatures,
        Func<TrackRecord, LibraryRepresentation> classify,
        Func<TrackRecord, string> albumKey)
    {
        var findings = new List<AnalysisFinding>();
        foreach (var album in records.GroupBy(albumKey))
        {
            var represented = album.Select(record => (Record: record, Role: classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other && signatures.ContainsKey(item.Record.Path))
                .ToList();
            if (represented.Select(item => item.Role).Distinct().Count() < 2)
                continue;
            var byRole = represented.GroupBy(item => item.Role).ToList();
            bool mixed = false;
            foreach (var role in byRole)
            {
                var distinct = role.Select(item => signatures[item.Record.Path]).Distinct(StringComparer.Ordinal).ToList();
                if (distinct.Count <= 1)
                    continue;
                mixed = true;
                findings.Add(new(role.First().Record.Path,
                    $"{Display(role.Key)} has {distinct.Count:N0} different embedded-artwork signatures within the album.",
                    "Mixed representation artwork"));
            }
            if (mixed)
                continue;
            var roleSignatures = byRole.Select(role => (role.Key, Signature: signatures[role.First().Record.Path])).ToList();
            if (roleSignatures.Select(item => item.Signature).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                findings.Add(new(represented[0].Record.Path,
                    $"Album artwork differs across {string.Join(", ", roleSignatures.Select(item =>
                        $"{Display(item.Key)} ({(item.Signature.Length == 0 ? "missing" : "embedded")})"))}.",
                    "Representation artwork drift"));
            }
        }
        return new("Representation artwork", findings);
    }

    public static IReadOnlyList<DecodedAudioPair> DecodedAudioCandidatePairs(
        IReadOnlyList<TrackRecord> records)
        => DecodedAudioCandidatePairs(records, Classify, AlbumKey);

    public static IReadOnlyList<DecodedAudioPair> DecodedAudioCandidatePairs(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration configuration) =>
        DecodedAudioCandidatePairs(records, record => Classify(record, configuration),
            record => LibraryAlbumIdentityResolver.Key(record, configuration));

    private static IReadOnlyList<DecodedAudioPair> DecodedAudioCandidatePairs(
        IReadOnlyList<TrackRecord> records,
        Func<TrackRecord, LibraryRepresentation> classify,
        Func<TrackRecord, string> albumKey)
    {
        var pairs = new List<DecodedAudioPair>();
        foreach (var album in records.GroupBy(albumKey))
        {
            var represented = album.Select(record => (Record: record, Role: classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other).ToList();
            foreach (var track in represented.GroupBy(item => TrackKey(item.Record)))
            {
                var unique = track.GroupBy(item => item.Role).Where(group => group.Count() == 1)
                    .Select(group => group.Single()).ToList();
                for (int first = 0; first < unique.Count; first++)
                for (int second = first + 1; second < unique.Count; second++)
                {
                    var left = unique[first].Record;
                    var right = unique[second].Record;
                    if (left.CodecType != CodecType.Lossless || right.CodecType != CodecType.Lossless ||
                        left.SampleRate == 0 || left.BitsPerSample == 0 || left.Channels == 0 ||
                        left.SampleRate != right.SampleRate || left.BitsPerSample != right.BitsPerSample ||
                        left.Channels != right.Channels)
                        continue;
                    pairs.Add(new(left.Path, right.Path,
                        $"{TrackDisplay(left)}: {Display(unique[first].Role)} versus {Display(unique[second].Role)}"));
                }
            }
        }
        return pairs;
    }

    private static void AddTrackDrift(
        IReadOnlyList<TrackRecord> counterparts,
        Func<TrackRecord, LibraryRepresentation> classify,
        List<AnalysisFinding> findings)
    {
        var source = counterparts[0];
        AddField("title", counterparts.Select(record => record.Title), Normalize, source, findings);
        AddField("artist", counterparts.Select(record => record.Artist), Normalize, source, findings);
        AddField("album artist", counterparts.Select(record => record.AlbumArtist), Normalize, source, findings);
        AddField("release date", counterparts.Select(record => record.ReleaseDate), Normalize, source, findings);
        AddField("track total", counterparts.Select(record => record.TrackTotal?.ToString()), value => value ?? "", source, findings);
        AddField("disc total", counterparts.Select(record => record.DiscTotal?.ToString()), value => value ?? "", source, findings);
        var durations = counterparts.Where(record => record.DurationInSeconds > 0).Select(record => record.DurationInSeconds).ToList();
        if (durations.Count == counterparts.Count && durations.Max() - durations.Min() > 2)
            findings.Add(new(source.Path,
                $"{TrackDisplay(source)} duration differs by {durations.Max() - durations.Min():N0} seconds across counterparts " +
                $"({string.Join(", ", counterparts.Select(record => $"{Display(classify(record))} {record.DurationInSeconds}s"))}).",
                "Representation duration drift"));
    }

    private static void AddField(
        string field,
        IEnumerable<string?> values,
        Func<string?, string> normalize,
        TrackRecord source,
        List<AnalysisFinding> findings)
    {
        var materialized = values.ToList();
        if (materialized.Select(normalize).Distinct(StringComparer.Ordinal).Count() <= 1)
            return;
        findings.Add(new(source.Path,
            $"{TrackDisplay(source)} has differing {field}: {string.Join(" / ", materialized.Select(value =>
                string.IsNullOrWhiteSpace(value) ? "(missing)" : value))}.",
            "Representation metadata drift"));
    }

    public static LibraryRepresentation Classify(TrackRecord record)
    {
        string extension = Path.GetExtension(record.Path).ToLowerInvariant();
        string path = record.Path.ToLowerInvariant();
        if (path.Contains("purchased", StringComparison.Ordinal) &&
            extension is ".m4a" or ".mp4" or ".mp3")
            return LibraryRepresentation.Purchased;
        if (extension == ".flac")
            return record.SampleRate > 48_000 || record.BitsPerSample > 16
                ? LibraryRepresentation.HighResolutionFlac
                : LibraryRepresentation.CdFlac;
        if (record.CodecType == CodecType.Lossy &&
            (extension is ".m4a" or ".mp4") &&
            (path.Contains("generated", StringComparison.Ordinal) ||
             path.Contains("aac", StringComparison.Ordinal) ||
             path.Contains("sync", StringComparison.Ordinal) ||
             (record.CodecName?.Contains("AAC", StringComparison.OrdinalIgnoreCase) ?? false)))
            return LibraryRepresentation.GeneratedAac;
        return LibraryRepresentation.Other;
    }

    public static LibraryRepresentation Classify(
        TrackRecord record,
        LibraryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(configuration);
        LibraryIndexLocation? root = LibraryRootPermissionPolicy.MostSpecific(
            record.Path, configuration.IndexLocations);
        if (root is null)
            return LibraryRepresentation.Other;

        return root.RepresentationRole switch
        {
            LibraryRepresentationRole.LegacyAutomatic => Classify(record),
            LibraryRepresentationRole.Ignore => LibraryRepresentation.Other,
            LibraryRepresentationRole.CdLossless => LibraryRepresentation.CdFlac,
            LibraryRepresentationRole.HighResolutionLossless =>
                LibraryRepresentation.HighResolutionFlac,
            LibraryRepresentationRole.Purchased => LibraryRepresentation.Purchased,
            LibraryRepresentationRole.GeneratedLossy => LibraryRepresentation.GeneratedAac,
            LibraryRepresentationRole.LosslessByQuality => ClassifyLosslessByQuality(
                record, configuration.GetEffectiveProfile(root).Quality),
            _ => throw new ArgumentOutOfRangeException(
                nameof(root.RepresentationRole), root.RepresentationRole, null),
        };
    }

    private static LibraryRepresentation ClassifyLosslessByQuality(
        TrackRecord record,
        LibraryQualityPolicy quality)
    {
        if (record.CodecType != CodecType.Lossless)
            return LibraryRepresentation.Other;
        return record.SampleRate >= quality.HighResolutionMinimumSampleRateHz ||
               record.BitsPerSample >= quality.HighResolutionMinimumBitsPerSample
            ? LibraryRepresentation.HighResolutionFlac
            : LibraryRepresentation.CdFlac;
    }

    private static string AlbumKey(TrackRecord record) =>
        Normalize(record.EffectiveAlbumArtist) + "\0" + Normalize(record.StrippedAlbum ?? record.Album);

    private static string TrackKey(TrackRecord record) => record.TrackNumber is int track
        ? $"{record.DiscNumber ?? 1:D4}\0{track:D6}"
        : $"title\0{Normalize(record.Title)}";

    private static string TrackDisplay(TrackRecord record) => record.TrackNumber is int track
        ? $"disc {record.DiscNumber ?? 1}, track {track} ({record.Title ?? "untitled"})"
        : record.Title ?? "untitled track";

    private static string Display(LibraryRepresentation role) => role switch
    {
        LibraryRepresentation.CdFlac => "CD FLAC",
        LibraryRepresentation.HighResolutionFlac => "high-resolution FLAC",
        LibraryRepresentation.Purchased => "purchased audio",
        LibraryRepresentation.GeneratedAac => "generated AAC",
        _ => "other representation",
    };

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? "").Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
