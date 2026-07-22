using System.Xml.Linq;
using MusicLibraryTools;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class LibraryArtworkAndSidecarPolicyTests
{
    [Fact]
    public void GenericPresetsPreserveUnknownAndKnownSidecars()
    {
        foreach (LibraryProfile profile in LibraryProfilePresets.All.Where(profile =>
                     profile.Preset != LibraryProfilePreset.LegacyMusicLibraryTools))
        {
            Assert.Equal(LibrarySidecarDisposition.Preserve,
                profile.Sidecars.ResolveDisposition(
                    "unknown/vendor-data.bin", LibrarySourceDisposition.Delete));
            Assert.Equal(LibrarySidecarDisposition.Preserve,
                profile.Sidecars.ResolveDisposition(
                    "booklet.pdf", LibrarySourceDisposition.Delete));
            Assert.Equal(LibrarySidecarDisposition.Preserve,
                profile.Sidecars.ResolveDisposition(
                    "covers/front.png", LibrarySourceDisposition.Delete));
        }
    }

    [Theory]
    [InlineData(LibrarySourceDisposition.Quarantine, LibrarySidecarDisposition.Quarantine)]
    [InlineData(LibrarySourceDisposition.Delete, LibrarySidecarDisposition.Delete)]
    [InlineData(LibrarySourceDisposition.Preserve, LibrarySidecarDisposition.Preserve)]
    public void LegacySidecarsFollowExistingSourceDisposition(
        LibrarySourceDisposition sourceDisposition,
        LibrarySidecarDisposition expected)
    {
        LibraryProfile legacy = LibraryProfilePresets.Create(
            LibraryProfilePreset.LegacyMusicLibraryTools);

        Assert.Equal(expected,
            legacy.Sidecars.ResolveDisposition("unknown.bin", sourceDisposition));
        Assert.Equal(expected,
            legacy.Sidecars.ResolveDisposition("album.cue", sourceDisposition));
    }

    [Fact]
    public void ArtworkAndSidecarPoliciesRoundTripThroughVersionTwoXml()
    {
        LibraryProfile original = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum, "custom-art", "Custom artwork") with
        {
            Metadata = new(
                PreserveReplayGain: false,
                PreserveMusicBrainzIdentifiers: true,
                PreserveCustomFields: false,
                PreserveCompilationSemantics: true),
            Artwork = new(
                LibraryArtworkStorage.Both,
                LibraryArtworkRoleSelection.FrontCoverOnly,
                LibraryArtworkEncoding.Png,
                1200,
                900_000,
                82,
                "album-{Role}{Extension}"),
            Sidecars = new(
                LibrarySidecarDisposition.Preserve,
                [
                    new("cue", "Cue sheets", true, ["*.cue"],
                        LibrarySidecarDisposition.Quarantine),
                    new("vendor", "Vendor metadata", true, ["metadata/*.json"],
                        LibrarySidecarDisposition.Delete),
                ]),
        };

        XElement xml = LibraryProfileXml.Write(original);
        LibraryProfile loaded = LibraryProfileXml.Parse(xml);

        Assert.Equal(original.Metadata, loaded.Metadata);
        Assert.Equal(original.Artwork, loaded.Artwork);
        Assert.Equal(original.Sidecars.UnknownFileDisposition,
            loaded.Sidecars.UnknownFileDisposition);
        Assert.Equal(original.Sidecars.Rules.Select(rule =>
                (rule.Id, rule.Name, rule.Enabled, Patterns: string.Join(',', rule.Patterns),
                    rule.Disposition)),
            loaded.Sidecars.Rules.Select(rule =>
                (rule.Id, rule.Name, rule.Enabled, Patterns: string.Join(',', rule.Patterns),
                    rule.Disposition)));
        Assert.Equal(LibrarySidecarDisposition.Quarantine,
            loaded.Sidecars.ResolveDisposition(
                "disc.cue", LibrarySourceDisposition.Preserve));
        Assert.Equal(LibrarySidecarDisposition.Delete,
            loaded.Sidecars.ResolveDisposition(
                "metadata/source.json", LibrarySourceDisposition.Preserve));
        Assert.Equal(LibrarySidecarDisposition.Preserve,
            loaded.Sidecars.ResolveDisposition(
                "metadata/unknown.bin", LibrarySourceDisposition.Delete));
    }

    [Fact]
    public void ProfileWriterRejectsUndefinedPolicyEnums()
    {
        LibraryProfile original = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum, "invalid-enums", "Invalid enums");
        LibraryProfile[] invalidProfiles =
        [
            original with
            {
                Naming = original.Naming with
                {
                    CollisionPolicy = (LibraryPathCollisionPolicy)int.MaxValue,
                },
            },
            original with
            {
                Disc = original.Disc with
                {
                    Strategy = (LibraryDiscStrategy)int.MaxValue,
                },
            },
            original with
            {
                Artwork = original.Artwork with
                {
                    Storage = (LibraryArtworkStorage)int.MaxValue,
                },
            },
            original with
            {
                Sidecars = original.Sidecars with
                {
                    UnknownFileDisposition =
                        (LibrarySidecarDisposition)int.MaxValue,
                },
            },
        ];

        foreach (LibraryProfile profile in invalidProfiles)
            Assert.Throws<InvalidDataException>(() => LibraryProfileXml.Write(profile));
    }
}
