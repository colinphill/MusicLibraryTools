using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class AnalysisRunActiveRepairCountTests
{
    private const string FirstPath =
        @"C:\Music\Artist\Album\01 First.flac";
    private const string SecondPath =
        @"C:\Music\Artist\Album\02 Second.flac";

    [Fact]
    public void Metadata_repair_count_tracks_active_dispositions()
    {
        AnalysisRepairItemViewModel first =
            MetadataItem(
                FirstPath);
        AnalysisRepairItemViewModel second =
            MetadataItem(
                SecondPath);
        first.Disposition =
            AnalysisRepairDisposition.Active;
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForRepairs(
                new AnalysisRepairPlan(
                    "Metadata repairs",
                    [first.Repair, second.Repair]),
                [first, second],
                Records(),
                "Two metadata repairs");

        AssertCountTransitions(
            run,
            nameof(
                AnalysisRunViewModel
                    .ActiveRepairCount),
            () => run.ActiveRepairCount,
            disposition =>
                second.Disposition =
                    disposition,
            disposition =>
                first.Disposition =
                    disposition);
    }

    [Fact]
    public void Representation_repair_count_tracks_active_dispositions()
    {
        AnalysisRunViewModel run =
            AnalysisRunViewModel
                .ForRepresentationRepairs(
                    [
                        RepresentationAction(
                            FirstPath,
                            "01 First.flac"),
                        RepresentationAction(
                            SecondPath,
                            "02 Second.flac"),
                    ],
                    [],
                    Records(),
                    "Two file repairs");
        RepresentationRepairActionItemViewModel
            first =
                run.RepresentationActionItems[0];
        RepresentationRepairActionItemViewModel
            second =
                run.RepresentationActionItems[1];
        first.Disposition =
            AnalysisRepairDisposition.Active;

        AssertCountTransitions(
            run,
            nameof(
                AnalysisRunViewModel
                    .ActiveRepresentationRepairCount),
            () =>
                run.ActiveRepresentationRepairCount,
            disposition =>
                second.Disposition =
                    disposition,
            disposition =>
                first.Disposition =
                    disposition);
    }

    [Fact]
    public void Itunes_repair_count_tracks_active_dispositions()
    {
        ItlMetadataRepairItem firstRepair =
            ItunesRepair(
                FirstPath);
        ItlMetadataRepairItem secondRepair =
            ItunesRepair(
                SecondPath);
        var first =
            new ItlMetadataRepairItemViewModel(
                firstRepair)
            {
                Disposition =
                    AnalysisRepairDisposition
                        .Active,
            };
        var second =
            new ItlMetadataRepairItemViewModel(
                secondRepair);
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForItlRepairs(
                new ItlMetadataRepairPlan(
                    "Library.itl",
                    "HASH",
                    DateTimeOffset.UnixEpoch,
                    [
                        firstRepair,
                        secondRepair,
                    ]),
                [first, second],
                "Two iTunes repairs");

        AssertCountTransitions(
            run,
            nameof(
                AnalysisRunViewModel
                    .ActiveItlRepairCount),
            () =>
                run.ActiveItlRepairCount,
            disposition =>
                second.Disposition =
                    disposition,
            disposition =>
                first.Disposition =
                    disposition);
    }

    [Fact]
    public void Artwork_repair_count_tracks_active_dispositions()
    {
        var library =
            new FakeLibrary([]);
        ArtworkRepairItemViewModel first =
            ArtworkItem(
                FirstPath,
                library);
        ArtworkRepairItemViewModel second =
            ArtworkItem(
                SecondPath,
                library);
        first.Disposition =
            AnalysisRepairDisposition.Active;
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForArtwork(
                new AnalysisReport(
                    "Artwork",
                    []),
                Records(),
                [first, second],
                "Two artwork repairs");

        AssertCountTransitions(
            run,
            nameof(
                AnalysisRunViewModel
                    .ActiveArtworkRepairCount),
            () =>
                run.ActiveArtworkRepairCount,
            disposition =>
                second.Disposition =
                    disposition,
            disposition =>
                first.Disposition =
                    disposition);
    }

    private static void AssertCountTransitions(
        AnalysisRunViewModel run,
        string countProperty,
        Func<int> count,
        Action<AnalysisRepairDisposition>
            activateSecond,
        Action<AnalysisRepairDisposition>
            ignoreFirst)
    {
        var changed =
            new List<string?>();
        run.PropertyChanged +=
            (_, args) =>
                changed.Add(
                    args.PropertyName);

        Assert.Equal(
            1,
            count());

        activateSecond(
            AnalysisRepairDisposition.Active);

        Assert.Equal(
            2,
            count());
        Assert.Contains(
            countProperty,
            changed);
        changed.Clear();

        ignoreFirst(
            AnalysisRepairDisposition.Ignored);

        Assert.Equal(
            1,
            count());
        Assert.Contains(
            countProperty,
            changed);
    }

    private static AnalysisRepairItemViewModel
        MetadataItem(
            string path) =>
        new(
            new AnalysisTagRepair(
                path,
                TagFields.Title,
                "Before",
                "After",
                "Reviewed title correction",
                100,
                DateTime.UnixEpoch));

    private static RepresentationRepairAction
        RepresentationAction(
            string source,
            string fileName) =>
        new(
            RepresentationRepairKind.Organize,
            source,
            Path.Combine(
                @"C:\Organized",
                fileName),
            "Move to the reviewed path");

    private static ItlMetadataRepairItem
        ItunesRepair(
            string path) =>
        new(
            Guid.NewGuid(),
            1,
            1,
            path,
            new ItlCachedTrackMetadata
            {
                Artist = "Artist",
                Album = "Album",
                Title = "After",
            },
            DateTime.UnixEpoch,
            [
                new(
                    "Title",
                    "Before",
                    "After"),
            ]);

    private static ArtworkRepairItemViewModel
        ArtworkItem(
            string path,
            FakeLibrary library) =>
        new(
            ArtworkRepairKind.NormalizeFile,
            Path.GetFileName(
                path),
            "Normalize the selected artwork",
            [path],
            [
                new ArtworkRepairCandidateViewModel(
                    path,
                    "Front cover",
                    "HASH",
                    "800 \u00D7 800",
                    800,
                    800,
                    64_000,
                    library,
                    null),
            ],
            showGallery: false,
            maximumBytes: 1_000_000,
            maximumDimension: 1_000,
            artist: "Artist",
            album: "Album");

    private static TrackRecord[] Records() =>
    [
        new()
        {
            Path = FirstPath,
            Artist = "Artist",
            Album = "Album",
            Title = "First",
        },
        new()
        {
            Path = SecondPath,
            Artist = "Artist",
            Album = "Album",
            Title = "Second",
        },
    ];
}
