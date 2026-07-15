using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
#if SQLITE
using Microsoft.Data.Sqlite;
#endif
using System.IO;
using MusicFileUtilities;
using MusicLibraryTools;
using System.Threading;
using System.Data.Common;
#if SQLSERVER
using System.Data.SqlClient;
#endif
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

[assembly: InternalsVisibleTo("MusicFileUtilities.Tests")]

namespace MetadataCaching
{

    /// <summary>
    /// A snapshot of indexing progress, reported periodically from <see cref="MetadataDatabase.IndexFilesAsync"/>
    /// so a GUI can show a live count without the library writing to the console.
    /// </summary>
    public readonly record struct IndexProgress(int Scanned, int Added, int Modified, int Unchanged);

    public partial class MetadataCacheEntry
    {
        public MetadataCacheEntry(DbDataReader reader)
        {
            _lastwritetime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            _codecname = reader.GetString("CodecName");
            _codectype = (CodecType)Enum.Parse(typeof(CodecType), reader.GetString("CodecType"));
            _averagebitrate = (uint)reader.GetInt64("AverageBitrate");
            _maxbitrate = (uint)reader.GetInt64("MaxBitrate"); ;
            _bitspersample = (uint)reader.GetInt64("BitsPerSample");
            _samplerate = (uint)reader.GetInt64("SampleRate");
            _channels = (uint)reader.GetInt64("Channels");
            _durationinseconds = (int)reader.GetInt64("DurationInFrames") / 75;
            _artist = reader.GetString("Artist");
            _albumartist = reader.GetString("AlbumArtist");
            _album = reader.GetString("Album");
            _tracknumber = reader.IsDBNull("TrackNumber") ? null : (int)reader.GetInt64("TrackNumber");
            _tracktotal = reader.IsDBNull("TrackTotal") ? null : (int)reader.GetInt64("TrackTotal");
            _discnumber = reader.IsDBNull("DiscNumber") ? null : (int)reader.GetInt64("DiscNumber");
            _disctotal = reader.IsDBNull("DiscTotal") ? null : (int)reader.GetInt64("DiscTotal");
            _releasedate = reader.IsDBNull("ReleaseDate") ? null : reader.GetString("ReleaseDate");
            _title = reader.GetString("Track");
        }

    }

    internal static class DbHelpers
    {

        public static DbCommand CreateCommand(this DbTransaction trans)
        {
            var res = trans.Connection.CreateCommand();
            res.Transaction = trans;
            return res;
        }

        public static DbParameter Add(this DbParameterCollection coll, string name, DbType dbtype, ParameterDirection dir = ParameterDirection.Input, string typename = null)
        {
#if SQLITE
            if (coll is SqliteParameterCollection litecoll)
            {
                var parm = new SqliteParameter();
                parm.ParameterName = name;
                parm.Direction = dir;
                parm.DbType = dbtype;
                litecoll.Add(parm);
                return parm;
            }
#endif
#if SQLSERVER
            if (coll is SqlParameterCollection sqlcoll)
            {
                SqlParameter parm = new SqlParameter();
                parm.ParameterName = name;
                parm.Direction = dir;
                if (typename != null)
                {
                    parm.SqlDbType = SqlDbType.Structured;
                    parm.TypeName = typename;
                }
                else
                {
                    try
                    {
                        parm.DbType = dbtype;
                    }
                    catch
                    {
                        if (dbtype == DbType.Object)
                            parm.SqlDbType = SqlDbType.VarBinary;
                        else
                            throw;
                    }
                    if (dbtype == DbType.Object)
                        parm.SqlDbType = SqlDbType.VarBinary;
                }
                sqlcoll.Add(parm);
                return parm;
            }
#endif
            throw new NotSupportedException();
        }
    }


    public class MetadataDatabase : IDisposable
    {
        private DbConnection conn_;
        private string lastidsql_;
        private List<(string Path, long ID)> scanSetsCache_;

        // Indexer write-batching tunables. Defaults bound the WAL / per-statement overhead on a
        // large library; tests lower them to exercise the multi-commit and multi-row-flush paths
        // without huge inputs.
        internal static int IndexFilesPerBatch = 2000;
        internal static int IndexMetaRowsPerInsert = 1000;

        // Scan-side tunables. IndexScanParallelism is the GLOBAL cap on concurrent subtree
        // scanners across all scan sets (per-set caps multiply by the set count and can
        // swamp a NAS). IndexFileQueueBound caps parsed-but-unwritten files so parsing
        // can't run arbitrarily far ahead of the SQLite writer (parsed files hold artwork
        // bytes, so an unbounded queue can balloon to GBs on a big scan).
        internal static int IndexScanParallelism = GetDefaultIndexScanParallelism();
        internal static int IndexFileQueueBound = 256;

        /// <summary>
        /// Global cap on concurrent filesystem/metadata readers used by this database instance.
        /// Network shares generally benefit from overlapping more opens than local disks. Set
        /// <c>MLT_INDEX_PARALLELISM</c> to override the default (16, clamped to 1-64), or set this
        /// property directly before calling <see cref="IndexFilesAsync"/>.
        /// </summary>
        public int ScanParallelism { get; set; } = IndexScanParallelism;

        private static int GetDefaultIndexScanParallelism()
        {
            string configured = Environment.GetEnvironmentVariable("MLT_INDEX_PARALLELISM");
            return int.TryParse(configured, out int value) ? Math.Clamp(value, 1, 64) : 16;
        }

        private sealed class ScanFileKeyComparer : IEqualityComparer<(long ScanSetID, string Path)>
        {
            private static readonly StringComparer PathComparer =
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            public bool Equals(
                (long ScanSetID, string Path) x,
                (long ScanSetID, string Path) y) =>
                x.ScanSetID == y.ScanSetID && PathComparer.Equals(x.Path, y.Path);

            public int GetHashCode((long ScanSetID, string Path) value) =>
                HashCode.Combine(value.ScanSetID, PathComparer.GetHashCode(value.Path));
        }

        private sealed class ExistingIndexedFile(long id, long length, DateTime lastWriteTime)
        {
            public long ID { get; } = id;
            public long Length { get; } = length;
            public DateTime LastWriteTime { get; } = lastWriteTime;
            public int Hit;
        }

#if SQLITE
        private static readonly string[] sqlitecreationsql_ = {
            "PRAGMA foreign_keys = off;\r\n",

            "CREATE TABLE ScanSets (ID INTEGER PRIMARY KEY, Path TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID BIGINT NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID INTEGER PRIMARY KEY, ArtistID BIGINT REFERENCES Artists (ID) NOT NULL, AlbumID BIGINT REFERENCES Albums (ID) NOT NULL, Name TEXT NOT NULL, TrackNumber BIGINT, TrackTotal BIGINT, DiscNumber BIGINT, DiscTotal BIGINT, ReleaseDate TEXT);\r\n" +
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, Path TEXT NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, TagType TEXT NOT NULL, ArtworkScanned BIGINT NOT NULL);\r\n" +
            "CREATE TABLE Images (ID INTEGER PRIMARY KEY, Hash TEXT NOT NULL, ImageType TEXT NOT NULL, Width BIGINT NOT NULL, Height BIGINT NOT NULL, Size BIGINT NOT NULL, Data BLOB NOT NULL);\r\n" +
            "CREATE TABLE ImageMetadata (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, ImageID BIGINT REFERENCES Images (ID), Description TEXT NOT NULL, Category TEXT NOT NULL);\r\n" +
            "CREATE TABLE MetadataKeys (ID INTEGER PRIMARY KEY, \"Key\" TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, KeyID BIGINT REFERENCES MetadataKeys (ID) NOT NULL, Value TEXT NOT NULL);\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
            "CREATE INDEX AlbumsLookupIndex ON Albums (ScanSetID ASC, Path ASC, AlbumArtistID ASC, Name ASC);\r\n" +
            "CREATE INDEX FilesPathIndex ON Files (Path ASC);\r\n" +
            "CREATE INDEX ImagesHashIndex ON Images (Hash ASC);\r\n" +
            "CREATE INDEX ImageMetadataFileIDIndex ON ImageMetadata (FileID ASC);\r\n" +
            "CREATE INDEX MetadataKeyIDIndex ON Metadata (KeyID ASC);\r\n" +
            "CREATE INDEX MetadataFileIDIndex ON Metadata (FileID ASC);\r\n" +
            "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
            "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n",

            "CREATE VIEW MetadataSummaryView AS SELECT Files.*, Albums.ScanSetID AS ScanSetID, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtist,\r\n" +
            "   Albums.Name AS Album, Albums.Path AS AlbumPath, Tracks.TrackNumber AS TrackNumber, Tracks.TrackTotal AS TrackTotal,\r\n" +
            "   Tracks.DiscNumber AS DiscNumber, Tracks.DiscTotal AS DiscTotal, Tracks.ReleaseDate AS ReleaseDate, Tracks.Name AS Track FROM\r\n" +
            "   Files JOIN Tracks ON Files.ID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
            "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n",
            };
#endif

#if SQLSERVER
        private static readonly string[] sqlservercreationsql_ = {
            "CREATE TABLE ScanSets (ID BIGINT IDENTITY PRIMARY KEY, Path NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID BIGINT IDENTITY PRIMARY KEY, Name NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE AlbumArtists (ID BIGINT IDENTITY PRIMARY KEY, Name NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID BIGINT IDENTITY PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID BIGINT NOT NULL REFERENCES AlbumArtists (ID), Name NVARCHAR(MAX) NOT NULL, Path NVARCHAR(MAX) NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID BIGINT PRIMARY KEY, ArtistID BIGINT REFERENCES Artists (ID) NOT NULL, AlbumID BIGINT REFERENCES Albums (ID) NOT NULL, Name NVARCHAR(MAX) NOT NULL, TrackNumber BIGINT, TrackTotal BIGINT, DiscNumber BIGINT, DiscTotal BIGINT, ReleaseDate NVARCHAR(MAX));\r\n" +
            "CREATE TABLE Files (ID BIGINT IDENTITY PRIMARY KEY, Path NVARCHAR(512) NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, CodecName NVARCHAR(MAX) NOT NULL, CodecType NVARCHAR(MAX) NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, TagType NVARCHAR(64) NOT NULL, ArtworkScanned BIGINT NOT NULL);\r\n" +
            "CREATE TABLE Images (ID BIGINT IDENTITY PRIMARY KEY, Hash VARCHAR(64), ImageType NVARCHAR(MAX) NOT NULL, Width BIGINT NOT NULL, Height BIGINT NOT NULL, Size BIGINT NOT NULL, Data VARBINARY(MAX) NOT NULL);\r\n" +
            "CREATE TABLE MetadataKeys (ID BIGINT IDENTITY PRIMARY KEY, \"Key\" NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID BIGINT IDENTITY PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, KeyID BIGINT REFERENCES MetadataKeys (ID) NOT NULL, Value NVARCHAR(MAX) NOT NULL);\r\n" +
            "CREATE TABLE ImageMetadata (ID BIGINT IDENTITY PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, ImageID BIGINT REFERENCES Images (ID) NOT NULL, Description NVARCHAR(MAX) NOT NULL, Category NVARCHAR(MAX) NOT NULL);\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
            "CREATE INDEX ImageMetadataFileIDIndex ON ImageMetadata (FileID ASC);\r\n" +
            "CREATE INDEX MetadataKeyIDIndex ON Metadata (KeyID ASC);\r\n" +
            "CREATE INDEX MetadataFileIDIndex ON Metadata (FileID ASC);\r\n" +
            "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
            "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n",

            "CREATE TYPE dbo.MetadataTableType AS TABLE (FileID BIGINT, KeyID BIGINT, Value NVARCHAR(MAX));\r\n",

            "CREATE VIEW MetadataSummaryView AS SELECT Files.*, Albums.ScanSetID AS ScanSetID, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtist,\r\n" +
            "   Albums.Name AS Album, Albums.Path AS AlbumPath, Tracks.TrackNumber AS TrackNumber, Tracks.TrackTotal AS TrackTotal,\r\n" +
            "   Tracks.DiscNumber AS DiscNumber, Tracks.DiscTotal AS DiscTotal, Tracks.ReleaseDate AS ReleaseDate, Tracks.Name AS Track FROM\r\n" +
            "   Files JOIN Tracks ON Files.ID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
            "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n",
            };
#endif

        private MetadataDatabase()
        {
        }

        public static MetadataDatabase OpenDatabase(string conn)
        {
#if SQLITE
            if (conn.ToLower().StartsWith("sqlite:"))
                return OpenSqliteDatabase(conn.Substring(7));
#endif
            if (conn.ToLower().StartsWith("sql:"))
            {
#if SQLSERVER
                string server = "(local)";
                string database = "metadata";
                string username = null;
                string password = null;
                bool utf8 = false;
                var args = conn.Substring(4).Split(':');
                foreach (var arg in args)
                {
                    var kv = arg.Split('=');
                    switch (kv[0].ToLower())
                    {
                        case "server":
                            server = kv[1];
                            break;
                        case "database":
                            database = kv[1];
                            break;
                        case "user":
                            username = kv[1];
                            break;
                        case "password":
                            password = kv[1];
                            break;
                        case "utf8":
                            utf8 = bool.Parse(kv[1]);
                            break;
                        default:
                            throw new ArgumentException("Bad connection string", "conn");
                    }
                }
                return OpenSqlServerDatabase(database, server, username, password, utf8);
#else
                throw new NotSupportedException();
#endif
            }
#if SQLITE
            return OpenSqliteDatabase(conn);
#else
            throw new NotSupportedException();
#endif
        }

#if SQLITE
        private static MetadataDatabase OpenSqliteDatabase(string filename)
        {
            var res = new MetadataDatabase();
            res.lastidsql_ = "SELECT last_insert_rowid();";
            bool createtables = !File.Exists(filename);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = filename,
                //DateTimeKind = DateTimeKind.Utc
            };
            res.conn_ = new SqliteConnection(builder.ConnectionString);
            res.conn_.Open();
            using var trans = res.conn_.BeginTransaction();
            try
            {
                using var comm = trans.CreateCommand();
                if (createtables)
                {
                    foreach (string sql in sqlitecreationsql_)
                    {
                        comm.CommandText = sql;
                        comm.ExecuteNonQuery();
                    }
                }
                trans.Commit();
            }
            catch
            {
                trans.Rollback();
                throw;
            }
            using var pcomm = res.conn_.CreateCommand();
            pcomm.CommandText = "PRAGMA foreign_keys = on;\r\nPRAGMA journal_mode = WAL;\r\npragma synchronous = normal;\r\n";
            pcomm.ExecuteNonQuery();

            // Metadata already stores the TagFields name/value projection used by the app. Older
            // builds duplicated every row into KnownMetadata; discard that redundant table. Add
            // indexes used by per-file refresh/detail queries for both new and existing databases.
            // SQLite can reuse freed pages without forcing an expensive VACUUM during startup.
            using var mcomm = res.conn_.CreateCommand();
            mcomm.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = 'ArtworkScanned'";
            if (Convert.ToInt64(mcomm.ExecuteScalar()) == 0)
            {
                // Existing caches already contain eagerly indexed artwork, so migrate their rows
                // as resolved. New/modified files explicitly insert ArtworkScanned = 0 below.
                mcomm.CommandText = "ALTER TABLE Files ADD COLUMN ArtworkScanned BIGINT NOT NULL DEFAULT 1";
                mcomm.ExecuteNonQuery();
            }
            mcomm.CommandText =
                "DROP TABLE IF EXISTS KnownMetadata;\r\n" +
                "CREATE INDEX IF NOT EXISTS AlbumsLookupIndex ON Albums (ScanSetID, Path, AlbumArtistID, Name);\r\n" +
                "CREATE INDEX IF NOT EXISTS FilesPathIndex ON Files (Path);\r\n" +
                "CREATE INDEX IF NOT EXISTS ImagesHashIndex ON Images (Hash);";
            mcomm.ExecuteNonQuery();

            return res;
        }
#endif

#if SQLSERVER
        private static MetadataDatabase OpenSqlServerDatabase(string database, string server = "(local)", string username = null, string password = null, bool utf8 = false)
        {
            var res = new MetadataDatabase();
            res.lastidsql_ = "SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";
            var builder = new SqlConnectionStringBuilder();
            builder.DataSource = server;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                builder.IntegratedSecurity = true;
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = username;
                builder.Password = password;
            }
            res.conn_ = new SqlConnection(builder.ConnectionString);
            res.conn_.Open();
            bool createtables = false;
            using (var comm = res.conn_.CreateCommand())
            {
                comm.CommandText = "IF DB_ID('" + database + "') IS NULL SELECT 0 ELSE SELECT 1";
                if ((int)comm.ExecuteScalar() == 0)
                {
                    comm.CommandText = "CREATE DATABASE " + database;
                    comm.ExecuteNonQuery();
                    if (utf8)
                        comm.CommandText = "ALTER DATABASE " + database + " COLLATE Latin1_General_100_BIN2_UTF8";
                    else
                        comm.CommandText = "ALTER DATABASE " + database + " COLLATE Latin1_General_BIN2";
                    comm.ExecuteNonQuery();
                    comm.CommandText = "USE " + database;
                    comm.ExecuteNonQuery();
                    createtables = true;
                }
                else
                {
                    comm.CommandText = "USE " + database;
                    comm.ExecuteNonQuery();
                    comm.CommandText = "IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AlbumArtists') SELECT 1 ELSE SELECT 0";
                    if ((int)comm.ExecuteScalar() == 0)
                        createtables = true;
                }
            }

            if (createtables)
            {
                using var trans = res.conn_.BeginTransaction();
                try
                {
                    using var comm = trans.CreateCommand();
                    foreach (string sql in sqlservercreationsql_)
                    {
                        if (utf8)
                            comm.CommandText = sql.Replace("NVARCHAR", "VARCHAR");
                        else
                            comm.CommandText = sql;
                        comm.ExecuteNonQuery();
                    }
                    trans.Commit();
                }
                catch
                {
                    trans.Rollback();
                    throw;
                }
            }

            return res;
        }
#endif

        public MetadataCache BuildCache(
            IEnumerable<(string Path, string[] Extensions)> paths,
            bool buildSecondaryIndexes = true)
        {
            var dbsets = new List<(string Path, long ID)>();
            using (var setscomm = conn_.CreateCommand())
            {
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using var reader = setscomm.ExecuteReader();
                while (reader.Read())
                    dbsets.Add((reader.GetString("Path"), reader.GetInt64("ID")));
            }

            MetadataCache cache = new MetadataCache(buildSecondaryIndexes);
            var sharedStrings = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                string requestedPath = Path.TrimEndingDirectorySeparator(path.Path);
                var set = dbsets.FirstOrDefault(s => Path.TrimEndingDirectorySeparator(s.Path).Equals(requestedPath, StringComparison.InvariantCultureIgnoreCase));
                if (set == default)
                    continue;
                HashSet<string> extensions = path.Extensions is null
                    ? null
                    : path.Extensions
                        .Select(NormalizeExtensionFilter)
                        .Where(e => e.Length != 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var querycomm = conn_.CreateCommand();
                querycomm.CommandText = "SELECT Path, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, Artist, AlbumArtist, Album, TrackNumber, TrackTotal, DiscNumber, DiscTotal, ReleaseDate, Track, AlbumPath FROM MetadataSummaryView WHERE ScanSetID = " + set.ID;
                using var reader = querycomm.ExecuteReader();
                int oAlbumPath = reader.GetOrdinal("AlbumPath"), oPath = reader.GetOrdinal("Path");
                while (reader.Read())
                {
                    var ce = new MetadataCacheEntry(reader);
                    ce.Strip(sharedStrings);
                    var fullpath = Path.Combine(set.Path, reader.GetString(oAlbumPath), reader.GetString(oPath));
                    if (extensions is null || extensions.Count == 0)
                        cache.AddDBCacheEntry(fullpath, ce);
                    else if (extensions.Contains(Path.GetExtension(fullpath)))
                        cache.AddDBCacheEntry(fullpath, ce);
                }
            }
            return cache;
        }

        private static string NormalizeExtensionFilter(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return string.Empty;

            extension = extension.Trim();
            if (extension.StartsWith("*.", StringComparison.Ordinal))
                extension = extension[1..];
            else if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;
            return extension;
        }

        public MetadataCache BuildCache(IEnumerable<string> paths, bool buildSecondaryIndexes = true)
        {
            return BuildCache(
                paths.Select<string, (string Path, string[] Extensions)>(p => (p, null)),
                buildSecondaryIndexes);
        }

        public (int Added, int Modified, int Removed, int Unchanged) IndexFiles(IEnumerable<string> paths, bool deletemissingsets = false, IProgress<IndexProgress> progress = null, CancellationToken ct = default)
        {
            return IndexFilesAsync(paths, deletemissingsets, progress, ct).GetAwaiter().GetResult();
        }

        // progress (optional): when supplied, periodic IndexProgress snapshots are reported instead
        // of the console "Scanned: N" line, so a GUI can drive its own progress UI. ct (optional):
        // cooperative cancellation — the file walk stops at the next file boundary; whatever was
        // already queued is still committed (the DB is a self-healing cache), and removal detection
        // is skipped on cancel so a partial scan never deletes files it simply hadn't reached.
        public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexFilesAsync(IEnumerable<string> paths, bool deletemissingsets = false, IProgress<IndexProgress> progress = null, CancellationToken ct = default)
        {
            var sets = new List<(string Path, long ID)>();
            var missedsets = new List<long>();

            using (var setscomm = conn_.CreateCommand())
            {
                // Materialized: this is re-enumerated once per ScanSets row below, and `paths`
                // itself may be a deferred query from the caller.
                var modpaths = paths.Select(Path.TrimEndingDirectorySeparator).ToList();
                var dbsets = new List<(string Path, long ID, bool Hit)>();
                var missing = new List<string>();
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt64(1), false));
                // O(1) membership instead of an O(sets) LINQ scan per requested path.
                var dbsetpaths = new HashSet<string>(dbsets.Select(s => Path.TrimEndingDirectorySeparator(s.Path)), StringComparer.InvariantCultureIgnoreCase);
                foreach (var path in modpaths)
                    if (!dbsetpaths.Contains(path))
                        missing.Add(path);
                setscomm.CommandText = "INSERT INTO ScanSets (Path) VALUES (@Path);";
                var pathvar = setscomm.Parameters.Add("@Path", DbType.String);
                using (var transaction = conn_.BeginTransaction())
                {
                    setscomm.Transaction = transaction;
                    foreach (var path in missing)
                    {
                        pathvar.Value = path;
                        setscomm.ExecuteNonQuery();                       
                    }
                    transaction.Commit();
                }
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                setscomm.Parameters.Clear();
                setscomm.Transaction = null;
                dbsets.Clear();
                var requestedpaths = new HashSet<string>(modpaths, StringComparer.InvariantCultureIgnoreCase);
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                    {
                        var setpath = reader.GetString(0);
                        dbsets.Add((setpath, reader.GetInt64(1), requestedpaths.Contains(Path.TrimEndingDirectorySeparator(setpath))));
                    }
                sets.AddRange(dbsets.Where(s => s.Hit)
                    .GroupBy(s => Path.TrimEndingDirectorySeparator(s.Path), StringComparer.InvariantCultureIgnoreCase)
                    .Select(g => (Path.TrimEndingDirectorySeparator(g.First().Path), g.First().ID)));
                missedsets.AddRange(dbsets.Where(s => !s.Hit).Select(s => (s.ID)));
                scanSetsCache_ = null;
            }

            int added = 0, modified = 0, removed = 0, unchanged = 0, scanned = 0;

            using var filequeue = new BlockingCollection<(long ID, long Set, string FileName, long Length, DateTime LastWriteTime, IMediaFile File)>(IndexFileQueueBound);
            using var pipelineCts = new CancellationTokenSource();

            var fileKeyComparer = new ScanFileKeyComparer();
            var filesdict = new ConcurrentDictionary<(long ScanSetID, string Path), ExistingIndexedFile>(fileKeyComparer);
            var metadatakeysdict = new Dictionary<string, long>();
            var artistsdict = new Dictionary<string, long>();
            var albumartistsdict = new Dictionary<string, long>();
            var imagesdict = new Dictionary<string, long>();
            var albumsdict = new Dictionary<(long ScanSetID, long AlbumArtistID, string Path, string Name), long>();

            using (var querycomm = conn_.CreateCommand())
            {
                querycomm.CommandText = "SELECT f.ID, a.ScanSetID, f.Path, f.FileSize, f.LastWriteTime, a.Path AS AlbumPath" +
                    " FROM Files f JOIN Tracks t ON f.ID = t.ID JOIN Albums a ON t.AlbumID = a.ID";
                using var reader = querycomm.ExecuteReader();
                // Resolve ordinals once instead of a name->ordinal lookup per column per row;
                // this loop walks the entire library.
                int oId = reader.GetOrdinal("ID"), oSet = reader.GetOrdinal("ScanSetID"),
                    oPath = reader.GetOrdinal("Path"), oSize = reader.GetOrdinal("FileSize"),
                    oLwt = reader.GetOrdinal("LastWriteTime"), oAlbumPath = reader.GetOrdinal("AlbumPath");
                while (reader.Read())
                {
                    var key = (reader.GetInt64(oSet), Path.Combine(reader.GetString(oAlbumPath), reader.GetString(oPath)));
                    filesdict[key] = new ExistingIndexedFile(
                        reader.GetInt64(oId), reader.GetInt64(oSize),
                        DateTime.SpecifyKind(reader.GetDateTime(oLwt), DateTimeKind.Utc));
                }
            }

            // Load writer lookup tables before starting the bounded producer. If this setup fails,
            // no producer can be left blocked waiting for a consumer that never started.
            using (var querycomm = conn_.CreateCommand())
            {
                querycomm.CommandText = "SELECT ID, \"Key\" FROM MetadataKeys";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        metadatakeysdict[reader.GetString("Key")] = reader.GetInt64("ID");
                querycomm.CommandText = "SELECT ID, Name FROM Artists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistsdict[reader.GetString("Name")] = reader.GetInt64("ID");
                querycomm.CommandText = "SELECT ID, Name FROM AlbumArtists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistsdict[reader.GetString("Name")] = reader.GetInt64("ID");
                querycomm.CommandText = "SELECT ID, ScanSetID, AlbumArtistID, Path, Name FROM Albums";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict[(reader.GetInt64("ScanSetID"), reader.GetInt64("AlbumArtistID"), reader.GetString("Path"), reader.GetString("Name"))] = reader.GetInt64("ID");
                // Artwork is intentionally unresolved during this pass, so do not load the global
                // image-hash table. On-demand hydration uses its indexed hash lookup only for the
                // requested files.
            }

            int scanFailed = 0;
            var metadatareadtask = Task.Run(() =>
            {
                try
                {
                // Split each scan set into per-subtree units, and retain any music files observed
                // while discovering those units. The old root-files unit listed every scan root a
                // second time; on a high-latency share that repeated a potentially large directory
                // query before doing useful work. A single recursive enumerator also serializes
                // every nested listing round-trip, so independent subtree walkers let listing fan
                // out across the share. All work shares ONE global parallelism cap (a per-set cap
                // multiplies by the set count — 12 sets x 32 threads once flooded the NAS with
                // ~384 readers).
                int scanParallelism = Math.Clamp(ScanParallelism, 1, 64);
                var scanParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = scanParallelism };
                var discoveredUnits = new ConcurrentBag<(string SetPath, long SetID, string Root, bool Recurse)>();
                var discoveredRootFiles = new ConcurrentBag<(string SetPath, long SetID, string Name, DateTime Modified, long Size)>();

                // Scan-set roots are independent network requests. Discover them concurrently so
                // configurations with several shares/roots do not pay every initial listing wait
                // in series. This phase uses the same cap as the readers below.
                Parallel.ForEach(sets, scanParallelOptions, scanpath =>
                {
                    foreach (var entry in new MusicFileEnumerator(scanpath.Path, recurse: false))
                    {
                        // Skip .itlp packages here too: a recursive unit rooted INSIDE the package
                        // would bypass the enumerator's recursion pruning.
                        if (entry.FileType == MFEType.Directory && !Path.GetFileName(entry.Name).Contains(".itlp", StringComparison.OrdinalIgnoreCase))
                            discoveredUnits.Add((scanpath.Path, scanpath.ID, entry.Name, true));
                        else if (entry.FileType == MFEType.MusicFile)
                            discoveredRootFiles.Add((scanpath.Path, scanpath.ID, entry.Name, entry.Modified, entry.Size));
                    }
                });
                var units = discoveredUnits.ToArray();
                var rootFiles = discoveredRootFiles.ToArray();

                void ProcessFile(
                    string setPath,
                    long setID,
                    (string Name, DateTime Modified, long Size, MFEType FileType) file,
                    ParallelLoopState loopstate)
                {
                   // Cooperative cancellation: stop the whole parallel walk at the next file
                   // boundary. loopstate.Stop() lets Parallel.For return normally so the
                   // filequeue.CompleteAdding() below still runs (an exception here would leave
                   // the consumer blocked forever).
                   if (ct.IsCancellationRequested)
                   {
                       loopstate.Stop();
                       return;
                   }
                   if (file.FileType != MFEType.MusicFile)
                       return;
                   var relativename = Path.GetRelativePath(setPath, file.Name);
                   var key = (SetID: setID, RelativeName: relativename);
                   var scan = false;
                   var isAdded = false;
                   var isModified = false;
                   long id = -1;
                   if (filesdict.TryGetValue(key, out var existing))
                   {
                       Interlocked.Exchange(ref existing.Hit, 1);
                       if ((Math.Abs((file.Modified - existing.LastWriteTime).TotalMilliseconds) > 500.0) || (file.Size != existing.Length))
                       {
                           id = existing.ID;
                           isModified = true;
                           scan = true;
                       }
                       else
                       {
                           Interlocked.Increment(ref unchanged);
                       }
                   }
                   else
                   {
                       scan = true;
                       isAdded = true;
                   }
                   if (scan)
                   {
                       try
                       {
                           filequeue.Add((id, setID, relativename, file.Size, file.Modified,
                               MediaFile.GetFile(file.Name, readOnly: true, readArtwork: false)), pipelineCts.Token);
                           if (isAdded)
                               Interlocked.Increment(ref added);
                           else if (isModified)
                               Interlocked.Increment(ref modified);
                           int done = Interlocked.Increment(ref scanned);
                           // Console is internally locked; printing from every scan thread
                           // every 100 files serializes them. A coarser cadence (and no
                           // explicit Flush, which the console stream does anyway) avoids it.
                           if ((done % 1000) == 0)
                           {
                               if (progress != null)
                                   progress.Report(new IndexProgress(done, added, modified, unchanged));
                               else
                                   Console.Write($"Scanned: {done}\r");
                           }
                       }
                       catch (IOException ex)
                       {
                           Console.WriteLine($"IOException On File: {file.Name} - {ex.Message} (0x{ex.HResult:x})");
                           Console.WriteLine(ex.StackTrace);
                       }
                       catch (Exception ex)
                       {
                           Console.WriteLine($"Unknown Exception On File: {file.Name} - {ex.Message}");
                           Interlocked.Exchange(ref scanFailed, 1);
                           loopstate.Stop();
                       }
                   }
                }

                // Directory units come first so their longer-running recursive walks begin as
                // soon as workers are available. Root files share the same work scheduler and
                // reader cap, but use the snapshots captured during the one discovery listing.
                Parallel.For(0, units.Length + rootFiles.Length, scanParallelOptions, (index, loopstate) =>
                {
                    if (index < units.Length)
                    {
                        var unit = units[index];
                        foreach (var file in new MusicFileEnumerator(unit.Root, unit.Recurse))
                        {
                            ProcessFile(unit.SetPath, unit.SetID, file, loopstate);
                            if (loopstate.IsStopped)
                                break;
                        }
                    }
                    else
                    {
                        var file = rootFiles[index - units.Length];
                        ProcessFile(file.SetPath, file.SetID,
                            (file.Name, file.Modified, file.Size, MFEType.MusicFile), loopstate);
                    }
                });

                // Removal detection must wait until every unit of every set has finished marking
                // hits, so it runs once here rather than per set. Only sets scanned in this call
                // count: files of unrequested sets were never walked, and those sets are handled
                // by the deletemissingsets path instead.
                // Skip removal detection on cancel: a partial walk didn't visit every file, so
                // unhit entries don't mean "deleted" — they mean "not reached yet".
                if (!ct.IsCancellationRequested && Volatile.Read(ref scanFailed) == 0)
                {
                    var scannedsets = new HashSet<long>(sets.Select(s => s.ID));
                    foreach (var file in filesdict.Where(kv => Volatile.Read(ref kv.Value.Hit) == 0 && scannedsets.Contains(kv.Key.ScanSetID)))
                    {
                        filequeue.Add((file.Value.ID, file.Key.ScanSetID, null, 0, DateTime.MinValue, null), pipelineCts.Token);
                        removed++;
                    }
                }

                if (progress != null)
                    progress.Report(new IndexProgress(scanned, added, modified, unchanged));
                else
                    Console.WriteLine($"Scanned: {scanned}");
                }
                finally
                {
                    filequeue.CompleteAdding();
                }
            });

            // Batch the writes: commit and start a fresh transaction every FilesPerBatch files so
            // the WAL can checkpoint instead of growing for the whole scan, and a crash loses only
            // the current batch (the DB is a cache and self-heals on the next run). Metadata rows
            // are buffered and flushed as multi-row INSERTs (MetaRowsPerInsert per statement).
            int FilesPerBatch = IndexFilesPerBatch;
            int MetaRowsPerInsert = IndexMetaRowsPerInsert;
            var metabuffer = new List<(long FileID, long KeyID, string Value)>();

            DbTransaction trans = null;
            try
            {
                trans = conn_.BeginTransaction();
                using DbCommand delcomm = trans.CreateCommand(), filecomm = trans.CreateCommand(), metacomm = trans.CreateCommand(), imagecomm = trans.CreateCommand(),
                    keycomm = trans.CreateCommand(), artistcomm = trans.CreateCommand(), albumartistcomm = trans.CreateCommand(), albumcomm = trans.CreateCommand(),
                    trackcomm = trans.CreateCommand(), imagemetacomm = trans.CreateCommand(), metabatchcomm = trans.CreateCommand();

                // After a batch commit, every command bound to the old transaction must be
                // re-pointed at the new one.
                DbCommand[] batchcommands = { delcomm, filecomm, metacomm, imagecomm, keycomm, artistcomm, albumartistcomm, albumcomm, trackcomm, imagemetacomm, metabatchcomm };

                    artistcomm.CommandText = "INSERT INTO Artists (Name) VALUES (@Name);\r\n" + lastidsql_;
                    var artistparam = artistcomm.Parameters.Add("@Name", DbType.String);

                    albumartistcomm.CommandText = "INSERT INTO AlbumArtists (Name) VALUES (@Name);\r\n" + lastidsql_;
                    var albumartistparam = albumartistcomm.Parameters.Add("@Name", DbType.String);

                    albumcomm.CommandText = "INSERT INTO Albums (ScanSetID, AlbumArtistID, Path, Name) VALUES (@ScanSetID, @AlbumArtistID, @Path, @Name);\r\n" + lastidsql_;
                    var albumscansetidparam = albumcomm.Parameters.Add("@ScanSetID", DbType.Int64);
                    var albumartistidparam = albumcomm.Parameters.Add("@AlbumArtistID", DbType.Int64);
                    var albumpathparam = albumcomm.Parameters.Add("@Path", DbType.String);
                    var albumnameparam = albumcomm.Parameters.Add("@Name", DbType.String);

                    trackcomm.CommandText = "INSERT INTO Tracks (ID, ArtistID, AlbumID, Name, TrackNumber, TrackTotal, DiscNumber, DiscTotal, ReleaseDate) VALUES (@ID, @ArtistID, @AlbumID, @Name, @TrackNumber, @TrackTotal, @DiscNumber, @DiscTotal, @ReleaseDate)";
                    var trackartistidparam = trackcomm.Parameters.Add("@ArtistID", DbType.Int64);
                    var trackalbumidparam = trackcomm.Parameters.Add("@AlbumID", DbType.Int64);
                    var tracknumberparam = trackcomm.Parameters.Add("@TrackNumber", DbType.Int64);
                    var tracktotalparam = trackcomm.Parameters.Add("@TrackTotal", DbType.Int64);
                    var discnumberparam = trackcomm.Parameters.Add("@DiscNumber", DbType.Int64);
                    var disctotalparam = trackcomm.Parameters.Add("@DiscTotal", DbType.Int64);
                    var releasedateparam = trackcomm.Parameters.Add("@ReleaseDate", DbType.DateTime);
                    var tracknameparam = trackcomm.Parameters.Add("@Name", DbType.String);
                    var trackidparam = trackcomm.Parameters.Add("@ID", DbType.Int64);

                    using var metadatafields = new DataTable();
                    metadatafields.Columns.Add("FileID", typeof(long));
                    metadatafields.Columns.Add("KeyID", typeof(long));
                    metadatafields.Columns.Add("Value", typeof(string));

                    delcomm.CommandText = "DELETE FROM Metadata WHERE FileID = @ID;\r\n" +
                        "DELETE FROM ImageMetadata WHERE FileID = @ID;\r\n" +
                        "DELETE FROM Files WHERE ID = @ID;\r\n" +
                        "DELETE FROM Tracks WHERE ID = @ID";

                    delcomm.Parameters.Clear();
                    var delidparam = delcomm.Parameters.Add("@ID", DbType.Int64);

                    var pathparam = filecomm.Parameters.Add("@Path", DbType.String);
                    var filesizeparam = filecomm.Parameters.Add("@FileSize", DbType.Int64);
                    var lastwritetimeparam = filecomm.Parameters.Add("@LastWriteTime", DbType.DateTime);
                    var codecnameparam = filecomm.Parameters.Add("@CodecName", DbType.String);
                    var codectypeparam = filecomm.Parameters.Add("@CodecType", DbType.String);
                    var averagebitrateparam = filecomm.Parameters.Add("@AverageBitrate", DbType.Int64);
                    var maxbitrateparam = filecomm.Parameters.Add("@MaxBitrate", DbType.Int64);
                    var bitspersampleparam = filecomm.Parameters.Add("@BitsPerSample", DbType.Int64);
                    var samplerateparam = filecomm.Parameters.Add("@SampleRate", DbType.Int64);
                    var channelsparam = filecomm.Parameters.Add("@Channels", DbType.Int64);
                    var durationinframesparam = filecomm.Parameters.Add("@DurationInFrames", DbType.Int64);
                    var tagtypeparam = filecomm.Parameters.Add("@TagType", DbType.String);
                    filecomm.CommandText = "INSERT INTO Files (Path, FileSize, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, TagType, ArtworkScanned)" +
                        " VALUES (@Path, @FileSize, @LastWriteTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames, @TagType, 0);\r\n" +
                        lastidsql_;

                    // SQLite metadata rows are written via buffered multi-row INSERTs (see
                    // FlushMetadata below), so no single-row metadata command is prepared here.
#if SQLSERVER
                    DbParameter valueparam = null;
                    if (metacomm is SqlCommand)
                    {
                        metacomm.CommandText = "INSERT INTO Metadata (FileID, KeyID, Value) SELECT m.FileID, m.KeyID, m.Value FROM @tvpMetadata AS m";
                        valueparam = metacomm.Parameters.Add("@tvpMetadata", DbType.Object, ParameterDirection.Input, "dbo.MetadataTableType");
                    }
#endif

                    keycomm.CommandText = "INSERT INTO MetadataKeys (\"Key\") VALUES (@Key);\r\n" + lastidsql_;
                    var keyparam = keycomm.Parameters.Add("@Key", DbType.String);

                    imagemetacomm.CommandText = "INSERT INTO ImageMetadata (FileID, ImageID, Description, Category) VALUES (@FileID, @ImageID, @Description, @Category)";
                    var imagefileidparam = imagemetacomm.Parameters.Add("@FileID", DbType.Int64);
                    var imageidparam = imagemetacomm.Parameters.Add("@ImageID", DbType.Int64);
                    var descriptionparam = imagemetacomm.Parameters.Add("@Description", DbType.String);
                    var categoryparam = imagemetacomm.Parameters.Add("@Category", DbType.String);

                    imagecomm.CommandText = "INSERT INTO Images (Hash, ImageType, Width, Height, Size, Data) VALUES (@Hash, @ImageType, @Width, @Height, @Size, @Data);\r\n" + lastidsql_;
                    var imagehashparam = imagecomm.Parameters.Add("@Hash", DbType.String);
                    var imagetypeparam = imagecomm.Parameters.Add("@ImageType", DbType.String);
                    var widthparam = imagecomm.Parameters.Add("@Width", DbType.Int64);
                    var heightparam = imagecomm.Parameters.Add("@Height", DbType.Int64);
                    var sizeparam = imagecomm.Parameters.Add("@Size", DbType.Int64);
                    var dataparam = imagecomm.Parameters.Add("@Data", DbType.Object);

                    // Reusable full-size multi-row metadata INSERT (item 7). A short remainder
                    // chunk is built on demand inside FlushMetadata.
                    var metabatchparams = new DbParameter[MetaRowsPerInsert * 3];
                    {
                        var sb = new StringBuilder("INSERT INTO Metadata (FileID, KeyID, Value) VALUES ");
                        for (int i = 0; i < MetaRowsPerInsert; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append("(@f").Append(i).Append(",@k").Append(i).Append(",@v").Append(i).Append(')');
                            var fp = metabatchcomm.CreateParameter(); fp.ParameterName = "@f" + i; metabatchcomm.Parameters.Add(fp); metabatchparams[i * 3] = fp;
                            var kp = metabatchcomm.CreateParameter(); kp.ParameterName = "@k" + i; metabatchcomm.Parameters.Add(kp); metabatchparams[i * 3 + 1] = kp;
                            var vp = metabatchcomm.CreateParameter(); vp.ParameterName = "@v" + i; metabatchcomm.Parameters.Add(vp); metabatchparams[i * 3 + 2] = vp;
                        }
                        metabatchcomm.CommandText = sb.ToString();
                    }

                    void FlushMetadata()
                    {
                        int total = metabuffer.Count, start = 0;
                        while (start < total)
                        {
                            int count = Math.Min(MetaRowsPerInsert, total - start);
                            if (count == MetaRowsPerInsert)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    var row = metabuffer[start + i];
                                    metabatchparams[i * 3].Value = row.FileID;
                                    metabatchparams[i * 3 + 1].Value = row.KeyID;
                                    metabatchparams[i * 3 + 2].Value = row.Value;
                                }
                                metabatchcomm.ExecuteNonQuery();
                            }
                            else
                            {
                                using var cmd = trans.CreateCommand();
                                var sb = new StringBuilder("INSERT INTO Metadata (FileID, KeyID, Value) VALUES ");
                                for (int i = 0; i < count; i++)
                                {
                                    if (i > 0) sb.Append(',');
                                    sb.Append("(@f").Append(i).Append(",@k").Append(i).Append(",@v").Append(i).Append(')');
                                    var row = metabuffer[start + i];
                                    var fp = cmd.CreateParameter(); fp.ParameterName = "@f" + i; fp.Value = row.FileID; cmd.Parameters.Add(fp);
                                    var kp = cmd.CreateParameter(); kp.ParameterName = "@k" + i; kp.Value = row.KeyID; cmd.Parameters.Add(kp);
                                    var vp = cmd.CreateParameter(); vp.ParameterName = "@v" + i; vp.Value = row.Value; cmd.Parameters.Add(vp);
                                }
                                cmd.CommandText = sb.ToString();
                                cmd.ExecuteNonQuery();
                            }
                            start += count;
                        }
                        metabuffer.Clear();
                    }

                    int filesInBatch = 0;
                    foreach (var file in filequeue.GetConsumingEnumerable())
                    {
                        if (file.ID != -1)
                        {
                            delidparam.Value = file.ID;
                            delcomm.ExecuteNonQuery();
                        }

                        if (file.File is null)
                            continue;

                        long fileid;

                        var mp = file.File.Tags.First();
                        var cp = file.File.Codecs.First();

                        var artist = mp.Artist;
                        var albumartist = mp.AlbumArtist;
                        var album = mp.Album;
                        var title = mp.Title;
                        var track = mp.TrackNumber;
                        var tracktotal = mp.TrackTotal;
                        var disc = mp.DiscNumber;
                        var disctotal = mp.DiscTotal;
                        var releasedate = mp.ReleaseDate;

                        if (!artistsdict.TryGetValue(artist, out var artistid))
                        {
                            artistparam.Value = artist;
                            artistsdict.Add(artist, artistid = (long)artistcomm.ExecuteScalar());
                        }

                        if (!albumartistsdict.TryGetValue(albumartist, out var albumartistid))
                        {
                            albumartistparam.Value = albumartist;
                            albumartistsdict.Add(albumartist, albumartistid = (long)albumartistcomm.ExecuteScalar());
                        }

                        var albumkey = (SetID: file.Set, AlbumArtistID: albumartistid, AlbumPath: Path.GetDirectoryName(file.FileName), Album: album);
                        if (!albumsdict.TryGetValue(albumkey, out var albumid))
                        {
                            albumscansetidparam.Value = albumkey.SetID;
                            albumartistidparam.Value = albumkey.AlbumArtistID;
                            albumpathparam.Value = albumkey.AlbumPath;
                            albumnameparam.Value = albumkey.Album;
                            albumsdict.Add(albumkey, albumid = (long)albumcomm.ExecuteScalar());
                        }

                        trackalbumidparam.Value = albumid;
                        trackartistidparam.Value = artistid;
                        tracknumberparam.Value = (track is null) ? DBNull.Value : track.Value;
                        tracktotalparam.Value = (tracktotal is null) ? DBNull.Value : tracktotal.Value;
                        discnumberparam.Value = (disc is null) ? DBNull.Value : disc.Value;
                        disctotalparam.Value = (disctotal is null) ? DBNull.Value : disctotal.Value;
                        releasedateparam.Value = (releasedate is null) ? DBNull.Value : releasedate;
                        tracknameparam.Value = title;

                        pathparam.Value = Path.GetFileName(file.FileName);
                        filesizeparam.Value = file.Length;
                        lastwritetimeparam.Value = file.LastWriteTime;
                        codecnameparam.Value = cp.CodecName;
                        codectypeparam.Value = cp.CodecType.ToString();
                        averagebitrateparam.Value = cp.AverageBitrate;
                        maxbitrateparam.Value = cp.MaxBitrate;
                        bitspersampleparam.Value = cp.BitsPerSample;
                        samplerateparam.Value = cp.Samplerate;
                        channelsparam.Value = cp.Channels;
                        durationinframesparam.Value = cp.DurationInFrames;
                        tagtypeparam.Value = mp.TagType;

                        trackidparam.Value = imagefileidparam.Value = fileid = (long)filecomm.ExecuteScalar();
                        trackcomm.ExecuteNonQuery();

                        // Every parser's text metadata is the TagFields name/value projection of
                        // GetKnownMetadata(). Materialize it once: several tag implementations do
                        // non-trivial mapping work on every enumeration.
                        var knownMetadata = mp.GetKnownMetadata().ToArray();
                        foreach (var kv in knownMetadata)
                        {
                            string key = kv.Key.ToString();
                            if (!metadatakeysdict.TryGetValue(key, out var keyid))
                            {
                                keyparam.Value = key;
                                keyid = (long)keycomm.ExecuteScalar();
                                metadatakeysdict.Add(key, keyid);
                            }
#if SQLSERVER
                            if (metacomm is SqlCommand)
                                metadatafields.Rows.Add(fileid, keyid, kv.Value);
                            else
#endif
                                metabuffer.Add((fileid, keyid, kv.Value));
                        }

#if SQLSERVER
                        if ((metacomm is SqlCommand) && (metadatafields.Rows.Count > 1000))
                        {
                            valueparam.Value = metadatafields;
                            metacomm.ExecuteNonQuery();
                            metadatafields.Rows.Clear();
                        }
#endif
                        // Cap buffer memory between batch commits.
                        if (metabuffer.Count >= 4000)
                            FlushMetadata();

                        foreach (var image in mp.GetImageMetadata())
                        {
                            string hash = image.Hash;
                            if (!imagesdict.TryGetValue(hash, out var imageid))
                            {
                                imagehashparam.Value = hash;
                                imagetypeparam.Value = image.ImageType;
                                widthparam.Value = image.Width;
                                heightparam.Value = image.Height;
                                sizeparam.Value = image.Size;
                                dataparam.Value = image.Data;
                                imagesdict.Add(hash, imageid = (long)imagecomm.ExecuteScalar());
                            }
                            imageidparam.Value = imageid;
                            descriptionparam.Value = image.Description;
                            categoryparam.Value = image.Category;
                            imagemetacomm.ExecuteNonQuery();
                        }

                        if (++filesInBatch >= FilesPerBatch)
                        {
                            FlushMetadata();
                            trans.Commit();
                            trans.Dispose();
                            trans = conn_.BeginTransaction();
                            foreach (var c in batchcommands)
                                c.Transaction = trans;
                            filesInBatch = 0;
                        }
                    }

                    FlushMetadata();
#if SQLSERVER
                    if ((metacomm is SqlCommand) && (metadatafields.Rows.Count != 0))
                    {
                        valueparam.Value = metadatafields;
                        metacomm.ExecuteNonQuery();
                        metadatafields.Rows.Clear();
                    }
#endif
                    trans.Commit();
            }
            catch
            {
                // Unblock a producer waiting on the bounded queue if the database writer fails.
                pipelineCts.Cancel();
                try { await metadatareadtask; } catch { /* preserve the writer exception */ }
                throw;
            }
            finally
            {
                trans?.Dispose();
            }

            await metadatareadtask;

            if (deletemissingsets && (missedsets.Count != 0))
            {
                using var deltrans = conn_.BeginTransaction();
                using DbCommand setcomm = deltrans.CreateCommand(), albumcomm = deltrans.CreateCommand(), metacomm = deltrans.CreateCommand(),
                    imagecomm = deltrans.CreateCommand(), trackscomm = deltrans.CreateCommand(), filecomm = deltrans.CreateCommand();

                setcomm.CommandText = "DELETE FROM ScanSets WHERE ID = @ID";
                var idparam = setcomm.Parameters.Add("@ID", DbType.Int64);
                filecomm.CommandText = "DELETE FROM Files WHERE ID IN (SELECT ID FROM MetadataSummaryView WHERE ScanSetID = @ID)";
                var filesidparam = filecomm.Parameters.Add("@ID", DbType.Int64);
                trackscomm.CommandText = "DELETE FROM Tracks WHERE AlbumID IN (SELECT ID FROM Albums WHERE ScanSetID = @ID)";
                var tracksidparam = trackscomm.Parameters.Add("@ID", DbType.Int64);
                metacomm.CommandText = "DELETE FROM Metadata WHERE FileID IN (SELECT ID FROM MetadataSummaryView WHERE ScanSetID = @ID)";
                var metaidparam = metacomm.Parameters.Add("@ID", DbType.Int64);
                imagecomm.CommandText = "DELETE FROM ImageMetadata WHERE FileID IN (SELECT ID FROM MetadataSummaryView WHERE ScanSetID = @ID)";
                var imageidparam = imagecomm.Parameters.Add("@ID", DbType.Int64);
                albumcomm.CommandText = "DELETE FROM Albums WHERE ScanSetID = @ID";
                var albumidparam = albumcomm.Parameters.Add("@ID", DbType.Int64);
                metacomm.CommandTimeout = imagecomm.CommandTimeout = 0;

                foreach (var set in missedsets)
                {
                    idparam.Value = filesidparam.Value = tracksidparam.Value = metaidparam.Value = albumidparam.Value = imageidparam.Value = set;
                    metacomm.ExecuteNonQuery();
                    imagecomm.ExecuteNonQuery();
                    removed += filecomm.ExecuteNonQuery();
                    trackscomm.ExecuteNonQuery();
                    albumcomm.ExecuteNonQuery();
                    setcomm.ExecuteNonQuery();
                }
                deltrans.Commit();
                scanSetsCache_ = null;
            }

            if ((modified != 0) || (removed != 0))
            {
                using var prunetrans = conn_.BeginTransaction();
                using var comm = prunetrans.CreateCommand();
                comm.CommandText = "DELETE FROM Albums WHERE ID NOT IN (SELECT DISTINCT AlbumID FROM Tracks);\r\n" +
                    "DELETE FROM Artists WHERE ID NOT IN (SELECT DISTINCT ArtistID FROM Tracks);\r\n" +
                    "DELETE FROM AlbumArtists WHERE ID NOT IN (SELECT DISTINCT AlbumArtistID FROM Albums);\r\n" +
                    "DELETE FROM MetadataKeys WHERE ID NOT IN (SELECT DISTINCT KeyID FROM Metadata);\r\n" +
                    "DELETE FROM Images WHERE ID NOT IN (SELECT DISTINCT ImageID FROM ImageMetadata)";
                comm.ExecuteNonQuery();
                prunetrans.Commit();
            }

            return (added, modified, removed, unchanged);
        }

        // ---- Single-file read/write API (used by the GUI to read tags/artwork from the cache instead
        // of re-parsing files, and to re-index a single file immediately after it is edited) ----

        private List<(string Path, long ID)> LoadScanSets()
        {
            if (scanSetsCache_ is not null)
                return scanSetsCache_;

            var sets = new List<(string Path, long ID)>();
            using var cmd = conn_.CreateCommand();
            cmd.CommandText = "SELECT Path, ID FROM ScanSets";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                sets.Add((reader.GetString(0), reader.GetInt64(1)));
            return scanSetsCache_ = sets;
        }

        // Split a full path into (scan set, album-relative directory, filename), matching how the
        // indexer stores Files.Path (filename) and Albums.Path (directory relative to the set root).
        // Returns SetId = -1 when the path isn't under any known scan set.
        private (long SetId, string AlbumPath, string FileName) DecomposePath(string fullPath)
        {
            string bestRoot = null;
            long bestId = -1;
            foreach (var s in LoadScanSets())
            {
                var root = s.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.Length > root.Length + 1 &&
                    fullPath.StartsWith(root, StringComparison.InvariantCultureIgnoreCase) &&
                    (fullPath[root.Length] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.AltDirectorySeparatorChar) &&
                    root.Length > (bestRoot?.Length ?? -1))
                {
                    bestRoot = root;
                    bestId = s.ID;
                }
            }
            if (bestRoot is null)
                return (-1, null, null);

            var rel = fullPath.Substring(bestRoot.Length + 1);
            return (bestId, Path.GetDirectoryName(rel) ?? "", Path.GetFileName(fullPath));
        }

        private long? ResolveFileId(string fullPath)
        {
            var (setId, albumPath, fileName) = DecomposePath(fullPath);
            if (setId < 0)
                return null;

            using var cmd = conn_.CreateCommand();
            cmd.CommandText = "SELECT f.ID FROM Files f JOIN Tracks t ON f.ID = t.ID JOIN Albums a ON t.AlbumID = a.ID" +
                " WHERE a.ScanSetID = @set AND a.Path = @album AND f.Path = @file LIMIT 1";
            cmd.Parameters.Add("@set", DbType.Int64).Value = setId;
            cmd.Parameters.Add("@album", DbType.String).Value = albumPath;
            cmd.Parameters.Add("@file", DbType.String).Value = fileName;
            var r = cmd.ExecuteScalar();
            return (r is null || r is DBNull) ? (long?)null : Convert.ToInt64(r);
        }

        /// <summary>
        /// Read a single file's full cached metadata (structured + codec + normalized known fields +
        /// raw text frames + optionally embedded images) by its full path, or null if the file isn't
        /// in the cache. Lets the GUI avoid re-parsing files over the network.
        /// </summary>
        public FileDetails GetFileDetails(string fullPath, bool includeImages)
        {
            if (includeImages)
                EnsureArtworkHydrated([fullPath]);

            var (setId, albumPath, fileName) = DecomposePath(fullPath);
            if (setId < 0)
                return null;

            var details = new FileDetails { Path = fullPath };
            long fid;

            using (var cmd = conn_.CreateCommand())
            {
                // Column order matches BuildCache so the MetadataCacheEntry(reader) ordinal reads line up.
                cmd.CommandText = "SELECT Path, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate," +
                    " BitsPerSample, SampleRate, Channels, DurationInFrames, Artist, AlbumArtist, Album," +
                    " TrackNumber, TrackTotal, DiscNumber, DiscTotal, ReleaseDate, Track, TagType, ID" +
                    " FROM MetadataSummaryView WHERE ScanSetID = @set AND AlbumPath = @album AND Path = @file LIMIT 1";
                cmd.Parameters.Add("@set", DbType.Int64).Value = setId;
                cmd.Parameters.Add("@album", DbType.String).Value = albumPath;
                cmd.Parameters.Add("@file", DbType.String).Value = fileName;
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;
                fid = reader.GetInt64("ID");
                details.Entry = new MetadataCacheEntry(reader);
                details.TagType = reader.GetString("TagType");
            }

            using (var cmd = conn_.CreateCommand())
            {
                cmd.CommandText = "SELECT k.\"Key\", m.Value FROM Metadata m JOIN MetadataKeys k ON m.KeyID = k.ID WHERE m.FileID = @id";
                cmd.Parameters.Add("@id", DbType.Int64).Value = fid;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var field = new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1));
                    details.KnownFields.Add(field);
                    // GetTextMetadata() is currently the TagFields.ToString() projection of
                    // GetKnownMetadata() for every parser. Preserve the public raw/text collection
                    // without querying the duplicate Metadata rows a second time.
                    details.TextFields.Add(field);
                }
            }

            if (includeImages)
            {
                using var cmd = conn_.CreateCommand();
                cmd.CommandText = "SELECT im.Description, im.Category, i.ImageType, i.Width, i.Height, i.Size, i.Hash, i.Data" +
                    " FROM ImageMetadata im JOIN Images i ON im.ImageID = i.ID WHERE im.FileID = @id";
                cmd.Parameters.Add("@id", DbType.Int64).Value = fid;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    details.Images.Add(new FileImage
                    {
                        Description = reader.GetString(0),
                        Category = reader.GetString(1),
                        ImageType = reader.GetString(2),
                        Width = (int)reader.GetInt64(3),
                        Height = (int)reader.GetInt64(4),
                        Size = (int)reader.GetInt64(5),
                        Hash = reader.GetString(6),
                        Data = (byte[])reader["Data"],
                    });
            }

            return details;
        }

        /// <summary>
        /// For each path, a signature of its embedded images (its image hashes, order-independent),
        /// or "" if it has none. Lets callers cheaply tell whether a selection's artwork is uniform
        /// without loading any image data. Same length/order as the input.
        /// </summary>
        public List<string> GetImageSignatures(IReadOnlyList<string> fullPaths)
        {
            EnsureArtworkHydrated(fullPaths);
            var result = Enumerable.Repeat("", fullPaths.Count).ToArray();
            const int pathsPerQuery = 200; // 800 parameters, below SQLite and SQL Server limits.
            for (int start = 0; start < fullPaths.Count; start += pathsPerQuery)
            {
                int count = Math.Min(pathsPerQuery, fullPaths.Count - start);
                using var cmd = conn_.CreateCommand();
                string requested = AddRequestedPaths(cmd, fullPaths, start, count);
                cmd.CommandText = requested +
                    " SELECT r.RequestOrdinal, i.Hash FROM Requested r" +
                    " LEFT JOIN Albums a ON a.ScanSetID = r.ScanSetID AND a.Path = r.AlbumPath" +
                    " LEFT JOIN Tracks t ON t.AlbumID = a.ID" +
                    " LEFT JOIN Files f ON f.ID = t.ID AND f.Path = r.FileName" +
                    " LEFT JOIN ImageMetadata im ON im.FileID = f.ID" +
                    " LEFT JOIN Images i ON i.ID = im.ImageID" +
                    " ORDER BY r.RequestOrdinal, i.Hash";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int ordinal = reader.GetInt32(0);
                    if (!reader.IsDBNull(1))
                    {
                        string hash = reader.GetString(1);
                        result[ordinal] = result[ordinal].Length == 0 ? hash : result[ordinal] + "|" + hash;
                    }
                }
            }
            return [.. result];
        }

        /// <summary>The bytes of a file's first embedded image (for thumbnails), or null if none.</summary>
        public byte[] GetFirstImageData(string fullPath)
        {
            return GetFirstImageData([fullPath])[0];
        }

        /// <summary>
        /// Batch form used by virtualized thumbnail grids. It resolves paths and image rows in one
        /// query per 200 paths instead of issuing two commands for every visible cell.
        /// </summary>
        public List<byte[]> GetFirstImageData(IReadOnlyList<string> fullPaths)
        {
            EnsureArtworkHydrated(fullPaths);
            var result = Enumerable.Repeat<byte[]>(null, fullPaths.Count).ToArray();
            const int pathsPerQuery = 200;
            for (int start = 0; start < fullPaths.Count; start += pathsPerQuery)
            {
                int count = Math.Min(pathsPerQuery, fullPaths.Count - start);
                using var cmd = conn_.CreateCommand();
                string requested = AddRequestedPaths(cmd, fullPaths, start, count);
                cmd.CommandText = requested +
                    " SELECT r.RequestOrdinal, i.Data FROM Requested r" +
                    " LEFT JOIN Albums a ON a.ScanSetID = r.ScanSetID AND a.Path = r.AlbumPath" +
                    " LEFT JOIN Tracks t ON t.AlbumID = a.ID" +
                    " LEFT JOIN Files f ON f.ID = t.ID AND f.Path = r.FileName" +
                    " LEFT JOIN ImageMetadata im ON im.FileID = f.ID" +
                    " LEFT JOIN Images i ON i.ID = im.ImageID" +
                    " ORDER BY r.RequestOrdinal, im.ID";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int ordinal = reader.GetInt32(0);
                    if (result[ordinal] is null && !reader.IsDBNull(1))
                        result[ordinal] = (byte[])reader[1];
                }
            }
            return [.. result];
        }

        private sealed record ArtworkHydrationTarget(
            long FileID,
            string Path,
            long Length,
            DateTime LastWriteTime);

        private void EnsureArtworkHydrated(IReadOnlyList<string> fullPaths)
        {
            if (fullPaths.Count == 0)
                return;

            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var targets = new List<ArtworkHydrationTarget>();
            var uniquePaths = fullPaths.Distinct(pathComparer).ToList();
            const int pathsPerQuery = 200;
            for (int start = 0; start < uniquePaths.Count; start += pathsPerQuery)
            {
                using var command = conn_.CreateCommand();
                int count = Math.Min(pathsPerQuery, uniquePaths.Count - start);
                string requested = AddRequestedPaths(command, uniquePaths, start, count);
                command.CommandText = requested +
                    " SELECT r.RequestOrdinal, f.ID, f.FileSize, f.LastWriteTime" +
                    " FROM Requested r" +
                    " JOIN Albums a ON a.ScanSetID = r.ScanSetID AND a.Path = r.AlbumPath" +
                    " JOIN Tracks t ON t.AlbumID = a.ID" +
                    " JOIN Files f ON f.ID = t.ID AND f.Path = r.FileName" +
                    " WHERE f.ArtworkScanned = 0";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int ordinal = reader.GetInt32(0);
                    targets.Add(new ArtworkHydrationTarget(
                        reader.GetInt64(1),
                        uniquePaths[ordinal],
                        reader.GetInt64(2),
                        DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
                }
            }
            if (targets.Count == 0)
                return;

            var hydrated = new (ArtworkHydrationTarget Target, IMediaFile File)[targets.Count];
            foreach (var target in targets)
                ValidateArtworkSource(target);
            try
            {
                Parallel.For(
                    0,
                    targets.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(ScanParallelism, 1, 64) },
                    index =>
                    {
                        var target = targets[index];
                        using var sha = SHA256.Create();
                        var file = MediaFile.GetFile(target.Path, sha, readOnly: true, readArtwork: true);
                        ValidateArtworkSource(target);
                        hydrated[index] = (target, file);
                    });
            }
            catch (AggregateException exception)
            {
                var flattened = exception.Flatten();
                if (flattened.InnerExceptions.Count == 1)
                    ExceptionDispatchInfo.Capture(flattened.InnerExceptions[0]).Throw();
                throw;
            }

            using var transaction = conn_.BeginTransaction();
            try
            {
                var imageCache = new Dictionary<string, long>(StringComparer.Ordinal);
                using var imageMetadata = transaction.CreateCommand();
                imageMetadata.CommandText =
                    "INSERT INTO ImageMetadata (FileID, ImageID, Description, Category) VALUES (@f, @i, @d, @c)";
                var fileParameter = imageMetadata.Parameters.Add("@f", DbType.Int64);
                var imageParameter = imageMetadata.Parameters.Add("@i", DbType.Int64);
                var descriptionParameter = imageMetadata.Parameters.Add("@d", DbType.String);
                var categoryParameter = imageMetadata.Parameters.Add("@c", DbType.String);

                using var mark = transaction.CreateCommand();
                mark.CommandText = "UPDATE Files SET ArtworkScanned = 1 WHERE ID = @id AND ArtworkScanned = 0";
                var markParameter = mark.Parameters.Add("@id", DbType.Int64);

                foreach (var (target, file) in hydrated)
                {
                    fileParameter.Value = target.FileID;
                    foreach (var image in file.Tags.SelectMany(tag => tag.GetImageMetadata()))
                    {
                        imageParameter.Value = GetOrInsertImage(transaction, image, imageCache);
                        descriptionParameter.Value = image.Description ?? "";
                        categoryParameter.Value = image.Category ?? "";
                        imageMetadata.ExecuteNonQuery();
                    }
                    markParameter.Value = target.FileID;
                    if (mark.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException(
                            $"Artwork cache state changed while hydrating: {target.Path}");
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static void ValidateArtworkSource(ArtworkHydrationTarget target)
        {
            var info = new FileInfo(target.Path);
            if (!info.Exists || info.Length != target.Length ||
                Math.Abs((info.LastWriteTimeUtc - target.LastWriteTime).TotalMilliseconds) > 500)
                throw new InvalidOperationException(
                    $"Source changed since metadata indexing; re-index before loading artwork: {target.Path}");
        }

        private string AddRequestedPaths(DbCommand cmd, IReadOnlyList<string> fullPaths, int start, int count)
        {
            var sql = new StringBuilder("WITH Requested(RequestOrdinal, ScanSetID, AlbumPath, FileName) AS (VALUES ");
            for (int i = 0; i < count; i++)
            {
                int ordinal = start + i;
                var (setId, albumPath, fileName) = DecomposePath(fullPaths[ordinal]);
                if (i > 0)
                    sql.Append(',');
                sql.Append("(@o").Append(i).Append(",@s").Append(i).Append(",@a").Append(i).Append(",@f").Append(i).Append(')');
                cmd.Parameters.Add("@o" + i, DbType.Int32).Value = ordinal;
                cmd.Parameters.Add("@s" + i, DbType.Int64).Value = setId;
                cmd.Parameters.Add("@a" + i, DbType.String).Value = albumPath ?? "";
                cmd.Parameters.Add("@f" + i, DbType.String).Value = fileName ?? "";
            }
            return sql.Append(')').ToString();
        }

        /// <summary>
        /// Remove a single file's rows from the cache (used after an Organize move relocates it, so the
        /// stale entry at the old path is dropped). Returns false if the file isn't in the cache.
        /// </summary>
        public bool RemoveFile(string fullPath)
        {
            var id = ResolveFileId(fullPath);
            if (id is null)
                return false;

            using var trans = conn_.BeginTransaction();
            try
            {
                using var del = trans.CreateCommand();
                del.CommandText = "DELETE FROM Metadata WHERE FileID = @id;\r\n" +
                    "DELETE FROM ImageMetadata WHERE FileID = @id;\r\n" +
                    "DELETE FROM Files WHERE ID = @id;\r\n" +
                    "DELETE FROM Tracks WHERE ID = @id";
                del.Parameters.Add("@id", DbType.Int64).Value = id.Value;
                del.ExecuteNonQuery();
                trans.Commit();
                return true;
            }
            catch
            {
                trans.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Re-parse a single file and refresh its rows in the cache (delete + re-insert). Called right
        /// after the file's tags/artwork are edited so the cache stays in sync without a full re-index.
        /// Returns false if the file isn't under any known scan set (nothing to place it under).
        /// </summary>
        public bool ReindexFile(string fullPath)
        {
            IMediaFile file;
            using (var sha = SHA256.Create())
                file = MediaFile.GetFile(fullPath, sha, readOnly: true);

            return ReindexFileCore(fullPath, file);
        }

        /// <summary>
        /// Refresh a file from the already parsed object that was just saved. Artwork hashes are
        /// computed from its in-memory payloads, avoiding another open/read across the scan share.
        /// </summary>
        public bool ReindexFile(string fullPath, IMediaFile savedFile)
        {
            ArgumentNullException.ThrowIfNull(savedFile);
            using (var sha = SHA256.Create())
                foreach (var image in savedFile.Tags.SelectMany(t => t.GetImageMetadata()))
                    image.HashImage(sha);

            return ReindexFileCore(fullPath, savedFile);
        }

        private bool ReindexFileCore(string fullPath, IMediaFile file)
        {
            using var trans = conn_.BeginTransaction();
            try
            {
                bool result = ReindexFileCore(fullPath, file, trans);
                trans.Commit();
                return result;
            }
            catch
            {
                trans.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Refresh several already-saved files in one transaction. Used after bounded-parallel tag
        /// writes so SQLite does one commit/fsync for the batch rather than one per track.
        /// </summary>
        public int ReindexFiles(IReadOnlyList<(string Path, IMediaFile File)> files)
        {
            using (var sha = SHA256.Create())
                foreach (var file in files)
                    foreach (var image in file.File.Tags.SelectMany(t => t.GetImageMetadata()))
                        image.HashImage(sha);

            using var trans = conn_.BeginTransaction();
            try
            {
                var lookups = ReindexLookupCache.Load(trans);
                int indexed = 0;
                foreach (var file in files)
                    if (ReindexFileCore(file.Path, file.File, trans, lookups))
                        indexed++;
                trans.Commit();
                return indexed;
            }
            catch
            {
                trans.Rollback();
                throw;
            }
        }

        private sealed class ReindexLookupCache
        {
            public Dictionary<string, long> Artists { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, long> AlbumArtists { get; } = new(StringComparer.Ordinal);
            public Dictionary<(long ScanSetID, long AlbumArtistID, string Path, string Name), long> Albums { get; } = new();
            public Dictionary<string, long> Images { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, long> MetadataKeys { get; } = new(StringComparer.Ordinal);

            public static ReindexLookupCache Load(DbTransaction trans)
            {
                var result = new ReindexLookupCache();
                using var command = trans.CreateCommand();
                command.CommandText = "SELECT ID, \"Key\" FROM MetadataKeys";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.MetadataKeys[reader.GetString(1)] = reader.GetInt64(0);
                return result;
            }
        }

        private bool ReindexFileCore(
            string fullPath,
            IMediaFile file,
            DbTransaction trans,
            ReindexLookupCache lookups = null)
        {
            var (setId, albumPath, fileName) = DecomposePath(fullPath);
            if (setId < 0)
                return false;

            var mp = file.Tags.First();
            var cp = file.Codecs.First();
            var fi = new FileInfo(fullPath);
            var knownMetadata = mp.GetKnownMetadata().ToArray();

            string KnownValue(TagFields field) =>
                knownMetadata.FirstOrDefault(kv => kv.Key == field).Value;
            int? KnownInt(TagFields field) =>
                int.TryParse(KnownValue(field), out int value) ? value : null;

            string artist = KnownValue(TagFields.Artist) ?? "";
            string albumArtist = KnownValue(TagFields.AlbumArtist);
            if (string.IsNullOrEmpty(albumArtist))
                albumArtist = artist;
            string album = KnownValue(TagFields.Album) ?? "";
            string title = KnownValue(TagFields.Title) ?? "";

                // Remove any existing rows for this file first.
                using (var find = trans.CreateCommand())
                {
                    find.CommandText = "SELECT f.ID FROM Files f JOIN Tracks t ON f.ID = t.ID JOIN Albums a ON t.AlbumID = a.ID" +
                        " WHERE a.ScanSetID = @set AND a.Path = @album AND f.Path = @file LIMIT 1";
                    find.Parameters.Add("@set", DbType.Int64).Value = setId;
                    find.Parameters.Add("@album", DbType.String).Value = albumPath;
                    find.Parameters.Add("@file", DbType.String).Value = fileName;
                    var existing = find.ExecuteScalar();
                    if (existing is not null && existing is not DBNull)
                    {
                        using var del = trans.CreateCommand();
                        del.CommandText = "DELETE FROM Metadata WHERE FileID = @id;\r\n" +
                            "DELETE FROM ImageMetadata WHERE FileID = @id;\r\n" +
                            "DELETE FROM Files WHERE ID = @id;\r\n" +
                            "DELETE FROM Tracks WHERE ID = @id";
                        del.Parameters.Add("@id", DbType.Int64).Value = Convert.ToInt64(existing);
                        del.ExecuteNonQuery();
                    }
                }

                long artistId = GetOrInsert(trans, "Artists", "Name", artist, lookups?.Artists);
                long albumArtistId = GetOrInsert(trans, "AlbumArtists", "Name", albumArtist, lookups?.AlbumArtists);
                long albumId = GetOrInsertAlbum(trans, setId, albumArtistId, albumPath, album, lookups?.Albums);

                long fileId;
                using (var fc = trans.CreateCommand())
                {
                    fc.CommandText = "INSERT INTO Files (Path, FileSize, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, TagType, ArtworkScanned)" +
                        " VALUES (@Path, @FileSize, @LastWriteTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames, @TagType, 1);\r\n" + lastidsql_;
                    fc.Parameters.Add("@Path", DbType.String).Value = fileName;
                    fc.Parameters.Add("@FileSize", DbType.Int64).Value = fi.Length;
                    fc.Parameters.Add("@LastWriteTime", DbType.DateTime).Value = fi.LastWriteTimeUtc;
                    fc.Parameters.Add("@CodecName", DbType.String).Value = cp.CodecName;
                    fc.Parameters.Add("@CodecType", DbType.String).Value = cp.CodecType.ToString();
                    fc.Parameters.Add("@AverageBitrate", DbType.Int64).Value = (long)cp.AverageBitrate;
                    fc.Parameters.Add("@MaxBitrate", DbType.Int64).Value = (long)cp.MaxBitrate;
                    fc.Parameters.Add("@BitsPerSample", DbType.Int64).Value = (long)cp.BitsPerSample;
                    fc.Parameters.Add("@SampleRate", DbType.Int64).Value = (long)cp.Samplerate;
                    fc.Parameters.Add("@Channels", DbType.Int64).Value = (long)cp.Channels;
                    fc.Parameters.Add("@DurationInFrames", DbType.Int64).Value = (long)cp.DurationInFrames;
                    fc.Parameters.Add("@TagType", DbType.String).Value = mp.TagType;
                    fileId = Convert.ToInt64(fc.ExecuteScalar());
                }

                using (var tc = trans.CreateCommand())
                {
                    tc.CommandText = "INSERT INTO Tracks (ID, ArtistID, AlbumID, Name, TrackNumber, TrackTotal, DiscNumber, DiscTotal, ReleaseDate)" +
                        " VALUES (@ID, @ArtistID, @AlbumID, @Name, @TrackNumber, @TrackTotal, @DiscNumber, @DiscTotal, @ReleaseDate)";
                    tc.Parameters.Add("@ID", DbType.Int64).Value = fileId;
                    tc.Parameters.Add("@ArtistID", DbType.Int64).Value = artistId;
                    tc.Parameters.Add("@AlbumID", DbType.Int64).Value = albumId;
                    tc.Parameters.Add("@Name", DbType.String).Value = title;
                    tc.Parameters.Add("@TrackNumber", DbType.Int64).Value = (object)(long?)KnownInt(TagFields.TrackNumber) ?? DBNull.Value;
                    tc.Parameters.Add("@TrackTotal", DbType.Int64).Value = (object)(long?)KnownInt(TagFields.TotalTracks) ?? DBNull.Value;
                    tc.Parameters.Add("@DiscNumber", DbType.Int64).Value = (object)(long?)KnownInt(TagFields.DiscNumber) ?? DBNull.Value;
                    tc.Parameters.Add("@DiscTotal", DbType.Int64).Value = (object)(long?)KnownInt(TagFields.TotalDiscs) ?? DBNull.Value;
                    tc.Parameters.Add("@ReleaseDate", DbType.String).Value = (object)KnownValue(TagFields.Date) ?? DBNull.Value;
                    tc.ExecuteNonQuery();
                }

                using (var mc = trans.CreateCommand())
                {
                    mc.CommandText = "INSERT INTO Metadata (FileID, KeyID, Value) VALUES (@f, @k, @v)";
                    var fileParam = mc.Parameters.Add("@f", DbType.Int64);
                    var keyParam = mc.Parameters.Add("@k", DbType.Int64);
                    var valueParam = mc.Parameters.Add("@v", DbType.String);
                    fileParam.Value = fileId;
                    foreach (var kv in knownMetadata)
                    {
                        keyParam.Value = GetOrInsert(
                            trans,
                            "MetadataKeys",
                            "\"Key\"",
                            kv.Key.ToString(),
                            lookups?.MetadataKeys);
                        valueParam.Value = kv.Value ?? "";
                        mc.ExecuteNonQuery();
                    }
                }

                foreach (var image in mp.GetImageMetadata())
                {
                    long imageId = GetOrInsertImage(trans, image, lookups?.Images);
                    using var imc = trans.CreateCommand();
                    imc.CommandText = "INSERT INTO ImageMetadata (FileID, ImageID, Description, Category) VALUES (@f, @i, @d, @c)";
                    imc.Parameters.Add("@f", DbType.Int64).Value = fileId;
                    imc.Parameters.Add("@i", DbType.Int64).Value = imageId;
                    imc.Parameters.Add("@d", DbType.String).Value = image.Description ?? "";
                    imc.Parameters.Add("@c", DbType.String).Value = image.Category ?? "";
                    imc.ExecuteNonQuery();
                }

                return true;
        }

        private long GetOrInsert(
            DbTransaction trans,
            string table,
            string column,
            string value,
            Dictionary<string, long> cache = null)
        {
            value ??= "";
            if (cache is not null && cache.TryGetValue(value, out long cached))
                return cached;

            long id;
            using (var sel = trans.CreateCommand())
            {
                sel.CommandText = $"SELECT ID FROM {table} WHERE {column} = @v LIMIT 1";
                sel.Parameters.Add("@v", DbType.String).Value = value;
                var found = sel.ExecuteScalar();
                if (found is not null && found is not DBNull)
                {
                    id = Convert.ToInt64(found);
                    if (cache is not null)
                        cache[value] = id;
                    return id;
                }
            }
            using var ins = trans.CreateCommand();
            ins.CommandText = $"INSERT INTO {table} ({column}) VALUES (@v);\r\n" + lastidsql_;
            ins.Parameters.Add("@v", DbType.String).Value = value;
            id = Convert.ToInt64(ins.ExecuteScalar());
            if (cache is not null)
                cache[value] = id;
            return id;
        }

        private long GetOrInsertAlbum(
            DbTransaction trans,
            long setId,
            long albumArtistId,
            string albumPath,
            string name,
            Dictionary<(long ScanSetID, long AlbumArtistID, string Path, string Name), long> cache = null)
        {
            albumPath ??= "";
            name ??= "";
            var key = (setId, albumArtistId, albumPath, name);
            if (cache is not null && cache.TryGetValue(key, out long cached))
                return cached;

            long id;
            using (var sel = trans.CreateCommand())
            {
                sel.CommandText = "SELECT ID FROM Albums WHERE ScanSetID = @s AND AlbumArtistID = @aa AND Path = @p AND Name = @n LIMIT 1";
                sel.Parameters.Add("@s", DbType.Int64).Value = setId;
                sel.Parameters.Add("@aa", DbType.Int64).Value = albumArtistId;
                sel.Parameters.Add("@p", DbType.String).Value = albumPath;
                sel.Parameters.Add("@n", DbType.String).Value = name;
                var found = sel.ExecuteScalar();
                if (found is not null && found is not DBNull)
                {
                    id = Convert.ToInt64(found);
                    if (cache is not null)
                        cache[key] = id;
                    return id;
                }
            }
            using var ins = trans.CreateCommand();
            ins.CommandText = "INSERT INTO Albums (ScanSetID, AlbumArtistID, Path, Name) VALUES (@s, @aa, @p, @n);\r\n" + lastidsql_;
            ins.Parameters.Add("@s", DbType.Int64).Value = setId;
            ins.Parameters.Add("@aa", DbType.Int64).Value = albumArtistId;
            ins.Parameters.Add("@p", DbType.String).Value = albumPath;
            ins.Parameters.Add("@n", DbType.String).Value = name;
            id = Convert.ToInt64(ins.ExecuteScalar());
            if (cache is not null)
                cache[key] = id;
            return id;
        }

        private long GetOrInsertImage(
            DbTransaction trans,
            IMetadataImage image,
            Dictionary<string, long> cache = null)
        {
            string hash = image.Hash ?? "";
            if (cache is not null && cache.TryGetValue(hash, out long cached))
                return cached;

            long id;
            using (var sel = trans.CreateCommand())
            {
                sel.CommandText = "SELECT ID FROM Images WHERE Hash = @h LIMIT 1";
                sel.Parameters.Add("@h", DbType.String).Value = hash;
                var found = sel.ExecuteScalar();
                if (found is not null && found is not DBNull)
                {
                    id = Convert.ToInt64(found);
                    if (cache is not null)
                        cache[hash] = id;
                    return id;
                }
            }
            using var ins = trans.CreateCommand();
            ins.CommandText = "INSERT INTO Images (Hash, ImageType, Width, Height, Size, Data) VALUES (@h, @t, @w, @ht, @s, @d);\r\n" + lastidsql_;
            ins.Parameters.Add("@h", DbType.String).Value = hash;
            ins.Parameters.Add("@t", DbType.String).Value = image.ImageType;
            ins.Parameters.Add("@w", DbType.Int64).Value = (long)image.Width;
            ins.Parameters.Add("@ht", DbType.Int64).Value = (long)image.Height;
            ins.Parameters.Add("@s", DbType.Int64).Value = (long)image.Size;
            ins.Parameters.Add("@d", DbType.Object).Value = image.Data;
            id = Convert.ToInt64(ins.ExecuteScalar());
            if (cache is not null)
                cache[hash] = id;
            return id;
        }

        public virtual void Dispose()
        {
            conn_.Dispose();
        }

    }

    /// <summary>A single file's full cached metadata, read straight from the database.</summary>
    public sealed class FileDetails
    {
        public string Path { get; set; }
        public string TagType { get; set; }
        /// <summary>Structured fields + codec properties (reused from the summary view).</summary>
        public MetadataCacheEntry Entry { get; set; }
        /// <summary>Normalized known fields (TagFields name → value), first-value order preserved.</summary>
        public List<KeyValuePair<string, string>> KnownFields { get; } = new();
        /// <summary>Raw format-native text frames.</summary>
        public List<KeyValuePair<string, string>> TextFields { get; } = new();
        /// <summary>Embedded images (only populated when requested).</summary>
        public List<FileImage> Images { get; } = new();
    }

    /// <summary>One embedded image read from the cache.</summary>
    public sealed class FileImage
    {
        public string Description { get; set; }
        public string Category { get; set; }
        public string ImageType { get; set; }
        public string Hash { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Size { get; set; }
        public byte[] Data { get; set; }
    }
}
