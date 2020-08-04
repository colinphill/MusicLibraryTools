using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using MusicFileUtilities;
using MusicLibraryTools;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Transactions;
using System.Runtime.InteropServices;

namespace MetadataDBWork
{
    class Program
    {
        static readonly string[] ValidExtensions = { ".dsf", ".m4a", ".mp3", ".flac", ".ogg" };

        static string creationsql_ =
            "PRAGMA foreign_keys = off;\r\n" +
            "BEGIN TRANSACTION;\r\n" +
            "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE);\r\n" +
            "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, AlbumArtistID INTEGER NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE);\r\n" +
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, \"Set\" INTEGER NOT NULL, Path TEXT UNIQUE, ScanTime DATETIME NOT NULL, ArtistID INTEGER REFERENCES Artists (ID), AlbumID INTEGER REFERENCES Albums (ID), CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate INTEGER NOT NULL, MaxBitrate INTEGER NOT NULL, BitsPerSample INTEGER NOT NULL, SampleRate INTEGER NOT NULL, Channels INTEGER NOT NULL, DurationInFrames INTEGER NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, \"Key\" TEXT NOT NULL, Value TEXT NOT NULL);\r\n" +
            "CREATE INDEX FileIDIndex ON Metadata(FileID ASC);\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE VIEW MetadataMapView AS SELECT *,\r\n"+
            "(SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ARTIST') AS Artist,\r\n" +
            "COALESCE(\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ALBUMARTIST'),\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ARTIST')) AS AlbumArtist,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ALBUM') As Album,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'TRACKNUMBER') As TrackNumber\r\n" +
            "   FROM Files;\r\n" +
            "COMMIT TRANSACTION;\r\n" +
            "PRAGMA foreign_keys = on\r\n";

        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            bool createtables = !File.Exists("cache.db");
            using var conn = new SQLiteConnection("URI=file:cache.db;DateTimeKind=Utc");
            conn.Open();

            if (createtables)
            {
                using var command = conn.CreateCommand();
                command.CommandText = creationsql_;
                command.ExecuteNonQuery();
            }

            var paths = new []
            {
                ( @"\\ritsuko.projecteva.net\Roon\Purchased Sync", 30 ),
                ( @"\\ritsuko.projecteva.net\AllMusic\FLAC", 1 ),
                ( @"\\ritsuko.projecteva.net\AllMusic\FLAC2", 2 ),
                ( @"\\ritsuko.projecteva.net\AllMusic\HiRes\Stereo", 10 ),
                ( @"\\ritsuko.projecteva.net\AllMusic\HiRes\Multi", 20 )
            };

            var filesdict = new ConcurrentDictionary<string, ValueTuple<int, int, DateTime>>();

            using var getfilescomm = conn.CreateCommand();
            getfilescomm.CommandText = "SELECT ID, \"Set\", Path, ScanTime FROM Files";
            using (var reader = getfilescomm.ExecuteReader())
                while (reader.Read())
                    filesdict[reader.GetString(2)] = (reader.GetInt32(0), reader.GetInt32(1), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));

            foreach (var scanpath in paths)
            {
                GC.Collect();

                DirectoryInfo di = new DirectoryInfo(scanpath.Item1);
                int scanset = scanpath.Item2;
                var files = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Where(fsi => ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

                var bag = new ConcurrentBag<Tuple<string, DateTime, IMetadataProvider>>();
                var deletekeys = new ConcurrentBag<int>();

                Parallel.ForEach(files, (fi) =>
                {
                    bool scan = false;
                    if (filesdict.ContainsKey(fi.FullName))
                    {
                        var file = filesdict[fi.FullName];
                        if (fi.LastWriteTimeUtc > file.Item3)
                        {
                            deletekeys.Add(file.Item1);
                            scan = true;
                        }
                    }
                    else
                        scan = true;
                    if (scan)
                        bag.Add(new Tuple<string, DateTime, IMetadataProvider>(fi.FullName, DateTime.UtcNow, Metadata.GetProvider(fi.FullName)));
                });

                if (!deletekeys.IsEmpty)
                {
                    using var deletecomm = conn.CreateCommand();
                    var idparam = deletecomm.Parameters.Add("@ID", System.Data.DbType.Int32);
                    using (var transaction = conn.BeginTransaction())
                    {
                        deletecomm.CommandText = "DELETE FROM Metadata WHERE FileID = @ID";
                        foreach (var id in deletekeys)
                        {
                            idparam.Value = id;
                            deletecomm.ExecuteNonQuery();
                        }
                        deletecomm.CommandText = "DELETE FROM Files WHERE ID = @ID";
                        foreach (var id in deletekeys)
                        {
                            idparam.Value = id;
                            deletecomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                if (!bag.IsEmpty)
                {
                    using var filescomm = conn.CreateCommand();
                    filescomm.Parameters.AddWithValue("@Set", scanset);
                    var pathparam = filescomm.Parameters.Add("@Path", System.Data.DbType.String);
                    var scantimeparam = filescomm.Parameters.Add("@ScanTime", System.Data.DbType.DateTime);
                    var codecnameparam = filescomm.Parameters.Add("@CodecName", System.Data.DbType.String);
                    var codectypeparam = filescomm.Parameters.Add("@CodecType", System.Data.DbType.String);
                    var averagebitrateparam = filescomm.Parameters.Add("@AverageBitrate", System.Data.DbType.Int32);
                    var maxbitrateparam = filescomm.Parameters.Add("@MaxBitrate", System.Data.DbType.Int32);
                    var bitspersampleparam = filescomm.Parameters.Add("@BitsPerSample", System.Data.DbType.Int32);
                    var samplerateparam = filescomm.Parameters.Add("@SampleRate", System.Data.DbType.Int32);
                    var channelsparam = filescomm.Parameters.Add("@Channels", System.Data.DbType.Int32);
                    var durationinframesparam = filescomm.Parameters.Add("@DurationInFrames", System.Data.DbType.Int32);
                    filescomm.CommandText = "INSERT INTO Files (Path, \"Set\", ScanTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames)" +
                    " VALUES (@Path, @Set, @ScanTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames);";

                    using (var transaction = conn.BeginTransaction())
                    {
                        foreach (var file in bag)
                        {
                            string path = file.Item1;
                            IMetadataProvider mp = file.Item3;
                            ICodecProvider cp = mp as ICodecProvider;
                            pathparam.Value = path;
                            scantimeparam.Value = file.Item2;
                            codecnameparam.Value = cp.CodecName;
                            codectypeparam.Value = cp.CodecType.ToString();
                            averagebitrateparam.Value = cp.AverageBitrate;
                            maxbitrateparam.Value = cp.MaxBitrate;
                            bitspersampleparam.Value = cp.BitsPerSample;
                            samplerateparam.Value = cp.Samplerate;
                            channelsparam.Value = cp.Channels;
                            durationinframesparam.Value = cp.DurationInFrames;
                            filescomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }

                    var keymap = new Dictionary<string, int>();
                    using (var query = conn.CreateCommand())
                    {
                        query.CommandText = "SELECT ID, Path FROM Files";
                        using (var reader = query.ExecuteReader())
                            while (reader.Read())
                                keymap.Add(reader.GetString(1), reader.GetInt32(0));
                    }

                    using var metacomm = conn.CreateCommand();
                    metacomm.CommandText = "INSERT INTO Metadata (FileID, \"Key\", Value) VALUES (@FileID, @Key, @Value)";
                    var fileidparam = metacomm.Parameters.Add("@FileID", System.Data.DbType.Int32);
                    var keyparam = metacomm.Parameters.Add("@Key", System.Data.DbType.String);
                    var valueparam = metacomm.Parameters.Add("@Value", System.Data.DbType.String);

                    using (var transaction = conn.BeginTransaction())
                    {
                        foreach (var file in bag)
                        {
                            fileidparam.Value = keymap[file.Item1];
                            foreach (var kv in file.Item3.GetTextMetadata())
                            {
                                keyparam.Value = kv.Key;
                                valueparam.Value = kv.Value;
                                metacomm.ExecuteNonQuery();
                            }
                        }
                        transaction.Commit();
                    }
                }
            }

            using (var querycomm = conn.CreateCommand())
            {
                var artistlist = new List<string>();
                var albumartistlist = new List<string>();

                querycomm.CommandText = "INSERT INTO Artists (NAME) SELECT DISTINCT Artist FROM MetadataMapView WHERE Artist NOT IN (SELECT Name FROM Artists)";
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "INSERT INTO AlbumArtists (NAME) SELECT DISTINCT AlbumArtist FROM MetadataMapView WHERE AlbumArtist NOT IN (SELECT Name FROM AlbumArtists)";
                querycomm.ExecuteNonQuery();

                var artistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM Artists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var albumartistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM AlbumArtists";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var toupdate = new List<(int, string, string, string, string)>();
                querycomm.CommandText = "SELECT ID, Path, Artist, AlbumArtist, Album FROM MetadataMapView WHERE ArtistID IS NULL OR AlbumID IS NULL";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        toupdate.Add((reader.GetInt32(0), reader[1] as string, reader[2] as string, reader[3] as string, reader[4] as string));

                var distinctalbums = toupdate.Select(t => (Path.GetDirectoryName(t.Item2), t.Item4, t.Item5)).Distinct().ToDictionary(da => da, da => true);
                var albumsdict = new Dictionary<(string, string, string), int>();
                querycomm.CommandText = "SELECT Albums.ID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader[1] as string, reader[2] as string, reader[3] as string), reader.GetInt32(0));

                var differences = distinctalbums.Keys.Except(albumsdict.Keys).ToArray();
                if (differences.Length > 0)
                {
                    using (var transaction = conn.BeginTransaction())
                    {
                        querycomm.CommandText = "INSERT INTO Albums (Name, Path, AlbumArtistID) VALUES (@Name, @Path, @AlbumArtistID)";
                        var nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        var pathparam = querycomm.Parameters.Add("@Path", System.Data.DbType.String);
                        var albumartistidparam = querycomm.Parameters.Add("@AlbumArtistID", System.Data.DbType.String);
                        foreach (var album in differences)
                        {
                            nameparam.Value = album.Item3;
                            pathparam.Value = album.Item1;
                            albumartistidparam.Value = albumartistdict[album.Item2];
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                albumsdict.Clear();
                querycomm.CommandText = "SELECT Albums.ID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader[1] as string, reader[2] as string, reader[3] as string), reader.GetInt32(0));

                if (toupdate.Count > 0)
                {
                    querycomm.CommandText = "UPDATE Files SET ArtistID = @ArtistID, AlbumID = @AlbumID WHERE ID = @ID";
                    querycomm.Parameters.Clear();
                    var artistidparam = querycomm.Parameters.Add("@ArtistID", System.Data.DbType.String);
                    var albumidparam = querycomm.Parameters.Add("@AlbumID", System.Data.DbType.String);
                    var idparam = querycomm.Parameters.Add("@ID", System.Data.DbType.String);
                    using (var transaction = conn.BeginTransaction())
                    {
                        foreach (var u in toupdate)
                        {
                            idparam.Value = u.Item1;
                            artistidparam.Value = artistdict[u.Item3];
                            albumidparam.Value = albumsdict[(Path.GetDirectoryName(u.Item2), u.Item4, u.Item5)];
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();

                    }
                }

                    Console.WriteLine();                    



            }

            Console.WriteLine();

        }
    }
}
