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
            foreach (string basedir in config.IndexLocations.Select(il => il.Target))
            {
                LogConsole.WriteLine("Indexing: " + basedir);
                MetadataCache cache = new MetadataCache();
                if (File.Exists(basedir.FixPath() + ".cache"))
                    cache.Load(basedir.FixPath() + ".cache");
                cache.BeginBuildCache();
                cache.BuildCache(basedir);
                cache.EndBuildCache();
                cache.Save(basedir.FixPath() + ".cache");

                int count = 0;
                foreach (var f in cache.FileCache)
                {
                    count++;
                    string tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + Path.GetExtension(f.Key));
                    if (!f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase))
                    {
                        LogConsole.WriteLine(count.ToString() + ") " + f.Key + " -> " + tgt);
                        Directory.CreateDirectory(Path.GetDirectoryName(tgt));
                        File.Move(f.Key, tgt);
                    }
                }
                Extensions.CleanEmptyMusicFolders(new DirectoryInfo(basedir), deletenonmusic);
                Console.WriteLine();
            }
        }
    }
}
