using MetadataCaching;
using MusicFileUtilities;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using iTunes.Binary;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AnalyzerViewModelTests
{
    [Fact]
    public void ConfiguredFfmpegAlwaysTracksTheActiveLibraryConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"analyzer-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "first.xml");
            string second = Path.Combine(directory, "second.xml");
            File.WriteAllText(first,
                "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
                "<FfmpegPath>configured-ffmpeg</FfmpegPath></LibraryConfiguration>");
            File.WriteAllText(second,
                "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
                "<FfmpegPath>replacement-ffmpeg</FfmpegPath></LibraryConfiguration>");
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(first);
            var viewModel = new AnalyzerViewModel(
                new StubLibrary([]), new StubReconciler(), new StubRepairs(), settings);

            Assert.Equal("configured-ffmpeg", viewModel.FfmpegPath);
            settings.LoadConfig(second);
            Assert.Equal("replacement-ffmpeg", viewModel.FfmpegPath);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public void FindingRun_GroupsByProblemArtistAndAlbumAndTracksDisposition()
    {
        var records = new[]
        {
            Track(Path.Combine("AA", "First", "01.flac"), "AA", "First"),
            Track(Path.Combine("AA", "First", "02.flac"), "AA", "First"),
            Track(Path.Combine("BB", "Second", "01.flac"), "BB", "Second"),
        };
        var report = new AnalysisReport("Health", [
            new(records[0].Path, "missing total", "Missing track total"),
            new(records[1].Path, "missing total", "Missing track total"),
            new(records[2].Path, "lossy", "Lossy codec"),
        ]);

        var run = AnalysisRunViewModel.ForFindings(report, records, "3 findings");

        Assert.Equal(2, run.FindingGroups.Count);
        var missing = run.FindingGroups.Single(group => group.Problem == "Missing track total");
        var artist = Assert.Single(missing.Artists);
        Assert.Equal("AA", artist.Artist);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("First", album.Album);
        Assert.Equal([AnalysisFindingDisposition.None, AnalysisFindingDisposition.Filter],
            album.Findings[0].Dispositions);
        Assert.DoesNotContain("Deferred", Enum.GetNames<AnalysisRepairDisposition>());
        Assert.Equal(2, album.ActiveCount);
        Assert.Equal(2, artist.ActiveCount);
        Assert.Equal(3, run.ActiveFindingCount);

        album.Findings[0].Disposition = AnalysisFindingDisposition.Filter;

        Assert.Equal(1, album.ActiveCount);
        Assert.Equal(1, artist.ActiveCount);
        Assert.Equal(1, missing.ActiveCount);
        Assert.Equal(2, run.ActiveFindingCount);
        Assert.Equal(AnalysisFindingDisposition.Mixed, album.Disposition);
        Assert.Equal(AnalysisFindingDisposition.Mixed, artist.Disposition);
        Assert.Equal(AnalysisFindingDisposition.Mixed, missing.Disposition);
        Assert.Equal([records[0].Path], run.FilteredPaths);

        missing.Disposition = AnalysisFindingDisposition.Filter;

        Assert.Equal([records[0].Path, records[1].Path], run.FilteredPaths);

        missing.Disposition = AnalysisFindingDisposition.None;

        Assert.Equal(AnalysisFindingDisposition.None, missing.Disposition);
        Assert.Equal(2, missing.ActiveCount);
        Assert.All(missing.Artists.SelectMany(group => group.Albums)
            .SelectMany(group => group.Findings),
            finding => Assert.Equal(AnalysisFindingDisposition.None,
                finding.Disposition));
    }

    [Fact]
    public void FindingRun_SortsHierarchyAndUsesUnknownMetadataFallbacks()
    {
        var records = new[]
        {
            Track("z.flac", "Zulu", "Second"),
            Track("a.flac", "Alpha", "First"),
            Track("alpha.flac", "alpha", "first"),
            Track("unknown.flac", "", "") with
            {
                AlbumArtist = null,
                HasAlbumArtist = false,
                Artist = null,
                Album = null,
                StrippedAlbum = null,
            },
        };
        var report = new AnalysisReport("Health", [
            new(records[0].Path, "issue", "Category"),
            new(records[1].Path, "issue", "Category"),
            new(records[2].Path, "issue", "Category"),
            new(records[3].Path, "issue", "Category"),
        ]);

        var category = Assert.Single(
            AnalysisRunViewModel.ForFindings(report, records, "4 findings").FindingGroups);

        Assert.Equal(["Alpha", "Unknown Artist", "Zulu"],
            category.Artists.Select(artist => artist.Artist));
        var alpha = category.Artists.Single(artist => artist.Artist == "Alpha");
        Assert.Equal(2, Assert.Single(alpha.Albums).Count);
        var unknown = category.Artists.Single(artist => artist.Artist == "Unknown Artist");
        Assert.Equal("Unknown Album", Assert.Single(unknown.Albums).Album);
    }

    [Fact]
    public async Task Analyzer_FindingTreeSelectionIncludesEveryDescendantFile()
    {
        var viewModel = Create([
            Track("aa-first-1.mp3", "AA", "First", CodecType.Lossy, "One", 1),
            Track("aa-first-2.mp3", "AA", "First", CodecType.Lossy, "Two", 2),
            Track("aa-second.mp3", "AA", "Second", CodecType.Lossy, "Three", 1),
            Track("bb-third.mp3", "BB", "Third", CodecType.Lossy, "Four", 1),
        ]);

        await viewModel.RunLossyCommand.ExecuteAsync(null);

        var reason = Assert.Single(viewModel.FindingGroups);
        Assert.Equal(4, viewModel.DisplayedFindings.Count);

        viewModel.SelectedFindingNode = reason.Artists.Single(artist => artist.Artist == "AA");
        Assert.Equal(3, viewModel.DisplayedFindings.Count);

        viewModel.SelectedFindingNode = reason.Artists.Single(artist => artist.Artist == "AA")
            .Albums.Single(album => album.Album == "First");
        Assert.Equal(2, viewModel.DisplayedFindings.Count);

        viewModel.SelectedFindingNode = reason;
        Assert.Equal(4, viewModel.DisplayedFindings.Count);
    }

    [Fact]
    public void RepresentationRepairRun_PropagatesBranchDispositionsAndCalculatesMixedState()
    {
        var records = new[]
        {
            Track(@"Z:\FLAC\First\01.flac", "AA", "First"),
            Track(@"Z:\FLAC\Second\01.flac", "BB", "Second"),
        };
        var run = AnalysisRunViewModel.ForRepresentationRepairs(
            [
                new(RepresentationRepairKind.DeriveAac, records[0].Path,
                    @"Z:\AAC\First\01.m4a", "Encode first."),
                new(RepresentationRepairKind.DeriveAac, records[1].Path,
                    @"Z:\AAC\Second\01.m4a", "Encode second."),
                new(RepresentationRepairKind.Organize, records[0].Path,
                    @"Z:\FLAC\AA\First\01.flac", "Organize first."),
            ],
            [],
            records,
            "3 actions");

        var aac = run.RepresentationActionGroups.Single(
            group => group.Artists
                .SelectMany(artist => artist.Albums)
                .SelectMany(album => album.Items)
                .All(item =>
                    item.Action.Kind ==
                    RepresentationRepairKind.DeriveAac));
        Assert.Equal(0, aac.ActiveCount);
        Assert.All(run.RepresentationActionItems,
            item => Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition));

        aac.Disposition = AnalysisRepairDisposition.Active;

        Assert.Equal(2, aac.ActiveCount);
        var firstAlbum = Assert.Single(
            aac.Artists.Single(artist => artist.Artist == "AA").Albums);

        firstAlbum.Disposition = AnalysisRepairDisposition.Filter;

        Assert.Equal(AnalysisRepairDisposition.Mixed, aac.Disposition);
        Assert.Equal(1, aac.ActiveCount);

        aac.Disposition = AnalysisRepairDisposition.Ignored;

        Assert.Equal(AnalysisRepairDisposition.Ignored, aac.Disposition);
        Assert.Equal(0, aac.ActiveCount);
        Assert.All(aac.Artists.SelectMany(artist => artist.Albums)
            .SelectMany(album => album.Items),
            item => Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition));
    }

    [Fact]
    public async Task Analyzer_RetainsRunsAndRestoresEachTypedResult()
    {
        var records = new[]
        {
            Track("one.mp3", "AA", "Album", CodecType.Lossy, "Song", 1),
            Track("two.flac", "AA", "Album", CodecType.Lossless, "Song", 1),
        };
        var viewModel = Create(records);

        await viewModel.RunDuplicatesCommand.ExecuteAsync(null);
        var duplicateRun = Assert.Single(viewModel.Runs);
        Assert.Single(viewModel.Duplicates);
        Assert.Equal(1, viewModel.ActiveResultIndex);
        Assert.Equal(AppActivityState.Completed, viewModel.LastActivityState);
        Assert.Equal(MessageTone.Success, viewModel.StatusTone);

        await viewModel.RunLossyCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Runs.Count);
        Assert.Equal("Lossy files", viewModel.SelectedRun!.Name);
        Assert.Equal(0, viewModel.ActiveResultIndex);
        Assert.Single(viewModel.FindingGroups);
        Assert.Empty(viewModel.Duplicates);

        viewModel.SelectedRun = duplicateRun;

        Assert.Equal(AnalysisResultView.Duplicates, viewModel.ActiveView);
        Assert.Equal(1, viewModel.ActiveResultIndex);
        Assert.Single(viewModel.Duplicates);
        Assert.Empty(viewModel.FindingGroups);
    }

    [Fact]
    public void Analyzer_ResultIndexSupportsTwoWaySelection()
    {
        var viewModel = Create([]);

        viewModel.ActiveResultIndex = 5;

        Assert.Equal(AnalysisResultView.Conflicts, viewModel.ActiveView);
        Assert.Equal(5, viewModel.ActiveResultIndex);
    }

    [Fact]
    public async Task Analyzer_FindingDispositionSurvivesNavigationBetweenRuns()
    {
        var viewModel = Create([
            Track("one.mp3", "AA", "Album", CodecType.Lossy, "One", 1),
            Track("two.flac", "AA", "Album", CodecType.Lossless, "Two", null),
        ]);
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        var lossyRun = viewModel.SelectedRun!;
        var finding = Assert.Single(
            Assert.Single(Assert.Single(lossyRun.FindingGroups).Artists).Albums).Findings[0];
        finding.Disposition = AnalysisFindingDisposition.Filter;

        await viewModel.RunInconsistenciesCommand.ExecuteAsync(null);
        viewModel.SelectedRun = lossyRun;

        Assert.Equal(AnalysisFindingDisposition.Filter,
            Assert.Single(
                Assert.Single(Assert.Single(viewModel.FindingGroups).Artists).Albums)
                .Findings[0].Disposition);
        Assert.Equal(0, lossyRun.ActiveFindingCount);
    }

    [Fact]
    public async Task Analyzer_PublishesFilteredPathsAndClearsThemWithTheRun()
    {
        TrackRecord record = Track(
            "one.mp3", "AA", "Album", CodecType.Lossy, "One", 1);
        var viewModel = Create([record]);
        IReadOnlyList<string>? published = null;
        viewModel.FilterChanged += paths => published = paths;
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        AnalysisFindingViewModel finding = Assert.Single(
            Assert.Single(Assert.Single(viewModel.FindingGroups).Artists).Albums).Findings[0];

        finding.Disposition = AnalysisFindingDisposition.Filter;

        Assert.Equal([record.Path], viewModel.FilteredPaths);
        Assert.Equal([record.Path], published);

        viewModel.RemoveRunCommand.Execute(null);

        Assert.Empty(viewModel.FilteredPaths);
        Assert.Empty(published!);
    }

    [Fact]
    public async Task Analyzer_ClearFilterDispositionsClearsFindingsAndPreservesOtherChoices()
    {
        TrackRecord record = Track(
            "one.mp3", "AA", "Album", CodecType.Lossy, "One", 1);
        var viewModel = Create([record]);
        IReadOnlyList<string>? published = null;
        viewModel.FilterChanged += paths => published = paths;
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        AnalysisFindingViewModel finding = Assert.Single(
            Assert.Single(Assert.Single(viewModel.FindingGroups).Artists).Albums).Findings[0];
        finding.Disposition = AnalysisFindingDisposition.Filter;

        var activeRepair = new AnalysisTagRepair(
            record.Path, TagFields.AlbumArtist, null, "AA", "Fill album artist", 1,
            DateTime.UtcNow);
        var filteredRepair = activeRepair with
        {
            Path = "two.mp3",
            Reason = "Fill second album artist",
        };
        var completedRepair = activeRepair with
        {
            Path = "three.mp3",
            Reason = "Already completed album artist",
        };
        var activeItem = new AnalysisRepairItemViewModel(activeRepair)
        {
            Disposition = AnalysisRepairDisposition.Active,
        };
        var filteredItem = new AnalysisRepairItemViewModel(filteredRepair)
        {
            Disposition = AnalysisRepairDisposition.Filter,
        };
        var completedItem = new AnalysisRepairItemViewModel(completedRepair)
        {
            Disposition = AnalysisRepairDisposition.Completed,
            IsApplied = true,
        };
        viewModel.Runs.Add(AnalysisRunViewModel.ForRepairs(
            new AnalysisRepairPlan("Repairs", [activeRepair, filteredRepair, completedRepair]),
            [activeItem, filteredItem, completedItem], [record], "3 repairs"));
        int clearPublicationCount = 0;
        viewModel.FilterChanged += _ => clearPublicationCount++;

        viewModel.ClearFilterDispositions();

        Assert.Equal(AnalysisFindingDisposition.None, finding.Disposition);
        Assert.Equal(AnalysisRepairDisposition.Active, activeItem.Disposition);
        Assert.Equal(AnalysisRepairDisposition.Ignored, filteredItem.Disposition);
        Assert.Equal(AnalysisRepairDisposition.Completed, completedItem.Disposition);
        Assert.Empty(viewModel.FilteredPaths);
        Assert.Empty(published!);
        Assert.Equal(1, clearPublicationCount);
    }

    [Fact]
    public async Task Analyzer_RemoveAndClearResultsSelectAValidRun()
    {
        var viewModel = Create([Track("one.mp3", "AA", "Album", CodecType.Lossy, "One", 1)]);
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        await viewModel.RunInconsistenciesCommand.ExecuteAsync(null);

        viewModel.RemoveRunCommand.Execute(null);

        Assert.Single(viewModel.Runs);
        Assert.Same(viewModel.Runs[0], viewModel.SelectedRun);

        viewModel.ClearRunsCommand.Execute(null);

        Assert.Empty(viewModel.Runs);
        Assert.Null(viewModel.SelectedRun);
        Assert.False(viewModel.HasRuns);
    }

    [Fact]
    public async Task Analyzer_MetadataRepairPreviewUsesTheCombinedSafePlan()
    {
        var record = Track("track.flac", "AA", "Album");
        var repairs = new TrackingRepairs(new AnalysisRepairPlan("Safe metadata repairs", [
            new(record.Path, TagFields.TotalTracks, null, "10", "reason", 100, DateTime.UtcNow),
        ]));
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([record]), new StubReconciler(), repairs,
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.PreviewMetadataRepairsCommand.ExecuteAsync(null);

        Assert.Equal(1, repairs.SafePreviewCalls);
        Assert.Equal("Safe metadata repairs", viewModel.SelectedRun!.Name);
        Assert.Single(viewModel.RepairItems);
    }

    [Fact]
    public void MetadataRepairRun_GroupsPathRepairsAndMakesBlockedLeavesInactive()
    {
        var record = Track(@"Z:\FLAC\Artist\Album\One Song.flac", "Artist", "Album");
        var pathRepair = new AnalysisTagRepair(
            record.Path,
            TagFields.NullField,
            record.Path,
            @"Z:\FLAC\Artist\Album\One Song.flac",
            "Replace non-breaking spaces.",
            100,
            DateTime.UtcNow,
            AnalysisRepairKind.Path,
            OperationPathSnapshot.Missing(@"Z:\FLAC\Artist\Album\One Song.flac"));
        var blockedRepair = pathRepair with
        {
            Path = @"Z:\FLAC\Artist\Album\Two Song.flac",
            Before = @"Z:\FLAC\Artist\Album\Two Song.flac",
            After = @"Z:\FLAC\Artist\Album\Two Song.flac",
            BlockingReason = "Destination already exists.",
        };
        var items = new[]
        {
            new AnalysisRepairItemViewModel(pathRepair),
            new AnalysisRepairItemViewModel(blockedRepair),
        };

        AnalysisRunViewModel run = AnalysisRunViewModel.ForRepairs(
            new AnalysisRepairPlan("Safe metadata repairs", [pathRepair, blockedRepair]),
            items,
            [record],
            "2 repairs");

        var category = Assert.Single(run.RepairGroups);
        Assert.Equal("Path", category.Category);
        Assert.Equal(2, category.Count);
        Assert.Equal(0, category.ActiveCount);
        Assert.All(items, item => Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition));
        Assert.Contains("⟦NBSP⟧", items[0].Before);
        Assert.True(items[0].CanChangeDisposition);
        Assert.True(items[1].CanChangeDisposition);
        Assert.Contains(AnalysisRepairDisposition.Filter, items[1].Dispositions);
        Assert.DoesNotContain(AnalysisRepairDisposition.Active, items[1].Dispositions);
        Assert.False(items[1].IsActive);

        items[1].Disposition = AnalysisRepairDisposition.Filter;
        Assert.Equal([items[1].Path], run.FilteredPaths);

        items[0].Disposition = AnalysisRepairDisposition.Active;
        Assert.Equal(1, category.ActiveCount);
    }

    [Fact]
    public void ItlRepairRun_GroupsRepairsByCategoryArtistAndAlbum()
    {
        string path = @"Z:\iTunes\AAC\Music\Artist\Album\Track.m4a";
        var repair = new ItlMetadataRepairItem(
            Guid.NewGuid(), 1, 1, path,
            new ItlCachedTrackMetadata
            {
                Artist = "Artist",
                AlbumArtist = "Album Artist",
                HasExplicitAlbumArtist = true,
                Album = "Album",
                Title = "Correct title",
            },
            DateTime.UtcNow,
            [new("Title", "Wrong title", "Correct title")]);
        var item = new ItlMetadataRepairItemViewModel(repair);
        var plan = new ItlMetadataRepairPlan(
            "Library.itl", "HASH", DateTimeOffset.UtcNow, [repair]);

        AnalysisRunViewModel run = AnalysisRunViewModel.ForItlRepairs(plan, [item], "1 repair");

        ItlMetadataRepairCategoryGroupViewModel category = Assert.Single(run.ItlRepairGroups);
        Assert.Equal("Cached metadata", category.Category);
        ItlMetadataRepairArtistGroupViewModel artist = Assert.Single(category.Artists);
        Assert.Equal("Album Artist", artist.Artist);
        ItlMetadataRepairAlbumGroupViewModel album = Assert.Single(artist.Albums);
        Assert.Equal("Album", album.Album);
        Assert.Same(item, Assert.Single(album.Items));
        Assert.Equal(AnalysisRepairDisposition.Ignored, category.Disposition);

        category.Disposition = AnalysisRepairDisposition.Active;

        Assert.True(item.IsActive);
        Assert.Equal(1, album.ActiveCount);
        Assert.Equal(1, artist.ActiveCount);
        Assert.Equal(1, category.ActiveCount);
    }

    [Fact]
    public void MetadataRepairRun_LabelsId3VersionUpgrades()
    {
        var record = Track(@"Z:\Music\Artist\Album\Track.mp3", "Artist", "Album") with
        {
            TagType = "ID3v22",
        };
        var repair = new AnalysisTagRepair(
            record.Path,
            TagFields.NullField,
            "ID3v2.2",
            "ID3v2.3",
            "Upgrade the legacy tag.",
            100,
            DateTime.UtcNow,
            TargetId3Version: ID3v2Version.V23);
        var item = new AnalysisRepairItemViewModel(repair);

        AnalysisRunViewModel run = AnalysisRunViewModel.ForRepairs(
            new AnalysisRepairPlan("Safe metadata repairs", [repair]),
            [item],
            [record],
            "1 repair");

        Assert.Equal("ID3 tag version", item.Field);
        Assert.Equal("ID3 tag version", Assert.Single(run.RepairGroups).Category);
        Assert.Equal("ID3v2.2", item.Before);
        Assert.Equal("ID3v2.3", item.After);
        Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition);
        Assert.False(item.IsActive);
        item.Disposition = AnalysisRepairDisposition.Active;
        Assert.True(item.IsActive);
    }

    [Fact]
    public void MetadataRepairRun_HighlightsAndDescribesUnicodeScalarDifferences()
    {
        var repair = new AnalysisTagRepair(
            @"Z:\Music\Track.flac",
            TagFields.Title,
            "Caf\u00E9\u00A0Mix",
            "Cafe\u0301 Mix",
            "Normalize Unicode text.",
            100,
            DateTime.UtcNow);

        var item = new AnalysisRepairItemViewModel(repair);

        Assert.Equal("Caf", item.BeforeDifference[0].Text);
        Assert.False(item.BeforeDifference[0].IsDifferent);
        Assert.Contains(item.BeforeDifference,
            segment => segment.IsDifferent && segment.Text.Contains("é⟦NBSP⟧"));
        Assert.Contains(item.AfterDifference,
            segment => segment.IsDifferent && segment.Text.Contains("é "));
        Assert.Contains("U+00E9", item.UnicodeDifferenceDetails);
        Assert.Contains("U+0301", item.UnicodeDifferenceDetails);
        Assert.Contains("U+00A0 NO-BREAK SPACE", item.UnicodeDifferenceDetails);
        Assert.Contains("U+0020 SPACE", item.UnicodeDifferenceDetails);
    }

    [Fact]
    public async Task Analyzer_ConflictChoiceCreatesASeparateRepairPreview()
    {
        var records = new[]
        {
            Track("one.flac", "First Artist", "Album"),
            Track("two.flac", "Second Artist", "Album"),
        };
        var conflict = new AnalysisTagConflict(
            "Album",
            "library",
            TagFields.AlbumArtist,
            [new("First Artist", 1), new("Second Artist", 1)],
            [
                new(records[0].Path, "First Artist", 100, DateTime.UtcNow),
                new(records[1].Path, "Second Artist", 100, DateTime.UtcNow),
            ]);
        var repairs = new TrackingConflictRepairs(conflict);
        var viewModel = new AnalyzerViewModel(
            new StubLibrary(records), new StubReconciler(), repairs,
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.FindAlbumArtistConflictsCommand.ExecuteAsync(null);

        Assert.Equal(AnalysisResultView.Conflicts, viewModel.ActiveView);
        var conflictRun = Assert.Single(viewModel.Runs);
        var group = Assert.Single(viewModel.ConflictGroups);
        Assert.Null(group.SelectedOption);
        Assert.False(viewModel.PreviewConflictRepairsCommand.CanExecute(null));

        group.SelectedOption = group.Options[1];
        Assert.True(viewModel.PreviewConflictRepairsCommand.CanExecute(null));
        await viewModel.PreviewConflictRepairsCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Runs.Count);
        Assert.Equal(AnalysisResultView.Repairs, viewModel.ActiveView);
        var item = Assert.Single(viewModel.RepairItems);
        Assert.Equal("Second Artist", item.After);
        Assert.Same(conflictRun, viewModel.Runs[1]);
        Assert.Equal("Second Artist", Assert.Single(repairs.Resolutions!).SelectedValue);

        viewModel.SelectedRun = conflictRun;
        Assert.Same(group.Options[1], Assert.Single(viewModel.ConflictGroups).SelectedOption);
    }

    [Fact]
    public async Task Analyzer_SelectedResultUsesTheRetainedSnapshotDirectly()
    {
        var viewModel = Create([
            Track("one.mp3", "AA", "Album", CodecType.Lossy, "One", 1),
            Track("two.mp3", "AA", "Album", CodecType.Lossy, "Two", 2),
        ]);

        await viewModel.RunLossyCommand.ExecuteAsync(null);

        Assert.Same(viewModel.SelectedRun!.FindingGroups, viewModel.FindingGroups);
        Assert.Empty(viewModel.Duplicates);
        Assert.Empty(viewModel.ArtistGroups);
        Assert.Empty(viewModel.ConflictGroups);
        Assert.Empty(viewModel.RepairItems);
        Assert.Empty(viewModel.Matrices);
    }

    [Fact]
    public async Task Analyzer_SimilarArtistsUsesTheSelectedThresholdAndBuildsAReviewRun()
    {
        var reconciler = new RecordingArtistReconciler(SimilarGroup());
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([]), reconciler, new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")))
        {
            ArtistThreshold = 0.07,
        };

        await viewModel.RunSimilarArtistsCommand.ExecuteAsync(null);

        Assert.Equal(0.07, reconciler.Threshold);
        Assert.Equal(AnalysisResultView.Artists, viewModel.ActiveView);
        Assert.Contains("threshold 0.07", viewModel.StatusText);
        ArtistGroupViewModel group = Assert.Single(viewModel.ArtistGroups);
        Assert.Equal("Canonical", group.CanonicalName);
        Assert.All(group.Variants, variant =>
            Assert.Equal(AnalysisRepairDisposition.Ignored, variant.Disposition));
        Assert.False(viewModel.ApplySimilarArtistsCommand.CanExecute(null));
    }

    [Fact]
    public void Analyzer_ArtistThresholdPersistsAcrossSessions()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"analyzer-threshold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string settingsPath = Path.Combine(directory, "settings.json");
            var first = new AnalyzerViewModel(
                new StubLibrary([]), new StubReconciler(), new StubRepairs(),
                new AppSettings(settingsPath));

            first.ArtistThresholdText = "0.13";

            var restored = new AnalyzerViewModel(
                new StubLibrary([]), new StubReconciler(), new StubRepairs(),
                new AppSettings(settingsPath));
            Assert.Equal(0.13, restored.ArtistThreshold, precision: 3);
            Assert.Equal("0.13", restored.ArtistThresholdText);

            restored.ArtistThresholdText = "";
            var restoredEmptyValue = new AnalyzerViewModel(
                new StubLibrary([]), new StubReconciler(), new StubRepairs(),
                new AppSettings(settingsPath));
            Assert.Equal(0, restoredEmptyValue.ArtistThreshold);
            Assert.Equal("0", restoredEmptyValue.ArtistThresholdText);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public async Task Analyzer_AppliesOnlyActiveArtistVariantsAndPublishesChangedPaths()
    {
        var reconciler = new RecordingArtistReconciler(SimilarGroup());
        var dialogs = new RecordingDialogs(true);
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([]), reconciler, new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")),
            dialogs: dialogs);
        IReadOnlyList<string>? publishedPaths = null;
        viewModel.RepairsApplied += paths => publishedPaths = paths;
        await viewModel.RunSimilarArtistsCommand.ExecuteAsync(null);
        ArtistVariantViewModel typo = viewModel.ArtistGroups.Single().Variants
            .Single(variant => variant.Name == "Canoncial");

        typo.Disposition = AnalysisRepairDisposition.Active;
        Assert.True(viewModel.ApplySimilarArtistsCommand.CanExecute(null));
        await viewModel.ApplySimilarArtistsCommand.ExecuteAsync(null);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal("Canoncial", call.From);
        Assert.Equal("Canonical", call.To);
        Assert.Equal(typo.Files.Select(file => file.Path), call.Paths);
        Assert.Contains("2 tracks", dialogs.Message);
        Assert.Contains("no recovery journal", dialogs.Message);
        Assert.True(typo.IsApplied);
        Assert.Equal(AnalysisRepairDisposition.Completed, typo.Disposition);
        Assert.Equal(typo.Files.Select(file => file.Path), publishedPaths);
        Assert.False(viewModel.ApplySimilarArtistsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Analyzer_SimilarArtistApplyCanBeCancelled()
    {
        var reconciler = new BlockingArtistReconciler(SimilarGroup());
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([]), reconciler, new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")),
            dialogs: new RecordingDialogs(true));
        await viewModel.RunSimilarArtistsCommand.ExecuteAsync(null);
        ArtistVariantViewModel typo = viewModel.ArtistGroups.Single().Variants
            .Single(variant => variant.Name == "Canoncial");
        typo.Disposition = AnalysisRepairDisposition.Active;

        Task apply = viewModel.ApplySimilarArtistsCommand.ExecuteAsync(null);
        await reconciler.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        viewModel.CancelCommand.Execute(null);
        await apply;

        Assert.False(typo.IsApplied);
        Assert.Equal(AnalysisRepairDisposition.Active, typo.Disposition);
        Assert.Equal(MessageTone.Warning, viewModel.StatusTone);
        Assert.Equal(
            LocalizedText.Get(
                "Health.Status.ArtistMerge.Cancelled"),
            viewModel.StatusText);
    }

    [Fact]
    public async Task Analyzer_AlbumMatrixIsRetainedAsATypedRun()
    {
        var record = Track("track.flac", "AA", "Album") with { TrackTotal = null };
        var viewModel = Create([record]);

        await viewModel.RunAlbumMatrixCommand.ExecuteAsync(null);

        Assert.Equal(AnalysisResultView.Matrix, viewModel.ActiveView);
        Assert.Equal("Album metadata matrix", viewModel.SelectedRun!.Name);
        Assert.Single(viewModel.Matrices);
        Assert.Same(viewModel.Matrices[0], Assert.Single(viewModel.SelectedRun.Matrices));
    }

    [Fact]
    public async Task Analyzer_ArtworkHealthUsesMetadataAuditWithoutHydratingImages()
    {
        var library = new StubLibrary([Track("track.flac", "AA", "Album")]);
        var viewModel = new AnalyzerViewModel(
            library, new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.RunArtworkHealthCommand.ExecuteAsync(null);

        Assert.Equal("Artwork health", viewModel.SelectedRun!.Name);
        Assert.Null(library.ArtworkPaths);
        Assert.Contains(viewModel.FindingGroups, group => group.Problem == "Artwork scan deferred");
    }

    [Fact]
    public async Task Analyzer_ForceScansDeferredArtworkAndRefreshesTheAudit()
    {
        TrackRecord track = Track("track.flac", "AA", "Album");
        var library = new StubLibrary([track])
        {
            ArtworkAfterSignatureRead =
            [
                new ArtworkAuditFile(track.Path, true,
                    [new("cover", "image/jpeg", "FrontCover", 600, 600, 100_000)]),
            ],
        };
        var viewModel = new AnalyzerViewModel(
            library, new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.RunArtworkHealthCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.DeferredArtworkCount);

        await viewModel.ForceScanDeferredArtworkCommand.ExecuteAsync(null);

        Assert.Equal([track.Path], library.ArtworkPaths);
        Assert.Equal(0, viewModel.DeferredArtworkCount);
        Assert.DoesNotContain(viewModel.FindingGroups,
            group => group.Problem == "Artwork scan deferred");
    }

    [Fact]
    public async Task Analyzer_AppliesOnlyActiveArtworkActionsToEveryAffectedFile()
    {
        TrackRecord one = Track("one.flac", "AA", "Album", title: "One");
        TrackRecord two = Track("two.flac", "AA", "Album", title: "Two");
        ArtworkAuditFile[] audit =
        [
            new(one.Path, true,
                [new("one", "image/jpeg", "FrontCover", 900, 900, 300_000)]),
            new(two.Path, true,
                [new("two", "image/jpeg", "FrontCover", 800, 800, 250_000)]),
        ];
        var library = new StubLibrary([one, two], audit,
            new Dictionary<string, byte[]>
            {
                [one.Path] = [1, 2, 3],
                [two.Path] = [4, 5, 6],
            });
        var artwork = new RecordingArtworkService();
        var viewModel = new AnalyzerViewModel(
            library, new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")),
            artwork: artwork);

        await viewModel.RunArtworkHealthCommand.ExecuteAsync(null);
        ArtworkRepairItemViewModel item = Assert.Single(viewModel.ArtworkRepairItems);
        item.SelectedCandidate = item.Candidates[1];
        item.Disposition = AnalysisRepairDisposition.Active;

        await viewModel.ApplyArtworkRepairsCommand.ExecuteAsync(null);

        Assert.Equal([one.Path, two.Path], artwork.SavedPaths);
        Assert.All(artwork.SavedImages, images =>
            Assert.Equal([4, 5, 6], Assert.Single(images).Data));
        Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetDimension,
            artwork.PrepareDimensions.Single());
        Assert.Equal(AnalysisRepairDisposition.Completed, item.Disposition);
        Assert.True(item.IsApplied);
    }

    [Fact]
    public async Task Analyzer_MixedArtworkHierarchyScopesAutomaticCandidateSelection()
    {
        TrackRecord[] records =
        [
            Track("one-a.flac", "Artist", "One", title: "One A"),
            Track("one-b.flac", "Artist", "One", title: "One B"),
            Track("two-a.flac", "Artist", "Two", title: "Two A"),
            Track("two-b.flac", "Artist", "Two", title: "Two B"),
        ];
        ArtworkAuditFile[] audit =
        [
            new(records[0].Path, true,
                [new("one-a", "image/jpeg", "FrontCover", 600, 600, 500_000)]),
            new(records[1].Path, true,
                [new("one-b", "image/jpeg", "FrontCover", 900, 900, 300_000)]),
            new(records[2].Path, true,
                [new("two-a", "image/jpeg", "FrontCover", 700, 700, 600_000)]),
            new(records[3].Path, true,
                [new("two-b", "image/jpeg", "FrontCover", 800, 800, 400_000)]),
        ];
        var viewModel = new AnalyzerViewModel(
            new StubLibrary(records, audit), new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")),
            artwork: new RecordingArtworkService());

        await viewModel.RunArtworkHealthCommand.ExecuteAsync(null);

        ArtworkRepairCategoryGroupViewModel category =
            Assert.Single(viewModel.ArtworkRepairGroups);
        ArtworkRepairArtistGroupViewModel artist = Assert.Single(category.Artists);
        Assert.Equal("Artist", artist.Artist);
        Assert.Equal(["One", "Two"], artist.Albums.Select(album => album.Album));
        ArtworkRepairAlbumGroupViewModel album = artist.Albums[0];
        ArtworkRepairItemViewModel leaf = Assert.Single(album.Items);
        Assert.True(viewModel.CanAutomaticallySelectMixedArtwork(category));
        Assert.True(viewModel.CanAutomaticallySelectMixedArtwork(artist));
        Assert.True(viewModel.CanAutomaticallySelectMixedArtwork(album));
        Assert.True(viewModel.CanAutomaticallySelectMixedArtwork(leaf));

        Assert.Equal(1, viewModel.AutomaticallySelectMixedArtwork(
            album, ArtworkCandidateSelectionRule.HighestResolution));
        Assert.Equal("One B", leaf.SelectedCandidate!.Label);
        Assert.True(leaf.IsActive);
        Assert.False(Assert.Single(artist.Albums[1].Items).IsActive);

        Assert.Equal(2, viewModel.AutomaticallySelectMixedArtwork(
            category, ArtworkCandidateSelectionRule.LargestFile));
        Assert.Equal("One A", leaf.SelectedCandidate!.Label);
        Assert.Equal("Two A", Assert.Single(artist.Albums[1].Items).SelectedCandidate!.Label);
        Assert.All(category.DescendantItems, item => Assert.True(item.IsActive));
    }

    [Fact]
    public async Task Analyzer_ArtworkHealthUsesThresholdsFromActiveLibraryConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"analyzer-artwork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string configurationPath = Path.Combine(directory, "library.xml");
            new EditableLibraryConfig
            {
                OversizedArtworkByteThreshold = 2_000_000,
                OversizedArtworkDimensionThreshold = 500,
            }.Save(configurationPath);
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(configurationPath);
            TrackRecord record = Track("track.flac", "AA", "Album");
            var library = new StubLibrary([record],
            [
                new ArtworkAuditFile(record.Path, true,
                    [new("cover", "image/jpeg", "FrontCover", 501, 500, 100_000)]),
            ]);
            var viewModel = new AnalyzerViewModel(
                library, new StubReconciler(), new StubRepairs(), settings);

            await viewModel.RunArtworkHealthCommand.ExecuteAsync(null);

            Assert.Contains(viewModel.FindingGroups,
                group => group.Problem == "Oversized artwork");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public async Task Analyzer_LossyAuditHonorsDisabledActiveProfileRule()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"analyzer-health-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string configurationPath = Path.Combine(directory, "library.xml");
            var editable = EditableLibraryConfig.CreateNew();
            LibraryProfile catalog = editable.Profiles.Single(profile =>
                profile.Id == LibraryProfilePresets.CatalogOnlyId);
            editable.Profiles[editable.Profiles.IndexOf(catalog)] = catalog with
            {
                Health = catalog.Health with
                {
                    Rules = catalog.Health.Rules.Select(rule =>
                        rule.Id == LibraryHealthRuleIds.LossyFile
                            ? rule with { Enabled = false }
                            : rule).ToArray(),
                },
            };
            editable.Save(configurationPath);
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(configurationPath);
            var viewModel = new AnalyzerViewModel(
                new StubLibrary([Track("one.mp3", "AA", "Album", CodecType.Lossy)]),
                new StubReconciler(), new StubRepairs(), settings);

            await viewModel.RunLossyCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.FindingGroups);
            Assert.Contains("No lossy files", viewModel.StatusText);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public async Task Analyzer_RepresentationsHydratesArtworkOnlyForMatchedCandidates()
    {
        var cd = Track(@"Z:\FLAC\Album\01.flac", "AA", "Album", title: "Song", track: 1);
        var purchased = Track(@"Z:\iTunes\purchased sync\Album\01.m4a", "AA", "Album",
            CodecType.Lossy, "Song", 1);
        var unrelated = Track(@"Z:\FLAC\Other\01.flac", "AA", "Other", title: "Other", track: 1);
        var library = new StubLibrary([cd, purchased, unrelated]);
        var viewModel = new AnalyzerViewModel(
            library, new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.RunRepresentationsCommand.ExecuteAsync(null);

        Assert.Equal(2, library.ArtworkPaths!.Count);
        Assert.Contains(cd.Path, library.ArtworkPaths);
        Assert.Contains(purchased.Path, library.ArtworkPaths);
        Assert.DoesNotContain(unrelated.Path, library.ArtworkPaths);
        Assert.Equal("Album representations", viewModel.SelectedRun!.Name);
    }

    [Fact]
    public async Task Analyzer_DecodedVerificationIsExplicitAndUsesCompatiblePairs()
    {
        var cd = Track(@"Z:\FLAC\Album\01.flac", "AA", "Album", title: "Song", track: 1) with
        {
            SampleRate = 44_100, BitsPerSample = 16, Channels = 2,
        };
        var purchased = Track(@"Z:\iTunes\purchased sync\Album\01.m4a", "AA", "Album",
            CodecType.Lossless, "Song", 1) with
        {
            SampleRate = 44_100, BitsPerSample = 16, Channels = 2,
        };
        var verifier = new TrackingDecodedAudio();
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([cd, purchased]), new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")),
            verifier);

        await viewModel.RunRepresentationsCommand.ExecuteAsync(null);

        Assert.True(viewModel.VerifyDecodedAudioCommand.CanExecute(null));
        Assert.Equal(0, verifier.Calls);

        await viewModel.VerifyDecodedAudioCommand.ExecuteAsync(null);

        Assert.Equal(1, verifier.Calls);
        Assert.Single(verifier.Pairs!);
        Assert.Equal("Decoded-audio verification", viewModel.SelectedRun!.Name);
    }

    [Fact]
    public async Task Analyzer_RepresentationRepairPreviewGroupsMetadataByCategoryArtistAndAlbum()
    {
        var records = new[]
        {
            Track(@"Z:\FLAC\First\01.flac", "AA", "First", title: "One", track: 1),
            Track(@"Z:\FLAC\First\02.flac", "AA", "First", title: "Two", track: 2),
            Track(@"Z:\FLAC\Second\01.flac", "BB", "Second", title: "Three", track: 1),
        };
        var metadata = new AnalysisRepairPlan("Copy representation metadata", [
            new(records[0].Path, TagFields.Title, "Old One", "One", "Copy from high-resolution FLAC",
                100, DateTime.UtcNow),
            new(records[1].Path, TagFields.Title, "Old Two", "Two", "Copy from high-resolution FLAC",
                100, DateTime.UtcNow),
            new(records[2].Path, TagFields.Title, "Old Three", "Three", "Copy from CD FLAC",
                100, DateTime.UtcNow),
            new(records[0].Path, TagFields.Album, "Old First", "First", "Copy from high-resolution FLAC",
                100, DateTime.UtcNow),
        ]);
        var previewer = new StubRepresentationRepairs(new RepresentationRepairPreview(
            metadata,
            [new(RepresentationRepairKind.DeriveAac, records[0].Path, @"Z:\AAC\First\01.m4a", "Encode AAC.")],
            []));
        var settings = new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json"));
        string configPath = Path.Combine(Path.GetTempPath(), $"analyzer-config-{Guid.NewGuid():N}.xml");
        new EditableLibraryConfig().Save(configPath);
        settings.LoadConfig(configPath);
        var viewModel = new AnalyzerViewModel(
            new StubLibrary(records), new StubReconciler(), new StubRepairs(), settings,
            representationRepairs: previewer);

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var callerContext = new SynchronizationContext();
        Task previewTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            previewTask = viewModel.PreviewRepresentationRepairsCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        await previewTask;

        Assert.Same(settings.Configuration, previewer.Configuration);
        Assert.Null(previewer.PreviewSynchronizationContext);
        Assert.Equal(2, viewModel.Runs.Count);
        Assert.Equal("Copy representation metadata", viewModel.SelectedRun!.Name);
        Assert.Equal(4, viewModel.RepairItems.Count);
        Assert.Same(viewModel.SelectedRun.RepairGroups, viewModel.RepairGroups);
        Assert.Equal(["Album", "Title"], viewModel.RepairGroups.Select(group => group.Category));
        var titles = viewModel.RepairGroups.Single(group => group.Category == "Title");
        Assert.Equal(["AA", "BB"], titles.Artists.Select(artist => artist.Artist));
        var first = Assert.Single(titles.Artists.Single(artist => artist.Artist == "AA").Albums);
        Assert.Equal("First", first.Album);
        Assert.Equal(2, first.Count);
        Assert.Equal(0, first.ActiveCount);
        Assert.All(viewModel.RepairItems,
            item => Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition));

        titles.Disposition = AnalysisRepairDisposition.Active;

        Assert.Equal(2, first.ActiveCount);

        viewModel.SelectedRepairNode = titles;
        Assert.Equal(3, viewModel.DisplayedRepairItems.Count);
        viewModel.SelectedRepairNode = titles.Artists.Single(artist => artist.Artist == "AA");
        Assert.Equal(2, viewModel.DisplayedRepairItems.Count);
        viewModel.SelectedRepairNode = first;
        Assert.Equal(2, viewModel.DisplayedRepairItems.Count);

        first.Items[0].Disposition = AnalysisRepairDisposition.Filter;

        Assert.Equal(1, first.ActiveCount);
        Assert.Equal(AnalysisRepairDisposition.Mixed, first.Disposition);
        Assert.Equal(1, titles.Artists.Single(artist => artist.Artist == "AA").ActiveCount);
        Assert.Equal(2, titles.ActiveCount);

        titles.Disposition = AnalysisRepairDisposition.Ignored;

        Assert.All(titles.Artists.SelectMany(artist => artist.Albums)
            .SelectMany(album => album.Items),
            item => Assert.Equal(AnalysisRepairDisposition.Ignored, item.Disposition));

        var fileRun = viewModel.Runs.Single(run => run.Name == "Representation file repairs");
        viewModel.SelectedRun = fileRun;
        var category = Assert.Single(viewModel.RepresentationActionGroups);
        viewModel.SelectedRepresentationNode = category;
        Assert.Single(viewModel.DisplayedRepresentationItems);
        category.Disposition = AnalysisRepairDisposition.Filter;
        Assert.All(viewModel.RepresentationActionItems,
            item => Assert.Equal(AnalysisRepairDisposition.Filter, item.Disposition));
        Assert.False(viewModel.ApplyRepresentationRepairsCommand.CanExecute(null));

        category.Disposition = AnalysisRepairDisposition.Active;
        Assert.True(viewModel.ApplyRepresentationRepairsCommand.CanExecute(null));
        await viewModel.ApplyRepresentationRepairsCommand.ExecuteAsync(null);

        Assert.Single(previewer.AppliedActions!);
        Assert.Equal(AnalysisRepairDisposition.Completed,
            Assert.Single(viewModel.RepresentationActionItems).Disposition);
        Assert.Equal("Applied", Assert.Single(viewModel.RepresentationActionItems).ResultText);
    }

    private static AnalyzerViewModel Create(IReadOnlyList<TrackRecord> records) =>
        new(new StubLibrary(records), new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

    private static TrackRecord Track(
        string path,
        string artist,
        string album,
        CodecType codec = CodecType.Lossless,
        string title = "Track",
        int? track = 1) => new()
        {
            Path = path,
            AlbumArtist = artist,
            HasAlbumArtist = true,
            Album = album,
            StrippedAlbum = album,
            Title = title,
            TrackNumber = track,
            TrackTotal = 1,
            CodecType = codec,
            CodecName = codec == CodecType.Lossy ? "MP3" : "FLAC",
        };

    private static SimilarArtistGroup SimilarGroup() => new(
    [
        new ArtistVariant("Canonical", [@"C:\one.flac", @"C:\two.flac", @"C:\three.flac"]),
        new ArtistVariant("Canoncial", [@"C:\four.flac", @"C:\five.flac"]),
    ]);

    private sealed class StubLibrary(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile>? artwork = null,
        IReadOnlyDictionary<string, byte[]>? images = null) : ILibraryService
    {
        private IReadOnlyList<ArtworkAuditFile> _artwork = artwork ?? [];
        public IReadOnlyList<string>? ArtworkPaths { get; private set; }
        public IReadOnlyList<ArtworkAuditFile>? ArtworkAfterSignatureRead { get; init; }
        public bool IsReady => true;
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult(records);
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            Task.FromResult(new AnalysisReport("Cross-set check", []));
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(images is not null && images.TryGetValue(path, out byte[]? data)
                ? data
                : null);
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) => images is null
            ? throw new NotSupportedException()
            : Task.FromResult<IReadOnlyList<byte[]?>>(paths.Select(path =>
                images.TryGetValue(path, out byte[]? data) ? data : null).ToArray());
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            ArtworkPaths = paths;
            if (ArtworkAfterSignatureRead is not null)
                _artwork = ArtworkAfterSignatureRead;
            return Task.FromResult<IReadOnlyList<string>>(
                Enumerable.Repeat("same-cover", paths.Count).ToList());
        }
        public Task<IReadOnlyList<ArtworkAuditFile>> GetArtworkAuditFilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult(_artwork);
    }

    private sealed class RecordingArtworkService : IArtworkService
    {
        public List<string> SavedPaths { get; } = [];
        public List<IReadOnlyList<ArtworkInput>> SavedImages { get; } = [];
        public List<int> PrepareDimensions { get; } = [];
        public bool SupportsWrite(string musicPath) => true;
        public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0,
            int quality = 90, CancellationToken ct = default)
        {
            PrepareDimensions.Add(maxDimension);
            return Task.FromResult<PreparedImage?>(
                new PreparedImage(data, "image/jpeg", maxDimension, maxDimension));
        }
        public Task<ArtworkOpResult> SaveImagesAsync(string musicPath,
            IReadOnlyList<ArtworkInput> images, CancellationToken ct = default)
        {
            SavedPaths.Add(musicPath);
            SavedImages.Add(images);
            return Task.FromResult(new ArtworkOpResult { Success = true });
        }
        public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath,
            int maxDimension = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension,
            int quality = 90, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> RemoveAsync(string musicPath,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PreparedImage?> PrepareFromFileAsync(string imagePath,
            int maxDimension = 0, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubReconciler : IArtistReconciler
    {
        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2, CancellationToken ct = default) => [];
        public Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class RecordingArtistReconciler(SimilarArtistGroup group) : IArtistReconciler
    {
        public double? Threshold { get; private set; }
        public List<(IReadOnlyList<string> Paths, string From, string To)> Calls { get; } = [];

        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2,
            CancellationToken ct = default)
        {
            Threshold = threshold;
            return [group];
        }

        public Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Calls.Add((paths, from, to));
            return Task.FromResult(paths.Count);
        }
    }

    private sealed class BlockingArtistReconciler(SimilarArtistGroup group) : IArtistReconciler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2,
            CancellationToken ct = default) => [group];

        public async Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }

    private sealed class RecordingDialogs(bool result) : IDialogCoordinator
    {
        public string? Message { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string primaryText)
        {
            Message = message;
            return Task.FromResult(result);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class TrackingDecodedAudio : IDecodedAudioVerificationService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<DecodedAudioPair>? Pairs { get; private set; }
        public Task<AnalysisReport> VerifyAsync(string ffmpegExecutable,
            IReadOnlyList<DecodedAudioPair> pairs, IProgress<DecodedAudioProgress>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            Pairs = pairs;
            return Task.FromResult(new AnalysisReport("Decoded-audio verification", []));
        }
    }

    private sealed class StubRepresentationRepairs(RepresentationRepairPreview preview)
        : IRepresentationRepairService
    {
        public LibraryConfiguration? Configuration { get; private set; }
        public SynchronizationContext? PreviewSynchronizationContext { get; private set; }
        public IReadOnlyList<RepresentationRepairAction>? AppliedActions { get; private set; }
        public Task<RepresentationRepairPreview> PreviewAsync(IReadOnlyList<TrackRecord> records,
            LibraryConfiguration? configuration, CancellationToken ct = default)
        {
            Configuration = configuration;
            PreviewSynchronizationContext = SynchronizationContext.Current;
            return Task.FromResult(preview);
        }

        public Task<RepresentationRepairApplyResult> ApplyAsync(
            IReadOnlyList<RepresentationRepairAction> actions,
            LibraryConfiguration? configuration,
            IProgress<RepresentationRepairProgress>? progress = null,
            CancellationToken ct = default)
        {
            AppliedActions = actions;
            Configuration = configuration;
            return Task.FromResult(new RepresentationRepairApplyResult(
                actions.Select(action => new RepresentationRepairActionResult(
                    action, RepresentationRepairOutcome.Applied)).ToList()));
        }
    }

    private sealed class StubRepairs : IAnalysisRepairService
    {
        public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records) =>
            new("Safe metadata repairs", []);
        public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records) =>
            new("Fill missing album artists", []);
        public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records) =>
            new("Repair numbering and totals", []);
        public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records) =>
            new("Normalize metadata text", []);
        public AnalysisRepairPlan PreviewMultiDiscAlbumNames(IReadOnlyList<TrackRecord> records) =>
            new("Normalize multi-disc album names", []);
        public AnalysisRepairPlan PreviewId3VersionUpgrades(IReadOnlyList<TrackRecord> records) =>
            new("Upgrade ID3 tags", []);
        public IReadOnlyList<AnalysisTagConflict> FindAlbumArtistConflicts(IReadOnlyList<TrackRecord> records) => [];
        public AnalysisRepairPlan PreviewConflictRepairs(IReadOnlyList<AnalysisConflictResolution> resolutions) =>
            new("Resolve album artist conflicts", []);
        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan, IProgress<int>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new BatchWriteResult([]));
    }

    private sealed class TrackingRepairs(AnalysisRepairPlan plan) : IAnalysisRepairService
    {
        public int SafePreviewCalls { get; private set; }
        public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records)
        {
            SafePreviewCalls++;
            return plan;
        }
        public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewMultiDiscAlbumNames(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewId3VersionUpgrades(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public IReadOnlyList<AnalysisTagConflict> FindAlbumArtistConflicts(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewConflictRepairs(IReadOnlyList<AnalysisConflictResolution> resolutions) =>
            throw new NotSupportedException();
        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan, IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingConflictRepairs(AnalysisTagConflict conflict) : IAnalysisRepairService
    {
        public IReadOnlyList<AnalysisConflictResolution>? Resolutions { get; private set; }

        public IReadOnlyList<AnalysisTagConflict> FindAlbumArtistConflicts(IReadOnlyList<TrackRecord> records) =>
            [conflict];

        public AnalysisRepairPlan PreviewConflictRepairs(IReadOnlyList<AnalysisConflictResolution> resolutions)
        {
            Resolutions = resolutions;
            var resolution = Assert.Single(resolutions);
            var target = resolution.Conflict.Targets[0];
            return new AnalysisRepairPlan("Resolve album artist conflicts", [
                new(target.Path, TagFields.AlbumArtist, target.Before, resolution.SelectedValue,
                    "User selected", target.SourceLength, target.SourceLastWriteTimeUtc),
            ]);
        }

        public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewMultiDiscAlbumNames(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewId3VersionUpgrades(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan, IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
