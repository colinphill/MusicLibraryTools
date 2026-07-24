using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed partial class AudioDiscoveryRow : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private readonly string _statusResourceKey;

    internal AudioDiscoveryRow(
        string path,
        double? durationSeconds,
        string? fingerprint,
        Guid? acoustId,
        double? score,
        ImmutableArray<Guid> musicBrainzRecordingIdValues,
        string statusResourceKey,
        string? diagnosticDetail,
        ILocalizationService? localization)
    {
        Path = path;
        DurationSeconds = durationSeconds;
        Fingerprint = fingerprint;
        AcoustId = acoustId;
        Score = score;
        MusicBrainzRecordingIdValues =
            musicBrainzRecordingIdValues;
        _statusResourceKey = statusResourceKey;
        DiagnosticDetail = diagnosticDetail;
        _localization = localization;
    }

    public string Path { get; }
    public double? DurationSeconds { get; }
    public string? Fingerprint { get; }
    public Guid? AcoustId { get; }
    public double? Score { get; }
    public ImmutableArray<Guid>
        MusicBrainzRecordingIdValues { get; }
    public string Status => Text(
        _localization,
        _statusResourceKey);
    public string? DiagnosticDetail { get; }
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(DiagnosticDetail);
    public string File => System.IO.Path.GetFileName(Path);
    public string Duration => DurationSeconds is null
        ? ""
        : TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"h\:mm\:ss");
    public string Confidence => Score is null ? "" : $"{Score:P1}";
    public string MusicBrainzRecordingIds =>
        string.Join(", ", MusicBrainzRecordingIdValues);

    public void RefreshLocalizedText() =>
        OnPropertyChanged(nameof(Status));

    private static string Text(
        ILocalizationService? localization,
        string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);
}

public static class AudioDiscoveryRows
{
    public static IEnumerable<AudioDiscoveryRow> Create(
        AcoustIdDiscoveryResult result,
        ILocalizationService? localization = null)
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
                    "OnlineMetadata.AudioDiscovery.Status.NoMatch",
                    file.Issues.FirstOrDefault()?.Message,
                    localization);
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
                        ? "OnlineMetadata.AudioDiscovery.Status.CandidateWithoutRecordingId"
                        : file.Lookup?.OfflineFallback == true
                            ? "OnlineMetadata.AudioDiscovery.Status.OfflineCachedCandidate"
                            : file.Lookup?.FromCache == true
                                ? "OnlineMetadata.AudioDiscovery.Status.CachedCandidate"
                                : "OnlineMetadata.AudioDiscovery.Status.Candidate",
                    diagnosticDetail: null,
                    localization: localization);
        }
    }

    public static OperationRecipe CreateTagRecipe(
        AudioDiscoveryRow row,
        ILocalizationService? localization = null)
    {
        if (row.AcoustId is null || string.IsNullOrWhiteSpace(row.Fingerprint))
            throw new InvalidOperationException(
                Text(
                    localization,
                    "OnlineMetadata.AudioDiscovery.Validation.SelectMatchedCandidate"));
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
            Format(
                localization,
                "OnlineMetadata.AudioDiscovery.RecipeName",
                row.File),
            [.. operations]);
    }

    private static string Text(
        ILocalizationService? localization,
        string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private static string Format(
        ILocalizationService? localization,
        string key,
        params object?[] arguments) =>
        localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);
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
    private readonly ILocalizationService? _localization;

    public MusicBrainzReleaseSearchViewModel(
        ILocalizationService? localization = null) =>
        _localization = localization;

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
                    _localization?.Get(
                        "OnlineMetadata.MusicBrainz.Validation.InvalidReleaseId") ??
                    LocalizedText.Get(
                        "OnlineMetadata.MusicBrainz.Validation.InvalidReleaseId"));
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

public sealed record DiscogsReleaseRow(
    long ReleaseId,
    string Title,
    string Artist,
    string Year,
    string Country,
    string Labels,
    string CatalogNumbers,
    string Formats,
    string Genres,
    string Styles,
    int TrackCount,
    string Source,
    DiscogsReleaseCandidate Candidate)
{
    public static DiscogsReleaseRow Create(
        DiscogsReleaseCandidate release,
        string source) =>
        new(
            release.ReleaseId,
            release.Title,
            release.ArtistCredit,
            release.Year?.ToString() ?? "",
            release.Country ?? "",
            string.Join("; ", release.Labels),
            string.Join("; ", release.CatalogNumbers),
            string.Join("; ", release.Formats),
            string.Join("; ", release.Genres),
            string.Join("; ", release.Styles),
            release.Tracks.Length,
            source,
            release);
}

public partial class DiscogsReleaseSearchViewModel : ObservableObject
{
    private readonly ILocalizationService? _localization;

    public DiscogsReleaseSearchViewModel(
        ILocalizationService? localization = null) =>
        _localization = localization;

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

    public DiscogsReleaseSearchQuery CreateQuery()
    {
        long? releaseId = null;
        if (!string.IsNullOrWhiteSpace(ReleaseId))
        {
            if (!long.TryParse(ReleaseId, out long parsed) || parsed <= 0)
                throw new InvalidOperationException(
                    _localization?.Get(
                        "OnlineMetadata.Discogs.Validation.InvalidReleaseId") ??
                    LocalizedText.Get(
                        "OnlineMetadata.Discogs.Validation.InvalidReleaseId"));
            releaseId = parsed;
        }
        return new(
            EmptyToNull(Artist),
            EmptyToNull(Album),
            EmptyToNull(Barcode),
            EmptyToNull(CatalogNumber),
            releaseId);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record DiscogsTrackChoice(
    DiscogsRankedTrack Track,
    string Display)
{
    public string Position =>
        $"{Track.DiscNumber}-{Track.TrackNumber}";
}

public partial class DiscogsTrackMappingRow : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private readonly DiscogsMappingConfidence _confidence;

    public DiscogsTrackMappingRow(
        DiscogsTrackMatch match,
        ILocalizationService? localization = null)
    {
        _localization = localization;
        _confidence = match.Confidence;
        Path = match.Source.Path;
        File = System.IO.Path.GetFileName(match.Source.Path);
        DiagnosticDetail = match.Status;
        TrackChoices = match.Candidates
            .Select(candidate => new DiscogsTrackChoice(
                candidate,
                $"{candidate.DiscNumber}-{candidate.TrackNumber} — " +
                $"{candidate.Track.Title} — {candidate.Track.ArtistCredit} " +
                $"({candidate.Score})"))
            .ToArray();
        _selectedTrack = match.SuggestedTrack is null
            ? null
            : TrackChoices.FirstOrDefault(choice =>
                choice.Track.SourceIndex ==
                match.SuggestedTrack.SourceIndex);
        _isIncluded = _selectedTrack is not null &&
            match.Confidence != DiscogsMappingConfidence.Ambiguous;
    }

    public string Path { get; }
    public string File { get; }
    public string Confidence => Text(
        $"OnlineMetadata.Mapping.Confidence.{_confidence}");
    public string Status => Text(
        $"OnlineMetadata.Discogs.MappingStatus.{_confidence}");
    public string DiagnosticDetail { get; }
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(DiagnosticDetail);
    public IReadOnlyList<DiscogsTrackChoice> TrackChoices { get; }
    public string Position => SelectedTrack?.Position ?? "";

    [ObservableProperty]
    private bool _isIncluded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Position))]
    private DiscogsTrackChoice? _selectedTrack;

    partial void OnSelectedTrackChanged(DiscogsTrackChoice? value)
    {
        if (value is not null)
            IsIncluded = true;
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(Status));
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
}

public partial class DiscogsImportSelectionViewModel : ObservableObject
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
    private bool _genresAndStyles = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _discogsIdentifier = true;

    public bool HasSelection =>
        TrackTitles ||
        TrackArtists ||
        ReleaseIdentity ||
        Numbering ||
        ReleaseDetails ||
        GenresAndStyles ||
        DiscogsIdentifier;

    public DiscogsImportOptions CreateOptions() => new(
        TrackTitles,
        TrackArtists,
        ReleaseIdentity,
        Numbering,
        ReleaseDetails,
        GenresAndStyles,
        DiscogsIdentifier);
}

public partial class CoverArtCandidateRow : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private string? _thumbnailStatusResourceKey;
    private object?[] _thumbnailStatusArguments = [];
    private long? _thumbnailStatusCount;

    public CoverArtCandidateRow(
        CoverArtArchiveCandidate candidate,
        ILocalizationService? localization = null)
    {
        Candidate = candidate;
        _localization = localization;
    }

    public CoverArtArchiveCandidate Candidate { get; }
    public string Id => Candidate.Id;
    public string Roles => Candidate.Types.Length == 0
        ? Text(
            "OnlineMetadata.CoverArt.Role.OtherRole")
        : string.Join(", ", Candidate.Types);
    public string Front => Candidate.IsFront
        ? Text("Common.Yes")
        : "";
    public string Back => Candidate.IsBack
        ? Text("Common.Yes")
        : "";
    public string Approved => Candidate.Approved
        ? Text("Common.Yes")
        : Text("Common.No");
    public string Comment => Candidate.Comment ?? "";
    public string ImageUrl => Candidate.ImageUri.AbsoluteUri;

    [ObservableProperty]
    private object? _thumbnailSource;

    [ObservableProperty]
    private string? _thumbnailStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnailDiagnosticDetail))]
    private string? _thumbnailDiagnosticDetail;

    public bool HasThumbnailDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            ThumbnailDiagnosticDetail);

    public void SetThumbnailStatus(
        string resourceKey,
        params object?[] arguments)
    {
        _thumbnailStatusResourceKey =
            resourceKey;
        _thumbnailStatusArguments =
            arguments;
        _thumbnailStatusCount = null;
        ThumbnailStatus =
            Format(resourceKey, arguments);
    }

    public void SetCountStatus(
        string resourceKey,
        long count,
        params object?[] arguments)
    {
        _thumbnailStatusResourceKey =
            resourceKey;
        _thumbnailStatusArguments =
            arguments;
        _thumbnailStatusCount = count;
        ThumbnailStatus =
            FormatCount(
                resourceKey,
                count,
                arguments);
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Roles));
        OnPropertyChanged(nameof(Front));
        OnPropertyChanged(nameof(Back));
        OnPropertyChanged(nameof(Approved));
        if (_thumbnailStatusResourceKey is not null)
            ThumbnailStatus =
                _thumbnailStatusCount is { } count
                    ? FormatCount(
                        _thumbnailStatusResourceKey,
                        count,
                        _thumbnailStatusArguments)
                    : Format(
                        _thumbnailStatusResourceKey,
                        _thumbnailStatusArguments);
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        arguments.Length == 0
            ? Text(key)
            : _localization?.Format(
                  key,
                  arguments) ??
              LocalizedText.Format(
                  key,
                  arguments);

    private string FormatCount(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);
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
    private readonly ILocalizationService? _localization;
    private readonly MusicBrainzMappingConfidence _confidence;

    public MusicBrainzTrackMappingRow(
        MusicBrainzTrackMatch match,
        ILocalizationService? localization = null)
    {
        _localization = localization;
        _confidence = match.Confidence;
        Path = match.Source.Path;
        DiagnosticDetail = match.Status;
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
    public string Confidence => Text(
        $"OnlineMetadata.Mapping.Confidence.{_confidence}");
    public string Status => Text(
        $"OnlineMetadata.MusicBrainz.MappingStatus.{_confidence}");
    public string DiagnosticDetail { get; }
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(DiagnosticDetail);
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

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(Status));
    }

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
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
