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
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, Path TEXT UNIQUE, ScanTime DATETIME NOT NULL, ArtistID INTEGER REFERENCES Artists (ID), AlbumID INTEGER REFERENCES Albums (ID), CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate INTEGER NOT NULL, MaxBitrate INTEGER NOT NULL, BitsPerSample INTEGER NOT NULL, SampleRate INTEGER NOT NULL, Channels INTEGER NOT NULL, DurationInFrames INTEGER NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, \"Key\" TEXT NOT NULL, Value TEXT NOT NULL);\r\n" +
            "COMMIT TRANSACTION;\r\n" +
            "PRAGMA foreign_keys = on\r\n";

        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            File.Delete("cache.db");
            using var conn = new SQLiteConnection("URI=file:cache.db");
            conn.Open();

            using var command = conn.CreateCommand();
            command.CommandText = creationsql_;
            command.ExecuteNonQuery();

            string[] paths = new string[]
            {
                @"\\ritsuko.projecteva.net\Roon\Purchased Sync",
                @"\\ritsuko.projecteva.net\AllMusic\FLAC",
                @"\\ritsuko.projecteva.net\AllMusic\FLAC2",
                @"\\ritsuko.projecteva.net\AllMusic\HiRes",
            };

            foreach (var scanpath in paths)
            {
                GC.Collect();

                DirectoryInfo di = new DirectoryInfo(scanpath);
                var files = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Where(fsi => ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

                var bag = new ConcurrentBag<Tuple<string, DateTime, IMetadataProvider>>();

                Parallel.ForEach(files, (fi) => { bag.Add(new Tuple<string, DateTime, IMetadataProvider>(fi.FullName, DateTime.UtcNow, Metadata.GetProvider(fi.FullName))); });

                using var filescomm = conn.CreateCommand();
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
                filescomm.CommandText = "INSERT INTO Files (Path, ScanTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames)" +
                " VALUES (@Path, @ScanTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames);";

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
                command.CommandText = "SELECT ID, Path FROM Files";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        keymap.Add(reader.GetString(1), reader.GetInt32(0));

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

            Console.WriteLine();

        }
    }
}
