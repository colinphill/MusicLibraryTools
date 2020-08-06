using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.IO;
using MusicFileUtilities;

namespace MetadataCaching
{
    public class MetadataDatabase : IDisposable
    {
        private SQLiteConnection conn_;

        private static readonly string creationsql_ =
             "PRAGMA foreign_keys = off;\r\n" +
             "BEGIN TRANSACTION;\r\n" +
             "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE);\r\n" +
             "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, AlbumArtistID INTEGER NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
             "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE);\r\n" +
             "CREATE TABLE Files (ID INTEGER PRIMARY KEY, \"Set\" INTEGER NOT NULL, Path TEXT UNIQUE, ScanTime DATETIME NOT NULL, TrackID INTEGER REFERENCES Tracks (ID), CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate INTEGER NOT NULL, MaxBitrate INTEGER NOT NULL, BitsPerSample INTEGER NOT NULL, SampleRate INTEGER NOT NULL, Channels INTEGER NOT NULL, DurationInFrames INTEGER NOT NULL, UNIQUE(\"Set\", Path));\r\n" +
             "CREATE TABLE Images (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, Description TEXT, Category TEXT, ImageType TEXT, Width INTEGER, Height INTEGER, Size INTEGER, Data BLOB);\r\n" +
             "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, \"Key\" TEXT NOT NULL, Value TEXT NOT NULL);\r\n" +
             "CREATE TABLE Tracks (ID INTEGER PRIMARY KEY, ArtistID INTEGER REFERENCES Artists (ID) NOT NULL, AlbumID INTEGER REFERENCES Albums (ID) NOT NULL, Number INTEGER, Name TEXT);\r\n" +
             "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
             "CREATE INDEX FilesPathIndex ON Files(Path ASC);\r\n" +
             "CREATE INDEX FilesSetIndex ON Files(\"Set\" ASC);\r\n" +
             "CREATE INDEX FilesTrackIDIndex ON Files(TrackID ASC);\r\n" +
             "CREATE INDEX ImagesFileIDIndex ON Images (FileID ASC);\r\n" +
             "CREATE INDEX MetadataFileIDIndex ON Metadata(FileID ASC);\r\n" +
             "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
             "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n" +
             "CREATE VIEW MetadataMapView AS SELECT *,\r\n" +
             "(SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ARTIST') AS Artist,\r\n" +
             "COALESCE(\r\n" +
             "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ALBUMARTIST'),\r\n" +
             "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ARTIST')) AS AlbumArtist,\r\n" +
             "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'ALBUM') AS Album,\r\n" +
             "   CAST((SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'Number') AS INTEGER) AS Number,\r\n" +
             "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'TITLE') AS Name\r\n" +
             "   FROM Files;\r\n" +
             "CREATE VIEW MetadataSummaryView AS SELECT Files.ID, Files.\"Set\", Files.Path, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtists,\r\n" +
             "   Albums.Name AS Album, Tracks.Number AS TrackNumber, Tracks.Name AS Track FROM\r\n" +
             "   Files JOIN Tracks ON Files.TrackID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
             "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n" +
             "COMMIT TRANSACTION;\r\n" +
             "PRAGMA foreign_keys = on\r\n";

        public MetadataDatabase(string filename)
        {
            bool createtables = !File.Exists(filename);
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = filename,
                DateTimeKind = DateTimeKind.Utc
            };
            conn_ = new SQLiteConnection(builder.ConnectionString);
            conn_.Open();
            if (createtables)
            {
                using (var comm = conn_.CreateCommand())
                {
                    comm.CommandText = creationsql_;
                    comm.ExecuteNonQuery();
                }
            }
        }

        public void IndexFiles(IEnumerable<(string path, int set)> sets, bool allsets = false)
        {
            var setlist = sets.Aggregate("", (a, b) => a + ", " + b.Item2.ToString()).Substring(2);

            var filesdict = new ConcurrentDictionary<string, ValueTuple<int, DateTime>>();
            var fileshitdict = new ConcurrentDictionary<string, bool>();

            int count = 0;
            using (var getfilescomm = conn_.CreateCommand())
            {

                getfilescomm.CommandText = "SELECT ID, Path, ScanTime FROM Files" + (allsets ? "" : (" WHERE \"SET\" IN (" + setlist + ")"));
                using (var reader = getfilescomm.ExecuteReader())
                    while (reader.Read())
                    {
                        filesdict[reader.GetString(1)] = (reader.GetInt32(0), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
                        fileshitdict[reader.GetString(1)] = false;
                        count++;
                    }
            }

            foreach (var scanpath in sets)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);

                DirectoryInfo di = new DirectoryInfo(scanpath.Item1);
                int scanset = scanpath.Item2;
                var files = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).AsParallel().Where(fsi => MetadataExtensions.ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0));

                var bag = new ConcurrentBag<Tuple<string, DateTime, IMetadataProvider>>();
                var deletekeys = new ConcurrentBag<int>();

                Parallel.ForEach(files, (fi) =>
                {

                    bool scan = false;
                    if (filesdict.ContainsKey(fi.FullName))
                    {
                        var file = filesdict[fi.FullName];
                        if (fi.LastWriteTimeUtc > file.Item2)
                            scan = true;
                        else
                            fileshitdict[fi.FullName] = true;
                    }
                    else
                        scan = true;
                    if (scan)
                        bag.Add(new Tuple<string, DateTime, IMetadataProvider>(fi.FullName, DateTime.UtcNow, Metadata.GetProvider(fi.FullName)));
                });

                if (!bag.IsEmpty)
                {
                    using (var filescomm = conn_.CreateCommand())
                    {
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

                        using (var transaction = conn_.BeginTransaction())
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
                    }

                    var keymap = new Dictionary<string, int>();
                    using (var query = conn_.CreateCommand())
                    {
                        query.CommandText = "SELECT ID, Path FROM Files WHERE \"Set\" = " + scanset;
                        using (var reader = query.ExecuteReader())
                            while (reader.Read())
                                keymap.Add(reader.GetString(1), reader.GetInt32(0));
                    }

                    using (var metacomm = conn_.CreateCommand())
                    {
                        metacomm.CommandText = "INSERT INTO Metadata (FileID, \"Key\", Value) VALUES (@FileID, @Key, @Value)";
                        var fileidparam = metacomm.Parameters.Add("@FileID", System.Data.DbType.Int32);
                        var keyparam = metacomm.Parameters.Add("@Key", System.Data.DbType.String);
                        var valueparam = metacomm.Parameters.Add("@Value", System.Data.DbType.String);

                        using (var transaction = conn_.BeginTransaction())
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

                    using (var imagecomm = conn_.CreateCommand())
                    {
                        imagecomm.CommandText = "INSERT INTO Images (FileID, Description, Category, ImageType, Width, Height, Size, Data) VALUES (@FileID, @Description, @Category, @ImageType, @Width, @Height, @Size, @Data)";
                        var fileidparam = imagecomm.Parameters.Add("@FileID", System.Data.DbType.Int32);
                        var descriptionparam = imagecomm.Parameters.Add("@Description", System.Data.DbType.String);
                        var categoryparam = imagecomm.Parameters.Add("@Category", System.Data.DbType.String);
                        var imagetypeparam = imagecomm.Parameters.Add("@ImageType", System.Data.DbType.String);
                        var widthparam = imagecomm.Parameters.Add("@Width", System.Data.DbType.Int32);
                        var heightparam = imagecomm.Parameters.Add("@Height", System.Data.DbType.Int32);
                        var sizeparam = imagecomm.Parameters.Add("@Size", System.Data.DbType.Int32);
                        var dataparam = imagecomm.Parameters.Add("@Data", System.Data.DbType.Object);

                        using (var transaction = conn_.BeginTransaction())
                        {
                            foreach (var file in bag)
                            {
                                fileidparam.Value = keymap[file.Item1];
                                foreach (var image in file.Item3.GetImageMetadata())
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
                            transaction.Commit();
                        }
                    }

                }
            }

            using (var querycomm = conn_.CreateCommand())
            {
                var artistlist = new List<string>();
                var albumartistlist = new List<string>();

                querycomm.CommandText = "INSERT INTO Artists (NAME) SELECT DISTINCT Artist FROM MetadataMapView WHERE" + (allsets ? "" : (" \"SET\" IN (" + setlist + ") AND")) + " Artist NOT IN (SELECT Name FROM Artists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "INSERT INTO AlbumArtists (NAME) SELECT DISTINCT AlbumArtist FROM MetadataMapView WHERE" + (allsets ? "" : (" \"SET\" IN (" + setlist + ") AND")) + " AlbumArtist NOT IN (SELECT Name FROM AlbumArtists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();

                var artistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM Artists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var albumartistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM AlbumArtists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var toupdate = new List<(int, string, string, string, string, int, string)>();
                querycomm.CommandText = "SELECT ID, Path, Artist, AlbumArtist, Album, Number, Name FROM MetadataMapView WHERE" + (allsets ? "" : (" \"SET\" IN (" + setlist + ") AND")) + " TrackID IS NULL";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        toupdate.Add((reader.GetInt32(0), reader[1] as string, reader[2] as string, reader[3] as string, reader[4] as string, reader.IsDBNull(5) ? 0 : reader.GetInt32(5), reader[6] as string));

                var distinctalbums = toupdate.Select(t => (Path.GetDirectoryName(t.Item2), t.Item4, t.Item5)).Distinct().ToDictionary(da => da, da => true);
                var albumsdict = new Dictionary<(string, string, string), int>();
                querycomm.CommandText = "SELECT Albums.ID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader[1] as string, reader[2] as string, reader[3] as string), reader.GetInt32(0));

                var albumdifferences = distinctalbums.Keys.Except(albumsdict.Keys).ToArray();
                if (albumdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.CommandText = "INSERT INTO Albums (Name, Path, AlbumArtistID) VALUES (@Name, @Path, @AlbumArtistID)";
                        querycomm.Parameters.Clear();
                        var nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        var pathparam = querycomm.Parameters.Add("@Path", System.Data.DbType.String);
                        var albumartistidparam = querycomm.Parameters.Add("@AlbumArtistID", System.Data.DbType.String);
                        foreach (var album in albumdifferences)
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
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader[1] as string, reader[2] as string, reader[3] as string), reader.GetInt32(0));

                var distincttracks = toupdate.Select(t => (Path.GetDirectoryName(t.Item2), t.Item3, t.Item4, t.Item5, t.Item6, t.Item7)).Distinct().ToDictionary(dt => dt, dt => true);
                var tracksdict = new Dictionary<(string, string, string, string, int, string), int>();
                querycomm.CommandText = "SELECT Tracks.ID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader[1] as string, reader[2] as string, reader[3] as string, reader[4] as string, reader.GetInt32(5), reader[6] as string), reader.GetInt32(0));

                var trackdifferences = distincttracks.Keys.Except(tracksdict.Keys).ToArray();
                if (trackdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.CommandText = "INSERT INTO Tracks (ArtistID, AlbumID, Number, Name) VALUES (@ArtistID, @AlbumID, @Number, @Name)";
                        querycomm.Parameters.Clear();
                        var artistidparam = querycomm.Parameters.Add("@ArtistID", System.Data.DbType.Int32);
                        var albumidparam = querycomm.Parameters.Add("@AlbumID", System.Data.DbType.Int32);
                        var Numberparam = querycomm.Parameters.Add("@Number", System.Data.DbType.Int32);
                        var Nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        foreach (var track in trackdifferences)
                        {
                            artistidparam.Value = artistdict[track.Item2];
                            albumidparam.Value = albumsdict[(track.Item1, track.Item3, track.Item4)];
                            Numberparam.Value = track.Item5;
                            Nameparam.Value = track.Item6;
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                querycomm.CommandText = "SELECT Tracks.ID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                tracksdict.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader[1] as string, reader[2] as string, reader[3] as string, reader[4] as string, reader.GetInt32(5), reader[6] as string), reader.GetInt32(0));

                if (toupdate.Count > 0)
                {
                    querycomm.CommandText = "UPDATE Files SET TrackID = @TrackID WHERE ID = @ID";
                    querycomm.Parameters.Clear();
                    var trackidparam = querycomm.Parameters.Add("@TrackID", System.Data.DbType.String);
                    var idparam = querycomm.Parameters.Add("@ID", System.Data.DbType.String);
                    using (var transaction = conn_.BeginTransaction())
                    {
                        foreach (var u in toupdate)
                        {
                            idparam.Value = u.Item1;
                            trackidparam.Value = tracksdict[(Path.GetDirectoryName(u.Item2), u.Item3, u.Item4, u.Item5, u.Item6, u.Item7)];
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();

                    }
                }

                using (var transaction = conn_.BeginTransaction())
                {
                    querycomm.Parameters.Clear();
                    querycomm.CommandText = "DELETE FROM Files WHERE ID = @ID";
                    var idparam = querycomm.Parameters.Add("@ID", System.Data.DbType.Int32);
                    foreach (var kv in fileshitdict)
                    {
                        if (!kv.Value)
                        {
                            idparam.Value = filesdict[kv.Key].Item1;
                            querycomm.ExecuteNonQuery();
                        }
                    }
                    querycomm.CommandText =
                        "DELETE FROM Metadata WHERE FileID NOT IN (SELECT ID FROM Files);\r\n" +
                        "DELETE FROM Images WHERE FileID NOT IN (SELECT ID FROM Files);\r\n" +
                        "DELETE FROM Tracks WHERE ID NOT IN (SELECT TrackID FROM Files);\r\n" +
                        "DELETE FROM Artists WHERE ID NOT IN (SELECT ArtistID FROM Tracks);\r\n" +
                        "DELETE FROM Albums WHERE ID NOT IN (SELECT AlbumID FROM Tracks);\r\n" +
                        "DELETE FROM AlbumArtists WHERE ID NOT IN (SELECT AlbumArtistID FROM Albums);";
                    querycomm.ExecuteNonQuery();
                    transaction.Commit();
                }

                // Clean Up

            }

        }

        public void Dispose()
        {
            conn_.Dispose();
        }

    }
}
