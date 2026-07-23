using System.Collections.Immutable;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record MusicBrainzSourceFile(
    string Path,
    ImmutableArray<Guid> RecordingIds,
    string? Title = null,
    string? Artist = null,
    int? DiscNumber = null,
    int? TrackNumber = null,
    TimeSpan? Duration = null,
    ImmutableDictionary<Guid, double>? RecordingIdScores = null,
    string? Album = null,
    string? AlbumArtist = null);

public sealed record MusicBrainzRankedTrack(
    MusicBrainzTrackCandidate Track,
    int Score,
    string Reason);

public enum MusicBrainzMappingConfidence
{
    Unmatched,
    Ambiguous,
    Metadata,
    RecordingId,
}

public sealed record MusicBrainzTrackMatch(
    MusicBrainzSourceFile Source,
    MusicBrainzTrackCandidate? SuggestedTrack,
    ImmutableArray<MusicBrainzRankedTrack> Candidates,
    MusicBrainzMappingConfidence Confidence,
    string Status);

public sealed record MusicBrainzReleaseMapping(
    MusicBrainzReleaseCandidate Release,
    ImmutableArray<MusicBrainzTrackMatch> Files)
{
    public int SuggestedCount => Files.Count(file => file.SuggestedTrack is not null);
    public int AmbiguousCount =>
        Files.Count(file => file.Confidence == MusicBrainzMappingConfidence.Ambiguous);
}

public sealed record MusicBrainzImportOptions(
    bool TrackTitles = true,
    bool TrackArtists = true,
    bool ReleaseIdentity = true,
    bool Numbering = true,
    bool ReleaseDetails = true,
    bool MusicBrainzIdentifiers = true);

public sealed record MusicBrainzConfirmedTrack(
    string Path,
    MusicBrainzTrackCandidate Track);

public interface IMusicBrainzReleaseMappingService
{
    Task<MusicBrainzReleaseMapping> MapAsync(
        MusicBrainzReleaseCandidate release,
        IReadOnlyList<MusicBrainzSourceFile> files,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> CreateEdits(
        MusicBrainzReleaseCandidate release,
        IReadOnlyList<MusicBrainzConfirmedTrack> mappings,
        MusicBrainzImportOptions options);
}

/// <summary>
/// Suggests file-to-track mappings without silently committing an ambiguous match. Exact
/// recording IDs dominate metadata hints; users may still replace or exclude every suggestion
/// before a normal metadata preview is generated.
/// </summary>
public sealed class MusicBrainzReleaseMappingService
    : IMusicBrainzReleaseMappingService
{
    public Task<MusicBrainzReleaseMapping> MapAsync(
        MusicBrainzReleaseCandidate release,
        IReadOnlyList<MusicBrainzSourceFile> files,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => Map(release, files, progress, ct), ct);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> CreateEdits(
        MusicBrainzReleaseCandidate release,
        IReadOnlyList<MusicBrainzConfirmedTrack> mappings,
        MusicBrainzImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(options);
        var result = new Dictionary<string, IReadOnlyList<MetadataValueEdit>>(
            PathComparer);
        foreach (MusicBrainzConfirmedTrack mapping in mappings)
        {
            var edits = new Dictionary<MetadataFieldKey, MetadataValueEdit>();
            void Add(TagFields field, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    MetadataFieldKey key = MetadataFieldKey.Known(field);
                    edits[key] = new(key, [value.Trim()]);
                }
            }

            MusicBrainzTrackCandidate track = mapping.Track;
            if (options.TrackTitles)
                Add(TagFields.Title,
                    string.IsNullOrWhiteSpace(track.Title)
                        ? track.RecordingTitle
                        : track.Title);
            if (options.TrackArtists)
                Add(TagFields.Artist, track.ArtistCredit);
            if (options.ReleaseIdentity)
            {
                Add(TagFields.Album, release.Title);
                Add(TagFields.AlbumArtist, release.ArtistCredit);
                Add(TagFields.Date, release.Date);
            }
            if (options.Numbering)
            {
                Add(TagFields.TrackNumber,
                    Positive(track.TrackPosition)?.ToString());
                int trackTotal = release.Tracks.Count(candidate =>
                    candidate.MediumPosition == track.MediumPosition);
                Add(TagFields.TotalTracks, Positive(trackTotal)?.ToString());
                Add(TagFields.DiscNumber,
                    Positive(track.MediumPosition)?.ToString());
                int discTotal = release.Tracks
                    .Select(candidate => candidate.MediumPosition)
                    .Where(position => position > 0)
                    .Distinct()
                    .Count();
                Add(TagFields.TotalDiscs, Positive(discTotal)?.ToString());
            }
            if (options.ReleaseDetails)
            {
                Add(TagFields.Barcode, release.Barcode);
                Add(TagFields.CatalogNumber, release.CatalogNumber);
                Add(TagFields.Label, release.Label);
                Add(TagFields.ReleaseCountry, release.Country);
                Add(TagFields.ReleaseStatus, release.Status);
                Add(TagFields.ReleaseType, release.PrimaryType);
                Add(TagFields.Media,
                    release.Formats.Length == 1
                        ? release.Formats[0]
                        : null);
            }
            if (options.MusicBrainzIdentifiers)
            {
                Add(TagFields.MusicBrainz_RecordingID,
                    track.RecordingId.ToString("D"));
                Add(TagFields.MusicBrainz_TrackID,
                    track.TrackId.ToString("D"));
                Add(TagFields.MusicBrainz_AlbumID,
                    release.ReleaseId.ToString("D"));
                Add(TagFields.MusicBrainz_ReleaseGroupID,
                    release.ReleaseGroupId?.ToString("D"));
            }
            if (edits.Count > 0)
                result[Path.GetFullPath(mapping.Path)] = edits.Values.ToArray();
        }
        return result;
    }

    private static MusicBrainzReleaseMapping Map(
        MusicBrainzReleaseCandidate release,
        IReadOnlyList<MusicBrainzSourceFile> files,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var matches = new List<MusicBrainzTrackMatch>(files.Count);
        for (int index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            MusicBrainzSourceFile source = files[index];
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                files.Count,
                source.Path,
                $"Matching file {index + 1:N0} of {files.Count:N0}"));
            matches.Add(Match(source, release));
        }
        ResolveDuplicateSuggestions(matches);
        progress?.Report(new(
            OperationPhase.Completed,
            files.Count,
            files.Count,
            Message: $"Suggested {matches.Count(match => match.SuggestedTrack is not null):N0} " +
                $"of {files.Count:N0} track mapping(s)"));
        return new(release, [.. matches]);
    }

    private static MusicBrainzTrackMatch Match(
        MusicBrainzSourceFile source,
        MusicBrainzReleaseCandidate release)
    {
        MusicBrainzRankedTrack[] ranked = release.Tracks
            .Select(track => Score(source, release, track))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Track.MediumPosition)
            .ThenBy(candidate => candidate.Track.TrackPosition)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < 130)
            return new(source, null, [.. ranked],
                MusicBrainzMappingConfidence.Unmatched,
                "No reliable release-track match was found.");

        MusicBrainzRankedTrack best = ranked[0];
        if (ranked.Length > 1 && ranked[1].Score == best.Score)
            return new(source, null, [.. ranked],
                MusicBrainzMappingConfidence.Ambiguous,
                $"Two or more release tracks share the best score ({best.Score}).");

        bool exactRecording = source.RecordingIds.Contains(best.Track.RecordingId);
        return new(
            source,
            best.Track,
            [.. ranked],
            exactRecording
                ? MusicBrainzMappingConfidence.RecordingId
                : MusicBrainzMappingConfidence.Metadata,
            exactRecording
                ? $"Exact MusicBrainz recording ID; {best.Reason}."
                : $"Metadata suggestion; {best.Reason}.");
    }

    private static MusicBrainzRankedTrack Score(
        MusicBrainzSourceFile source,
        MusicBrainzReleaseCandidate release,
        MusicBrainzTrackCandidate track)
    {
        int score = 0;
        var reasons = new List<string>();
        if (source.RecordingIds.Contains(track.RecordingId))
        {
            if (source.RecordingIdScores?.TryGetValue(
                    track.RecordingId,
                    out double confidence) == true)
            {
                double bounded = Math.Clamp(confidence, 0, 1);
                score += 900 +
                    (int)Math.Round(
                        bounded * 100,
                        MidpointRounding.AwayFromZero);
                reasons.Add(
                    $"recording ID ({bounded:P1} AcoustID)");
            }
            else
            {
                score += 1000;
                reasons.Add("recording ID");
            }
        }
        if (source.DiscNumber is > 0 &&
            source.DiscNumber == track.MediumPosition)
        {
            score += 100;
            reasons.Add("disc");
        }
        if (source.TrackNumber is > 0 &&
            source.TrackNumber == track.TrackPosition)
        {
            score += 100;
            reasons.Add("track");
        }
        if (SameNormalized(source.Title, track.Title) ||
            SameNormalized(source.Title, track.RecordingTitle))
        {
            score += 80;
            reasons.Add("title");
        }
        if (SameNormalized(source.Artist, track.ArtistCredit))
        {
            score += 30;
            reasons.Add("artist");
        }
        if (SameNormalized(source.Album, release.Title))
        {
            score += 40;
            reasons.Add("album");
        }
        if (SameNormalized(
                source.AlbumArtist,
                release.ArtistCredit))
        {
            score += 25;
            reasons.Add("album artist");
        }
        if (source.Duration is not null && track.LengthMilliseconds is > 0)
        {
            double delta = Math.Abs(
                source.Duration.Value.TotalMilliseconds -
                track.LengthMilliseconds.Value);
            if (delta <= 2000)
            {
                score += 50;
                reasons.Add("duration");
            }
            else if (delta <= 5000)
            {
                score += 20;
                reasons.Add("near duration");
            }
        }
        return new(track, score,
            reasons.Count == 0 ? "no matching hints" : string.Join(", ", reasons));
    }

    private static void ResolveDuplicateSuggestions(
        List<MusicBrainzTrackMatch> matches)
    {
        foreach (IGrouping<Guid, MusicBrainzTrackMatch> duplicate in matches
                     .Where(match => match.SuggestedTrack is not null)
                     .GroupBy(match => match.SuggestedTrack!.TrackId)
                     .Where(group => group.Count() > 1))
        {
            int bestScore = duplicate.Max(match => match.Candidates[0].Score);
            MusicBrainzTrackMatch[] best = duplicate
                .Where(match => match.Candidates[0].Score == bestScore)
                .ToArray();
            foreach (MusicBrainzTrackMatch match in duplicate)
            {
                if (best.Length == 1 && ReferenceEquals(match, best[0]))
                    continue;
                int index = matches.IndexOf(match);
                matches[index] = match with
                {
                    SuggestedTrack = null,
                    Confidence = MusicBrainzMappingConfidence.Ambiguous,
                    Status = "This release track was also suggested for another file.",
                };
            }
        }
    }

    private static bool SameNormalized(string? left, string? right)
    {
        string normalizedLeft = Normalize(left);
        return normalizedLeft.Length > 0 &&
            StringComparer.Ordinal.Equals(normalizedLeft, Normalize(right));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var result = new StringBuilder(value.Length);
        foreach (char character in value.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
        return result.ToString();
    }

    private static int? Positive(int value) => value > 0 ? value : null;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
