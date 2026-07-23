using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DiscogsReleaseMappingServiceTests
{
    private readonly DiscogsReleaseMappingService _service = new();

    [Fact]
    public async Task MapUsesNumberTitleArtistAndDurationHints()
    {
        DiscogsReleaseCandidate release = Release();
        DiscogsSourceFile[] files =
        [
            new(
                @"C:\music\one.flac",
                "First Song",
                "Example Artist",
                1,
                1,
                TimeSpan.FromSeconds(181)),
            new(
                @"C:\music\two.flac",
                "Second Song",
                "Example Artist",
                1,
                2,
                TimeSpan.FromSeconds(242)),
        ];
        var reports = new List<OperationProgress>();

        DiscogsReleaseMapping mapping = await _service.MapAsync(
            release,
            files,
            new SynchronousProgress<OperationProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, mapping.SuggestedCount);
        Assert.Equal(0, mapping.AmbiguousCount);
        Assert.Equal("1-1",
            mapping.Files[0].SuggestedTrack!.Track.Position);
        Assert.Equal("1-2",
            mapping.Files[1].SuggestedTrack!.Track.Position);
        Assert.Equal(OperationPhase.Completed, reports[^1].Phase);
    }

    [Fact]
    public async Task TiedSuggestionsRemainUnselected()
    {
        DiscogsReleaseCandidate release = Release() with
        {
            Tracks =
            [
                new("A1", "Same", "3:00", "Artist"),
                new("B1", "Same", "3:00", "Artist"),
            ],
        };

        DiscogsReleaseMapping mapping = await _service.MapAsync(
            release,
            [new("track.flac", "Same", "Artist")],
            ct: TestContext.Current.CancellationToken);

        DiscogsTrackMatch match = Assert.Single(mapping.Files);
        Assert.Null(match.SuggestedTrack);
        Assert.Equal(
            DiscogsMappingConfidence.Ambiguous,
            match.Confidence);
    }

    [Fact]
    public async Task DuplicateSuggestionIsNotAppliedToTwoFiles()
    {
        DiscogsReleaseCandidate release = Release() with
        {
            Tracks =
            [
                new("1", "Only Song", "3:00", "Artist"),
            ],
        };

        DiscogsReleaseMapping mapping = await _service.MapAsync(
            release,
            [
                new("one.flac", "Only Song", "Artist"),
                new("two.flac", "Only Song", "Artist"),
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, mapping.SuggestedCount);
        Assert.All(mapping.Files, match =>
            Assert.Equal(
                DiscogsMappingConfidence.Ambiguous,
                match.Confidence));
    }

    [Fact]
    public async Task CreateEditsHonorsSelectiveImportOptions()
    {
        DiscogsReleaseCandidate release = Release();
        DiscogsReleaseMapping mapping = await _service.MapAsync(
            release,
            [
                new(
                    "one.flac",
                    "First Song",
                    "Example Artist",
                    1,
                    1),
            ],
            ct: TestContext.Current.CancellationToken);
        DiscogsRankedTrack track =
            mapping.Files[0].SuggestedTrack!;

        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> edits =
            _service.CreateEdits(
                release,
                [new("one.flac", track)],
                new(
                    TrackTitles: true,
                    TrackArtists: false,
                    ReleaseIdentity: true,
                    Numbering: true,
                    ReleaseDetails: true,
                    GenresAndStyles: true,
                    DiscogsIdentifier: true));

        IReadOnlyList<MetadataValueEdit> file = Assert.Single(edits).Value;
        AssertEdit(file, TagFields.Title, "First Song");
        Assert.DoesNotContain(file, edit =>
            edit.Field.KnownField == TagFields.Artist);
        AssertEdit(file, TagFields.Album, "Example Album");
        AssertEdit(file, TagFields.TrackNumber, "1");
        AssertEdit(file, TagFields.TotalTracks, "2");
        AssertEdit(file, TagFields.DiscNumber, "1");
        AssertEdit(file, TagFields.TotalDiscs, "1");
        AssertEdit(file, TagFields.Barcode, "0123456789012");
        AssertEdit(file, TagFields.CatalogNumber, "CAT-1");
        Assert.Equal(
            ["Electronic", "Downtempo"],
            file.Single(edit =>
                edit.Field.KnownField == TagFields.Genre).Values);
        MetadataValueEdit discogs = file.Single(edit =>
            !edit.Field.IsKnown &&
            edit.Field.CustomName == "DISCOGS_RELEASE_ID");
        Assert.Equal(["4242"], discogs.Values);
    }

    [Fact]
    public async Task MapObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.MapAsync(
                Release(),
                [new("one.flac")],
                ct: cancellation.Token));
    }

    private static void AssertEdit(
        IReadOnlyList<MetadataValueEdit> edits,
        TagFields field,
        string value) =>
        Assert.Equal(
            [value],
            edits.Single(edit =>
                edit.Field.KnownField == field).Values);

    private static DiscogsReleaseCandidate Release() =>
        new(
            4242,
            4000,
            "Example Album",
            "Example Artist",
            2001,
            "2001-02-03",
            "US",
            ["Example Label"],
            ["CAT-1"],
            ["1 CD (Album)"],
            ["Electronic"],
            ["Downtempo"],
            ["0123456789012"],
            new Uri("https://www.discogs.com/release/4242"),
            null,
            null,
            [
                new("1-1", "First Song", "3:01", "Example Artist"),
                new("1-2", "Second Song", "4:02", "Example Artist"),
            ]);
}
