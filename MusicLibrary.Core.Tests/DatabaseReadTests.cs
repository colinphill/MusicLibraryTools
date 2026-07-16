using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MusicLibrary.Core.Tests;

/// <summary>
/// Verifies the database-backed read path: the cache stores all known (TagFields) + raw text
/// metadata and artwork, the library service reads a file's full metadata straight from it, and an
/// edit is immediately re-indexed so the next read reflects the change without a full re-scan.
/// </summary>
public class DatabaseReadTests
{
    private static (string Work, string Music, string Config, string Song) Setup(
        string fixture, bool organize = true)
    {
        var work = Path.Combine(Path.GetTempPath(), "mldb_" + Guid.NewGuid().ToString("N"));
        var music = Path.Combine(work, "music");
        Directory.CreateDirectory(music);
        var song = Path.Combine(music, "song" + Path.GetExtension(fixture));
        File.Copy(MediaFixtures.Path_(fixture), song, overwrite: true);

        var config = Path.Combine(work, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = "cache.db",
            LengthLimit = 255,
            DiscNumLengthLimit = 255,
            IndexTargets = [new IndexTargetEntry { Target = music, Organize = organize }],
        }.Save(config);

        return (work, music, config, song);
    }

    [Fact]
    public async Task Index_StoresNormalizedMetadata_AndReadsItBack()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            var details = await library.GetFileDetailsAsync(song, includeArtwork: false);
            Assert.NotNull(details);
            Assert.Equal("TestTitle", details!.Entry.Title);
            Assert.Equal("TestArtist", details.Entry.Artist);
            Assert.Equal(new FileInfo(song).Length, details.Entry.Length);
            Assert.Equal(new FileInfo(song).Length, Assert.Single(await library.GetAllRecordsAsync()).Length);

            // Genre is not a structured column — its presence proves GetKnownMetadata() was persisted
            // to the canonical Metadata table and read back from the cache.
            Assert.Contains(details.KnownFields, k => k.Key == nameof(TagFields.Genre) && k.Value == "Rock");
            Assert.NotEmpty(details.TextFields);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EditingTags_ReindexesImmediately()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            // TagWriteService writes to the file AND re-indexes via the library (IReindexService).
            var writer = new TagWriteService(library);
            var result = await writer.ApplyAsync([song], [new TagEdit(TagFields.Title, "Changed Title")]);
            Assert.Equal(1, result.SavedCount);

            // The cache reflects the new value straight away (no full re-scan).
            var details = await library.GetFileDetailsAsync(song, includeArtwork: false);
            Assert.Equal("Changed Title", details!.Entry.Title);
            Assert.Contains(details.KnownFields, k => k.Key == nameof(TagFields.Title) && k.Value == "Changed Title");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Organize_MovesFile_AndSyncsCacheToNewPath()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            Assert.NotNull(await library.GetFileDetailsAsync(song, includeArtwork: false));

            // song.flac isn't in its canonical Artist/Album/## Title location, so a move is planned.
            var moves = await library.PreviewMovesAsync();
            Assert.NotEmpty(moves);
            var dest = moves[0].Destination;

            var result = await library.ApplyMovesAsync(moves);
            Assert.True(result.Moved > 0);
            Assert.True(File.Exists(dest));

            var operation = Assert.Single((await new OperationJournalService().DiscoverAsync([Path.GetDirectoryName(song)!])).Runs);
            Assert.Equal("OrganizeFiles", operation.ToolName);
            Assert.Equal(OperationJournalState.Completed, operation.State);
            Assert.Equal(1, operation.AffectedItemCount);

            // The cache is synced automatically: the old path is gone and the new path is indexed.
            Assert.Null(await library.GetFileDetailsAsync(song, includeArtwork: false));
            Assert.NotNull(await library.GetFileDetailsAsync(dest, includeArtwork: false));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Organize_ExcludedIndexTargetRemainsIndexedButCannotMove()
    {
        var (work, music, config, song) = Setup("sample.flac", organize: false);
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            Assert.NotNull(await library.GetFileDetailsAsync(song, includeArtwork: false));
            Assert.Empty(await library.PreviewMovesAsync());

            var move = new PlannedMove(song, Path.Combine(music, "elsewhere.flac"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => library.ApplyMovesAsync([move]));
            Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(song));
            Assert.False(File.Exists(move.Destination));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkAuditReadDoesNotHydrateDeferredImageData()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            var before = Assert.Single(await library.GetArtworkAuditFilesAsync());
            Assert.Equal(song, before.Path);
            Assert.False(before.ArtworkScanned);

            _ = await library.GetFirstImageAsync(song);
            var after = Assert.Single(await library.GetArtworkAuditFilesAsync());
            Assert.True(after.ArtworkScanned);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Organize_NonNormalizedSourcesStillReceiveDistinctDestinations()
    {
        var (work, music, config, song) = Setup("sample.flac");
        string first = Path.Combine(music, "a\u0301.flac");
        string second = Path.Combine(music, "b\u0301.flac");
        File.Move(song, first);
        File.Copy(first, second);

        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            var moves = await library.PreviewMovesAsync();

            Assert.Equal(2, moves.Count);
            Assert.Equal(2, moves.Select(m => m.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BatchEditingTags_ReindexesAllFilesInOneCacheOperation()
    {
        var (work, music, config, first) = Setup("sample.flac");
        string second = Path.Combine(music, "second.flac");
        File.Copy(first, second);
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            var writer = new TagWriteService(library, maxParallelism: 2);
            var result = await writer.ApplyAsync(
                [first, second], [new TagEdit(TagFields.Album, "Batch album")]);

            Assert.Equal(2, result.SavedCount);
            Assert.Equal("Batch album", (await library.GetFileDetailsAsync(first, false))!.Entry.Album);
            Assert.Equal("Batch album", (await library.GetFileDetailsAsync(second, false))!.Entry.Album);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EditingArtwork_ReindexesImageIntoCache()
    {
        var (work, _, config, song) = Setup("sample.flac");
        var png = Path.Combine(work, "cover.png");
        try
        {
            using (var image = new Image<Rgba32>(200, 200))
            {
                image.Mutate(x => x.BackgroundColor(Color.Blue));
                image.Save(png, new PngEncoder());
            }

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            // No artwork yet.
            Assert.Null(await library.GetFirstImageAsync(song));

            // ArtworkService writes the cover to the file AND re-indexes via the library.
            var art = new ArtworkService(library);
            var op = await art.SetCoverFromFileAsync(song, png, maxDimension: 150);
            Assert.True(op.Success, op.Error);

            // The cache now serves the embedded image (drives the grid thumbnail + artwork tab).
            var bytes = await library.GetFirstImageAsync(song);
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 0);

            string missing = Path.Combine(Path.GetDirectoryName(song)!, "missing.flac");
            var batch = await library.GetFirstImagesAsync([song, missing, song]);
            Assert.Equal(3, batch.Count);
            Assert.Equal(bytes, batch[0]);
            Assert.Null(batch[1]);
            Assert.Equal(bytes, batch[2]);

            var signatures = await library.GetImageSignaturesAsync([song, missing, song]);
            Assert.NotEmpty(signatures[0]);
            Assert.Equal("", signatures[1]);
            Assert.Equal(signatures[0], signatures[2]);

            var details = await library.GetFileDetailsAsync(song, includeArtwork: true);
            Assert.Single(details!.Images);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Index_DefersArtworkUntilItIsRequested()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            byte[] cover = CreatePngBytes(96, 96, Color.Purple);
            var media = MediaFile.GetFile(song);
            ((IArtworkWriter)media.Tags.First()).SetFrontCover(cover, "image/png");
            media.SaveTags();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.NotNull(await library.GetFileDetailsAsync(song, includeArtwork: false));
            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));

            Assert.Equal(cover, await library.GetFirstImageAsync(song));
            Assert.Equal(1L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Single((await library.GetFileDetailsAsync(song, includeArtwork: true))!.Images);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeferredArtworkRejectsAFileChangedSinceIndexing()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var media = MediaFile.GetFile(song);
            ((IArtworkWriter)media.Tags.First()).SetFrontCover(
                CreatePngBytes(64, 64, Color.Green), "image/png");
            media.SaveTags();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            File.AppendAllText(song, "changed");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => library.GetFirstImageAsync(song));
            Assert.Contains("changed since metadata indexing", error.Message);
            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static byte[] CreatePngBytes(int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(context => context.BackgroundColor(color));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static long ReadArtworkScanned(string databasePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ArtworkScanned FROM Files LIMIT 1";
        return (long)command.ExecuteScalar()!;
    }
}
