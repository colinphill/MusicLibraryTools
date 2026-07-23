using System.Collections.Immutable;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MusicBrainzReleaseMappingServiceTests
{
    private static readonly Guid ReleaseId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReleaseGroupId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FirstRecordingId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");
    private static readonly Guid SecondRecordingId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");

    [Fact]
    public async Task ExactRecordingId_DominatesConflictingMetadataHints()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "First", 181000),
            Track(1, 2, SecondRecordingId, "Second", 202000));
        var source = new MusicBrainzSourceFile(
            "second.flac",
            [SecondRecordingId],
            Title: "First",
            DiscNumber: 1,
            TrackNumber: 1,
            Duration: TimeSpan.FromSeconds(181));
        var progress = new RecordingProgress();

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release, [source], progress);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Equal(SecondRecordingId, match.SuggestedTrack!.RecordingId);
        Assert.Equal(MusicBrainzMappingConfidence.RecordingId, match.Confidence);
        Assert.Equal(1, progress.Items[^1].Completed);
    }

    [Fact]
    public async Task EqualRecordingOccurrences_AreLeftForUserConfirmation()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "Song", 181000),
            Track(2, 1, FirstRecordingId, "Song", 181000));
        var source = new MusicBrainzSourceFile(
            "song.flac",
            [FirstRecordingId],
            Title: "Song",
            Duration: TimeSpan.FromSeconds(181));

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release, [source]);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Null(match.SuggestedTrack);
        Assert.Equal(MusicBrainzMappingConfidence.Ambiguous, match.Confidence);
        Assert.Equal(2, match.Candidates.Length);
    }

    [Fact]
    public async Task MetadataHints_CanSuggestTrackWithoutRecordingId()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "First", 181000),
            Track(1, 2, SecondRecordingId, "Second", 202000));
        var source = new MusicBrainzSourceFile(
            "second.flac",
            [],
            Title: "Second",
            Artist: "Example Artist",
            Duration: TimeSpan.FromSeconds(202));

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release, [source]);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Equal(SecondRecordingId, match.SuggestedTrack!.RecordingId);
        Assert.Equal(MusicBrainzMappingConfidence.Metadata, match.Confidence);
    }

    [Fact]
    public async Task AcoustIdConfidence_RanksMultipleRecordingCandidates()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "Song", 181000),
            Track(1, 1, SecondRecordingId, "Song", 181000));
        var source = new MusicBrainzSourceFile(
            "song.flac",
            [FirstRecordingId, SecondRecordingId],
            Title: "Song",
            Artist: "Example Artist",
            Duration: TimeSpan.FromSeconds(181),
            RecordingIdScores:
                ImmutableDictionary<Guid, double>.Empty
                    .Add(FirstRecordingId, 0.61)
                    .Add(SecondRecordingId, 0.97));

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release,
                [source]);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Equal(
            SecondRecordingId,
            match.SuggestedTrack!.RecordingId);
        Assert.Equal(
            MusicBrainzMappingConfidence.RecordingId,
            match.Confidence);
        Assert.Contains("97.0% AcoustID", match.Status);
        Assert.True(
            match.Candidates[0].Score >
            match.Candidates[1].Score);
    }

    [Fact]
    public async Task AlbumContext_StrengthensPlausibleMetadataSuggestion()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "First", 181000));
        var source = new MusicBrainzSourceFile(
            "first.flac",
            [],
            Title: "First",
            Album: "Example Album",
            AlbumArtist: "Example Artist");

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release,
                [source]);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Equal(
            FirstRecordingId,
            match.SuggestedTrack!.RecordingId);
        Assert.Equal(
            MusicBrainzMappingConfidence.Metadata,
            match.Confidence);
        Assert.Contains("album", match.Status);
        Assert.Contains("album artist", match.Status);
    }

    [Fact]
    public async Task AlbumContextAlone_DoesNotCreateTrackSuggestion()
    {
        MusicBrainzReleaseCandidate release = Release(
            Track(1, 1, FirstRecordingId, "First", 181000));
        var source = new MusicBrainzSourceFile(
            "unknown.flac",
            [],
            Album: "Example Album",
            AlbumArtist: "Example Artist");

        MusicBrainzReleaseMapping result =
            await new MusicBrainzReleaseMappingService().MapAsync(
                release,
                [source]);

        MusicBrainzTrackMatch match = Assert.Single(result.Files);
        Assert.Null(match.SuggestedTrack);
        Assert.Equal(
            MusicBrainzMappingConfidence.Unmatched,
            match.Confidence);
    }

    [Fact]
    public void ConfirmedMappings_CreateSelectiveMultiFieldEdits()
    {
        MusicBrainzTrackCandidate track =
            Track(1, 2, SecondRecordingId, "Second", 202000);
        MusicBrainzReleaseCandidate release = Release(track);
        var service = new MusicBrainzReleaseMappingService();

        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> result =
            service.CreateEdits(
                release,
                [new("second.flac", track)],
                new(
                    TrackTitles: true,
                    TrackArtists: false,
                    ReleaseIdentity: true,
                    Numbering: true,
                    ReleaseDetails: false,
                    MusicBrainzIdentifiers: true));

        IReadOnlyList<MetadataValueEdit> edits = Assert.Single(result).Value;
        Assert.Equal("Second", Value(edits, TagFields.Title));
        Assert.Null(Value(edits, TagFields.Artist));
        Assert.Equal("Example Album", Value(edits, TagFields.Album));
        Assert.Equal("2", Value(edits, TagFields.TrackNumber));
        Assert.Equal(SecondRecordingId.ToString("D"),
            Value(edits, TagFields.MusicBrainz_RecordingID));
        Assert.Equal(ReleaseId.ToString("D"),
            Value(edits, TagFields.MusicBrainz_AlbumID));
        Assert.Null(Value(edits, TagFields.Label));
    }

    [Fact]
    public async Task Mapping_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MusicBrainzReleaseMappingService().MapAsync(
                Release(Track(1, 1, FirstRecordingId, "First", 181000)),
                [new("first.flac", [FirstRecordingId])],
                ct: cancellation.Token));
    }

    private static string? Value(
        IReadOnlyList<MetadataValueEdit> edits,
        TagFields field) =>
        edits.SingleOrDefault(edit => edit.Field.KnownField == field)
            ?.Values.Single();

    private static MusicBrainzReleaseCandidate Release(
        params MusicBrainzTrackCandidate[] tracks) =>
        new(
            ReleaseId,
            "Example Album",
            "Example Artist",
            "2001-02-03",
            "US",
            "Official",
            "0123456789012",
            ReleaseGroupId,
            "Example Album",
            "Album",
            "Example Label",
            "CAT-001",
            ["CD"],
            [.. tracks]);

    private static MusicBrainzTrackCandidate Track(
        int disc,
        int position,
        Guid recordingId,
        string title,
        int duration) =>
        new(
            Guid.NewGuid(),
            disc,
            position,
            position.ToString(),
            title,
            duration,
            recordingId,
            title,
            "Example Artist");

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
