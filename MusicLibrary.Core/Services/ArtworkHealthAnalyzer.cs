using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>Cache-only artwork health analysis. It never requests or decodes image blobs.</summary>
public static class ArtworkHealthAnalyzer
{
    public const int OversizedByteThreshold =
        LibraryArtworkHealthSettings.DefaultOversizedByteThreshold;
    public const int OversizedDimensionThreshold =
        LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold;

    public static AnalysisReport Analyze(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        CancellationToken ct = default)
        => Analyze(records, artwork, OversizedByteThreshold, OversizedDimensionThreshold, ct);

    public static AnalysisReport Analyze(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        int oversizedByteThreshold,
        int oversizedDimensionThreshold,
        CancellationToken ct = default)
        => Analyze(records, artwork, null, oversizedByteThreshold,
            oversizedDimensionThreshold, ct);

    public static AnalysisReport Analyze(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryConfiguration? configuration,
        int oversizedByteThreshold,
        int oversizedDimensionThreshold,
        CancellationToken ct = default)
        => Analyze(records, artwork, configuration, oversizedByteThreshold,
            oversizedDimensionThreshold, null, ct);

    public static AnalysisReport Analyze(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryConfiguration? configuration,
        int oversizedByteThreshold,
        int oversizedDimensionThreshold,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oversizedByteThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oversizedDimensionThreshold);
        var findings = new List<AnalysisFinding>();
        var byPath = artwork.ToDictionary(file => file.Path, PathComparer);

        progress?.Report(new(0, records.Count, "tracks", "Checking artwork metadata"));
        for (int index = 0; index < records.Count; index++)
        {
            TrackRecord record = records[index];
            ct.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(record.Path, out var file) || !file.ArtworkScanned)
            {
                findings.Add(new(record.Path,
                    "Artwork metadata is deferred. Hydrate this file when it is selected for review.",
                    "Artwork scan deferred"));
            }
            else if (file.Images.Count == 0)
            {
                findings.Add(new(record.Path, "No embedded artwork is cached for this file.", "Missing artwork"));
            }
            else
            {
                foreach (var image in file.Images)
                {
                    if (string.IsNullOrWhiteSpace(image.Hash) || string.IsNullOrWhiteSpace(image.ImageType) ||
                        image.Width <= 0 || image.Height <= 0 || image.Size <= 0)
                        findings.Add(new(record.Path,
                            $"Cached image metadata is invalid ({image.ImageType}, {image.Width}x{image.Height}, {image.Size:N0} bytes).",
                            "Unreadable artwork"));
                    if (image.Size > oversizedByteThreshold || image.Width > oversizedDimensionThreshold ||
                        image.Height > oversizedDimensionThreshold)
                        findings.Add(new(record.Path,
                            $"Embedded {image.ImageType} is {image.Width:N0}x{image.Height:N0} and {image.Size:N0} bytes.",
                            "Oversized artwork"));
                }
                foreach (var duplicate in file.Images.Where(image => !string.IsNullOrWhiteSpace(image.Hash))
                             .GroupBy(image => image.Hash, StringComparer.Ordinal).Where(group => group.Count() > 1))
                    findings.Add(new(record.Path,
                        $"The same embedded image appears {duplicate.Count():N0} times in this file.",
                        "Duplicate embedded artwork"));
            }
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == records.Count)
                progress?.Report(new(completed, records.Count, "tracks",
                    "Checking artwork metadata", record.Path));
        }

        Func<TrackRecord, string> albumKey = configuration is null
            ? AlbumKey
            : record => LibraryAlbumIdentityResolver.Key(record, configuration);
        int completedAlbumTracks = 0;
        int lastReportedAlbumTracks = 0;
        progress?.Report(new(0, records.Count, "tracks", "Comparing album artwork"));
        foreach (IGrouping<string, TrackRecord> album in records.GroupBy(albumKey))
        {
            ct.ThrowIfCancellationRequested();
            var scanned = album.Select(record => (Record: record, Artwork: byPath.GetValueOrDefault(record.Path)))
                .Where(item => item.Artwork?.ArtworkScanned == true).ToList();
            if (scanned.Count >= 2)
            {
                var signatures = scanned.Select(item => Signature(item.Artwork!))
                    .Distinct(StringComparer.Ordinal).ToList();
                if (signatures.Count > 1)
                    foreach (var item in scanned)
                        findings.Add(new(item.Record.Path,
                            $"Album tracks use {signatures.Count:N0} distinct embedded-artwork sets.",
                            "Mixed album artwork"));
            }
            completedAlbumTracks += album.Count();
            if (completedAlbumTracks - lastReportedAlbumTracks >= 128 ||
                completedAlbumTracks == records.Count)
            {
                progress?.Report(new(completedAlbumTracks, records.Count, "tracks",
                    "Comparing album artwork"));
                lastReportedAlbumTracks = completedAlbumTracks;
            }
        }

        return new("Artwork health", findings
            .OrderBy(finding => finding.Problem, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(finding => finding.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    private static string Signature(ArtworkAuditFile file) => string.Join("|", file.Images
        .Select(image => image.Hash).OrderBy(hash => hash, StringComparer.Ordinal));
    private static string AlbumKey(TrackRecord record) =>
        Normalize(record.EffectiveAlbumArtist) + "\0" + Normalize(record.StrippedAlbum ?? record.Album);
    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
