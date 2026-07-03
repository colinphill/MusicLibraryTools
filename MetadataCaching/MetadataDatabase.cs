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

[assembly: InternalsVisibleTo("MusicFileUtilities.Tests")]

namespace MetadataCaching
{
 
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

        // Indexer write-batching tunables. Defaults bound the WAL / per-statement overhead on a
        // large library; tests lower them to exercise the multi-commit and multi-row-flush paths
        // without huge inputs.
        internal static int IndexFilesPerBatch = 2000;
        internal static int IndexMetaRowsPerInsert = 500;

        // Scan-side tunables. IndexScanParallelism is the GLOBAL cap on concurrent subtree
        // scanners across all scan sets (per-set caps multiply by the set count and can
        // swamp a NAS). IndexFileQueueBound caps parsed-but-unwritten files so parsing
        // can't run arbitrarily far ahead of the SQLite writer (parsed files hold artwork
        // bytes, so an unbounded queue can balloon to GBs on a big scan).
        internal static int IndexScanParallelism = 8;
        internal static int IndexFileQueueBound = 256;

#if SQLITE
        private static readonly string[] sqlitecreationsql_ = {
            "PRAGMA foreign_keys = off;\r\n",

            "CREATE TABLE ScanSets (ID INTEGER PRIMARY KEY, Path TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID BIGINT NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID INTEGER PRIMARY KEY, ArtistID BIGINT REFERENCES Artists (ID) NOT NULL, AlbumID BIGINT REFERENCES Albums (ID) NOT NULL, Name TEXT NOT NULL, TrackNumber BIGINT, TrackTotal BIGINT, DiscNumber BIGINT, DiscTotal BIGINT, ReleaseDate TEXT);\r\n" +
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, Path TEXT NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, TagType TEXT NOT NULL);\r\n" +
            "CREATE TABLE Images (ID INTEGER PRIMARY KEY, Hash TEXT NOT NULL, ImageType TEXT NOT NULL, Width BIGINT NOT NULL, Height BIGINT NOT NULL, Size BIGINT NOT NULL, Data BLOB NOT NULL);\r\n" +
            "CREATE TABLE ImageMetadata (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, ImageID BIGINT REFERENCES Images (ID), Description TEXT NOT NULL, Category TEXT NOT NULL);\r\n" +
            "CREATE TABLE MetadataKeys (ID INTEGER PRIMARY KEY, \"Key\" TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, KeyID BIGINT REFERENCES MetadataKeys (ID) NOT NULL, Value TEXT NOT NULL);\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
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
            "CREATE TABLE Files (ID BIGINT IDENTITY PRIMARY KEY, Path NVARCHAR(512) NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, CodecName NVARCHAR(MAX) NOT NULL, CodecType NVARCHAR(MAX) NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, TagType NVARCHAR(64) NOT NULL);\r\n" +
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

        public MetadataCache BuildCache(IEnumerable<(string Path, string[] Extensions)> paths)
        {
            var dbsets = new List<(string Path, long ID)>();
            using (var setscomm = conn_.CreateCommand())
            {
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using var reader = setscomm.ExecuteReader();
                while (reader.Read())
                    dbsets.Add((reader.GetString("Path"), reader.GetInt64("ID")));
            }

            MetadataCache cache = new MetadataCache();

            foreach (var path in paths)
            {
                var set = dbsets.SingleOrDefault(s => s.Path.Equals(path.Path, StringComparison.InvariantCultureIgnoreCase));
                if (set == default)
                    continue;
                using var querycomm = conn_.CreateCommand();
                querycomm.CommandText = "SELECT Path, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, Artist, AlbumArtist, Album, TrackNumber, TrackTotal, DiscNumber, DiscTotal, ReleaseDate, Track, AlbumPath FROM MetadataSummaryView WHERE ScanSetID = " + set.ID;
                using var reader = querycomm.ExecuteReader();
                int oAlbumPath = reader.GetOrdinal("AlbumPath"), oPath = reader.GetOrdinal("Path");
                while (reader.Read())
                {
                    var ce = new MetadataCacheEntry(reader);
                    ce.Strip();
                    var fullpath = Path.Combine(set.Path, reader.GetString(oAlbumPath), reader.GetString(oPath));
                    if (path.Extensions == null)
                        cache.AddDBCacheEntry(fullpath, ce);
                    else if (path.Extensions.Contains(Path.GetExtension(fullpath)))
                        cache.AddDBCacheEntry(fullpath, ce);
                }
            }
            return cache;
        }

        public MetadataCache BuildCache(IEnumerable<string> paths)
        {
            return BuildCache(paths.Select<string, (string Path, string[] Extensions)>(p => (p, null)));
        }

        public (int Added, int Modified, int Removed, int Unchanged) IndexFiles(IEnumerable<string> paths, bool deletemissingsets = false)
        {
            return IndexFilesAsync(paths, deletemissingsets).GetAwaiter().GetResult();
        }

        public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexFilesAsync(IEnumerable<string> paths, bool deletemissingsets = false)
        {
            var sets = new List<(string Path, long ID)>();
            var missedsets = new List<long>();

            using (var setscomm = conn_.CreateCommand())
            {
                // Materialized: this is re-enumerated once per ScanSets row below, and `paths`
                // itself may be a deferred query from the caller.
                var modpaths = paths.Select(p => p.EndsWith(Path.PathSeparator.ToString()) ? p.Substring(0, p.Length - 1) : p).ToList();
                var dbsets = new List<(string Path, long ID, bool Hit)>();
                var missing = new List<string>();
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt64(1), false));
                // O(1) membership instead of an O(sets) LINQ scan per requested path.
                var dbsetpaths = new HashSet<string>(dbsets.Select(s => s.Path), StringComparer.InvariantCultureIgnoreCase);
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
                        dbsets.Add((setpath, reader.GetInt64(1), requestedpaths.Contains(setpath)));
                    }
                sets.AddRange(dbsets.Where(s => s.Hit).Select(s => (s.Path, s.ID)));
                missedsets.AddRange(dbsets.Where(s => !s.Hit).Select(s => (s.ID)));
            }

            int added = 0, modified = 0, removed = 0, unchanged = 0, scanned = 0;

            using var filequeue = new BlockingCollection<(long ID, long Set, string FileName, long Length, DateTime LastWriteTime, IMediaFile File)>(IndexFileQueueBound);

            var filesdict = new ConcurrentDictionary<(long ScanSetID, string Path), (long ID, long Length, DateTime LastWriteTime)>();
            var fileshitdict = new ConcurrentDictionary<(long ScanSetID, string Path), bool>();
            var metadatakeysdict = new Dictionary<string, long>();
            var artistsdict = new Dictionary<string, long>();
            var albumartistsdict = new Dictionary<string, long>();
            var imagesdict = new Dictionary<string, long>();
            var albumsdict = new Dictionary<(long ScanSetID, long AlbumArtistID, string Path, string Name), long>();

            using (var querycomm = conn_.CreateCommand())
            {
                querycomm.CommandText = "SELECT ID, ScanSetID, Path, FileSize, LastWriteTime, AlbumPath FROM MetadataSummaryView";
                using var reader = querycomm.ExecuteReader();
                // Resolve ordinals once instead of a name->ordinal lookup per column per row;
                // this loop walks the entire library.
                int oId = reader.GetOrdinal("ID"), oSet = reader.GetOrdinal("ScanSetID"),
                    oPath = reader.GetOrdinal("Path"), oSize = reader.GetOrdinal("FileSize"),
                    oLwt = reader.GetOrdinal("LastWriteTime"), oAlbumPath = reader.GetOrdinal("AlbumPath");
                while (reader.Read())
                {
                    var key = (reader.GetInt64(oSet), Path.Combine(reader.GetString(oAlbumPath), reader.GetString(oPath)));
                    filesdict[key] = (reader.GetInt64(oId), reader.GetInt64(oSize), DateTime.SpecifyKind(reader.GetDateTime(oLwt), DateTimeKind.Utc));
                    fileshitdict[key] = false;
                }
            }

            var metadatareadtask = Task.Run(() =>
            {
                // Split each scan set into per-subtree units: one non-recursive unit for files
                // sitting directly in the set root, plus one recursive walker per top-level
                // subdirectory. A single recursive enumerator serializes every directory-listing
                // round-trip; independent subtree walkers let listing itself fan out across the
                // share. All units share ONE global parallelism cap (a per-set cap multiplies by
                // the set count — 12 sets x 32 threads once flooded the NAS with ~384 readers).
                var units = new List<(string SetPath, long SetID, string Root, bool Recurse)>();
                foreach (var scanpath in sets)
                {
                    units.Add((scanpath.Path, scanpath.ID, scanpath.Path, false));
                    foreach (var entry in new MusicFileEnumerator(scanpath.Path, recurse: false))
                        // Skip .itlp packages here too: a recursive unit rooted INSIDE the package
                        // would bypass the enumerator's recursion pruning.
                        if (entry.FileType == MFEType.Directory && !Path.GetFileName(entry.Name).Contains(".itlp", StringComparison.OrdinalIgnoreCase))
                            units.Add((scanpath.Path, scanpath.ID, entry.Name, true));
                }

                var scanParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = IndexScanParallelism };
                Parallel.ForEach(units, scanParallelOptions, () => { return SHA256.Create(); }, (unit, loopstate, hash) =>
                {
                    foreach (var file in new MusicFileEnumerator(unit.Root, unit.Recurse))
                    {
                       if (file.FileType != MFEType.MusicFile)
                           continue;
                       var relativename = file.Name.Substring(unit.SetPath.Length + 1);
                       var key = (SetID: unit.SetID, RelativeName: relativename);
                       var scan = false;
                       long id = -1;
                       if (filesdict.ContainsKey(key))
                       {
                           fileshitdict[key] = true;
                           var (ID, Length, LastWriteTime) = filesdict[key];
                           if ((file.Modified.AddMilliseconds(-500.0) > LastWriteTime) || (file.Size != Length))
                           {
                               id = ID;
                               Interlocked.Increment(ref modified);
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
                           Interlocked.Increment(ref added);
                       }
                       if (scan)
                       {
                           try
                           {
                               filequeue.Add((id, unit.SetID, relativename, file.Size, file.Modified, MediaFile.GetFile(file.Name, hash)));
                               int done = Interlocked.Increment(ref scanned);
                               // Console is internally locked; printing from every scan thread
                               // every 100 files serializes them. A coarser cadence (and no
                               // explicit Flush, which the console stream does anyway) avoids it.
                               if ((done % 1000) == 0)
                                   Console.Write($"Scanned: {done}\r");
                           }
                           catch (IOException ex)
                           {
                               Console.WriteLine($"IOException On File: {file.Name} - {ex.Message} (0x{ex.HResult:x})");
                               Console.WriteLine(ex.StackTrace);
                           }
                           catch (Exception ex)
                           {
                               Console.WriteLine($"Unknown Exception On File: {file.Name} - {ex.Message}");
                               loopstate.Break();
                           }
                       }
                    }
                    return hash;
                }, (hash) => { hash.Dispose(); });

                // Removal detection must wait until every unit of every set has finished marking
                // hits, so it runs once here rather than per set. Only sets scanned in this call
                // count: files of unrequested sets were never walked, and those sets are handled
                // by the deletemissingsets path instead.
                var scannedsets = new HashSet<long>(sets.Select(s => s.ID));
                foreach (var file in fileshitdict.Where(kv => !kv.Value && scannedsets.Contains(kv.Key.ScanSetID)).Select(kv => kv.Key))
                {
                    filequeue.Add((filesdict[file].ID, file.ScanSetID, null, 0, DateTime.MinValue, null));
                    removed++;
                }

                Console.WriteLine($"Scanned: {scanned}");
                filequeue.CompleteAdding();
            });

            using (var querycomm = conn_.CreateCommand())
            {
                querycomm.CommandText = "SELECT ID, \"Key\" FROM MetadataKeys";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        metadatakeysdict.Add(reader.GetString("Key"), reader.GetInt64("ID"));
                querycomm.CommandText = "SELECT ID, Name FROM Artists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistsdict.Add(reader.GetString("Name"), reader.GetInt64("ID"));
                querycomm.CommandText = "SELECT ID, Name FROM AlbumArtists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistsdict.Add(reader.GetString("Name"), reader.GetInt64("ID"));
                querycomm.CommandText = "SELECT ID, ScanSetID, AlbumArtistID, Path, Name FROM Albums";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader.GetInt64("ScanSetID"), reader.GetInt64("AlbumArtistID"), reader.GetString("Path"), reader.GetString("Name")), reader.GetInt64("ID"));
                querycomm.CommandText = "SELECT ID, Hash FROM Images";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        imagesdict.Add(reader.GetString("Hash"), reader.GetInt64("ID"));
            }

            // Batch the writes: commit and start a fresh transaction every FilesPerBatch files so
            // the WAL can checkpoint instead of growing for the whole scan, and a crash loses only
            // the current batch (the DB is a cache and self-heals on the next run). Metadata rows
            // are buffered and flushed as multi-row INSERTs (MetaRowsPerInsert per statement).
            int FilesPerBatch = IndexFilesPerBatch;
            int MetaRowsPerInsert = IndexMetaRowsPerInsert;
            var metabuffer = new List<(long FileID, long KeyID, string Value)>();

            DbTransaction trans = conn_.BeginTransaction();
            try
            {
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
                    filecomm.CommandText = "INSERT INTO Files (Path, FileSize, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, TagType)" +
                        " VALUES (@Path, @FileSize, @LastWriteTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames, @TagType);\r\n" +
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

                        foreach (var kv in mp.GetTextMetadata())
                        {
                            if (!metadatakeysdict.TryGetValue(kv.Key, out var keyid))
                            {
                                keyparam.Value = kv.Key;
                                keyid = (long)keycomm.ExecuteScalar();
                                metadatakeysdict.Add(kv.Key, keyid);
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
            finally
            {
                trans.Dispose();
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

        public virtual void Dispose()
        {
            conn_.Dispose();
        }

    }
}
