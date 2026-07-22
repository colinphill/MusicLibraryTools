using System;
using System.IO;
using System.Linq;
using MetadataCaching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // End-to-end coverage of the SQLite indexer: scans real fixtures into a temp database and
    // verifies the cache. Batch sizes are forced tiny so 3 files cross multiple batch commits
    // (transaction re-binding) and the multi-row metadata flush chunks — the riskiest parts of
    // the WAL-batching / buffered-insert work.
    public class IndexingIntegrationTests
    {
        private static readonly string[] Fixtures = { "sample.flac", "sample.mp3", "sample.ogg" };

        [Fact]
        public void IndexThenBuildCacheRoundTrips()
        {
            string scanDir = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scanDir);
            string dbPath = Path.Combine(scanDir, "cache.db");
            string nestedDir = Path.Combine(scanDir, "artist", "album");
            Directory.CreateDirectory(nestedDir);
            foreach (var f in Fixtures)
                File.Copy(MediaFixtures.Path_(f), Path.Combine(f == "sample.ogg" ? nestedDir : scanDir, f));

            int savedFiles = MetadataDatabase.IndexFilesPerBatch;
            int savedRows = MetadataDatabase.IndexMetaRowsPerInsert;
            MetadataDatabase.IndexFilesPerBatch = 1;   // commit after every file
            MetadataDatabase.IndexMetaRowsPerInsert = 2; // force multi-row chunking + remainder
            try
            {
                using (var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath))
                {
                    var res = db.IndexFiles(new[] { scanDir }, ct: TestContext.Current.CancellationToken);
                    Assert.Equal(3, res.Added);
                    Assert.Equal(0, res.Modified);
                    Assert.Equal(0, res.Unchanged);

                    var cache = db.BuildCache(new[] { scanDir });
                    Assert.Equal(3, cache.FileCache.Count);
                    Assert.All(cache.FileCache.Values, e =>
                    {
                        Assert.Equal("TestTitle", e.Title);
                        Assert.Equal("TestArtist", e.Artist);
                        Assert.Equal("TestAlbum", e.Album);
                        Assert.Equal("Rock", e.Genre);
                        Assert.Equal(2021, e.Year);
                    });
                    Assert.NotEmpty(cache.AlbumCache);
                    var health = Assert.Single(db.GetScanRootHealth());
                    Assert.Equal(ScanRootState.Healthy, health.State);
                    Assert.NotNull(health.LastSuccessUtc);
                    Assert.Equal(3, health.Enumerated);
                    Assert.Equal(3, health.MetadataRead);
                    var cachedEntries = cache.FileCache.Values.ToArray();
                    Assert.False(cache.FileCache[Path.Combine(scanDir, "sample.flac")].HasAlbumArtist);
                    Assert.All(cachedEntries, entry => Assert.Equal(string.Empty, entry.AlbumArtist));
                    Assert.All(cachedEntries.Skip(1), entry =>
                    {
                        Assert.Same(cachedEntries[0].Artist, entry.Artist);
                        Assert.Same(cachedEntries[0].AlbumArtist, entry.AlbumArtist);
                        Assert.Same(cachedEntries[0].Album, entry.Album);
                        Assert.Same(cachedEntries[0].StrippedAlbum, entry.StrippedAlbum);
                    });

                    var leanCache = db.BuildCache(new[] { scanDir }, buildSecondaryIndexes: false);
                    Assert.Equal(3, leanCache.FileCache.Count);
                    Assert.Empty(leanCache.AlbumCache);
                    Assert.Empty(leanCache.ArtistCache);
                    Assert.Empty(leanCache.AlbumArtistCache);

                    // Re-indexing the untouched files marks them all unchanged.
                    var res2 = db.IndexFiles(new[] { scanDir }, ct: TestContext.Current.CancellationToken);
                    Assert.Equal(0, res2.Added);
                    Assert.Equal(3, res2.Unchanged);

                    // The targeted reindex path uses Artist internally to locate/group the album,
                    // but must not leak that fallback through the public cache projection.
                    string flac = Path.Combine(scanDir, "sample.flac");
                    Assert.True(db.ReindexFile(flac));
                    MetadataCache reindexed = db.BuildCache([scanDir]);
                    Assert.False(reindexed.FileCache[flac].HasAlbumArtist);
                    Assert.Equal(string.Empty, reindexed.FileCache[flac].AlbumArtist);
                }

                // Raw check that the buffered multi-row metadata INSERTs actually persisted rows.
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
                conn.Open();
                Assert.Equal(3L, ScalarLong(conn, "SELECT COUNT(*) FROM Files"));
                Assert.True(ScalarLong(conn, "SELECT COUNT(*) FROM Metadata") > 0);
                Assert.True(ScalarLong(conn, "SELECT COUNT(*) FROM Metadata WHERE Value = 'TestTitle'") >= 3);
                Assert.Equal(0L, ScalarLong(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'KnownMetadata'"));
                Assert.Equal(1L, ScalarLong(conn,
                    "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = 'ArtworkScanned'"));
                Assert.Equal(1L, ScalarLong(conn,
                    "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = 'HasAlbumArtist'"));
                Assert.Equal(3L, ScalarLong(conn,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('AlbumsLookupIndex', 'FilesPathIndex', 'ImagesHashIndex')"));
            }
            finally
            {
                MetadataDatabase.IndexFilesPerBatch = savedFiles;
                MetadataDatabase.IndexMetaRowsPerInsert = savedRows;
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scanDir, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void IndexProgressReportsStreamingPhasesThroughputAndDeferredArtwork()
        {
            string scanDir = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scanDir);
            string dbPath = Path.Combine(scanDir, "cache.db");
            File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(scanDir, "sample.flac"));
            var progress = new CollectingProgress();

            try
            {
                using var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                db.IndexFiles([scanDir], progress: progress, ct: TestContext.Current.CancellationToken);

                var reports = progress.Snapshot();
                Assert.Contains(reports, item => item.Phase == IndexPhase.Preparing);
                Assert.Contains(reports, item => item.Phase == IndexPhase.Enumeration);
                Assert.Contains(reports, item => item.Phase == IndexPhase.Metadata);
                Assert.Contains(reports, item => item.Phase == IndexPhase.Database);
                Assert.Contains(reports, item => item.Phase == IndexPhase.Artwork && item.ArtworkDeferred);
                var completed = Assert.Single(reports, item => item.Phase == IndexPhase.Completed);
                Assert.Equal(1, completed.Enumerated);
                Assert.Equal(1, completed.Scanned);
                Assert.Equal(1, completed.DatabaseProcessed);
                Assert.True(completed.Elapsed >= TimeSpan.Zero);
                Assert.All(reports, item => Assert.True(item.FilesPerSecond >= 0));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scanDir, true); } catch { }
            }
        }

        [Fact]
        public void PerRootFormatAndPathPoliciesControlIndexingAndRemoveNewlyExcludedFiles()
        {
            string scanDir = Path.Combine(
                Path.GetTempPath(), "mlt_idx_policy_" + Guid.NewGuid().ToString("N"));
            string musicDir = Path.Combine(scanDir, "Music");
            string skippedDir = Path.Combine(musicDir, "Skip");
            string otherDir = Path.Combine(scanDir, "Other");
            Directory.CreateDirectory(skippedDir);
            Directory.CreateDirectory(otherDir);
            string included = Path.Combine(musicDir, "included.flac");
            File.Copy(MediaFixtures.Path_("sample.flac"), included);
            File.Copy(MediaFixtures.Path_("sample.mp3"), Path.Combine(musicDir, "wrong-format.mp3"));
            File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(skippedDir, "excluded.flac"));
            File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(otherDir, "outside.flac"));
            string dbPath = Path.Combine(scanDir, "cache.db");

            try
            {
                using var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                var policy = new ScanRootDefinition(scanDir, [])
                {
                    Formats = [".flac"],
                    IncludePatterns = ["Music/**"],
                    ExcludePatterns = ["Music/Skip/**"],
                };

                var indexed = db.IndexFiles([policy],
                    ct: TestContext.Current.CancellationToken);

                Assert.Equal(1, indexed.Added);
                Assert.Equal([included], db.BuildCache([scanDir]).FileCache.Keys);

                var excludeEverything = policy with
                {
                    ExcludePatterns = ["Music/**"],
                };
                var rescanned = db.IndexFiles([excludeEverything],
                    ct: TestContext.Current.CancellationToken);

                Assert.Equal(1, rescanned.Removed);
                Assert.Empty(db.BuildCache([scanDir]).FileCache);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scanDir, true); } catch { }
            }
        }

        private sealed class CollectingProgress : IProgress<IndexProgress>
        {
            private readonly object sync = new();
            private readonly List<IndexProgress> reports = [];

            public void Report(IndexProgress value)
            {
                lock (sync) reports.Add(value);
            }

            public IReadOnlyList<IndexProgress> Snapshot()
            {
                lock (sync) return reports.ToList();
            }
        }

        [Fact]
        public void OpenDatabaseRestoresPerformanceIndexesOnExistingDatabase()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string dbPath = Path.Combine(directory, "cache.db");

            try
            {
                using (MetadataDatabase.OpenDatabase("sqlite:" + dbPath)) { }
                using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    conn.Open();
                    using var command = conn.CreateCommand();
                    command.CommandText =
                        "DROP INDEX AlbumsLookupIndex; DROP INDEX FilesPathIndex; DROP INDEX ImagesHashIndex;" +
                        "ALTER TABLE Files DROP COLUMN ArtworkScanned;" +
                        "ALTER TABLE Files DROP COLUMN HasAlbumArtist;";
                    command.ExecuteNonQuery();
                }

                using (MetadataDatabase.OpenDatabase("sqlite:" + dbPath)) { }
                using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    conn.Open();
                    Assert.Equal(3L, ScalarLong(conn,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('AlbumsLookupIndex', 'FilesPathIndex', 'ImagesHashIndex')"));
                    Assert.Equal(1L, ScalarLong(conn,
                        "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = 'ArtworkScanned'"));
                    Assert.Equal(1L, ScalarLong(conn,
                        "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = 'HasAlbumArtist'"));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void ExistingDatabaseMigrationRestoresExplicitAlbumArtistPresence()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string song = Path.Combine(directory, "sample.flac");
            string dbPath = Path.Combine(directory, "cache.db");
            File.Copy(MediaFixtures.Path_("sample.flac"), song);
            var media = MediaFile.GetFile(song);
            var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
            writer.SetField(TagFields.AlbumArtist, "Explicit Artist");
            writer.Save();

            try
            {
                using (var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath))
                {
                    db.IndexFiles([directory], ct: TestContext.Current.CancellationToken);
                    Assert.True(db.BuildCache([directory]).FileCache[song].HasAlbumArtist);
                }
                using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    conn.Open();
                    using var command = conn.CreateCommand();
                    command.CommandText = "ALTER TABLE Files DROP COLUMN HasAlbumArtist";
                    command.ExecuteNonQuery();
                }

                using var migrated = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                Assert.True(migrated.BuildCache([directory]).FileCache[song].HasAlbumArtist);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void ExistingDatabaseProjectsBrowseMetadataWithoutRescanningFiles()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string song = Path.Combine(directory, "sample.flac");
            string dbPath = Path.Combine(directory, "cache.db");
            File.Copy(MediaFixtures.Path_("sample.flac"), song);

            try
            {
                using (var database = MetadataDatabase.OpenDatabase("sqlite:" + dbPath))
                    database.IndexFiles([directory], ct: TestContext.Current.CancellationToken);

                using (var connection = new SqliteConnection(
                           new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "INSERT OR IGNORE INTO MetadataKeys (\"Key\") VALUES ('Composer');" +
                        "INSERT OR IGNORE INTO MetadataKeys (\"Key\") VALUES ('Grouping');" +
                        "INSERT INTO Metadata (FileID, KeyID, Value) " +
                        "SELECT f.ID, k.ID, 'Legacy Composer' FROM Files f, MetadataKeys k WHERE k.\"Key\" = 'Composer';" +
                        "INSERT INTO Metadata (FileID, KeyID, Value) " +
                        "SELECT f.ID, k.ID, 'Legacy Group' FROM Files f, MetadataKeys k WHERE k.\"Key\" = 'Grouping';";
                    command.ExecuteNonQuery();
                }

                using var reopened = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                MetadataCacheEntry entry = reopened.BuildCache([directory]).FileCache[song];
                Assert.Equal("Legacy Composer", entry.Composer);
                Assert.Equal("Legacy Group", entry.Grouping);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { /* best effort */ }
            }
        }

        [Theory]
        [InlineData("flac")]
        [InlineData("*.FLAC")]
        [InlineData(".FlAc")]
        public void BuildCacheNormalizesRootAndExtensionFilters(string extensionFilter)
        {
            string scanDir = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scanDir);
            string dbPath = Path.Combine(scanDir, "cache.db");
            string mediaPath = Path.Combine(scanDir, "SAMPLE.FLAC");
            File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);

            try
            {
                using var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                string rootWithSeparator = scanDir + Path.DirectorySeparatorChar;

                Assert.Equal(1, db.IndexFiles(new[] { rootWithSeparator }, ct: TestContext.Current.CancellationToken).Added);

                var cache = db.BuildCache(new[] { (scanDir, new[] { extensionFilter }) });
                Assert.Single(cache.FileCache);
                Assert.True(cache.FileCache.ContainsKey(mediaPath));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scanDir, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void ScanRootsSupportMultipleLogicalSetsOrNoLogicalSet()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            string firstRoot = Path.Combine(directory, "first");
            string unassignedRoot = Path.Combine(directory, "unassigned");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(unassignedRoot);
            string firstFile = Path.Combine(firstRoot, "first.flac");
            string unassignedFile = Path.Combine(unassignedRoot, "unassigned.flac");
            File.Copy(MediaFixtures.Path_("sample.flac"), firstFile);
            File.Copy(MediaFixtures.Path_("sample.flac"), unassignedFile);
            string dbPath = Path.Combine(directory, "cache.db");

            try
            {
                using var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                db.IndexFiles([
                    new ScanRootDefinition(firstRoot, ["Primary", "Portable2"]),
                    new ScanRootDefinition(unassignedRoot, []),
                ], ct: TestContext.Current.CancellationToken);

                var roots = db.GetScanRoots();
                Assert.Equal(["Portable2", "Primary"], roots.Single(root => root.Path == firstRoot).Sets);
                Assert.Empty(roots.Single(root => root.Path == unassignedRoot).Sets);
                Assert.Equal(["Portable2", "Primary"], db.GetScanRootHealth().Single(root => root.Root == firstRoot).Sets);
                Assert.True(db.BuildCacheForSets(["primary"]).FileCache.ContainsKey(firstFile));
                Assert.False(db.BuildCacheForSets(["Primary"]).FileCache.ContainsKey(unassignedFile));
                Assert.True(db.BuildCache([unassignedRoot]).FileCache.ContainsKey(unassignedFile));

                var second = db.IndexFiles([
                    new ScanRootDefinition(firstRoot, ["Portable2", "Archive3"]),
                    new ScanRootDefinition(unassignedRoot, []),
                ], ct: TestContext.Current.CancellationToken);

                Assert.Equal(2, second.Unchanged);
                Assert.Empty(db.BuildCacheForSets(["Primary"]).FileCache);
                Assert.True(db.BuildCacheForSets(["portable2"]).FileCache.ContainsKey(firstFile));
                Assert.True(db.BuildCacheForSets(["Archive3"]).FileCache.ContainsKey(firstFile));
                Assert.Equal(["Archive3", "Portable2"], db.GetScanRoots().Single(root => root.Path == firstRoot).Sets);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        [Fact]
        public void ExistingNumericMembershipsMigrateToTextWithoutRescanning()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string dbPath = Path.Combine(directory, "cache.db");
            try
            {
                using (MetadataDatabase.OpenDatabase("sqlite:" + dbPath)) { }
                using (var connection = new SqliteConnection(
                           new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "DROP TABLE ScanSetMemberships;" +
                        "CREATE TABLE ScanSetMemberships (ScanSetID INTEGER NOT NULL REFERENCES ScanSets (ID) ON DELETE CASCADE, " +
                        "SetNumber INTEGER NOT NULL, PRIMARY KEY (ScanSetID, SetNumber));" +
                        "INSERT INTO ScanSets (Path) VALUES ('Z:\\Legacy');" +
                        "INSERT INTO ScanSetMemberships (ScanSetID, SetNumber) VALUES (last_insert_rowid(), 42);";
                    command.ExecuteNonQuery();
                }

                using (MetadataDatabase.OpenDatabase("sqlite:" + dbPath)) { }
                using var migrated = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
                migrated.Open();
                Assert.Equal(1L, ScalarLong(migrated,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ScanSetMemberships'"));
                Assert.Equal(1L, ScalarLong(migrated,
                    "SELECT COUNT(*) FROM pragma_table_info('ScanSetMemberships') WHERE name='SetName'"));
                using var setName = migrated.CreateCommand();
                setName.CommandText = "SELECT SetName FROM ScanSetMemberships";
                Assert.Equal("42", (string)setName.ExecuteScalar()!);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        [Fact]
        public void ExplicitAlbumArtistEqualToArtistRemainsExplicit()
        {
            string directory = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string song = Path.Combine(directory, "sample.flac");
            string dbPath = Path.Combine(directory, "cache.db");
            File.Copy(MediaFixtures.Path_("sample.flac"), song);
            var media = MediaFile.GetFile(song);
            var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
            writer.SetField(TagFields.AlbumArtist, media.Tags.First().Artist);
            writer.Save();

            try
            {
                using var database = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                database.IndexFiles([directory], ct: TestContext.Current.CancellationToken);
                MetadataCacheEntry indexed = database.BuildCache([directory]).FileCache[song];
                Assert.True(indexed.HasAlbumArtist);
                Assert.Equal(indexed.Artist, indexed.AlbumArtist);

                Assert.True(database.ReindexFile(song));
                MetadataCacheEntry reindexed = database.BuildCache([directory]).FileCache[song];
                Assert.True(reindexed.HasAlbumArtist);
                Assert.Equal(reindexed.Artist, reindexed.AlbumArtist);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(directory, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public async Task WriterFailureUnblocksBoundedProducer()
        {
            string scanDir = Path.Combine(Path.GetTempPath(), "mlt_idx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scanDir);
            string dbPath = Path.Combine(scanDir, "cache.db");
            for (int i = 0; i < 8; i++)
                File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(scanDir, $"sample-{i}.flac"));

            int savedBound = MetadataDatabase.IndexFileQueueBound;
            MetadataDatabase.IndexFileQueueBound = 1;
            try
            {
                using var db = MetadataDatabase.OpenDatabase("sqlite:" + dbPath);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TRIGGER FailFiles BEFORE INSERT ON Files BEGIN SELECT RAISE(FAIL, 'forced writer failure'); END;";
                    command.ExecuteNonQuery();
                }

                var token = TestContext.Current.CancellationToken;
                var operation = Task.Run(() => db.IndexFiles(new[] { scanDir }, ct: token), token);
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(10), token));
            }
            finally
            {
                MetadataDatabase.IndexFileQueueBound = savedBound;
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scanDir, true); } catch { /* best effort */ }
            }
        }

        private static long ScalarLong(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar();
        }
    }
}
