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
                LengthLimit = 200,
                DiscNumLengthLimit = 180,
                SyncTarget = @"\\nas\music",
                IndexTargets =
                [
                    new IndexTargetEntry { Target = @"Z:\FLAC", Sets = [1, 3], Filter = "*.flac" },
                    new IndexTargetEntry { Target = @"Z:\HiRes" },
                ],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry { Target = @"Z:\WPL", Type = "wpl", Sets = [1] },
                    new PlaylistTargetEntry { Target = @"Z:\Portable", Type = "m3u", Sets = [1, 3] },
                ],
            };
            config.Save(path);

            var reloaded = EditableLibraryConfig.Load(path);
            Assert.Equal("mycache.db", reloaded.DatabaseFile);
            Assert.Equal(200, reloaded.LengthLimit);
            Assert.Equal(180, reloaded.DiscNumLengthLimit);
            Assert.Equal(@"\\nas\music", reloaded.SyncTarget);
            Assert.Equal(2, reloaded.IndexTargets.Count);
            Assert.Equal(@"Z:\FLAC", reloaded.IndexTargets[0].Target);
            Assert.Equal([1, 3], reloaded.IndexTargets[0].Sets);
            Assert.Empty(reloaded.IndexTargets[1].Sets);
            Assert.Equal("*.flac", reloaded.IndexTargets[0].Filter);
            Assert.Equal(2, reloaded.PlaylistTargets.Count);
            Assert.Equal(@"Z:\WPL", reloaded.PlaylistTargets[0].Target);
            Assert.Equal("wpl", reloaded.PlaylistTargets[0].Type);
            Assert.Equal([1], reloaded.PlaylistTargets[0].Sets);
            Assert.Equal([1, 3], reloaded.PlaylistTargets[1].Sets);

            var xml = System.Xml.Linq.XDocument.Load(path);
            Assert.Null(xml.Root!.Element("PlaylistType"));
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
                IndexTargets = [new IndexTargetEntry { Target = @"Z:\Music", Sets = [2, 4] }],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry { Target = @"Z:\Playlists", Type = "wpl", Sets = [2, 4] },
                ],
            }.Save(path);

            var config = new LibraryConfiguration(path);
            Assert.Equal("cache.db", config.DatabaseFile);
            Assert.Equal(255, config.LengthLimit);
            var roots = config.IndexLocations.ToList();
            Assert.Single(roots);
            Assert.Equal(@"Z:\Music", roots[0].Target);
            Assert.Equal([2, 4], roots[0].Sets);
            var playlistTarget = Assert.Single(config.PlaylistTargets);
            Assert.Equal(@"Z:\Playlists", playlistTarget.Target);
            Assert.Equal("wpl", playlistTarget.Type);
            Assert.Equal([2, 4], playlistTarget.Sets);
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
            target.Sets.Add(7);
            editable.IndexTargets.Add(new IndexTargetEntry { Target = @"Z:\Music", Sets = [7] });
            editable.Save(path);

            var migrated = Assert.Single(new LibraryConfiguration(path).PlaylistTargets);
            Assert.Equal("wpl", migrated.Type);
            Assert.Equal([7], migrated.Sets);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
