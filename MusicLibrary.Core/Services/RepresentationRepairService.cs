using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public interface IRepresentationRepairService
{
    Task<RepresentationRepairPreview> PreviewAsync(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration? configuration,
        CancellationToken ct = default);
}

/// <summary>
/// Builds a non-mutating repair preview from cached representation metadata. Metadata copies use
/// the normal analysis repair model, so applying them still verifies the cached size/timestamp.
/// Derivation and organization remain explicit file-operation previews.
/// </summary>
public sealed class RepresentationRepairService(ILibraryOrganizer organizer) : IRepresentationRepairService
{
    private static readonly LibraryRepresentation[] CanonicalOrder =
    [
        LibraryRepresentation.HighResolutionFlac,
        LibraryRepresentation.CdFlac,
        LibraryRepresentation.Purchased,
        LibraryRepresentation.GeneratedAac,
    ];

    private static readonly (TagFields Field, Func<TrackRecord, string?> Value)[] CopyFields =
    [
        (TagFields.Title, record => record.Title),
        (TagFields.Artist, record => record.Artist),
        (TagFields.AlbumArtist, record => record.AlbumArtist),
        (TagFields.Date, record => record.ReleaseDate),
        (TagFields.TotalTracks, record => record.TrackTotal?.ToString()),
        (TagFields.TotalDiscs, record => record.DiscTotal?.ToString()),
    ];

    public async Task<RepresentationRepairPreview> PreviewAsync(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration? libraryConfiguration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var metadata = PreviewMetadataCopies(records, ct);
        var actions = new List<RepresentationRepairAction>();
        var warnings = new List<string>();

        IngestMusicConfiguration? configuration = null;
        if (libraryConfiguration is null)
        {
            warnings.Add("Derivation preview unavailable: load a library configuration first.");
        }
        else
        {
            try
            {
                configuration = IngestMusicConfiguration.FromLibraryConfiguration(libraryConfiguration);
                actions.AddRange(PreviewDerivations(records, configuration, ct));
                if (string.IsNullOrWhiteSpace(configuration.AacDestination))
                    warnings.Add(
                        "AAC derivation preview unavailable: assign an AAC fallback IndexTarget. " +
                        "Direct iTunes import is handled by the Ingest workflow.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Derivation preview unavailable: {ex.Message}");
            }
        }

        try
        {
            var representedPaths = records
                .Where(record => RepresentationAnalyzer.Classify(record) != LibraryRepresentation.Other)
                .Select(record => record.Path)
                .ToHashSet(PathComparer);
            var moves = await organizer.PreviewMovesAsync(ct);
            actions.AddRange(moves
                .Where(move => representedPaths.Contains(move.Source))
                .Select(move => new RepresentationRepairAction(
                    RepresentationRepairKind.Organize,
                    move.Source,
                    move.Destination,
                    "Move this representation to its canonical artist/album/track path.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Organization preview unavailable: {ex.Message}");
        }

        return new RepresentationRepairPreview(
            metadata,
            actions.OrderBy(action => action.Kind)
                .ThenBy(action => action.SourcePath, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            warnings);
    }

    internal static AnalysisRepairPlan PreviewMetadataCopies(
        IReadOnlyList<TrackRecord> records,
        CancellationToken ct = default)
    {
        var repairs = new List<AnalysisTagRepair>();
        foreach (var album in records.GroupBy(AlbumKey))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var track in album.GroupBy(TrackKey))
            {
                var candidates = track
                    .Select(record => (Record: record, Role: RepresentationAnalyzer.Classify(record)))
                    .Where(item => item.Role != LibraryRepresentation.Other)
                    .GroupBy(item => item.Role)
                    .Where(group => group.Count() == 1)
                    .Select(group => group.Single())
                    .ToList();
                if (candidates.Count < 2)
                    continue;

                foreach (var (field, value) in CopyFields)
                {
                    var canonical = CanonicalOrder
                        .Select(role => candidates.FirstOrDefault(candidate => candidate.Role == role))
                        .FirstOrDefault(candidate => candidate.Record is not null &&
                            !string.IsNullOrWhiteSpace(value(candidate.Record)));
                    if (canonical.Record is null)
                        continue;
                    string after = value(canonical.Record)!.Trim();

                    foreach (var target in candidates.Where(candidate => candidate.Role != canonical.Role))
                    {
                        string? before = value(target.Record);
                        if (Normalize(before) == Normalize(after))
                            continue;
                        repairs.Add(new AnalysisTagRepair(
                            target.Record.Path,
                            field,
                            before,
                            after,
                            $"Copies {FieldName(field)} from the matched {Display(canonical.Role)} counterpart.",
                            target.Record.Length,
                            target.Record.LastWriteTime));
                    }
                }
            }
        }

        return new AnalysisRepairPlan("Copy representation metadata", repairs
            .GroupBy(repair => (repair.Path, repair.Field), PathFieldComparer.Instance)
            .Select(group => group.First())
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repair => repair.Field)
            .ToList());
    }

    internal static IReadOnlyList<RepresentationRepairAction> PreviewDerivations(
        IReadOnlyList<TrackRecord> records,
        IngestMusicConfiguration configuration,
        CancellationToken ct = default)
    {
        var actions = new List<RepresentationRepairAction>();
        var claimed = new HashSet<string>(records.Select(record => record.Path), PathComparer);
        foreach (var album in records.GroupBy(AlbumKey))
        foreach (var track in album.GroupBy(TrackKey))
        {
            ct.ThrowIfCancellationRequested();
            var byRole = track
                .Select(record => (Record: record, Role: RepresentationAnalyzer.Classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other)
                .GroupBy(item => item.Role)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Record);

            if (!byRole.ContainsKey(LibraryRepresentation.CdFlac) &&
                byRole.TryGetValue(LibraryRepresentation.HighResolutionFlac, out var highResolution))
            {
                string destination = ClaimCanonical(configuration.PairedCdDestination, highResolution,
                    ".flac", configuration, claimed);
                actions.Add(new(RepresentationRepairKind.DeriveCdFlac, highResolution.Path, destination,
                    "Downsample the high-resolution FLAC to a paired CD-quality FLAC, then copy normalized metadata."));
            }

            bool hasPortableCounterpart = byRole.ContainsKey(LibraryRepresentation.GeneratedAac) ||
                byRole.ContainsKey(LibraryRepresentation.Purchased);
            if (!hasPortableCounterpart &&
                !string.IsNullOrWhiteSpace(configuration.AacDestination))
            {
                TrackRecord? source = byRole.GetValueOrDefault(LibraryRepresentation.CdFlac) ??
                    byRole.GetValueOrDefault(LibraryRepresentation.HighResolutionFlac);
                if (source is not null)
                {
                    string destination = ClaimCanonical(configuration.AacDestination, source,
                        ".m4a", configuration, claimed);
                    actions.Add(new(RepresentationRepairKind.DeriveAac, source.Path, destination,
                        $"Encode AAC at {configuration.AacBitrateKbps:N0} kbit/s with " +
                        $"{configuration.AacEncoder}, then copy normalized metadata."));
                }
            }
        }
        return actions;
    }

    private static string ClaimCanonical(string root, TrackRecord record, string extension,
        IngestMusicConfiguration configuration, HashSet<string> claimed)
    {
        string artist = record.EffectiveAlbumArtist.LimitLength(configuration.LengthLimit).FixPath();
        string album = (record.StrippedAlbum ?? record.Album ?? "Unknown Album")
            .FormatDisc(configuration.LengthLimit, configuration.DiscNumLengthLimit).FixPath();
        string title = (record.Title ?? "Untitled").LimitLength(configuration.LengthLimit).FixPath();
        string name = (record.TrackNumber is int number ? $"{number:D2} " : "") + title;
        string basePath = Path.Combine(root, artist, album, name);
        string destination = basePath + extension;
        int suffix = 2;
        while (!claimed.Add(destination))
            destination = basePath + $"_{suffix++}" + extension;
        return destination;
    }

    private static string AlbumKey(TrackRecord record) =>
        Normalize(record.EffectiveAlbumArtist) + "\0" + Normalize(record.StrippedAlbum ?? record.Album);

    private static string TrackKey(TrackRecord record) => record.TrackNumber is int track
        ? $"{record.DiscNumber ?? 1:D4}\0{track:D6}"
        : $"title\0{Normalize(record.Title)}";

    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Display(LibraryRepresentation role) => role switch
    {
        LibraryRepresentation.HighResolutionFlac => "high-resolution FLAC",
        LibraryRepresentation.CdFlac => "CD FLAC",
        LibraryRepresentation.Purchased => "purchased audio",
        LibraryRepresentation.GeneratedAac => "generated AAC",
        _ => "other",
    };

    private static string FieldName(TagFields field) => field switch
    {
        TagFields.AlbumArtist => "album artist",
        TagFields.Date => "release date",
        TagFields.TotalTracks => "track total",
        TagFields.TotalDiscs => "disc total",
        _ => field.ToString().ToLowerInvariant(),
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class PathFieldComparer : IEqualityComparer<(string Path, TagFields Field)>
    {
        public static PathFieldComparer Instance { get; } = new();
        public bool Equals((string Path, TagFields Field) x, (string Path, TagFields Field) y) =>
            x.Field == y.Field && PathComparer.Equals(x.Path, y.Path);
        public int GetHashCode((string Path, TagFields Field) value) =>
            HashCode.Combine(PathComparer.GetHashCode(value.Path), value.Field);
    }
}
