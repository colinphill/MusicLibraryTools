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
                    new IndexTargetEntry { Target = @"Z:\FLAC", Set = 1, Filter = "*.flac" },
                    new IndexTargetEntry { Target = @"Z:\HiRes" },
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
            Assert.Equal(1, reloaded.IndexTargets[0].Set);
            Assert.Equal("*.flac", reloaded.IndexTargets[0].Filter);
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
                IndexTargets = [new IndexTargetEntry { Target = @"Z:\Music", Set = 2 }],
            }.Save(path);

            var config = new LibraryConfiguration(path);
            Assert.Equal("cache.db", config.DatabaseFile);
            Assert.Equal(255, config.LengthLimit);
            var roots = config.IndexLocations.ToList();
            Assert.Single(roots);
            Assert.Equal(@"Z:\Music", roots[0].Target);
            Assert.Equal(2, roots[0].Set);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
