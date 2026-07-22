using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using System.Xml.Linq;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class EditableLibraryConfigTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var config = new EditableLibraryConfig
            {
                DatabaseFile = "mycache.db",
                ItunesLibraryPath = @"C:\Music\iTunes Library.itl",
                FfmpegPath = @"C:\ffmpeg\ffmpeg.exe",
                WavpackPath = @"C:\wavpack\wavpack.exe",
                LengthLimit = 200,
                DiscNumLengthLimit = 180,
                AacEncoder = "aac-test",
                AacBitrateKbps = 320,
                OversizedArtworkByteThreshold = 3 * 1024 * 1024,
                OversizedArtworkDimensionThreshold = 2_400,
                ArtworkRepairTargetByteSize = 180 * 1024,
                ArtworkRepairTargetDimension = 720,
                DeleteSourcesAfterIngest = true,
                RemoveNonMusicAfterIngest = true,
                DeleteStaleCrossSyncFiles = true,
                CleanCrossSyncPlaylists = true,
                SyncPlaylists = ["Favorites", "Road Trip"],
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = @"Z:\FLAC", DefaultOffset = "/Music", Filter = "*.flac",
                        Organize = false,
                        IngestRole = LibraryIngestRole.Cd,
                        IsSyncTarget = true,
                        UseItunesCanonicalNaming = true,
                        Memberships =
                        [
                            new() { Name = "Lossless" },
                            new() { Name = "Car3", Offset = "/Car/FLAC" },
                        ],
                    },
                    new IndexTargetEntry
                    {
                        Target = @"Z:\HiRes",
                        IngestRole = LibraryIngestRole.HiRes,
                    },
                ],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry { Target = @"Z:\WPL", Type = "wpl", Sets = ["Lossless"] },
                    new PlaylistTargetEntry { Target = @"Z:\Portable", Type = "m3u", Sets = ["Car3"] },
                ],
            };
            config.Save(path);

            var reloaded = EditableLibraryConfig.Load(path);
            Assert.Equal("mycache.db", reloaded.DatabaseFile);
            Assert.Equal(@"C:\Music\iTunes Library.itl", reloaded.ItunesLibraryPath);
            Assert.Equal(@"C:\ffmpeg\ffmpeg.exe", reloaded.FfmpegPath);
            Assert.Equal(@"C:\wavpack\wavpack.exe", reloaded.WavpackPath);
            Assert.Equal(200, reloaded.LengthLimit);
            Assert.Equal(180, reloaded.DiscNumLengthLimit);
            Assert.Equal("aac-test", reloaded.AacEncoder);
            Assert.Equal(320, reloaded.AacBitrateKbps);
            Assert.Equal(3 * 1024 * 1024, reloaded.OversizedArtworkByteThreshold);
            Assert.Equal(2_400, reloaded.OversizedArtworkDimensionThreshold);
            Assert.Equal(180 * 1024, reloaded.ArtworkRepairTargetByteSize);
            Assert.Equal(720, reloaded.ArtworkRepairTargetDimension);
            Assert.True(reloaded.DeleteSourcesAfterIngest);
            Assert.True(reloaded.RemoveNonMusicAfterIngest);
            Assert.True(reloaded.DeleteStaleCrossSyncFiles);
            Assert.True(reloaded.CleanCrossSyncPlaylists);
            Assert.Equal(["Favorites", "Road Trip"], reloaded.SyncPlaylists);
            Assert.Equal(2, reloaded.IndexTargets.Count);
            Assert.Equal(@"Z:\FLAC", reloaded.IndexTargets[0].Target);
            Assert.Equal(["Lossless", "Car3"], reloaded.IndexTargets[0].Memberships.Select(set => set.Name));
            Assert.Equal("/Music", reloaded.IndexTargets[0].DefaultOffset);
            Assert.Null(reloaded.IndexTargets[0].Memberships[0].Offset);
            Assert.Equal("/Car/FLAC", reloaded.IndexTargets[0].Memberships[1].Offset);
            Assert.Empty(reloaded.IndexTargets[1].Memberships);
            Assert.Equal("*.flac", reloaded.IndexTargets[0].Filter);
            Assert.False(reloaded.IndexTargets[0].Organize);
            Assert.True(reloaded.IndexTargets[1].Organize);
            Assert.Equal(LibraryIngestRole.None, reloaded.IndexTargets[0].IngestRole);
            Assert.Equal(LibraryIngestRole.None, reloaded.IndexTargets[1].IngestRole);
            Assert.True(reloaded.IndexTargets[0].IsSyncTarget);
            Assert.False(reloaded.IndexTargets[1].IsSyncTarget);
            Assert.True(reloaded.IndexTargets[0].UseItunesCanonicalNaming);
            Assert.False(reloaded.IndexTargets[1].UseItunesCanonicalNaming);
            Assert.Equal(2, reloaded.PlaylistTargets.Count);
            Assert.Equal(@"Z:\WPL", reloaded.PlaylistTargets[0].Target);
            Assert.Equal("wpl", reloaded.PlaylistTargets[0].Type);
            Assert.Equal(["Lossless"], reloaded.PlaylistTargets[0].Sets);
            Assert.Equal(["Car3"], reloaded.PlaylistTargets[1].Sets);

            var xml = System.Xml.Linq.XDocument.Load(path);
            Assert.Null(xml.Root!.Element("PlaylistType"));
            Assert.Equal(@"Z:\FLAC", (string?)xml.Root.Elements("IndexTarget").First().Attribute("Path"));
            Assert.Equal("false",
                (string?)xml.Root.Elements("IndexTarget").First().Attribute("Organize"));
            Assert.Equal("true",
                (string?)xml.Root.Elements("IndexTarget").First().Attribute("SyncTarget"));
            Assert.Equal("true",
                (string?)xml.Root.Elements("IndexTarget").First()
                    .Attribute("ItunesCanonicalNaming"));
            Assert.Null(xml.Root.Element("SyncTarget"));
            Assert.Equal(["Favorites", "Road Trip"],
                xml.Root.Elements("SyncPlaylist").Select(element => element.Value));
            Assert.Equal("true", (string?)xml.Root.Element("CrossSyncMusicSettings")
                ?.Attribute("DeleteStaleFiles"));
            Assert.Equal("true", (string?)xml.Root.Element("CrossSyncPlaylistsSettings")
                ?.Attribute("Clean"));
            Assert.Equal((3 * 1024 * 1024).ToString(),
                (string?)xml.Root.Element("ArtworkHealthSettings")
                    ?.Attribute("OversizedByteThreshold"));
            Assert.Equal("2400", (string?)xml.Root.Element("ArtworkHealthSettings")
                ?.Attribute("OversizedDimensionThreshold"));
            Assert.Equal((180 * 1024).ToString(),
                (string?)xml.Root.Element("ArtworkHealthSettings")
                    ?.Attribute("RepairTargetByteSize"));
            Assert.Equal("720", (string?)xml.Root.Element("ArtworkHealthSettings")
                ?.Attribute("RepairTargetDimension"));
            Assert.Equal(2, xml.Root.Elements("IndexTarget").First().Elements("Set").Count());
            Assert.All(xml.Root.Elements("PlaylistTarget"), target =>
            {
                Assert.NotNull(target.Attribute("Type"));
                Assert.NotNull(target.Attribute("Set"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WrittenXml_IsReadableByLibraryConfiguration()
    {
        // The whole point is producing a file the real (read-only) parser accepts.
        var path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            new EditableLibraryConfig
            {
                DatabaseFile = "cache.db",
                LengthLimit = 255,
                DiscNumLengthLimit = 255,
                IndexTargets = [new IndexTargetEntry
                {
                    Target = @"Z:\Music",
                    Memberships = [new() { Name = "Desktop2" }, new() { Name = "Car4" }],
                }],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry { Target = @"Z:\Playlists", Type = "wpl", Sets = ["Desktop2", "Car4"] },
                ],
            }.Save(path);

            var config = new LibraryConfiguration(path);
            Assert.Equal("cache.db", config.DatabaseFile);
            Assert.Equal(255, config.LengthLimit);
            var roots = config.IndexLocations.ToList();
            Assert.Single(roots);
            Assert.Equal(@"Z:\Music", roots[0].Target);
            Assert.True(roots[0].Organize);
            Assert.Equal(["Desktop2", "Car4"], roots[0].Sets);
            var playlistTarget = Assert.Single(config.PlaylistTargets);
            Assert.Equal(@"Z:\Playlists", playlistTarget.Target);
            Assert.Equal("wpl", playlistTarget.Type);
            Assert.Equal(["Car4", "Desktop2"], playlistTarget.Sets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyConfigurationWithoutArtworkHealthSettingsUsesCurrentDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
                "</LibraryConfiguration>");

            EditableLibraryConfig editable = EditableLibraryConfig.Load(path);
            var configuration = new LibraryConfiguration(path);

            Assert.Equal(LibraryArtworkHealthSettings.DefaultOversizedByteThreshold,
                editable.OversizedArtworkByteThreshold);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold,
                editable.OversizedArtworkDimensionThreshold);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultOversizedByteThreshold,
                configuration.ArtworkHealthSettings.OversizedByteThreshold);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold,
                configuration.ArtworkHealthSettings.OversizedDimensionThreshold);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetByteSize,
                editable.ArtworkRepairTargetByteSize);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetDimension,
                editable.ArtworkRepairTargetDimension);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetByteSize,
                configuration.ArtworkHealthSettings.RepairTargetByteSize);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetDimension,
                configuration.ArtworkHealthSettings.RepairTargetDimension);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PartialArtworkHealthSettingsDefaultsTheMissingThreshold()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<LibraryConfiguration>" +
                "<ArtworkHealthSettings OversizedDimensionThreshold=\"2500\" />" +
                "</LibraryConfiguration>");

            LibraryArtworkHealthSettings settings =
                new LibraryConfiguration(path).ArtworkHealthSettings;

            Assert.Equal(LibraryArtworkHealthSettings.DefaultOversizedByteThreshold,
                settings.OversizedByteThreshold);
            Assert.Equal(2_500, settings.OversizedDimensionThreshold);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetByteSize,
                settings.RepairTargetByteSize);
            Assert.Equal(LibraryArtworkHealthSettings.DefaultRepairTargetDimension,
                settings.RepairTargetDimension);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyStandaloneSyncTargetMigratesToFlaggedIndexTarget()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path,
                "<LibraryConfiguration>" +
                "<IndexTarget Path=\"source\" />" +
                "<SyncTarget>portable</SyncTarget>" +
                "<SyncPlaylist>Favorites</SyncPlaylist>" +
                "</LibraryConfiguration>");

            EditableLibraryConfig editable = EditableLibraryConfig.Load(path);
            IndexTargetEntry syncTarget =
                Assert.Single(editable.IndexTargets, target => target.IsSyncTarget);
            Assert.Equal("portable", syncTarget.Target);
            Assert.Equal(["Favorites"], editable.SyncPlaylists);

            editable.Save(path);

            var xml = System.Xml.Linq.XDocument.Load(path);
            Assert.Null(xml.Root!.Element("SyncTarget"));
            Assert.Equal("true", (string?)xml.Root.Elements("IndexTarget")
                .Single(element => (string?)element.Attribute("Path") == "portable")
                .Attribute("SyncTarget"));
            var configuration = new LibraryConfiguration(path);
            Assert.Equal("portable", configuration.CrossSyncTargetLibraryPath);
            Assert.Equal(["Favorites"], configuration.SyncPlaylists);
            Assert.False(configuration.DeleteStaleCrossSyncFiles);
            Assert.False(configuration.CleanCrossSyncPlaylists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveRejectsMoreThanOneSyncTarget()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var configuration = new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new() { Target = "first", IsSyncTarget = true },
                    new() { Target = "second", IsSyncTarget = true },
                ],
            };

            InvalidDataException error =
                Assert.Throws<InvalidDataException>(() => configuration.Save(path));
            Assert.Contains("only one", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PartialLegacyIngestRolesMigrateToTheAvailableRecipe()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            string unavailable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cd");
            new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = unavailable,
                        IngestRole = LibraryIngestRole.Cd,
                    },
                ],
            }.Save(path);

            var configuration = new LibraryConfiguration(path);
            Assert.False(Directory.Exists(unavailable));
            Assert.Empty(configuration.IngestTargets);
            LibraryIngestRecipe recipe = Assert.Single(
                configuration.ActiveProfile.Ingest.Recipes, candidate => candidate.Enabled);
            Assert.Equal("legacy-cd-flac", recipe.Id);
            Assert.Equal(configuration.IndexLocations.Single().RootId,
                recipe.DestinationRootId);
            IReadOnlyList<string> missing =
                IngestMusicConfiguration.MissingLibrarySettings(configuration);
            Assert.Empty(missing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveRejectsDuplicateIngestRolesWithoutRequiringAllRoles()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var configuration = new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new() { Target = "first", IngestRole = LibraryIngestRole.HiRes },
                    new() { Target = "second", IngestRole = LibraryIngestRole.HiRes },
                ],
            };

            InvalidDataException error =
                Assert.Throws<InvalidDataException>(() => configuration.Save(path));
            Assert.Contains("only one", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompleteLibraryIngestSettingsResolveToTheRuntimeConfiguration()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            new EditableLibraryConfig
            {
                FfmpegPath = "configured-ffmpeg",
                AacEncoder = "configured-aac",
                AacBitrateKbps = 288,
                DeleteSourcesAfterIngest = true,
                RemoveNonMusicAfterIngest = true,
                IndexTargets =
                [
                    new() { Target = "cd", IngestRole = LibraryIngestRole.Cd },
                    new() { Target = "paired", IngestRole = LibraryIngestRole.CdFallback },
                    new() { Target = "hires", IngestRole = LibraryIngestRole.HiRes },
                    new() { Target = "aac", IngestRole = LibraryIngestRole.AacFallback },
                ],
            }.Save(path);

            var resolved = IngestMusicConfiguration.Resolve(
                new IngestRequest("incoming", path), settings: null).Configuration;

            Assert.Equal("configured-ffmpeg", resolved.FfmpegPath);
            Assert.Equal("configured-aac", resolved.AacEncoder);
            Assert.Equal(288, resolved.AacBitrateKbps);
            Assert.Equal("cd", resolved.CdDestination);
            Assert.Equal("paired", resolved.PairedCdDestination);
            Assert.Equal("hires", resolved.HighResolutionDestination);
            Assert.Equal("aac", resolved.AacDestination);
            Assert.True(resolved.DeleteSourcesAfterIngest);
            Assert.True(resolved.RemoveNonMusicAfterIngest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveRejectsPlaylistTargetWithoutSets()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var config = new EditableLibraryConfig
            {
                PlaylistTargets =
                [
                    new PlaylistTargetEntry { Target = @"Z:\Playlists", Type = "m3u" },
                ],
            };

            var error = Assert.Throws<InvalidDataException>(() => config.Save(path));
            Assert.Contains("at least one scan set", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EditableLoaderCanMigrateLegacyPlaylistTypeAfterSetsAreAssigned()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path,
                "<LibraryConfiguration><PlaylistTarget>Z:\\Playlists</PlaylistTarget>" +
                "<PlaylistType>wpl</PlaylistType></LibraryConfiguration>");

            var editable = EditableLibraryConfig.Load(path);
            var target = Assert.Single(editable.PlaylistTargets);
            Assert.Equal("wpl", target.Type);
            Assert.Empty(target.Sets);
            target.Sets.Add("7");
            editable.IndexTargets.Add(new IndexTargetEntry
            {
                Target = @"Z:\Music", Memberships = [new() { Name = "7" }],
            });
            editable.Save(path);

            var migrated = Assert.Single(new LibraryConfiguration(path).PlaylistTargets);
            Assert.Equal("wpl", migrated.Type);
            Assert.Equal(["7"], migrated.Sets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateNew_UsesCatalogOnlyPresetAndPersistsStableIds()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
            IndexTargetEntry target = editable.CreateIndexTarget("music");
            editable.IndexTargets.Add(target);
            Guid libraryId = editable.LibraryId;
            Guid rootId = target.Id;

            editable.Save(path);

            var runtime = new LibraryConfiguration(path);
            LibraryIndexLocation location = Assert.Single(runtime.IndexLocations);
            Assert.Equal(LibraryConfigurationSchema.CurrentVersion, runtime.SchemaVersion);
            Assert.Equal(libraryId, runtime.LibraryId);
            Assert.Equal(rootId, location.RootId);
            Assert.Equal(LibraryProfilePresets.CatalogOnlyId, runtime.ActiveProfileId);
            Assert.Equal(LibraryProfilePresets.CatalogOnlyId, location.ProfileId);
            Assert.Equal(LibraryRootPermissions.None, location.Permissions);
            Assert.False(location.Organize);
            Assert.DoesNotContain(runtime.Profiles,
                profile => profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools);
            Assert.Contains(runtime.Profiles,
                profile => profile.Preset == LibraryProfilePreset.ItunesMedia);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnversionedConfiguration_IsStableInMemoryAndBackedUpOnV2Save()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        string backup = path + ".v1.bak";
        const string original = "<LibraryConfiguration>" +
            "<IndexTarget Path=\"music\" Organize=\"false\" IngestRole=\"Cd\" />" +
            "</LibraryConfiguration>";
        try
        {
            File.WriteAllText(path, original);
            var first = new LibraryConfiguration(path);
            var second = new LibraryConfiguration(path);
            LibraryIndexLocation legacy = Assert.Single(first.IndexLocations);
            Assert.Equal(LibraryConfigurationSchema.LegacyVersion, first.SchemaVersion);
            Assert.Equal(first.LibraryId, second.LibraryId);
            Assert.Equal(legacy.RootId, Assert.Single(second.IndexLocations).RootId);
            Assert.Equal(LibraryProfilePresets.LegacyId, legacy.ProfileId);
            Assert.True(legacy.Permissions.HasFlag(LibraryRootPermissions.WriteMetadata));
            Assert.True(legacy.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork));
            Assert.True(legacy.Permissions.HasFlag(LibraryRootPermissions.IngestOutput));
            Assert.False(legacy.Permissions.HasFlag(LibraryRootPermissions.OrganizeFiles));

            EditableLibraryConfig editable = EditableLibraryConfig.Load(path);
            editable.Save(path);

            Assert.True(File.Exists(backup));
            Assert.Equal(original, File.ReadAllText(backup));
            var migrated = new LibraryConfiguration(path);
            LibraryIndexLocation migratedRoot = Assert.Single(migrated.IndexLocations);
            Assert.Equal(LibraryConfigurationSchema.CurrentVersion, migrated.SchemaVersion);
            Assert.Equal(first.LibraryId, migrated.LibraryId);
            Assert.Equal(legacy.RootId, migratedRoot.RootId);
            Assert.Equal(LibraryProfilePresets.LegacyId, migrated.ActiveProfileId);
            Assert.False(migratedRoot.Organize);
            Assert.Equal(LibraryIngestRole.None, migratedRoot.IngestRole);
        }
        finally
        {
            File.Delete(path);
            File.Delete(backup);
        }
    }

    [Fact]
    public void Profiles_RoundTripPoliciesAndOrderedLegacyRecipes()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var editable = new EditableLibraryConfig();
            LibraryProfile custom = LibraryProfilePresets.Create(
                LibraryProfilePreset.ArtistAlbum, "my-layout", "My layout") with
            {
                Naming = LibraryProfilePresets.Create(LibraryProfilePreset.ArtistAlbum).Naming with
                {
                    DirectoryTemplate = "{Genre}/{AlbumArtist}/{Album}",
                    CollisionPolicy = LibraryPathCollisionPolicy.Hash,
                },
                Disc = new(LibraryDiscStrategy.DiscFolder,
                    LibraryTrackTotalScope.Album, false, true),
            };
            LibraryIngestRecipe customRecipe = LibraryProfilePresets.Create(
                LibraryProfilePreset.LegacyMusicLibraryTools).Ingest.Recipes[^1] with
            {
                Id = "custom-aac",
                ExtraFfmpegOptions = "-af \"loudnorm=I=-16:LRA=11\" -movflags +faststart",
                AddToMediaCatalog = true,
            };
            custom = custom with
            {
                Ingest = custom.Ingest with { Recipes = [customRecipe] },
            };
            editable.Profiles.Add(custom);
            editable.ActiveProfileId = custom.Id;
            editable.IndexTargets.Add(new IndexTargetEntry
            {
                Target = "music",
                ProfileId = custom.Id,
                Permissions = custom.DefaultRootPermissions,
            });

            editable.Save(path);

            var runtime = new LibraryConfiguration(path);
            LibraryProfile loaded = runtime.ActiveProfile;
            Assert.Equal("My layout", loaded.Name);
            Assert.Equal("{Genre}/{AlbumArtist}/{Album}", loaded.Naming.DirectoryTemplate);
            Assert.Equal(LibraryPathCollisionPolicy.Hash, loaded.Naming.CollisionPolicy);
            Assert.Equal(LibraryDiscStrategy.DiscFolder, loaded.Disc.Strategy);
            Assert.Equal("-af \"loudnorm=I=-16:LRA=11\" -movflags +faststart",
                Assert.Single(loaded.Ingest.Recipes).ExtraFfmpegOptions);
            Assert.True(Assert.Single(loaded.Ingest.Recipes).AddToMediaCatalog);
            Assert.Equal("-af \"loudnorm=I=-16:LRA=11\" -movflags +faststart",
                (string?)XDocument.Load(path).Root!.Elements("LibraryProfile")
                    .Single(profile => (string?)profile.Attribute("Id") == custom.Id)
                    .Element("Ingest")!.Element("Recipe")!.Element("Output")!
                    .Attribute("ExtraFfmpegOptions"));
            LibraryProfile legacy = Assert.Single(runtime.Profiles,
                profile => profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools);
            Assert.Equal(
                ["legacy-hires-flac", "legacy-cd-flac", "legacy-paired-cd-flac", "legacy-aac"],
                legacy.Ingest.Recipes.Select(recipe => recipe.Id));
            LibraryIngestRecipe paired = legacy.Ingest.Recipes[2];
            Assert.Equal(LibraryIngestRole.None, paired.DestinationLegacyRole);
            Assert.Equal(LibraryIngestAlbumCondition.HasHighResolution,
                paired.AlbumCondition);
            Assert.Equal(LibraryIngestSourceSelection.PreferCdQuality,
                paired.SourceSelection);
            Assert.True(paired.RequireFallbackApproval);
            Assert.Equal(LibraryChannelSelection.Stereo, paired.InputChannels);
            Assert.Equal(LibraryChannelSelection.Stereo, paired.OutputChannels);
            XElement pairedXml = XDocument.Load(path).Root!
                .Elements("LibraryProfile")
                .Elements("Ingest")
                .Elements("Recipe")
                .Single(recipe => (string?)recipe.Attribute("Id") == paired.Id);
            Assert.Equal("Stereo", (string?)pairedXml.Element("Match")?
                .Attribute("InputChannels"));
            Assert.Equal("Stereo", (string?)pairedXml.Element("Output")?
                .Attribute("OutputChannels"));
            pairedXml.Element("Match")!.SetAttributeValue("InputChannels", "6");
            pairedXml.Document!.Save(path);
            LibraryProfile legacyNumeric = new LibraryConfiguration(path).Profiles.Single(
                profile => profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools);
            Assert.Equal(LibraryChannelSelection.Multi,
                legacyNumeric.Ingest.Recipes[2].InputChannels);
            Assert.Equal("custom-aac", Assert.Single(loaded.Ingest.Recipes).Id);
            Assert.Equal(64, runtime.PolicySnapshot.Fingerprint.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void V2Save_PreservesUnknownAttributesAndNestedElements()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var editable = new EditableLibraryConfig
            {
                IndexTargets = [new IndexTargetEntry { Target = "music" }],
            };
            editable.Save(path);
            XDocument document = XDocument.Load(path);
            document.Root!.SetAttributeValue("FutureRoot", "keep");
            XElement profile = document.Root.Elements("LibraryProfile").First();
            profile.SetAttributeValue("FutureProfile", "keep");
            profile.Element("Naming")!.SetAttributeValue(
                "LibraryProfileId", "keep-profile-naming-attribute");
            profile.Element("Naming")!.Add(new XElement("FutureNaming", "keep"));
            XElement sidecarRule = profile.Element("Sidecars")!.Element("Rule")!;
            sidecarRule.SetAttributeValue("Severity", "keep-sidecar-rule-attribute");
            XElement indexTarget = document.Root.Element("IndexTarget")!;
            indexTarget.SetAttributeValue("FutureTarget", "keep");
            indexTarget.Add(new XElement("FutureTargetChild", "keep"));
            document.Root.Add(new XElement("SyncPlaylist",
                new XAttribute("FuturePlaylist", "keep"),
                "Road Trip",
                new XElement("FuturePlaylistChild")));
            document.Save(path);

            EditableLibraryConfig.Load(path).Save(path);

            XDocument reloaded = XDocument.Load(path);
            Assert.Equal("keep", (string?)reloaded.Root!.Attribute("FutureRoot"));
            XElement savedProfile = reloaded.Root.Elements("LibraryProfile").First();
            Assert.Equal("keep", (string?)savedProfile.Attribute("FutureProfile"));
            Assert.Equal("keep", (string?)savedProfile.Element("Naming")
                ?.Element("FutureNaming"));
            Assert.Equal("keep-profile-naming-attribute",
                (string?)savedProfile.Element("Naming")?.Attribute("LibraryProfileId"));
            Assert.Equal("keep-sidecar-rule-attribute",
                (string?)savedProfile.Element("Sidecars")?.Element("Rule")
                    ?.Attribute("Severity"));
            XElement savedTarget = reloaded.Root.Element("IndexTarget")!;
            Assert.Equal("keep", (string?)savedTarget.Attribute("FutureTarget"));
            Assert.Equal("keep", (string?)savedTarget.Element("FutureTargetChild"));
            XElement savedPlaylist = Assert.Single(reloaded.Root.Elements("SyncPlaylist"));
            Assert.Equal("keep", (string?)savedPlaylist.Attribute("FuturePlaylist"));
            Assert.NotNull(savedPlaylist.Element("FuturePlaylistChild"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_RejectsRecipeDestinationWithoutIngestPermission()
    {
        EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
        LibraryProfile custom = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom, "recipe-policy", "Recipe policy");
        IndexTargetEntry root = editable.CreateIndexTarget("catalog");
        root.ProfileId = custom.Id;
        root.Permissions = LibraryRootPermissions.None;
        root.Organize = false;
        LibraryIngestRecipe recipe = new(
            "copy-flac", "Copy FLAC", true, [".flac"], true,
            null, null, null, false, LibraryIngestAction.Copy,
            root.Id, LibraryIngestRole.None, ".flac", "flac", null,
            null, null, null, null, custom.Id, true, true,
            LibraryPathCollisionPolicy.Stop);
        custom = custom with
        {
            Ingest = new LibraryIngestPolicy(
                true, LibrarySourceDisposition.Preserve, true, [recipe]),
        };
        editable.Profiles.Add(custom);
        editable.IndexTargets.Add(root);

        LibraryConfigurationIssue issue = Assert.Single(editable.Validate(), candidate =>
            candidate.Code == "recipe-root-permission");

        Assert.Contains("does not permit IngestOutput", issue.Message);
    }

    [Fact]
    public void MachineBindingsAndPortableConfigurationRollbackTogetherOnSaveFailure()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_transaction_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string bindingsPath = Path.Combine(work, "bindings.xml");
        string invalidConfigurationTarget = Path.Combine(work, "configuration-is-a-directory");
        Directory.CreateDirectory(invalidConfigurationTarget);
        const string originalBindings = "<LibraryBindings Original=\"keep\" />";
        File.WriteAllText(bindingsPath, originalBindings);
        try
        {
            EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
            editable.MachineBindingsFile = "bindings.xml";

            Exception? error = Record.Exception(() =>
                editable.Save(invalidConfigurationTarget));

            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Unexpected failure type: {error?.GetType().FullName ?? "none"}");

            Assert.Equal(originalBindings, File.ReadAllText(bindingsPath));
            Assert.Empty(Directory.EnumerateFiles(work, "*.tmp"));
            Assert.Empty(Directory.EnumerateFiles(work, "*.rollback"));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void MachineBindings_SeparatePathsAndRoundTripThroughRuntimeConfiguration()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_bindings_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(work, "library.xml");
        string bindingsPath = Path.Combine(work, "machine", "bindings.xml");
        string musicPath = Path.Combine(work, "Music");
        string databasePath = Path.Combine(work, "Cache", "library.db");
        string ffmpegPath = Path.Combine(work, "Tools", "ffmpeg");
        string wavpackPath = Path.Combine(work, "Tools", "wavpack");
        string itunesPath = Path.Combine(work, "Catalog", "Library.itl");
        try
        {
            var editable = new EditableLibraryConfig
            {
                MachineBindingsFile = Path.Combine("machine", "bindings.xml"),
                DatabaseFile = databasePath,
                FfmpegPath = ffmpegPath,
                WavpackPath = wavpackPath,
                ItunesLibraryPath = itunesPath,
                IndexTargets = [new IndexTargetEntry { Target = musicPath }],
            };
            Guid rootId = editable.IndexTargets[0].Id;

            editable.Save(configPath);

            XDocument portable = XDocument.Load(configPath);
            Assert.Equal(Path.Combine("machine", "bindings.xml"),
                (string?)portable.Root!.Element("MachineBindings")?.Attribute("File"));
            Assert.Null(portable.Root.Element("DatabaseFile"));
            Assert.Null(portable.Root.Element("FfmpegPath"));
            Assert.Null(portable.Root.Element("WavpackPath"));
            Assert.Null(portable.Root.Element("ItunesLibrary"));
            Assert.Null(portable.Root.Element("IndexTarget")?.Attribute("Path"));

            XDocument bindings = XDocument.Load(bindingsPath);
            Assert.Equal(editable.LibraryId.ToString("D"),
                (string?)bindings.Root!.Attribute("LibraryId"));
            Assert.Equal(rootId.ToString("D"),
                (string?)bindings.Root.Element("RootBinding")?.Attribute("RootId"));
            Assert.Equal(musicPath,
                (string?)bindings.Root.Element("RootBinding")?.Attribute("Path"));

            var runtime = new LibraryConfiguration(configPath);
            Assert.Equal(Path.GetFullPath(musicPath),
                Assert.Single(runtime.IndexLocations).Target);
            Assert.Equal(Path.GetFullPath(databasePath), runtime.DatabaseFile);
            Assert.Equal(Path.GetFullPath(ffmpegPath), runtime.FfmpegPath);
            Assert.Equal(Path.GetFullPath(wavpackPath), runtime.WavpackPath);
            Assert.Equal(Path.GetFullPath(itunesPath), runtime.ItunesLibraryPath);

            EditableLibraryConfig reloaded = EditableLibraryConfig.Load(configPath);
            Assert.Equal(Path.Combine("machine", "bindings.xml"),
                reloaded.MachineBindingsFile);
            reloaded.Save(configPath);
            Assert.Null(XDocument.Load(configPath).Root!
                .Element("IndexTarget")?.Attribute("Path"));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void MachineBindings_InlinePathsRemainFallbackAndBoundPathsOverrideThem()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_bindings_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(work, "library.xml");
        string bindingsPath = Path.Combine(work, "bindings.xml");
        try
        {
            var editable = new EditableLibraryConfig
            {
                IndexTargets = [new IndexTargetEntry { Target = "inline-music" }],
            };
            editable.Save(configPath);
            Guid rootId = editable.IndexTargets[0].Id;
            XDocument portable = XDocument.Load(configPath);
            portable.Root!.AddFirst(new XElement("MachineBindings",
                new XAttribute("File", "bindings.xml")));
            portable.Save(configPath);

            new XDocument(new XElement("LibraryBindings",
                new XAttribute("SchemaVersion", 1),
                new XAttribute("LibraryId", editable.LibraryId.ToString("D"))))
                .Save(bindingsPath);
            Assert.Equal("inline-music",
                Assert.Single(new LibraryConfiguration(configPath).IndexLocations).Target);

            XDocument bindings = XDocument.Load(bindingsPath);
            bindings.Root!.Add(new XElement("RootBinding",
                new XAttribute("RootId", rootId.ToString("D")),
                new XAttribute("Path", "bound-music")));
            bindings.Save(bindingsPath);
            Assert.Equal(Path.GetFullPath(Path.Combine(work, "bound-music")),
                Assert.Single(new LibraryConfiguration(configPath).IndexLocations).Target);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void MachineBindings_RejectMismatchedLibraryAndRootReferences()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_bindings_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(work, "library.xml");
        string bindingsPath = Path.Combine(work, "bindings.xml");
        try
        {
            var editable = new EditableLibraryConfig
            {
                MachineBindingsFile = "bindings.xml",
                IndexTargets = [new IndexTargetEntry { Target = "music" }],
            };
            editable.Save(configPath);

            XDocument bindings = XDocument.Load(bindingsPath);
            bindings.Root!.SetAttributeValue("LibraryId", Guid.NewGuid().ToString("D"));
            bindings.Save(bindingsPath);
            Assert.Throws<InvalidDataException>(() =>
                new LibraryConfiguration(configPath));

            bindings.Root.SetAttributeValue("LibraryId", editable.LibraryId.ToString("D"));
            bindings.Root.Add(new XElement("RootBinding",
                new XAttribute("RootId", Guid.NewGuid().ToString("D")),
                new XAttribute("Path", "unrecognized-root")));
            bindings.Save(bindingsPath);
            var runtime = new LibraryConfiguration(configPath);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                runtime.IndexLocations.ToList());
            Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void MachineBindings_PathlessPortableRootRequiresALocalBinding()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_bindings_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(work, "library.xml");
        string bindingsPath = Path.Combine(work, "bindings.xml");
        try
        {
            new EditableLibraryConfig
            {
                MachineBindingsFile = "bindings.xml",
                IndexTargets = [new IndexTargetEntry { Target = "music" }],
            }.Save(configPath);

            XDocument bindings = XDocument.Load(bindingsPath);
            bindings.Root!.Element("RootBinding")!.Remove();
            bindings.Save(bindingsPath);

            var runtime = new LibraryConfiguration(configPath);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                runtime.IndexLocations.ToList());
            Assert.Contains("no inline Path", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void MachineBindings_PreserveUnknownXmlDuringRoundTrip()
    {
        string work = Path.Combine(Path.GetTempPath(),
            "cfg_bindings_" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(work, "library.xml");
        string bindingsPath = Path.Combine(work, "bindings.xml");
        try
        {
            var editable = new EditableLibraryConfig
            {
                MachineBindingsFile = "bindings.xml",
                IndexTargets = [new IndexTargetEntry { Target = "music" }],
            };
            editable.Save(configPath);

            XDocument portable = XDocument.Load(configPath);
            portable.Root!.Element("MachineBindings")!
                .SetAttributeValue("FutureReference", "keep");
            portable.Save(configPath);

            XDocument bindings = XDocument.Load(bindingsPath);
            bindings.Root!.SetAttributeValue("FutureRoot", "keep");
            XElement rootBinding = bindings.Root.Element("RootBinding")!;
            rootBinding.SetAttributeValue("FutureBinding", "keep");
            rootBinding.Add(new XElement("FutureChild", "keep"));
            bindings.Root.Add(new XElement("ToolBinding",
                new XAttribute("Name", "FutureEncoder"),
                new XAttribute("Path", "future-tool"),
                new XAttribute("FutureTool", "keep")));
            bindings.Root.Add(new XElement("FutureBindings", "keep"));
            bindings.Save(bindingsPath);

            EditableLibraryConfig.Load(configPath).Save(configPath);

            portable = XDocument.Load(configPath);
            Assert.Equal("keep", (string?)portable.Root!.Element("MachineBindings")
                ?.Attribute("FutureReference"));
            bindings = XDocument.Load(bindingsPath);
            Assert.Equal("keep", (string?)bindings.Root!.Attribute("FutureRoot"));
            rootBinding = bindings.Root.Element("RootBinding")!;
            Assert.Equal("keep", (string?)rootBinding.Attribute("FutureBinding"));
            Assert.Equal("keep", (string?)rootBinding.Element("FutureChild"));
            Assert.Equal("keep", (string?)bindings.Root.Elements("ToolBinding")
                .Single(element => (string?)element.Attribute("Name") == "FutureEncoder")
                .Attribute("FutureTool"));
            Assert.Equal("keep", (string?)bindings.Root.Element("FutureBindings"));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    [Fact]
    public void IndexTargetFormatAndPatternPolicies_RoundTripNormalized()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var target = new IndexTargetEntry
            {
                Target = "music",
                IndexFormats = [" FLAC ", ".m4a", ".FLAC"],
                IndexIncludePatterns = [" Music/** ", "*.flac", "music/**"],
                IndexExcludePatterns = [" Temp/** ", "*.tmp", "temp/**"],
            };
            new EditableLibraryConfig { IndexTargets = [target] }.Save(path);

            XElement element = XDocument.Load(path).Root!.Element("IndexTarget")!;
            Assert.Equal(".flac,.m4a", (string?)element.Attribute("IndexFormats"));
            Assert.Equal("Music/**;*.flac", (string?)element.Attribute("IndexInclude"));
            Assert.Equal("Temp/**;*.tmp", (string?)element.Attribute("IndexExclude"));

            LibraryIndexLocation runtime = Assert.Single(
                new LibraryConfiguration(path).IndexLocations);
            Assert.Equal([".flac", ".m4a"], runtime.IndexFormats);
            Assert.Equal(["Music/**", "*.flac"], runtime.IndexIncludePatterns);
            Assert.Equal(["Temp/**", "*.tmp"], runtime.IndexExcludePatterns);

            IndexTargetEntry editable = Assert.Single(
                EditableLibraryConfig.Load(path).IndexTargets);
            Assert.Equal(runtime.IndexFormats, editable.IndexFormats);
            Assert.Equal(runtime.IndexIncludePatterns, editable.IndexIncludePatterns);
            Assert.Equal(runtime.IndexExcludePatterns, editable.IndexExcludePatterns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IndexTargetFormatPolicy_RejectsFormatsWithoutLibraryIndexCapability()
    {
        var editable = new EditableLibraryConfig
        {
            IndexTargets =
            [
                new IndexTargetEntry
                {
                    Target = "music",
                    IndexFormats = [".mp4"],
                },
            ],
        };

        LibraryConfigurationIssue issue = Assert.Single(editable.Validate(), candidate =>
            candidate.Code == "root-index-format");
        Assert.Contains("not registered for library indexing", issue.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_SeparatesOfflineWarningsFromPermissionErrors()
    {
        EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
        IndexTargetEntry target = editable.CreateIndexTarget(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        editable.IndexTargets.Add(target);
        LibraryProfile profile = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom, "validation-ingest", "Validation ingest");
        LibraryIngestRecipe recipe = new(
            "copy-flac", "Copy FLAC", true, [".flac"], true,
            null, null, null, false, LibraryIngestAction.Copy,
            target.Id, LibraryIngestRole.None, ".flac", "flac", null,
            null, null, null, null, profile.Id, true, true,
            LibraryPathCollisionPolicy.Stop);
        profile = profile with
        {
            Ingest = new(true, LibrarySourceDisposition.Preserve, true, [recipe]),
        };
        editable.Profiles.Add(profile);
        editable.ActiveProfileId = profile.Id;

        IReadOnlyList<LibraryConfigurationIssue> issues = editable.Validate(
            includePathAvailabilityWarnings: true);

        Assert.Contains(issues, issue => issue.Code == "root-offline" &&
            issue.Severity == LibraryConfigurationIssueSeverity.Warning);
        Assert.Contains(issues, issue => issue.Code == "recipe-root-permission" &&
            issue.Severity == LibraryConfigurationIssueSeverity.Error);
    }

    [Fact]
    public void Validate_RejectsUnknownNamingTemplateTokensBeforeSaving()
    {
        var editable = new EditableLibraryConfig();
        LibraryProfile legacy = editable.Profiles.Single(profile =>
            profile.Id == LibraryProfilePresets.LegacyId);
        editable.Profiles[editable.Profiles.IndexOf(legacy)] = legacy with
        {
            Naming = legacy.Naming with { DirectoryTemplate = "{Label}/{Album}" },
        };

        LibraryConfigurationIssue issue = Assert.Single(editable.Validate(), candidate =>
            candidate.Code == "profile-invalid");

        Assert.Contains("unknown naming token", issue.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PolicyFingerprintIncludesResolvedRootPathAndIndexPolicy()
    {
        string firstPath = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        string secondPath = Path.Combine(Path.GetTempPath(),
            "cfg_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var editable = EditableLibraryConfig.CreateNew();
            IndexTargetEntry root = editable.CreateIndexTarget("music-a");
            editable.IndexTargets.Add(root);
            editable.Save(firstPath);
            string first = new LibraryConfiguration(firstPath).PolicySnapshot.Fingerprint;

            root.Target = "music-b";
            root.IndexFormats = [".flac"];
            root.IndexIncludePatterns = ["Albums/**"];
            root.IndexExcludePatterns = ["Temp/**"];
            editable.Save(secondPath);
            string second = new LibraryConfiguration(secondPath).PolicySnapshot.Fingerprint;

            Assert.NotEqual(first, second);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public void PlaylistSourcesAndWriterOptionsRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "playlist_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "library.xml");
        try
        {
            EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
            IndexTargetEntry root = editable.CreateIndexTarget("music");
            root.Memberships.Add(new() { Name = "Portable" });
            editable.IndexTargets.Add(root);
            editable.PlaylistSources.Add(new()
            {
                Type = "m3u",
                Location = Path.Combine("inputs", "playlists"),
                Recursive = true,
            });
            editable.PlaylistTargets.Add(new()
            {
                Target = "output",
                Type = "m3u8",
                Sets = ["Portable"],
                PathStyle = "relative",
                Encoding = "utf-8",
                EmitByteOrderMark = false,
                LineEnding = "lf",
                IncludeExtendedInfo = false,
                FileNameTransform = "preserve",
                MaxTrackCount = 123,
                CollisionPolicy = LibraryPathCollisionPolicy.Suffix,
            });

            editable.Save(path);

            var configuration = new LibraryConfiguration(path);
            LibraryPlaylistSource source = Assert.Single(configuration.PlaylistSources);
            Assert.Equal("m3u", source.Type);
            Assert.Equal(Path.GetFullPath(Path.Combine(directory, "inputs", "playlists")),
                source.Location);
            Assert.True(source.Recursive);
            LibraryPlaylistTarget target = Assert.Single(configuration.PlaylistTargets);
            Assert.Equal("relative", target.PathStyle);
            Assert.False(target.EmitByteOrderMark);
            Assert.Equal("lf", target.LineEnding);
            Assert.False(target.IncludeExtendedInfo);
            Assert.Equal("preserve", target.FileNameTransform);
            Assert.Equal(123, target.MaxTrackCount);
            Assert.Equal(LibraryPathCollisionPolicy.Suffix, target.CollisionPolicy);
            string firstFingerprint = configuration.PolicySnapshot.Fingerprint;

            EditableLibraryConfig reloaded = EditableLibraryConfig.Load(path);
            Assert.Single(reloaded.PlaylistSources);
            Assert.Equal("inputs" + Path.DirectorySeparatorChar + "playlists",
                reloaded.PlaylistSources[0].Location.Replace(Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar));
            Assert.Equal(123, Assert.Single(reloaded.PlaylistTargets).MaxTrackCount);
            reloaded.PlaylistTargets[0].MaxTrackCount = 124;
            reloaded.Save(path);
            Assert.NotEqual(firstFingerprint,
                new LibraryConfiguration(path).PolicySnapshot.Fingerprint);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
