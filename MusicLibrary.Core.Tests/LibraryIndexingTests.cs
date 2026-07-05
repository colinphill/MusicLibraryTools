using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class LibraryIndexingTests
{
    [Fact]
    public async Task Index_Then_Snapshot_FindsFixtureTracks()
    {
        // Exercises exactly what the "Index" button drives: load a config, open/create the cache,
        // scan a folder of real media, then build the browsable snapshot.
        var work = Path.Combine(Path.GetTempPath(), "mlidx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var configPath = Path.Combine(work, "library.xml");
        try
        {
            new EditableLibraryConfig
            {
                DatabaseFile = "cache.db",   // resolved next to the config
                LengthLimit = 255,
                DiscNumLengthLimit = 255,
                IndexTargets = [new IndexTargetEntry { Target = MediaFixtures.Dir }],
            }.Save(configPath);

            var settings = new AppSettings();
            settings.LoadConfig(configPath);
            Assert.NotNull(settings.Configuration);

            using var library = new LibraryService(settings);
            Assert.True(library.IsReady);

            var (added, _, _, _) = await library.IndexAsync();
            Assert.True(added > 0, "indexing should add the fixture tracks");

            var snapshot = await library.BuildSnapshotAsync();
            Assert.True(snapshot.TotalTracks > 0);
            Assert.True(snapshot.RootCount > 0);

            // And the records path (drives the details grid / analyzers).
            var records = await library.GetAllRecordsAsync();
            Assert.NotEmpty(records);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
