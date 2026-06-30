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
                    var res = db.IndexFiles(new[] { scanDir });
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

                    // Re-indexing the untouched files marks them all unchanged.
                    var res2 = db.IndexFiles(new[] { scanDir });
                    Assert.Equal(0, res2.Added);
                    Assert.Equal(3, res2.Unchanged);
                }

                // Raw check that the buffered multi-row metadata INSERTs actually persisted rows.
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
                conn.Open();
                Assert.Equal(3L, ScalarLong(conn, "SELECT COUNT(*) FROM Files"));
                Assert.True(ScalarLong(conn, "SELECT COUNT(*) FROM Metadata") > 0);
                Assert.True(ScalarLong(conn, "SELECT COUNT(*) FROM Metadata WHERE Value = 'TestTitle'") >= 3);
            }
            finally
            {
                MetadataDatabase.IndexFilesPerBatch = savedFiles;
                MetadataDatabase.IndexMetaRowsPerInsert = savedRows;
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
