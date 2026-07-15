using MusicFileUtilities;
using MusicLibrary.Core.Models;

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
    {
        var findings = new List<AnalysisFinding>();
        foreach (var album in records.GroupBy(AlbumKey))
        {
            ct.ThrowIfCancellationRequested();
            var represented = album.Select(record => (Record: record, Role: Classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other)
                .ToList();
            var roles = represented.Select(item => item.Role).Distinct().Order().ToList();
            if (roles.Count < 2)
                continue;

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
            }
        }
        return new("Album representations", findings
            .OrderBy(finding => finding.Path, StringComparer.CurrentCultureIgnoreCase).ToList());
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
