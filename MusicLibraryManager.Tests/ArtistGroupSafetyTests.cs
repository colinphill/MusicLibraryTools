using MusicLibrary.Core.Models;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ArtistGroupSafetyTests
{
    [Fact]
    public void Similar_artist_variants_expose_their_files_and_folders_for_review()
    {
        string firstFolder = Path.Combine("Music", "First");
        string secondFolder = Path.Combine("Music", "Second");
        var viewModel = new ArtistGroupViewModel(new SimilarArtistGroup(
        [
            new ArtistVariant("Canonical",
                [Path.Combine(firstFolder, "one.flac"), Path.Combine(firstFolder, "two.flac")]),
            new ArtistVariant("Canoncial",
                [Path.Combine(secondFolder, "three.flac")]),
        ]));

        ArtistVariantViewModel canonical = viewModel.Variants[0];
        ArtistVariantViewModel typo = viewModel.Variants[1];
        Assert.True(canonical.IsCanonical);
        Assert.False(canonical.CanChangeDisposition);
        Assert.False(typo.IsCanonical);
        Assert.Equal([firstFolder], canonical.Folders.Select(folder => folder.Path));
        Assert.Equal(["one.flac", "two.flac"], canonical.Files.Select(file => file.Name));
        Assert.Equal([secondFolder], typo.Folders.Select(folder => folder.Path));
    }

    [Fact]
    public void Similar_artist_cluster_propagates_dispositions_only_to_noncanonical_variants()
    {
        var viewModel = new ArtistGroupViewModel(Group());
        ArtistVariantViewModel canonical = viewModel.Variants[0];
        ArtistVariantViewModel typo = viewModel.Variants[1];

        viewModel.Disposition = AnalysisRepairDisposition.Active;

        Assert.Equal(AnalysisRepairDisposition.Ignored, canonical.Disposition);
        Assert.Equal(AnalysisRepairDisposition.Active, typo.Disposition);
        Assert.Equal(AnalysisRepairDisposition.Active, viewModel.Disposition);
        Assert.Equal(1, viewModel.ActiveCount);
        Assert.Equal(2, viewModel.ActiveTrackCount);

        typo.Disposition = AnalysisRepairDisposition.Filter;

        AnalysisRunViewModel run = AnalysisRunViewModel.ForArtists(
            "Similar artists", [viewModel], "Review variants");
        Assert.Equal(typo.Files.Select(file => file.Path), run.FilteredPaths);
        Assert.True(run.ClearFilterDispositions());
        Assert.Equal(AnalysisRepairDisposition.Ignored, typo.Disposition);
    }

    [Fact]
    public void Changing_the_canonical_name_updates_which_variant_is_actionable()
    {
        var viewModel = new ArtistGroupViewModel(Group());

        viewModel.CanonicalName = "Canoncial";

        Assert.False(viewModel.Variants[0].IsCanonical);
        Assert.True(viewModel.Variants[0].CanChangeDisposition);
        Assert.True(viewModel.Variants[1].IsCanonical);
        Assert.False(viewModel.Variants[1].CanChangeDisposition);
    }

    private static SimilarArtistGroup Group() => new(
    [
        new ArtistVariant("Canonical", [@"C:\one.flac", @"C:\two.flac", @"C:\three.flac"]),
        new ArtistVariant("Canoncial", [@"C:\four.flac", @"C:\five.flac"]),
    ]);
}
