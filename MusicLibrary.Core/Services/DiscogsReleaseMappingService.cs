using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record DiscogsSourceFile(
    string Path,
    string? Title = null,
    string? Artist = null,
    int? DiscNumber = null,
    int? TrackNumber = null,
    TimeSpan? Duration = null);

public sealed record DiscogsRankedTrack(
    DiscogsTrackCandidate Track,
    int SourceIndex,
    int DiscNumber,
    int TrackNumber,
    int Score,
    string Reason);

public enum DiscogsMappingConfidence
{
    Unmatched,
    Ambiguous,
    Metadata,
}

public sealed record DiscogsTrackMatch(
    DiscogsSourceFile Source,
    DiscogsRankedTrack? SuggestedTrack,
    ImmutableArray<DiscogsRankedTrack> Candidates,
    DiscogsMappingConfidence Confidence,
    string Status);

public sealed record DiscogsReleaseMapping(
    DiscogsReleaseCandidate Release,
    ImmutableArray<DiscogsTrackMatch> Files)
{
    public int SuggestedCount =>
        Files.Count(file => file.SuggestedTrack is not null);
    public int AmbiguousCount =>
        Files.Count(file =>
            file.Confidence == DiscogsMappingConfidence.Ambiguous);
}

public sealed record DiscogsImportOptions(
    bool TrackTitles = true,
    bool TrackArtists = true,
    bool ReleaseIdentity = true,
    bool Numbering = true,
    bool ReleaseDetails = true,
    bool GenresAndStyles = true,
    bool DiscogsIdentifier = true);

public sealed record DiscogsConfirmedTrack(
    string Path,
    DiscogsRankedTrack Track);

public interface IDiscogsReleaseMappingService
{
    Task<DiscogsReleaseMapping> MapAsync(
        DiscogsReleaseCandidate release,
        IReadOnlyList<DiscogsSourceFile> files,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> CreateEdits(
        DiscogsReleaseCandidate release,
        IReadOnlyList<DiscogsConfirmedTrack> mappings,
        DiscogsImportOptions options);
}

public sealed class DiscogsReleaseMappingService :
    IDiscogsReleaseMappingService
{
    public Task<DiscogsReleaseMapping> MapAsync(
        DiscogsReleaseCandidate release,
        IReadOnlyList<DiscogsSourceFile> files,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => Map(release, files, progress, ct), ct);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>>
        CreateEdits(
            DiscogsReleaseCandidate release,
            IReadOnlyList<DiscogsConfirmedTrack> mappings,
            DiscogsImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(options);
        var result =
            new Dictionary<string, IReadOnlyList<MetadataValueEdit>>(
                PathComparer);
        int totalDiscs = IndexedTracks(release.Tracks)
            .Select(track => track.DiscNumber)
            .Distinct()
            .Count();
        foreach (DiscogsConfirmedTrack mapping in mappings)
        {
            var edits =
                new Dictionary<MetadataFieldKey, MetadataValueEdit>();
            void Add(TagFields field, string? value) =>
                AddValues(MetadataFieldKey.Known(field),
                    string.IsNullOrWhiteSpace(value)
                        ? []
                        : [value.Trim()]);
            void AddValues(
                MetadataFieldKey field,
                ImmutableArray<string> values)
            {
                if (values.Length > 0)
                    edits[field] = new(field, values);
            }

            DiscogsRankedTrack ranked = mapping.Track;
            DiscogsTrackCandidate track = ranked.Track;
            if (options.TrackTitles)
                Add(TagFields.Title, track.Title);
            if (options.TrackArtists)
                Add(TagFields.Artist,
                    string.IsNullOrWhiteSpace(track.ArtistCredit)
                        ? release.ArtistCredit
                        : track.ArtistCredit);
            if (options.ReleaseIdentity)
            {
                Add(TagFields.Album, release.Title);
                Add(TagFields.AlbumArtist, release.ArtistCredit);
                Add(TagFields.Date,
                    release.Released ??
                    release.Year?.ToString(CultureInfo.InvariantCulture));
            }
            if (options.Numbering)
            {
                Add(TagFields.TrackNumber,
                    ranked.TrackNumber.ToString(CultureInfo.InvariantCulture));
                int totalTracks = IndexedTracks(release.Tracks).Count(item =>
                    item.DiscNumber == ranked.DiscNumber);
                Add(TagFields.TotalTracks,
                    totalTracks.ToString(CultureInfo.InvariantCulture));
                Add(TagFields.DiscNumber,
                    ranked.DiscNumber.ToString(CultureInfo.InvariantCulture));
                Add(TagFields.TotalDiscs,
                    totalDiscs.ToString(CultureInfo.InvariantCulture));
            }
            if (options.ReleaseDetails)
            {
                Add(TagFields.Barcode, release.Barcodes.FirstOrDefault());
                Add(TagFields.CatalogNumber,
                    release.CatalogNumbers.FirstOrDefault());
                Add(TagFields.Label, release.Labels.FirstOrDefault());
                Add(TagFields.ReleaseCountry, release.Country);
                Add(TagFields.Media,
                    release.Formats.Length == 1
                        ? release.Formats[0]
                        : null);
                Add(TagFields.Website, release.WebUri?.ToString());
            }
            if (options.GenresAndStyles)
            {
                AddValues(
                    MetadataFieldKey.Known(TagFields.Genre),
                    release.Genres
                        .Concat(release.Styles)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToImmutableArray());
            }
            if (options.DiscogsIdentifier)
            {
                AddValues(
                    MetadataFieldKey.Custom("DISCOGS_RELEASE_ID"),
                    [release.ReleaseId.ToString(
                        CultureInfo.InvariantCulture)]);
            }
            if (edits.Count > 0)
                result[Path.GetFullPath(mapping.Path)] =
                    edits.Values.ToArray();
        }
        return result;
    }

    private static DiscogsReleaseMapping Map(
        DiscogsReleaseCandidate release,
        IReadOnlyList<DiscogsSourceFile> files,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ImmutableArray<DiscogsRankedTrack> tracks =
            IndexedTracks(release.Tracks);
        var matches = new List<DiscogsTrackMatch>(files.Count);
        for (int index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            DiscogsSourceFile source = files[index];
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                files.Count,
                source.Path,
                $"Matching file {index + 1:N0} of {files.Count:N0}"));
            matches.Add(Match(source, tracks));
        }
        ResolveDuplicateSuggestions(matches);
        progress?.Report(new(
            OperationPhase.Completed,
            files.Count,
            files.Count,
            Message:
                $"Suggested {matches.Count(match => match.SuggestedTrack is not null):N0} " +
                $"of {files.Count:N0} Discogs track mapping(s)"));
        return new(release, [.. matches]);
    }

    private static DiscogsTrackMatch Match(
        DiscogsSourceFile source,
        ImmutableArray<DiscogsRankedTrack> tracks)
    {
        DiscogsRankedTrack[] ranked = tracks
            .Select(track => Score(source, track))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.SourceIndex)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < 80)
            return new(
                source,
                null,
                [.. ranked],
                DiscogsMappingConfidence.Unmatched,
                "No reliable Discogs release-track match was found.");
        DiscogsRankedTrack best = ranked[0];
        if (ranked.Length > 1 && ranked[1].Score == best.Score)
            return new(
                source,
                null,
                [.. ranked],
                DiscogsMappingConfidence.Ambiguous,
                $"Two or more Discogs tracks share the best score ({best.Score}).");
        return new(
            source,
            best,
            [.. ranked],
            DiscogsMappingConfidence.Metadata,
            $"Metadata suggestion; {best.Reason}.");
    }

    private static DiscogsRankedTrack Score(
        DiscogsSourceFile source,
        DiscogsRankedTrack track)
    {
        int score = 0;
        var reasons = new List<string>();
        if (source.DiscNumber is > 0 &&
            source.DiscNumber == track.DiscNumber)
        {
            score += 100;
            reasons.Add("disc");
        }
        if (source.TrackNumber is > 0 &&
            source.TrackNumber == track.TrackNumber)
        {
            score += 100;
            reasons.Add("track");
        }
        if (SameNormalized(source.Title, track.Track.Title))
        {
            score += 80;
            reasons.Add("title");
        }
        if (SameNormalized(source.Artist, track.Track.ArtistCredit))
        {
            score += 30;
            reasons.Add("artist");
        }
        if (source.Duration is not null &&
            ParseDuration(track.Track.Duration) is TimeSpan duration)
        {
            double delta = Math.Abs(
                (source.Duration.Value - duration).TotalSeconds);
            if (delta <= 2)
            {
                score += 50;
                reasons.Add("duration");
            }
            else if (delta <= 5)
            {
                score += 20;
                reasons.Add("near duration");
            }
        }
        return track with
        {
            Score = score,
            Reason = reasons.Count == 0
                ? "no matching hints"
                : string.Join(", ", reasons),
        };
    }

    private static ImmutableArray<DiscogsRankedTrack> IndexedTracks(
        ImmutableArray<DiscogsTrackCandidate> tracks)
    {
        var result = ImmutableArray.CreateBuilder<DiscogsRankedTrack>(
            tracks.Length);
        int currentDisc = 1;
        int trackWithinDisc = 0;
        for (int index = 0; index < tracks.Length; index++)
        {
            DiscogsTrackCandidate track = tracks[index];
            (int? disc, int? position) = ParsePosition(track.Position);
            if (disc is not null)
                currentDisc = disc.Value;
            trackWithinDisc = position ?? trackWithinDisc + 1;
            result.Add(new(
                track,
                index,
                currentDisc,
                trackWithinDisc,
                0,
                ""));
        }
        return result.MoveToImmutable();
    }

    private static (int? Disc, int? Track) ParsePosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        string trimmed = value.Trim();
        if (int.TryParse(trimmed, out int simple) && simple > 0)
            return (null, simple);
        int separator = trimmed.IndexOfAny(['-', '.', '/']);
        if (separator > 0 &&
            int.TryParse(trimmed.AsSpan(0, separator), out int disc) &&
            int.TryParse(trimmed.AsSpan(separator + 1), out int track) &&
            disc > 0 && track > 0)
            return (disc, track);
        int digits = 0;
        while (digits < trimmed.Length &&
               !char.IsDigit(trimmed[digits]))
            digits++;
        return digits < trimmed.Length &&
               int.TryParse(trimmed.AsSpan(digits), out int suffix) &&
               suffix > 0
            ? (null, suffix)
            : (null, null);
    }

    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string[] parts = value.Split(':');
        if (parts.Length is < 2 or > 3 ||
            parts.Any(part => !int.TryParse(part, out _)))
            return null;
        int[] numbers = parts.Select(int.Parse).ToArray();
        return numbers.Length == 2
            ? new TimeSpan(0, numbers[0], numbers[1])
            : new TimeSpan(numbers[0], numbers[1], numbers[2]);
    }

    private static void ResolveDuplicateSuggestions(
        List<DiscogsTrackMatch> matches)
    {
        foreach (IGrouping<int, DiscogsTrackMatch> duplicate in matches
                     .Where(match => match.SuggestedTrack is not null)
                     .GroupBy(match => match.SuggestedTrack!.SourceIndex)
                     .Where(group => group.Count() > 1))
        {
            int bestScore = duplicate.Max(
                match => match.Candidates[0].Score);
            DiscogsTrackMatch[] best = duplicate
                .Where(match => match.Candidates[0].Score == bestScore)
                .ToArray();
            foreach (DiscogsTrackMatch match in duplicate)
            {
                if (best.Length == 1 && ReferenceEquals(match, best[0]))
                    continue;
                int index = matches.IndexOf(match);
                matches[index] = match with
                {
                    SuggestedTrack = null,
                    Confidence = DiscogsMappingConfidence.Ambiguous,
                    Status =
                        "This Discogs track was also suggested for another file.",
                };
            }
        }
    }

    private static bool SameNormalized(string? left, string? right)
    {
        string normalizedLeft = Normalize(left);
        return normalizedLeft.Length > 0 &&
            StringComparer.Ordinal.Equals(
                normalizedLeft,
                Normalize(right));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var result = new StringBuilder(value.Length);
        foreach (char character in
                 value.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
        return result.ToString();
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
