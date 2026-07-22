using System.Xml.Linq;
using MusicLibraryTools;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkSidecarConfigurationTests
{
    [Fact]
    public void EditableConfigurationRoundTripDoesNotDuplicatePolicyElements()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "art-sidecar-config-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            EditableLibraryConfig initial = EditableLibraryConfig.CreateNew();
            LibraryProfile custom = LibraryProfilePresets.Create(
                LibraryProfilePreset.PreserveLayoutAndTags, "art-policy", "Artwork policy") with
            {
                Artwork = new(
                    LibraryArtworkStorage.Both,
                    LibraryArtworkRoleSelection.AllRoles,
                    LibraryArtworkEncoding.PreserveSource,
                    1600,
                    1_500_000,
                    88,
                    "scan-{Role}{Extension}"),
                Sidecars = new(
                    LibrarySidecarDisposition.Preserve,
                    [new("cue", "Cue", true, ["*.cue"],
                        LibrarySidecarDisposition.Quarantine)]),
            };
            initial.Profiles.Add(custom);
            initial.ActiveProfileId = custom.Id;
            initial.Save(path);

            EditableLibraryConfig.Load(path).Save(path);

            XDocument saved = XDocument.Load(path);
            XElement profile = Assert.Single(saved.Root!.Elements("LibraryProfile"),
                item => (string?)item.Attribute("Id") == custom.Id);
            Assert.Single(profile.Elements("Artwork"));
            Assert.Single(profile.Elements("Sidecars"));
            LibraryProfile loaded = new LibraryConfiguration(path).ActiveProfile;
            Assert.Equal(custom.Artwork, loaded.Artwork);
            Assert.Equal(LibrarySidecarDisposition.Quarantine,
                loaded.Sidecars.ResolveDisposition(
                    "album.cue", LibrarySourceDisposition.Preserve));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
