using System.Collections.Immutable;
using System.Xml.Linq;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ExportProfileConfigurationTests
{
    [Fact]
    public void EditableConfigurationRoundTripsEveryExportPolicy()
    {
        string path = TemporaryConfigurationPath();
        try
        {
            LibraryExportProfile expected = ConfiguredProfile();
            var editable = EditableLibraryConfig.CreateNew();
            editable.ExportProfiles.Add(expected);

            editable.Save(path);

            LibraryExportProfile readOnly = Assert.Single(
                new LibraryConfiguration(path).ExportProfiles);
            LibraryExportProfile reloaded = Assert.Single(
                EditableLibraryConfig.Load(path).ExportProfiles);
            Assert.Equal(expected.Fingerprint, readOnly.Fingerprint);
            Assert.Equal(expected.Fingerprint, reloaded.Fingerprint);
            Assert.True(readOnly.IsVisible);
            Assert.Equal(["Favorites", "Road Trip"], readOnly.Selection.Values);
            Assert.Equal("usb-123", readOnly.Transport.Options["serial"]);
            Assert.Equal(ExportExtraFileDisposition.Quarantine,
                readOnly.Reconciliation.ExtraFiles);
        }
        finally
        {
            DeleteConfiguration(path);
        }
    }

    [Fact]
    public void ExportProfileUnknownXmlSurvivesEditableRoundTrip()
    {
        string path = TemporaryConfigurationPath();
        try
        {
            var editable = EditableLibraryConfig.CreateNew();
            editable.ExportProfiles.Add(ConfiguredProfile());
            editable.Save(path);

            XDocument document = XDocument.Load(path);
            XElement profile = Assert.Single(document.Root!.Elements("ExportProfile"));
            profile.SetAttributeValue("FutureVersion", "3");
            profile.Element("Selection")!.SetAttributeValue("FutureSelection", "yes");
            profile.Element("Selection")!.Elements("Value").First()
                .SetAttributeValue("ExternalId", "playlist-1");
            profile.Element("Transport")!.Add(
                new XElement("FutureCredentials", new XAttribute("Mode", "vault")));
            profile.Add(new XElement("FutureExportPolicy", new XAttribute("Enabled", true)));
            document.Save(path);

            EditableLibraryConfig.Load(path).Save(path);

            XElement saved = Assert.Single(
                XDocument.Load(path).Root!.Elements("ExportProfile"));
            Assert.Equal("3", (string?)saved.Attribute("FutureVersion"));
            Assert.Equal("yes",
                (string?)saved.Element("Selection")!.Attribute("FutureSelection"));
            Assert.Equal("playlist-1", (string?)saved.Element("Selection")!
                .Elements("Value").First().Attribute("ExternalId"));
            Assert.Equal("vault", (string?)saved.Element("Transport")!
                .Element("FutureCredentials")!.Attribute("Mode"));
            Assert.Equal("true",
                (string?)saved.Element("FutureExportPolicy")!.Attribute("Enabled"));
        }
        finally
        {
            DeleteConfiguration(path);
        }
    }

    [Fact]
    public void NewConfigurationDoesNotImplicitlyEnableSpecializedExports()
    {
        string path = TemporaryConfigurationPath();
        try
        {
            var editable = EditableLibraryConfig.CreateNew();

            Assert.Empty(editable.ExportProfiles);
            editable.Save(path);

            Assert.Empty(new LibraryConfiguration(path).ExportProfiles);
            Assert.All(BuiltInExportProfiles.All, profile =>
            {
                Assert.False(profile.Enabled);
                Assert.False(profile.IsVisible);
            });
        }
        finally
        {
            DeleteConfiguration(path);
        }
    }

    [Fact]
    public void ValidationRejectsDuplicateAndDanglingExportReferences()
    {
        var editable = EditableLibraryConfig.CreateNew();
        LibraryExportProfile profile = ConfiguredProfile() with
        {
            Naming = new(LibraryProfileId: "missing-profile"),
            Transform = new(ExportTransformMode.Transcode, RecipeId: "missing-recipe"),
        };
        editable.ExportProfiles.Add(profile);
        editable.ExportProfiles.Add(profile with { Name = "Duplicate" });

        IReadOnlyList<LibraryConfigurationIssue> issues = editable.Validate();

        Assert.Contains(issues, issue => issue.Code == "export-profile-duplicate");
        Assert.Contains(issues, issue => issue.Code == "export-naming-profile");
        Assert.Contains(issues, issue => issue.Code == "export-transform-recipe");
    }

    [Fact]
    public void EnabledExportRequiresConfiguredTransport()
    {
        var editable = EditableLibraryConfig.CreateNew();
        editable.ExportProfiles.Add(ConfiguredProfile() with
        {
            Transport = new(LocalFileSystemExportTransport.ProviderId, ""),
        });

        LibraryConfigurationIssue issue = Assert.Single(editable.Validate(), candidate =>
            candidate.Code == "export-profile-invalid");
        Assert.Contains("requires a transport provider and destination", issue.Message);
    }

    [Fact]
    public void ExportPolicyChangesInvalidateLibraryPolicySnapshot()
    {
        string firstPath = TemporaryConfigurationPath();
        string secondPath = TemporaryConfigurationPath();
        try
        {
            var first = EditableLibraryConfig.CreateNew();
            first.ExportProfiles.Add(ConfiguredProfile());
            first.Save(firstPath);
            var second = EditableLibraryConfig.CreateNew();
            second.LibraryId = first.LibraryId;
            second.ExportProfiles.Add(ConfiguredProfile() with
            {
                Reconciliation = ConfiguredProfile().Reconciliation with
                {
                    MaximumRemovals = 101,
                },
            });
            second.Save(secondPath);

            Assert.NotEqual(new LibraryConfiguration(firstPath).PolicySnapshot.Fingerprint,
                new LibraryConfiguration(secondPath).PolicySnapshot.Fingerprint);
        }
        finally
        {
            DeleteConfiguration(firstPath);
            DeleteConfiguration(secondPath);
        }
    }

    [Fact]
    public void ExportProfileRejectsUndefinedOptionalCollisionPolicy()
    {
        LibraryExportProfile profile = ConfiguredProfile() with
        {
            Naming = ConfiguredProfile().Naming with
            {
                CollisionPolicy = (LibraryPathCollisionPolicy)int.MaxValue,
            },
        };

        Assert.Throws<InvalidDataException>(() =>
            LibraryExportProfileXml.Write(profile));
    }

    [Fact]
    public void ConfigurationSaveRejectsInvalidSelfContainedExportNamingTemplate()
    {
        string path = TemporaryConfigurationPath();
        try
        {
            var editable = EditableLibraryConfig.CreateNew();
            editable.ExportProfiles.Add(ConfiguredProfile() with
            {
                Naming = new(
                    FolderTemplate: "{AlbumArtist}/{UnknownToken}",
                    FileNameTemplate: "{Track:00} {Title}{Extension}"),
            });

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                editable.Save(path));

            Assert.Contains("unknown naming token", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteConfiguration(path);
        }
    }

    [Fact]
    public void MachineBindingsKeepExportDestinationAndOptionsOutOfPortablePolicy()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "export-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string configurationPath = Path.Combine(directory, "library.xml");
        string bindingsPath = Path.Combine(directory, "machine.xml");
        string destination = Path.Combine(directory, "portable-device");
        try
        {
            EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
            editable.MachineBindingsFile = "machine.xml";
            LibraryExportProfile profile = ConfiguredProfile() with
            {
                Transport = ConfiguredProfile().Transport with
                {
                    Destination = destination,
                },
            };
            editable.ExportProfiles.Add(profile);

            editable.Save(configurationPath);

            XElement portableTransport = XDocument.Load(configurationPath).Root!
                .Element("ExportProfile")!.Element("Transport")!;
            Assert.Null(portableTransport.Attribute("Destination"));
            Assert.Empty(portableTransport.Elements("Option"));
            XElement binding = Assert.Single(XDocument.Load(bindingsPath).Root!
                .Elements("ExportBinding"));
            Assert.Equal(profile.Id, (string?)binding.Attribute("ProfileId"));
            Assert.Equal(destination, (string?)binding.Attribute("Destination"));
            Assert.Equal("usb-123", (string?)Assert.Single(binding.Elements("Option"))
                .Attribute("Value"));

            LibraryExportProfile loaded = Assert.Single(
                new LibraryConfiguration(configurationPath).ExportProfiles);
            Assert.Equal(Path.GetFullPath(destination), loaded.Transport.Destination);
            Assert.Equal("usb-123", loaded.Transport.Options["serial"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyPolicySnapshotRejectsDanglingExportReferences()
    {
        string path = TemporaryConfigurationPath();
        try
        {
            var editable = EditableLibraryConfig.CreateNew();
            editable.ExportProfiles.Add(ConfiguredProfile());
            editable.Save(path);
            XDocument document = XDocument.Load(path);
            document.Root!.Element("ExportProfile")!.Element("Naming")!
                .SetAttributeValue("LibraryProfileId", "missing-profile");
            document.Save(path);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                new LibraryConfiguration(path).PolicySnapshot);
            Assert.Contains("unknown library profile", error.Message);
        }
        finally
        {
            DeleteConfiguration(path);
        }
    }

    private static LibraryExportProfile ConfiguredProfile() => new(
        "portable-car",
        "Portable car library",
        true,
        ExportSelectionPolicy.FromPlaylists(["Road Trip", "Favorites"]),
        new(ExportTransformMode.Copy),
        new(LibraryProfileId: LibraryProfilePresets.ArtistAlbumId,
            CollisionPolicy: LibraryPathCollisionPolicy.Hash),
        new(ExportArtworkMode.EmbeddedAndSidecar, FrontCoverOnly: false,
            PreserveEncoding: false, MaximumDimension: 1_200, MaximumBytes: 512_000),
        new(Enabled: true, Format: "m3u8", RelativePaths: true,
            IncludeExtendedInfo: true, EncodingName: "utf-8", WriteByteOrderMark: false,
            LineEnding: "lf", MaximumTracks: 5_000),
        new(LocalFileSystemExportTransport.ProviderId, @"D:\Music",
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("serial", "usb-123")),
        new(ExportExtraFileDisposition.Quarantine, ReplaceChangedFiles: true,
            RemoveEmptyDirectories: true, MaximumRemovals: 100));

    private static string TemporaryConfigurationPath() => Path.Combine(
        Path.GetTempPath(), "export-profile-" + Guid.NewGuid().ToString("N") + ".xml");

    private static void DeleteConfiguration(string path)
    {
        File.Delete(path);
        File.Delete(path + ".v1.bak");
    }
}
