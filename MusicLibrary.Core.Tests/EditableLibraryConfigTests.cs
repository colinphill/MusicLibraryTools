using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
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
            Assert.Equal(LibraryIngestRole.Cd, reloaded.IndexTargets[0].IngestRole);
            Assert.Equal(LibraryIngestRole.HiRes, reloaded.IndexTargets[1].IngestRole);
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
    public void PartialAndUnavailableIngestTargetsAreValidButReportedAsNotReady()
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
            Assert.Equal(unavailable,
                configuration.IngestTargets[LibraryIngestRole.Cd].Target);
            IReadOnlyList<string> missing =
                IngestMusicConfiguration.MissingLibrarySettings(configuration);
            Assert.DoesNotContain(missing,
                item => item.Contains("CD ingest", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(missing,
                item => item.Contains("CD fallback", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(missing,
                item => item.Contains("Hi-res", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(missing,
                item => item.Contains("AAC fallback", StringComparison.OrdinalIgnoreCase));
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
}
