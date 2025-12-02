using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using MusicFileUtilities;
using MusicLibraryTools;
using ConsoleTools;
using MetadataCaching;

namespace OrganizeFiles
{
    class Program
    {

 
        static void Main(string[] args)
        {
            LibraryConfiguration config = new LibraryConfiguration(args[0]);
            bool deletenonmusic = config["DeleteNonMusic"].Count() != 0;
            int LENGTH_LIMIT = config.LengthLimit;
            int DISC_NUM_LENGTH_LIMIT = config.DiscNumLengthLimit;

            LogConsole.WriteLine("Indexing...");

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(config.DatabaseFile); // TBD Dispose
            db.IndexFiles(config.IndexLocations.Select(l => l.Target));

            foreach (string basedir in config.IndexLocations.Select(il => il.Target))
            {
                LogConsole.WriteLine("Indexing: " + basedir);

                var cache = db.BuildCache(new string[] { basedir });

                int count = 0;
                foreach (var f in cache.FileCache)
                {
                    count++;
                    string tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + Path.GetExtension(f.Key));
                    if (!f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase))
                    {
                        int index = 2;
                        bool move = true;
                        while (File.Exists(tgt))
                        {
                            tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + $"_{index++}" + Path.GetExtension(f.Key));
                            if (f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase))
                            {
                                move = false; 
                                break; 
                            }
                        }
                        if (move)
                        {
                            LogConsole.WriteLine(count.ToString() + ") " + f.Key + " -> " + tgt);
                            Directory.CreateDirectory(Path.GetDirectoryName(tgt));
                            File.Move(f.Key, tgt);
                        }
                    }
                }
                MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(basedir), deletenonmusic);
                Console.WriteLine();
            }
        }
    }
}
