using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.IO;
using MusicFileUtilities;
using System.Threading;
using System.Data.Common;
using System.Data.SqlClient;

namespace MetadataCaching
{
 
    public partial class MetadataCacheEntry
    {
        public MetadataCacheEntry(DbDataReader reader)
        {
            _lastwritetime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            _codecname = reader[2] as string;
            _codectype = (CodecType)Enum.Parse(typeof(CodecType), reader[3] as string);
            _averagebitrate = (uint)(long)reader[4];
            _maxbitrate = (uint)(long)reader[5];
            _bitspersample = (uint)(long)reader[6];
            _samplerate = (uint)(long)reader[7];
            _channels = (uint)(long)reader[8];
            _durationinseconds = (int)(long)reader[9] / 75;
            _artist = reader[10] as string;
            _albumartist = reader[11] as string;
            _album = reader[12] as string;
            _tracknumber = (int)(long)reader[13];
            _title = reader[14] as string;
            _compilation = false;
        }

    }

    internal static class DbHelpers
    {

        public static DbParameter Add(this DbParameterCollection coll, string name, System.Data.DbType dbtype, System.Data.ParameterDirection dir = System.Data.ParameterDirection.Input)
        {
            var litecoll = coll as SQLiteParameterCollection;
            if (litecoll != null)
            {
                var parm = litecoll.Add(name, dbtype);
                parm.Direction = dir;
                return parm;
            }
            var sqlcoll = coll as SqlParameterCollection;
            if (sqlcoll != null)
            {
                SqlParameter parm = new SqlParameter();
                parm.ParameterName = name;
                parm.Direction = dir;
                try
                {
                    parm.DbType = dbtype;
                }
                catch
                {
                    if (dbtype == System.Data.DbType.Object)
                        parm.SqlDbType = System.Data.SqlDbType.VarBinary;
                    else
                        throw;
                }
                if (dbtype == System.Data.DbType.Object)
                    parm.SqlDbType = System.Data.SqlDbType.VarBinary;
                sqlcoll.Add(parm);
                return parm;
            }
            throw new NotSupportedException();
        }
    }


    public class MetadataDatabase : IDisposable
    {
        private DbConnection conn_;
        private string lastidsql_;

        private static readonly string[] sqlitecreationsql_ = {
            "PRAGMA foreign_keys = off;\r\n",

            "CREATE TABLE ScanSets (ID INTEGER PRIMARY KEY, Path TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID BIGINT NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID INTEGER PRIMARY KEY, ArtistID BIGINT REFERENCES Artists (ID) NOT NULL, AlbumID BIGINT REFERENCES Albums (ID) NOT NULL, Number BIGINT NOT NULL, Name TEXT NOT NULL);\r\n" +
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, Path TEXT NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, TrackID BIGINT REFERENCES Tracks (ID), CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, UNIQUE(ScanSetID, Path));\r\n" +
            "CREATE TABLE Images (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, Description TEXT NOT NULL, Category TEXT NOT NULL, ImageType TEXT NOT NULL, Width BIGINT NOT NULL, Height BIGINT NOT NULL, Size BIGINT NOT NULL, Data BLOB NOT NULL);\r\n" +
            "CREATE TABLE MetadataKeys (ID INTEGER PRIMARY KEY, \"Key\" TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, KeyID BIGINT REFERENCES MetadataKeys (ID) NOT NULL, Value TEXT NOT NULL);\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (1, 'Artist');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (2, 'AlbumArtist');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (3, 'Album');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (4, 'TrackNumber');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (5, 'Title');\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesPathIndex ON Files (Path ASC);\r\n" +
            "CREATE INDEX FilesScanSetIDIndex ON Files (ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesTrackIDIndex ON Files (TrackID ASC);\r\n" +
            "CREATE INDEX ImagesFileIDIndex ON Images (FileID ASC);\r\n" +
            "CREATE INDEX MetadataKeyIDIndex ON Metadata (KeyID ASC);\r\n" +
            "CREATE INDEX MetadataFileIDIndex ON Metadata (FileID ASC);\r\n" +
            "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
            "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n",

            "CREATE VIEW MetadataMapView AS SELECT *,\r\n" +
            "(SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 1 LIMIT 1) AS Artist,\r\n" +
            "COALESCE(\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 2 LIMIT 1),\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 1 LIMIT 1)) AS AlbumArtist,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 3 LIMIT 1) AS Album,\r\n" +
            "   CAST((SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 4 LIMIT 1) AS BIGINT) AS Number,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 5 LIMIT 1) AS Name\r\n" +
            "   FROM Files;\r\n",

            "CREATE VIEW MetadataSummaryView AS SELECT Files.*, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtist,\r\n" +
            "   Albums.Name AS Album, Tracks.Number AS TrackNumber, Tracks.Name AS Track FROM\r\n" +
            "   Files JOIN Tracks ON Files.TrackID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
            "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n",
            };

        private static readonly string[] sqlservercreationsql_ = {
            "CREATE TABLE ScanSets (ID BIGINT IDENTITY PRIMARY KEY, Path NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID BIGINT IDENTITY PRIMARY KEY, Name NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE AlbumArtists (ID BIGINT IDENTITY PRIMARY KEY, Name NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID BIGINT IDENTITY PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID BIGINT NOT NULL REFERENCES AlbumArtists (ID), Name NVARCHAR(MAX) NOT NULL, Path NVARCHAR(MAX) NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID BIGINT IDENTITY PRIMARY KEY, ArtistID BIGINT REFERENCES Artists (ID) NOT NULL, AlbumID BIGINT REFERENCES Albums (ID) NOT NULL, Number BIGINT NOT NULL, Name NVARCHAR(MAX) NOT NULL);\r\n" +
            "CREATE TABLE Files (ID BIGINT IDENTITY PRIMARY KEY, ScanSetID BIGINT REFERENCES ScanSets (ID) NOT NULL, Path NVARCHAR(512) NOT NULL, FileSize BIGINT NOT NULL, LastWriteTime DATETIME NOT NULL, TrackID BIGINT REFERENCES Tracks (ID), CodecName NVARCHAR(MAX) NOT NULL, CodecType NVARCHAR(MAX) NOT NULL, AverageBitrate BIGINT NOT NULL, MaxBitrate BIGINT NOT NULL, BitsPerSample BIGINT NOT NULL, SampleRate BIGINT NOT NULL, Channels BIGINT NOT NULL, DurationInFrames BIGINT NOT NULL, UNIQUE(ScanSetID, Path));\r\n" +
            "CREATE TABLE Images (ID BIGINT IDENTITY PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, Description NVARCHAR(MAX) NOT NULL, Category NVARCHAR(MAX) NOT NULL, ImageType NVARCHAR(MAX) NOT NULL, Width BIGINT NOT NULL, Height BIGINT NOT NULL, Size BIGINT NOT NULL, Data VARBINARY(MAX) NOT NULL);\r\n" +
            "CREATE TABLE MetadataKeys (ID BIGINT IDENTITY PRIMARY KEY, \"Key\" NVARCHAR(512) UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID BIGINT IDENTITY PRIMARY KEY, FileID BIGINT REFERENCES Files (ID) NOT NULL, KeyID BIGINT REFERENCES MetadataKeys (ID) NOT NULL, Value NVARCHAR(MAX) NOT NULL);\r\n" +
            "SET IDENTITY_INSERT MetadataKeys ON;\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (1, 'Artist');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (2, 'AlbumArtist');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (3, 'Album');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (4, 'TrackNumber');\r\n" +
            "INSERT INTO MetadataKeys (ID, \"Key\") VALUES (5, 'Title');\r\n" +
            "SET IDENTITY_INSERT MetadataKeys OFF;\r\n",
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesPathIndex ON Files (Path ASC);\r\n" +
            "CREATE INDEX FilesScanSetIDIndex ON Files (ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesTrackIDIndex ON Files (TrackID ASC);\r\n" +
            "CREATE INDEX ImagesFileIDIndex ON Images (FileID ASC);\r\n" +
            "CREATE INDEX MetadataKeyIDIndex ON Metadata (KeyID ASC);\r\n" +
            "CREATE INDEX MetadataFileIDIndex ON Metadata (FileID ASC);\r\n" +
            "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
            "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n",
 
            "CREATE VIEW MetadataMapView AS SELECT *,\r\n" +
            "(SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 1) AS Artist,\r\n" +
            "COALESCE(\r\n" +
            "   (SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 2),\r\n" +
            "   (SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 1)) AS AlbumArtist,\r\n" +
            "   (SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 3) AS Album,\r\n" +
            "   CAST((SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 4) AS BIGINT) AS Number,\r\n" +
            "   (SELECT TOP 1 Value FROM Metadata WHERE FileID = Files.ID AND KeyID = 5) AS Name\r\n" +
            "   FROM Files;\r\n",

            "CREATE VIEW MetadataSummaryView AS SELECT Files.*, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtist,\r\n" +
            "   Albums.Name AS Album, Tracks.Number AS TrackNumber, Tracks.Name AS Track FROM\r\n" +
            "   Files JOIN Tracks ON Files.TrackID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
            "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n",
            };

        private MetadataDatabase()
        {
        }

        public static MetadataDatabase OpenDatabase(string conn)
        {
            if (conn.ToLower().StartsWith("sqlite:"))
                return OpenSqliteDatabase(conn.Substring(7));
            if (conn.ToLower().StartsWith("sql:"))
            {
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
            }
            return OpenSqliteDatabase(conn);
        }

        private static MetadataDatabase OpenSqliteDatabase(string filename)
        {
            var res = new MetadataDatabase();
            res.lastidsql_ = "SELECT last_insert_rowid();";
            bool createtables = !File.Exists(filename);
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = filename,
                DateTimeKind = DateTimeKind.Utc
            };
            res.conn_ = new SQLiteConnection(builder.ConnectionString);
            res.conn_.Open();
            using (var trans = res.conn_.BeginTransaction())
            {
                try
                {
                    using (var comm = res.conn_.CreateCommand())
                    {
                        comm.Transaction = trans;
                        if (createtables)
                        {
                            foreach (string sql in sqlitecreationsql_)
                            {
                                comm.CommandText = sql;
                                comm.ExecuteNonQuery();
                            }
                        }
                        comm.CommandText = "PRAGMA foreign_keys = on;\r\n";
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
                using (var trans = res.conn_.BeginTransaction())
                {
                    try
                    {
                        using (var comm = res.conn_.CreateCommand())
                        {
                            comm.Transaction = trans;
                            foreach (string sql in sqlservercreationsql_)
                            {
                                if (utf8)
                                    comm.CommandText = sql.Replace("NVARCHAR", "VARCHAR");
                                else
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
                }
            }

            return res;
        }

        public MetadataCache BuildCache(IEnumerable<string> paths)
        {
            var dbsets = new List<(string Path, long ID)>();
            using (var setscomm = conn_.CreateCommand())
            {
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt64(1)));
            }

            MetadataCache cache = new MetadataCache();

            foreach (var path in paths)
            {
                var set = dbsets.SingleOrDefault(s => s.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));
                if (set == default)
                    continue;
                using (var querycomm = conn_.CreateCommand())
                {
                    querycomm.CommandText = "SELECT Path, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, Artist, AlbumArtist, Album, TrackNumber, Track FROM MetadataSummaryView WHERE ScanSetID = " + set.ID;
                    using (var reader = querycomm.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var ce = new MetadataCacheEntry(reader);
                            ce.Strip();
                            cache.AddDBCacheEntry(Path.Combine(set.Path, reader[0] as string), ce);
                        }
                    }
                }

            }

            return cache;
        }

        public (int Added, int Modified, int Removed, int Unchanged) IndexFiles(IEnumerable<string> paths, bool fixup = true, bool deletemissingsets = false)
        {
            return IndexFilesAsync(paths, fixup, deletemissingsets).GetAwaiter().GetResult();
        }

        public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexFilesAsync(IEnumerable<string> paths, bool fixup = true, bool deletemissingsets = false)
        {
            var sets = new List<(string Path, long ID)>();
            var missedsets = new List<long>();

            using (var setscomm = conn_.CreateCommand())
            {
                var modpaths = paths.Select(p => p.EndsWith(Path.PathSeparator.ToString()) ? p.Substring(0, p.Length - 1) : p);
                var dbsets = new List<(string Path, long ID, bool Hit)>();
                var missing = new List<string>();
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt64(1), false));
                foreach (var path in modpaths)
                    if (dbsets.Select(s => s.Path).Count(predicate => predicate.Equals(path, StringComparison.InvariantCultureIgnoreCase)) == 0)
                        missing.Add(path);
                setscomm.CommandText = "INSERT INTO ScanSets (Path) VALUES (@Path);";
                var pathvar = setscomm.Parameters.Add("@Path", System.Data.DbType.String);
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
                dbsets.Clear();
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt64(1), modpaths.Count(p => p.Equals(reader.GetString(0), StringComparison.InvariantCultureIgnoreCase)) != 0));
                sets.AddRange(dbsets.Where(s => s.Hit).Select(s => (s.Path, s.ID)));
                missedsets.AddRange(dbsets.Where(s => !s.Hit).Select(s => (s.ID)));
            }

            int added = 0, modified = 0, removed = 0, unchanged = 0;

            using var filequeue = new BlockingCollection<(long ID, long Set, string FileName, long Length, DateTime LastWriteTime, IMetadataProvider Metadata)>();

            var filesdict = new ConcurrentDictionary<(long ScanSetID, string Path), (long ID, long Length, DateTime LastWriteTime)>();
            var fileshitdict = new ConcurrentDictionary<(long ScanSetID, string Path), bool>();
            var metadatakeysdict = new Dictionary<string, long>();

            using (var getidscomm = conn_.CreateCommand())
            {
                getidscomm.CommandText = "SELECT ID, ScanSetID, Path, FileSize, LastWriteTime FROM Files";
                using (var reader = getidscomm.ExecuteReader())
                    while (reader.Read())
                    {
                        var key = (reader.GetInt64(1), reader.GetString(2));
                        filesdict[key] = (reader.GetInt64(0), reader.GetInt64(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
                        fileshitdict[key] = false;
                    }
                getidscomm.CommandText = "SELECT ID, \"Key\" FROM MetadataKeys";
                using (var reader = getidscomm.ExecuteReader())
                    while (reader.Read())
                        metadatakeysdict.Add(reader.GetString(1), reader.GetInt64(0));
            }

            var metadatareadtask = Task.Run(() =>
            {
                Parallel.ForEach(sets, (scanpath) =>
                {
                   DirectoryInfo di = new DirectoryInfo(scanpath.Path);
                   long scanset = scanpath.ID;
                   var files = di.EnumerateFiles("*", SearchOption.AllDirectories).AsParallel().Where(fsi => MetadataExtensions.ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

                   Parallel.ForEach(files, (fi) =>
                   {
                       var relativename = fi.FullName.Substring(scanpath.Path.Length + 1);
                       var key = (scanset, relativename);
                       bool scan = false;
                       long id = -1;
                       if (filesdict.ContainsKey(key))
                       {
                           fileshitdict[key] = true;
                           var file = filesdict[key];
                           if ((fi.LastWriteTimeUtc.AddMilliseconds(-500.0) > file.Item3) || (fi.Length != file.Item2))
                           {
                               id = file.Item1;
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
                           filequeue.Add((id, scanpath.ID, relativename, fi.Length, fi.LastWriteTimeUtc, Metadata.GetProvider(fi.FullName)));
                   });

                   foreach (var file in fileshitdict.Where(kv => (kv.Key.ScanSetID == scanset) && !kv.Value).Select(kv => kv.Key))
                   {
                       filequeue.Add((filesdict[file].Item1, scanpath.ID, null, 0, DateTime.MinValue, null));
                       removed++;
                   }

                });
                filequeue.CompleteAdding();
            });

            using (var transaction = conn_.BeginTransaction())
            {
                using (DbCommand delcomm = conn_.CreateCommand(), filecomm = conn_.CreateCommand(), metacomm = conn_.CreateCommand(), imagecomm = conn_.CreateCommand(),
                    keycomm = conn_.CreateCommand())
                {
                    delcomm.Transaction = transaction;
                    filecomm.Transaction = transaction;
                    metacomm.Transaction = transaction;
                    imagecomm.Transaction = transaction;
                    keycomm.Transaction = transaction;

                    delcomm.CommandText = "DELETE FROM Metadata WHERE FileID = @ID;\r\n" +
                        "DELETE FROM Images WHERE FileID = @ID;\r\n" + 
                        "DELETE FROM Files WHERE ID = @ID";
                    delcomm.Parameters.Clear();
                    var delidparam = delcomm.Parameters.Add("@ID", System.Data.DbType.Int64);

                    var setparam = filecomm.Parameters.Add("@Set", System.Data.DbType.Int64);
                    var pathparam = filecomm.Parameters.Add("@Path", System.Data.DbType.String);
                    var filesizeparam = filecomm.Parameters.Add("@FileSize", System.Data.DbType.Int64);
                    var lastwritetimeparam = filecomm.Parameters.Add("@LastWriteTime", System.Data.DbType.DateTime);
                    var codecnameparam = filecomm.Parameters.Add("@CodecName", System.Data.DbType.String);
                    var codectypeparam = filecomm.Parameters.Add("@CodecType", System.Data.DbType.String);
                    var averagebitrateparam = filecomm.Parameters.Add("@AverageBitrate", System.Data.DbType.Int64);
                    var maxbitrateparam = filecomm.Parameters.Add("@MaxBitrate", System.Data.DbType.Int64);
                    var bitspersampleparam = filecomm.Parameters.Add("@BitsPerSample", System.Data.DbType.Int64);
                    var samplerateparam = filecomm.Parameters.Add("@SampleRate", System.Data.DbType.Int64);
                    var channelsparam = filecomm.Parameters.Add("@Channels", System.Data.DbType.Int64);
                    var durationinframesparam = filecomm.Parameters.Add("@DurationInFrames", System.Data.DbType.Int64);
                    filecomm.CommandText = "INSERT INTO Files (Path, ScanSetID, FileSize, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames)" +
                        " VALUES (@Path, @Set, @FileSize, @LastWriteTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames);\r\n" +
                        lastidsql_;

                    metacomm.CommandText = "INSERT INTO Metadata (FileID, KeyID, Value) VALUES (@FileID, @KeyID, @Value)";
                    var metafileidparam = metacomm.Parameters.Add("@FileID", System.Data.DbType.Int64);
                    var keyidparam = metacomm.Parameters.Add("@KeyID", System.Data.DbType.Int64);
                    var valueparam = metacomm.Parameters.Add("@Value", System.Data.DbType.String);

                    keycomm.CommandText = "INSERT INTO MetadataKeys (\"Key\") VALUES (@Key);\r\n" + lastidsql_;
                    var keyparam = keycomm.Parameters.Add("@Key", System.Data.DbType.String);

                    imagecomm.CommandText = "INSERT INTO Images (FileID, Description, Category, ImageType, Width, Height, Size, Data) VALUES (@FileID, @Description, @Category, @ImageType, @Width, @Height, @Size, @Data)";
                    var imagefileidparam = imagecomm.Parameters.Add("@FileID", System.Data.DbType.Int64);
                    var descriptionparam = imagecomm.Parameters.Add("@Description", System.Data.DbType.String);
                    var categoryparam = imagecomm.Parameters.Add("@Category", System.Data.DbType.String);
                    var imagetypeparam = imagecomm.Parameters.Add("@ImageType", System.Data.DbType.String);
                    var widthparam = imagecomm.Parameters.Add("@Width", System.Data.DbType.Int64);
                    var heightparam = imagecomm.Parameters.Add("@Height", System.Data.DbType.Int64);
                    var sizeparam = imagecomm.Parameters.Add("@Size", System.Data.DbType.Int64);
                    var dataparam = imagecomm.Parameters.Add("@Data", System.Data.DbType.Object);

                    foreach (var file in filequeue.GetConsumingEnumerable())
                    {

                        if (file.ID != -1)
                        {
                            delidparam.Value = file.ID;
                            delcomm.ExecuteNonQuery();
                        }
                        if (file.Metadata != null)
                        {
                            IMetadataProvider mp = file.Metadata;
                            ICodecProvider cp = mp as ICodecProvider;
                            pathparam.Value = file.FileName;
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
                            setparam.Value = file.Set;

                            metafileidparam.Value = imagefileidparam.Value = (long)filecomm.ExecuteScalar();
                            foreach (var kv in mp.GetTextMetadata())
                            {
                                long keyid;
                                if (!metadatakeysdict.TryGetValue(kv.Key, out keyid))
                                {
                                    keyparam.Value = kv.Key;
                                    keyid = (long)keycomm.ExecuteScalar();
                                    metadatakeysdict.Add(kv.Key, keyid);
                                }
                                keyidparam.Value = keyid;
                                valueparam.Value = kv.Value;
                                metacomm.ExecuteNonQuery();
                            }

                            foreach (var image in mp.GetImageMetadata())
                            {
                                descriptionparam.Value = image.Description;
                                categoryparam.Value = image.Category;
                                imagetypeparam.Value = image.ImageType;
                                widthparam.Value = image.Width;
                                heightparam.Value = image.Height;
                                sizeparam.Value = image.Size;
                                dataparam.Value = image.Data;
                                imagecomm.ExecuteNonQuery();
                            }
                        }
                    }
                }
                transaction.Commit();
            }

            await metadatareadtask;

            if (deletemissingsets && (missedsets.Count != 0))
            {
                using (var transaction = conn_.BeginTransaction())
                {
                    using (DbCommand setcomm = conn_.CreateCommand(), albumcomm = conn_.CreateCommand(), metacomm = conn_.CreateCommand(),
                        imagecomm = conn_.CreateCommand(), trackscomm = conn_.CreateCommand(), filecomm = conn_.CreateCommand())
                    {
                        setcomm.CommandText = "DELETE FROM ScanSets WHERE ID = @ID";
                        var idparam = setcomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        filecomm.CommandText = "DELETE FROM Files WHERE ScanSetID = @ID";
                        var filesidparam = filecomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        trackscomm.CommandText = "DELETE FROM Tracks WHERE AlbumID IN (SELECT ID From Albums WHERE ScanSetID = @ID)";
                        var tracksidparam = trackscomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        metacomm.CommandText = "DELETE FROM Metadata WHERE FileID IN (SELECT ID FROM Files WHERE ScanSetID = @ID)";
                        var metaidparam = metacomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        imagecomm.CommandText = "DELETE FROM Images WHERE FileID IN (SELECT ID FROM Files WHERE ScanSetID = @ID)";
                        var imageidparam = imagecomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        albumcomm.CommandText = "DELETE FROM Albums WHERE ScanSetID = @ID";
                        var albumidparam = albumcomm.Parameters.Add("@ID", System.Data.DbType.Int64);
                        metacomm.CommandTimeout = imagecomm.CommandTimeout = 0;

                        setcomm.Transaction = transaction;
                        filecomm.Transaction = transaction;
                        trackscomm.Transaction = transaction;
                        metacomm.Transaction = transaction;
                        imagecomm.Transaction = transaction;
                        albumcomm.Transaction = transaction;

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
                    }
                    transaction.Commit();
                }
            }

            if (fixup && ((added != 0) || (modified != 0) || (removed != 0)))
                Fixup();

            return (added, modified, removed, unchanged);
        }

        private void Fixup()
        {
            using (var querycomm = conn_.CreateCommand())
            {
                var artistlist = new List<string>();
                var albumartistlist = new List<string>();

                querycomm.CommandText = "INSERT INTO Artists (Name) SELECT DISTINCT Artist FROM MetadataMapView WHERE Artist NOT IN (SELECT Name FROM Artists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "INSERT INTO AlbumArtists (Name) SELECT DISTINCT AlbumArtist FROM MetadataMapView WHERE AlbumArtist NOT IN (SELECT Name FROM AlbumArtists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();

                var artistdict = new Dictionary<string, long>();
                querycomm.CommandText = "SELECT * FROM Artists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistdict.Add(reader.GetString(1), reader.GetInt64(0));

                var albumartistdict = new Dictionary<string, long>();
                querycomm.CommandText = "SELECT * FROM AlbumArtists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistdict.Add(reader.GetString(1), reader.GetInt64(0));

                var toupdate = new List<(long, long, string, string, string, string, long, string)>();
                querycomm.CommandText = "SELECT ID, ScanSetID, Path, Artist, AlbumArtist, Album, Number, Name FROM MetadataMapView WHERE TrackID IS NULL";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        toupdate.Add((reader.GetInt64(0), reader.GetInt64(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.IsDBNull(6) ? 0 : reader.GetInt64(6), reader[7] as string));

                var distinctalbums = toupdate.Select(t => (t.Item2, Path.GetDirectoryName(t.Item3), t.Item5, t.Item6)).Distinct().ToDictionary(da => da, da => true);
                var albumsdict = new Dictionary<(long, string, string, string), long>();
                querycomm.CommandText = "SELECT Albums.ID, Albums.ScanSetID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader.GetInt64(1), reader[2] as string, reader[3] as string, reader[4] as string), reader.GetInt64(0));

                var albumdifferences = distinctalbums.Keys.Except(albumsdict.Keys).ToArray();
                if (albumdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.Transaction = transaction;
                        querycomm.CommandText = "INSERT INTO Albums (Name, Path, AlbumArtistID, ScanSetID) VALUES (@Name, @Path, @AlbumArtistID, @ScanSetID)";
                        querycomm.Parameters.Clear();
                        var nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        var pathparam = querycomm.Parameters.Add("@Path", System.Data.DbType.String);
                        var albumartistidparam = querycomm.Parameters.Add("@AlbumArtistID", System.Data.DbType.Int64);
                        var scansetidparam = querycomm.Parameters.Add("@ScanSetID", System.Data.DbType.Int64);
                        foreach (var album in albumdifferences)
                        {
                            nameparam.Value = album.Item4;
                            pathparam.Value = album.Item2;
                            albumartistidparam.Value = albumartistdict[album.Item3];
                            scansetidparam.Value = album.Item1;
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                albumsdict.Clear();
                querycomm.CommandText = "SELECT Albums.ID, Albums.ScanSetID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader.GetInt64(1), reader[2] as string, reader[3] as string, reader[4] as string), reader.GetInt64(0));

                var distincttracks = toupdate.Select(t => (t.Item2, Path.GetDirectoryName(t.Item3), t.Item4, t.Item5, t.Item6, t.Item7, t.Item8)).Distinct().ToDictionary(dt => dt, dt => true);
                var tracksdict = new Dictionary<(long, string, string, string, string, long, string), long>();
                querycomm.CommandText = "SELECT Tracks.ID, Albums.ScanSetID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader.GetInt64(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.GetInt64(6), reader[7] as string), reader.GetInt64(0));

                var trackdifferences = distincttracks.Keys.Except(tracksdict.Keys).ToArray();
                if (trackdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.Transaction = transaction;
                        querycomm.CommandText = "INSERT INTO Tracks (ArtistID, AlbumID, Number, Name) VALUES (@ArtistID, @AlbumID, @Number, @Name)";
                        querycomm.Parameters.Clear();
                        var artistidparam = querycomm.Parameters.Add("@ArtistID", System.Data.DbType.Int64);
                        var albumidparam = querycomm.Parameters.Add("@AlbumID", System.Data.DbType.Int64);
                        var Numberparam = querycomm.Parameters.Add("@Number", System.Data.DbType.Int64);
                        var Nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        foreach (var track in trackdifferences)
                        {
                            artistidparam.Value = artistdict[track.Item3];
                            albumidparam.Value = albumsdict[(track.Item1, track.Item2, track.Item4, track.Item5)];
                            Numberparam.Value = track.Item6;
                            Nameparam.Value = track.Item7;
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                querycomm.Transaction = null;
                querycomm.CommandText = "SELECT Tracks.ID, Albums.ScanSetID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                tracksdict.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader.GetInt64(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.GetInt64(6), reader[7] as string), reader.GetInt64(0));

                if (toupdate.Count > 0)
                {
                    querycomm.CommandText = "UPDATE Files SET TrackID = @TrackID WHERE ID = @ID";
                    querycomm.Parameters.Clear();
                    var trackidparam = querycomm.Parameters.Add("@TrackID", System.Data.DbType.String);
                    var idparam = querycomm.Parameters.Add("@ID", System.Data.DbType.String);
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.Transaction = transaction;
                        foreach (var u in toupdate)
                        {
                            idparam.Value = u.Item1;
                            trackidparam.Value = tracksdict[(u.Item2, Path.GetDirectoryName(u.Item3), u.Item4, u.Item5, u.Item6, u.Item7, u.Item8)];
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();

                    }
                }

                querycomm.Transaction = null;
                querycomm.Parameters.Clear();
                querycomm.CommandText = "DELETE FROM Tracks WHERE ID NOT IN (SELECT TrackID FROM Files)";
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "DELETE FROM Albums WHERE ID NOT IN (SELECT AlbumID FROM Tracks)";
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "DELETE FROM Artists WHERE ID NOT IN (SELECT ArtistID FROM Tracks)";
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "DELETE FROM AlbumArtists WHERE ID NOT IN (SELECT AlbumArtistID FROM Albums)";
                querycomm.ExecuteNonQuery();

            }
        }

        public void Dispose()
        {
            conn_.Dispose();
        }

    }
}
