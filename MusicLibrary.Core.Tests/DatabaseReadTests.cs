using MusicFileUtilities;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Microsoft.Data.Sqlite;
using SkiaSharp;
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
        string fixture, bool organize = true, bool useItunesCanonicalNaming = false,
        string libraryFolder = "music")
    {
        var work = Path.Combine(Path.GetTempPath(), "mldb_" + Guid.NewGuid().ToString("N"));
        var music = Path.Combine(work, libraryFolder);
        Directory.CreateDirectory(music);
        var song = Path.Combine(music, "song" + Path.GetExtension(fixture));
        File.Copy(MediaFixtures.Path_(fixture), song, overwrite: true);

        var config = Path.Combine(work, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = "cache.db",
            LengthLimit = 255,
            DiscNumLengthLimit = 255,
            IndexTargets =
            [
                new IndexTargetEntry
                {
                    Target = music,
                    Organize = organize,
                    UseItunesCanonicalNaming = useItunesCanonicalNaming,
                },
            ],
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
            TrackRecord record = Assert.Single(await library.GetAllRecordsAsync());
            Assert.Equal(new FileInfo(song).Length, record.Length);
            Assert.Equal("Rock", record.Genre);
            Assert.Equal(2021, record.Year);

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
    public async Task BrowseProjectionPreservesScalarFieldsWithoutRetainingMetadata()
    {
        var (work, _, config, song) = Setup(
            "sample.flac");
        try
        {
            IMediaFile media =
                MediaFile.GetFile(song);
            var custom = Assert.IsAssignableFrom<
                IUserStringMetadata>(
                media.Tags.First());
            custom.SetUserString(
                "MEMORY_TEST_VALUE",
                new string('x', 16_384));
            media.SaveTags();

            var settings = new AppSettings(
                Path.Combine(
                    work,
                    "settings.json"));
            settings.LoadConfig(config);
            using var library =
                new LibraryService(settings);
            await library.IndexAsync();

            TrackRecord browse =
                Assert.Single(
                    await library
                        .GetBrowseRecordsAsync());
            Assert.Equal("TestTitle", browse.Title);
            Assert.Equal("TestArtist", browse.Artist);
            Assert.Equal("Rock", browse.Genre);
            Assert.Empty(browse.Metadata);

            MetadataFieldKey requested =
                MetadataFieldKey.Custom(
                    "MEMORY_TEST_VALUE");
            LibraryMetadataProjection projected =
                Assert.Single(
                    await library
                        .GetMetadataProjectionAsync(
                            [song],
                            [requested]));
            Assert.Equal(song, projected.Path);
            Assert.Equal(
                new string('x', 16_384),
                Assert.Single(
                    projected.Values[
                        requested]));
            Assert.Single(projected.Values);

            TrackRecord complete =
                Assert.Single(
                    await library
                        .GetAllRecordsAsync());
            Assert.Equal(
                new string('x', 16_384),
                Assert.Single(
                    complete.Metadata[
                        CachedMetadataKeys.Custom(
                            "MEMORY_TEST_VALUE")]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(
                    work,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task LibrarySummaryMatchesBrowseProjectionWithoutLoadingMetadata()
    {
        var (work, _, config, _) = Setup(
            "sample.flac");
        try
        {
            var settings = new AppSettings(
                Path.Combine(
                    work,
                    "settings.json"));
            settings.LoadConfig(config);
            using var library =
                new LibraryService(settings);
            await library.IndexAsync();

            LibrarySummary summary =
                await library
                    .GetLibrarySummaryAsync();

            Assert.Equal(1, summary.TrackCount);
            Assert.Equal(1, summary.AlbumCount);
            Assert.Equal(1, summary.ArtistCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(
                    work,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task MetadataSortOrderReturnsPathsWithoutHydratingBrowseRows()
    {
        var (work, music, config, song) =
            Setup("sample.flac");
        string second = Path.Combine(
            music,
            "second.flac");
        string missing = Path.Combine(
            music,
            "missing.flac");
        try
        {
            File.Copy(song, second);
            File.Copy(song, missing);
            SetCustom(song, "SORT_VALUE", "10");
            SetCustom(second, "SORT_VALUE", "2");

            var settings = new AppSettings(
                Path.Combine(
                    work,
                    "settings.json"));
            settings.LoadConfig(config);
            using var library =
                new LibraryService(settings);
            await library.IndexAsync();

            IReadOnlyList<string> ordered =
                await library
                    .GetMetadataSortOrderAsync(
                        MetadataFieldKey.Custom(
                            "SORT_VALUE"),
                        LibraryMetadataSortKind
                            .Numeric);

            Assert.Equal(
                [missing, second, song],
                ordered);
            Assert.All(
                await library
                    .GetBrowseRecordsAsync(),
                record =>
                    Assert.Empty(
                        record.Metadata));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(
                    work,
                    recursive: true);
            }
            catch
            {
            }
        }

        static void SetCustom(
            string path,
            string key,
            string value)
        {
            IMediaFile media =
                MediaFile.GetFile(path);
            Assert.IsAssignableFrom<
                    IUserStringMetadata>(
                    media.Tags.First())
                .SetUserString(key, value);
            media.SaveTags();
        }
    }

    [Fact]
    public async Task CustomMetadataBackfillIgnoresUnnamedFieldsWithoutFailingIndex()
    {
        var (work, _, config, song) = Setup("sample.mp3");
        try
        {
            IMediaFile media = MediaFile.GetFile(song);
            ID3v2Tag tag = Assert.Single(media.Tags.OfType<ID3v2Tag>());
            tag.Frames.Add(new UserStringFrame(tag)
            {
                Key = "",
                Value = "unnamed value",
            });
            media.SaveTags();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);

            await library.IndexAsync();

            FileDetails details = Assert.IsType<FileDetails>(
                await library.GetFileDetailsAsync(
                    song, includeArtwork: false));
            Assert.DoesNotContain(details.TextFields,
                field => string.IsNullOrWhiteSpace(field.Key));
            using var database = MetadataDatabase.OpenDatabase(
                Path.Combine(work, "cache.db"));
            Assert.True(database.HasCacheFeature(
                CachedMetadataKeys.CacheFeature));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task IndexAndReindexExposeNativeCustomMetadataToLibraryRows()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            IMediaFile media = MediaFile.GetFile(song);
            var custom = Assert.IsAssignableFrom<
                IUserStringMetadata>(media.Tags.First());
            custom.SetUserString("DJ_SET", "Sunrise");
            media.SaveTags();

            var settings = new AppSettings(
                Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            TrackRecord record = Assert.Single(
                await library.GetAllRecordsAsync());
            Assert.Equal(
                ["Sunrise"],
                record.Metadata[
                    CachedMetadataKeys.Custom("DJ_SET")]);
            FileDetails details = Assert.IsType<FileDetails>(
                await library.GetFileDetailsAsync(
                    song,
                    includeArtwork: false));
            Assert.Contains(details.TextFields, field =>
                field.Key.Equals(
                    "DJ_SET",
                    StringComparison.OrdinalIgnoreCase) &&
                field.Value == "Sunrise");
            Assert.DoesNotContain(details.KnownFields, field =>
                field.Key.StartsWith(
                    CachedMetadataKeys.CustomPrefix,
                    StringComparison.Ordinal));

            string database = Path.Combine(work, "cache.db");
            using (var connection =
                   new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "DELETE FROM Metadata WHERE KeyID IN (" +
                    "SELECT ID FROM MetadataKeys WHERE \"Key\" LIKE '__CUSTOM__:%'); " +
                    "DELETE FROM CacheFeatures WHERE Name = $feature";
                command.Parameters.AddWithValue(
                    "$feature",
                    CachedMetadataKeys.CacheFeature);
                command.ExecuteNonQuery();
            }

            var refresh = await library.IndexAsync();
            Assert.Equal(1, refresh.Modified);
            record = Assert.Single(
                await library.GetAllRecordsAsync());
            Assert.Equal(
                ["Sunrise"],
                record.Metadata[
                    CachedMetadataKeys.Custom("DJ_SET")]);

            media = MediaFile.GetFile(song);
            custom = Assert.IsAssignableFrom<
                IUserStringMetadata>(media.Tags.First());
            custom.SetUserString("DJ_SET", "Sunset");
            media.SaveTags();
            await library.ReindexFileAsync(song);

            record = Assert.Single(
                await library.GetAllRecordsAsync());
            Assert.Equal(
                ["Sunset"],
                record.Metadata[
                    CachedMetadataKeys.Custom("DJ_SET")]);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
            }
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
    public async Task BrowseMetadata_RoundTripsGenreComposerGroupingAndYear()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            IMediaFile media = MediaFile.GetFile(song);
            var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
            writer.SetField(TagFields.Genre, "Modal Jazz");
            writer.SetField(TagFields.Composer, "Miles Davis");
            writer.SetField(TagFields.Grouping, "Studio masters");
            writer.SetField(TagFields.Date, "1959-08-17");
            writer.Save();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            TrackRecord record = Assert.Single(await library.GetAllRecordsAsync());
            Assert.Equal("Modal Jazz", record.Genre);
            Assert.Equal("Miles Davis", record.Composer);
            Assert.Equal("Studio masters", record.Grouping);
            Assert.Equal(1959, record.Year);

            FileDetails details = Assert.IsType<FileDetails>(
                await library.GetFileDetailsAsync(song, includeArtwork: false));
            Assert.Equal(record.Genre, details.Entry.Genre);
            Assert.Equal(record.Composer, details.Entry.Composer);
            Assert.Equal(record.Grouping, details.Entry.Grouping);
            Assert.Equal(record.Year, details.Entry.Year);
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
    public async Task ItunesCanonicalNaming_IsUsedByOrganizationAndAnalysis()
    {
        var (work, music, config, song) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: Path.Combine("iTunes Media", "Music"));
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            string expected = Path.Combine(music, "TestArtist",
                "TestAlbum", "03 TestTitle.flac");
            PlannedMove move = Assert.Single(await library.PreviewMovesAsync());
            Assert.Equal(expected, move.Destination);

            IReadOnlyList<TrackRecord> records = await library.GetAllRecordsAsync();
            RepresentationRepairPreview analysis =
                await new RepresentationRepairService(library)
                    .PreviewAsync(records, settings.Configuration);
            Assert.Contains(analysis.FileActions, action =>
                action.Kind == RepresentationRepairKind.Organize &&
                action.SourcePath == song &&
                action.DestinationPath == expected);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ItunesCanonicalNaming_LoadsCompilationFlagWithoutPerTrackMetadataQueries()
    {
        var (work, music, config, song) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: Path.Combine("iTunes Media", "Music"));
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            BatchWriteResult write = await new TagWriteService(library).ApplyAsync(
                [song], [new TagEdit(TagFields.Compilation, "1")]);
            Assert.Equal(1, write.SavedCount);

            PlannedMove move = Assert.Single(await library.PreviewMovesAsync());

            Assert.Equal(Path.Combine(music, "Compilations",
                "TestAlbum", "03 TestTitle.flac"), move.Destination);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ItunesCanonicalNaming_UsesCorpusPunctuationAndPeriodRules()
    {
        var (work, music, config, song) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: Path.Combine("iTunes Media", "Music"));
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            BatchWriteResult write = await new TagWriteService(library).ApplyAsync(
                [song],
                [
                    new TagEdit(TagFields.AlbumArtist, "R.E.M."),
                    new TagEdit(TagFields.Album, "Happier."),
                    new TagEdit(TagFields.Title, "P.I.M.P."),
                ]);
            Assert.Equal(1, write.SavedCount);

            PlannedMove move = Assert.Single(await library.PreviewMovesAsync());

            Assert.Equal(Path.Combine(music, "R.E.M_", "Happier_",
                "03 P.I.M.P..flac"), move.Destination);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ItunesCanonicalNaming_UsesTaggedDiscNumberInTrackPrefix()
    {
        var (work, music, config, song) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: Path.Combine("iTunes Media", "Music"));
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            BatchWriteResult write = await new TagWriteService(library).ApplyAsync(
                [song], [new TagEdit(TagFields.DiscNumber, "1")]);
            Assert.Equal(1, write.SavedCount);

            PlannedMove move = Assert.Single(await library.PreviewMovesAsync());

            Assert.Equal(Path.Combine(music, "TestArtist", "TestAlbum",
                "1-03 TestTitle.flac"), move.Destination);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ItunesCanonicalNaming_PreservesMatchingExistingDiscPrefixWhenTagIsMissing()
    {
        var (work, music, config, song) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: Path.Combine("iTunes Media", "Music"));
        try
        {
            string canonical = Path.Combine(music, "TestArtist", "TestAlbum",
                "1-03 TestTitle.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            File.Move(song, canonical);

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            Assert.Empty(await library.PreviewMovesAsync());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ItunesCanonicalNaming_MediaRootAddsMusicDirectory()
    {
        var (work, mediaRoot, config, _) = Setup(
            "sample.flac", useItunesCanonicalNaming: true,
            libraryFolder: "iTunes Media");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            PlannedMove move = Assert.Single(await library.PreviewMovesAsync());

            Assert.Equal(Path.Combine(mediaRoot, "Music", "TestArtist",
                "TestAlbum", "03 TestTitle.flac"), move.Destination);
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

            Assert.Equal(0, await library.GetMaterializedArtworkFileCountAsync());
            var before = Assert.Single(await library.GetArtworkAuditFilesAsync());
            Assert.Equal(song, before.Path);
            Assert.False(before.ArtworkScanned);

            _ = await library.GetFirstImageAsync(song);
            Assert.Equal(0, await library.GetMaterializedArtworkFileCountAsync());
            var after = Assert.Single(await library.GetArtworkAuditFilesAsync());
            Assert.True(after.ArtworkScanned);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EveryArtworkMaterializingLibraryPathPublishesCountChange()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            int changes = 0;
            library.ArtworkMaterializationChanged += () => changes++;

            await library.IndexAsync();
            Assert.Equal(1, changes);
            changes = 0;

            _ = await library.GetFileDetailsAsync(song, includeArtwork: false);
            Assert.Equal(0, changes);

            _ = await library.GetFileDetailsAsync(song, includeArtwork: true);
            _ = await library.GetFirstImageAsync(song);
            _ = await library.GetFirstImagesAsync([song]);
            _ = await library.GetImageSignaturesAsync([song]);
            await library.ReindexFileAsync(song);
            IMediaFile savedFile = MediaFile.GetFile(song);
            await library.ReindexFileAsync(song, savedFile);
            await library.ReindexFilesAsync([(song, savedFile)]);

            Assert.Equal(7, changes);
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
            await File.WriteAllBytesAsync(
                png,
                TestImageFactory.Png(
                    200,
                    200,
                    SKColors.Blue));

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
            byte[] cover = CreatePngBytes(96, 96, SKColors.Purple);
            var media = MediaFile.GetFile(song);
            ((IArtworkWriter)media.Tags.First()).SetFrontCover(cover, "image/png");
            media.SaveTags();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Equal(0, await library.GetMaterializedArtworkFileCountAsync());
            Assert.NotNull(await library.GetFileDetailsAsync(song, includeArtwork: false));
            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Equal(0, await library.GetMaterializedArtworkFileCountAsync());

            Assert.Equal(cover, await library.GetFirstImageAsync(song));
            Assert.Equal(1L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Equal(1, await library.GetMaterializedArtworkFileCountAsync());
            Assert.Single((await library.GetFileDetailsAsync(song, includeArtwork: true))!.Images);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Index_EagerArtworkPolicyMaterializesArtworkImmediately()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            byte[] cover = CreatePngBytes(96, 96, SKColors.Orange);
            var media = MediaFile.GetFile(song);
            ((IArtworkWriter)media.Tags.First()).SetFrontCover(cover, "image/png");
            media.SaveTags();

            EditableLibraryConfig editable = EditableLibraryConfig.Load(config);
            int profileIndex = editable.Profiles.FindIndex(profile =>
                profile.Id.Equals(editable.ActiveProfileId,
                    StringComparison.OrdinalIgnoreCase));
            editable.Profiles[profileIndex] = editable.Profiles[profileIndex] with
            {
                Artwork = editable.Profiles[profileIndex].Artwork with
                {
                    ReadAtIndexTime = true,
                },
            };
            editable.Save(config);

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();

            Assert.Equal(1L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Equal(1, await library.GetMaterializedArtworkFileCountAsync());
            Assert.Equal(cover, await library.GetFirstImageAsync(song));
            Assert.Single((await library.GetFileDetailsAsync(
                song, includeArtwork: true))!.Images);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnablingEagerArtworkHydratesPreviouslyDeferredUnchangedFiles()
    {
        var (work, _, config, song) = Setup("sample.flac");
        try
        {
            byte[] cover = CreatePngBytes(72, 72, SKColors.Gold);
            var media = MediaFile.GetFile(song);
            ((IArtworkWriter)media.Tags.First()).SetFrontCover(cover, "image/png");
            media.SaveTags();

            var settings = new AppSettings(Path.Combine(work, "settings.json"));
            settings.LoadConfig(config);
            using var library = new LibraryService(settings);
            await library.IndexAsync();
            Assert.Equal(0L, ReadArtworkScanned(Path.Combine(work, "cache.db")));

            EditableLibraryConfig editable = EditableLibraryConfig.Load(config);
            int profileIndex = editable.Profiles.FindIndex(profile =>
                profile.Id.Equals(editable.ActiveProfileId,
                    StringComparison.OrdinalIgnoreCase));
            editable.Profiles[profileIndex] = editable.Profiles[profileIndex] with
            {
                Artwork = editable.Profiles[profileIndex].Artwork with
                {
                    ReadAtIndexTime = true,
                },
            };
            editable.Save(config);
            settings.LoadConfig(config);

            await library.IndexAsync();

            Assert.Equal(1L, ReadArtworkScanned(Path.Combine(work, "cache.db")));
            Assert.Equal(cover, await library.GetFirstImageAsync(song));
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
                CreatePngBytes(64, 64, SKColors.Green), "image/png");
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

    private static byte[] CreatePngBytes(
        int width,
        int height,
        SKColor color) =>
        TestImageFactory.Png(
            width,
            height,
            color);

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
