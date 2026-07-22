using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryPathLayoutResolverTests
{
    private static readonly LibraryPathMetadata Track = new(
        "Track Artist",
        "Album Artist",
        "Album (HiRes)",
        "A $ong",
        3,
        2,
        false,
        "2024-01-02",
        "original",
        ".flac");

    [Fact]
    public void LegacyProfileRetainsHistoricalNamingRules()
    {
        LibraryProfile profile = LibraryProfilePresets.Create(
            LibraryProfilePreset.LegacyMusicLibraryTools);

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track, 255, 255);

        Assert.Equal(Path.GetFullPath(Path.Combine(
            "library", "Album Artist", "Album", "03 A song.flac")), path);
    }

    [Fact]
    public void LegacyProfileUsesItsIndependentMigratedNamingLimits()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.LegacyMusicLibraryTools);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with
            {
                ComponentLengthLimit = 12,
                DiscAlbumLengthLimit = 8,
            },
        };
        LibraryPathMetadata track = Track with
        {
            AlbumArtist = "Very Long Album Artist",
            Album = "Very Long Album Name (Disc 2)",
            Title = "Very Long Track Title",
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, track, 255, 255);

        Assert.Equal(Path.GetFullPath(Path.Combine(
            "library", "Very Long Al", "Very Lon (Disc 2)",
            "03 Very Long Tr.flac")), path);
    }

    [Fact]
    public void ItunesNamingAppliesAlbumSuffixWithoutAlsoAddingDiscFilePrefix()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ItunesMedia);
        LibraryProfile profile = baseline with
        {
            Disc = baseline.Disc with { Strategy = LibraryDiscStrategy.AlbumSuffix },
        };
        LibraryPathMetadata metadata = Track with
        {
            Album = "Some Album",
            Title = "Some Track",
            TrackNumber = 3,
            DiscNumber = 2,
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, metadata, 255, 255);

        Assert.Equal("Some Album (Disc 2)",
            Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal("03 Some Track.flac", Path.GetFileName(path));
    }

    [Fact]
    public void ItunesNamingHonorsDiscFolderAsTheOnlyPathRepresentation()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ItunesMedia);
        LibraryProfile profile = baseline with
        {
            Disc = baseline.Disc with { Strategy = LibraryDiscStrategy.DiscFolder },
        };
        LibraryPathMetadata metadata = Track with
        {
            Album = "Some Album",
            Title = "Some Track",
            TrackNumber = 3,
            DiscNumber = 2,
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, metadata, 255, 255);

        Assert.Equal("Disc 2", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal("Some Album", Path.GetFileName(
            Path.GetDirectoryName(Path.GetDirectoryName(path))));
        Assert.Equal("03 Some Track.flac", Path.GetFileName(path));
    }

    [Fact]
    public void GenericTemplatePreservesEditionUnicodeAndOptionalYear()
    {
        LibraryProfile profile = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum) with
        {
            Naming = LibraryProfilePresets.Create(LibraryProfilePreset.ArtistAlbum).Naming with
            {
                DirectoryTemplate = "{AlbumArtist}/[{Year} - ]{Album}",
                FileNameTemplate = "{Track:000} {Title}{Extension}",
            },
        };
        LibraryPathMetadata track = Track with { Title = "Déjà vu" };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, track, 255, 255);

        Assert.Equal(Path.GetFullPath(Path.Combine(
            "library", "Album Artist", "2024 - Album (HiRes)", "003 Déjà vu.flac")), path);
    }

    [Fact]
    public void GenreTokenUsesIndexedGenre()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with
            {
                DirectoryTemplate = "{Genre}/{AlbumArtist}/{Album}",
            },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track with { Genre = "Jazz" }, 255, 255);

        Assert.Contains(Path.Combine("Jazz", "Album Artist", "Album (HiRes)"), path);
    }

    [Fact]
    public void DiscFolderPolicyAddsFolderWithoutChangingAlbumValue()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Disc = baseline.Disc with { Strategy = LibraryDiscStrategy.DiscFolder },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track, 255, 255);

        Assert.Contains(Path.Combine("Album (HiRes)", "Disc 2"), path);
    }

    [Fact]
    public void FlattenContinuousPolicyUsesAlbumContextAcrossUnequalDiscLengths()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Disc = baseline.Disc with { Strategy = LibraryDiscStrategy.FlattenContinuous },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track with
            {
                TrackTotal = 10,
                FlattenedTrackNumber = 8,
            }, 255, 255);

        Assert.EndsWith(Path.Combine("Album (HiRes)", "08 A $ong.flac"), path);
    }

    [Fact]
    public void ContinuousTrackProjectionDoesNotAssumeEqualDiscLengths()
    {
        var tracks = new[]
        {
            (Path: "d1t1", Album: "album", Disc: (int?)1, Track: (int?)1),
            (Path: "d1t2", Album: "album", Disc: (int?)1, Track: (int?)2),
            (Path: "d2t1", Album: "album", Disc: (int?)2, Track: (int?)1),
            (Path: "d2t2", Album: "album", Disc: (int?)2, Track: (int?)2),
            (Path: "d2t3", Album: "album", Disc: (int?)2, Track: (int?)3),
        };

        IReadOnlyDictionary<string, int> result =
            LibraryAlbumIdentityResolver.ContinuousTrackNumbers(
                tracks, item => item.Album, item => item.Path,
                item => item.Disc, item => item.Track);

        Assert.Equal(3, result["d2t1"]);
        Assert.Equal(5, result["d2t3"]);
    }

    [Fact]
    public void TokenSlashesAreSanitizedInsteadOfCreatingDirectories()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with
            {
                DirectoryTemplate = "{AlbumArtist}/{Album}",
                FileNameTemplate = "{Track:00} {Title}{Extension}",
            },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track with
            {
                AlbumArtist = "AC/DC",
                Album = "Live/Studio",
                Title = "A/B",
            }, 255, 255);

        Assert.Equal(Path.GetFullPath(Path.Combine(
            "library", "AC_DC", "Live_Studio", "03 A_B.flac")), path);
    }

    [Fact]
    public void StopCollisionPolicyProducesActionableFailure()
    {
        LibraryProfile profile = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LibraryPathLayoutResolver.Shared.ResolveCollision(
                Path.GetFullPath("track.flac"), Path.GetFullPath("source.flac"), profile, 2));

        Assert.Contains(profile.Name, error.Message);
    }

    [Fact]
    public void GenericNamingUsesConfiguredMissingValueAndCompilationFallbacks()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with
            {
                DirectoryTemplate = "{Compilation}/{AlbumArtist}/{Album}",
                MissingArtistFallback = "Uncredited",
                MissingAlbumFallback = "Loose tracks",
                MissingTitleFallback = "No title",
                CompilationValue = "Various Artists",
            },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track with
            {
                Artist = "",
                AlbumArtist = null,
                Album = "",
                Title = "",
                Compilation = true,
            }, 255, 255);

        Assert.Equal(Path.GetFullPath(Path.Combine("library", "Various Artists",
            "Uncredited", "Loose tracks", "03 No title.flac")), path);
    }

    [Fact]
    public void GenericNamingAppliesUnicodeNormalizationAndComponentLimit()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with
            {
                UnicodeNormalization = LibraryUnicodeNormalization.FormC,
                ComponentLengthLimit = 16,
            },
        };

        string path = LibraryPathLayoutResolver.Shared.Resolve(
            "library", profile, Track with { Title = "Cafe\u0301 extended" }, 255, 255);

        Assert.Contains("Caf\u00e9", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.True(Path.GetFileName(path).Length <= 16);
    }

    [Fact]
    public void GenericNamingRejectsPathsOverProfileCompletePathLimit()
    {
        LibraryProfile baseline = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum);
        LibraryProfile profile = baseline with
        {
            Naming = baseline.Naming with { CompletePathLengthLimit = 16 },
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LibraryPathLayoutResolver.Shared.Resolve(
                "library", profile, Track, 255, 255));

        Assert.Contains("complete-path limit", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
