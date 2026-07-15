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
            foreach (var f in Fixtures)
                File.Copy(MediaFixtures.Path_(f), Path.Combine(scanDir, f));

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
                    });
                    Assert.NotEmpty(cache.AlbumCache);

                    var leanCache = db.BuildCache(new[] { scanDir }, buildSecondaryIndexes: false);
                    Assert.Equal(3, leanCache.FileCache.Count);
                    Assert.Empty(leanCache.AlbumCache);
                    Assert.Empty(leanCache.ArtistCache);
                    Assert.Empty(leanCache.AlbumArtistCache);

                    // Re-indexing the untouched files marks them all unchanged.
                    var res2 = db.IndexFiles(new[] { scanDir }, ct: TestContext.Current.CancellationToken);
                    Assert.Equal(0, res2.Added);
                    Assert.Equal(3, res2.Unchanged);
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
                        "ALTER TABLE Files DROP COLUMN ArtworkScanned;";
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
                }
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
