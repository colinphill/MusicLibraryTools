using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record AudioDiscoveryRow(
    string Path,
    double? DurationSeconds,
    string? Fingerprint,
    Guid? AcoustId,
    double? Score,
    ImmutableArray<Guid> MusicBrainzRecordingIdValues,
    string Status)
{
    public string File => System.IO.Path.GetFileName(Path);
    public string Duration => DurationSeconds is null
        ? ""
        : TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"h\:mm\:ss");
    public string Confidence => Score is null ? "" : $"{Score:P1}";
    public string MusicBrainzRecordingIds =>
        string.Join(", ", MusicBrainzRecordingIdValues);
}

public static class AudioDiscoveryRows
{
    public static IEnumerable<AudioDiscoveryRow> Create(
        AcoustIdDiscoveryResult result)
    {
        foreach (AcoustIdFileDiscovery file in result.Files)
        {
            AcoustIdCandidate[] candidates =
                file.Lookup?.Candidates.ToArray() ?? [];
            if (candidates.Length == 0)
            {
                yield return new(
                    file.Path,
                    file.Fingerprint?.Duration.TotalSeconds,
                    file.Fingerprint?.Fingerprint,
                    null,
                    null,
                    [],
                    file.Issues.FirstOrDefault()?.Message ?? "No AcoustID match");
                continue;
            }
            foreach (AcoustIdCandidate candidate in candidates)
                yield return new(
                    file.Path,
                    file.Fingerprint?.Duration.TotalSeconds,
                    file.Fingerprint?.Fingerprint,
                    candidate.AcoustId,
                    candidate.Score,
                    candidate.MusicBrainzRecordingIds,
                    candidate.MusicBrainzRecordingIds.Length == 0
                        ? "Candidate has no MusicBrainz recording ID"
                        : "Candidate");
        }
    }

    public static OperationRecipe CreateTagRecipe(AudioDiscoveryRow row)
    {
        if (row.AcoustId is null || string.IsNullOrWhiteSpace(row.Fingerprint))
            throw new InvalidOperationException("Select a matched AcoustID candidate.");
        var operations = new List<MetadataOperation>
        {
            new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.AcoustID_Fingerprint),
                row.Fingerprint),
            new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.AcoustID_ID),
                row.AcoustId.Value.ToString()),
        };
        if (row.MusicBrainzRecordingIdValues.Length == 1)
            operations.Add(new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.MusicBrainz_RecordingID),
                row.MusicBrainzRecordingIdValues[0].ToString()));
        return OperationRecipe.Create(
            $"Audio identifiers: {row.File}", [.. operations]);
    }
}

public sealed record MusicBrainzReleaseRow(
    string SourcePath,
    Guid? RecordingId,
    Guid ReleaseId,
    string Title,
    string Artist,
    string? Date,
    string? Country,
    string? Status,
    string? Label,
    string? CatalogNumber,
    string Formats,
    string MatchedTrackPositions,
    int TrackCount,
    MusicBrainzReleaseCandidate Candidate)
{
    public string File => System.IO.Path.GetFileName(SourcePath);
}

public static class MusicBrainzReleaseRows
{
    public static IEnumerable<MusicBrainzReleaseRow> Create(
        string sourcePath,
        MusicBrainzReleaseResult result)
        => Create(sourcePath, result.Releases, result.RecordingId);

    public static IEnumerable<MusicBrainzReleaseRow> CreateSearch(
        string sourcePath,
        MusicBrainzReleaseSearchResult result)
        => Create(sourcePath, result.Releases, recordingId: null);

    public static MusicBrainzReleaseRow CreateDetailed(
        string sourcePath,
        MusicBrainzReleaseCandidate release,
        Guid? recordingId = null) =>
        Create(sourcePath, [release], recordingId).Single();

    private static IEnumerable<MusicBrainzReleaseRow> Create(
        string sourcePath,
        IEnumerable<MusicBrainzReleaseCandidate> releases,
        Guid? recordingId)
    {
        foreach (MusicBrainzReleaseCandidate release in releases)
        {
            MusicBrainzTrackCandidate[] matches = release.Tracks
                .Where(track => recordingId is not null &&
                    track.RecordingId == recordingId.Value)
                .ToArray();
            yield return new(
                sourcePath,
                recordingId,
                release.ReleaseId,
                release.Title,
                release.ArtistCredit,
                release.Date,
                release.Country,
                release.Status,
                release.Label,
                release.CatalogNumber,
                string.Join(", ", release.Formats),
                string.Join(", ", matches.Select(track =>
                    $"{track.MediumPosition}-{track.Number}")),
                release.Tracks.Length,
                release);
        }
    }
}

public partial class MusicBrainzReleaseSearchViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    private string? _artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    private string? _album;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    private string? _barcode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    private string? _catalogNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    private string? _releaseId;

    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(Album) ||
        !string.IsNullOrWhiteSpace(Barcode) ||
        !string.IsNullOrWhiteSpace(CatalogNumber) ||
        !string.IsNullOrWhiteSpace(ReleaseId);

    public MusicBrainzReleaseSearchQuery CreateQuery()
    {
        Guid? releaseId = null;
        if (!string.IsNullOrWhiteSpace(ReleaseId))
        {
            if (!Guid.TryParse(ReleaseId.Trim(), out Guid parsed))
                throw new InvalidOperationException(
                    "The MusicBrainz release ID is not a valid GUID.");
            releaseId = parsed;
        }
        return new(
            Artist?.Trim(),
            Album?.Trim(),
            Barcode?.Trim(),
            CatalogNumber?.Trim(),
            releaseId);
    }
}

public sealed record MusicBrainzTrackChoice(
    MusicBrainzTrackCandidate Track,
    int Score,
    string Reason)
{
    public string Position => $"{Track.MediumPosition}-{Track.Number}";
    public string Display =>
        $"{Position}  {Track.Title} — {Track.ArtistCredit}" +
        (Score > 0 ? $"  [{Score}]" : "");
}

public partial class MusicBrainzTrackMappingRow : ObservableObject
{
    public MusicBrainzTrackMappingRow(MusicBrainzTrackMatch match)
    {
        Path = match.Source.Path;
        Confidence = match.Confidence.ToString();
        Status = match.Status;
        TrackChoices = match.Candidates
            .Select(candidate => new MusicBrainzTrackChoice(
                candidate.Track, candidate.Score, candidate.Reason))
            .ToArray();
        _selectedTrack = match.SuggestedTrack is null
            ? null
            : TrackChoices.FirstOrDefault(choice =>
                choice.Track.TrackId == match.SuggestedTrack.TrackId);
        _isIncluded = _selectedTrack is not null;
    }

    public string Path { get; }
    public string File => System.IO.Path.GetFileName(Path);
    public string Confidence { get; }
    public string Status { get; }
    public IReadOnlyList<MusicBrainzTrackChoice> TrackChoices { get; }
    public string Position => SelectedTrack?.Position ?? "";
    public string TrackTitle => SelectedTrack?.Track.Title ?? "";
    public string TrackArtist => SelectedTrack?.Track.ArtistCredit ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Position))]
    [NotifyPropertyChangedFor(nameof(TrackTitle))]
    [NotifyPropertyChangedFor(nameof(TrackArtist))]
    private MusicBrainzTrackChoice? _selectedTrack;

    [ObservableProperty]
    private bool _isIncluded;

    partial void OnSelectedTrackChanged(MusicBrainzTrackChoice? value)
    {
        if (value is not null)
            IsIncluded = true;
    }
}

public partial class MusicBrainzImportSelectionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _trackTitles = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _trackArtists = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _releaseIdentity = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _numbering = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _releaseDetails = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _musicBrainzIdentifiers = true;

    public bool HasSelection =>
        TrackTitles || TrackArtists || ReleaseIdentity || Numbering ||
        ReleaseDetails || MusicBrainzIdentifiers;

    public MusicBrainzImportOptions CreateOptions() => new(
        TrackTitles,
        TrackArtists,
        ReleaseIdentity,
        Numbering,
        ReleaseDetails,
        MusicBrainzIdentifiers);
}
