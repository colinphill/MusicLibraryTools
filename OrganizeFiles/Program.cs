using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using MusicFileUtilities;
using MusicLibraryTools;

namespace OrganizeFiles
{
    class Program
    {

 
        static void Main(string[] args)
        {
            LibraryConfiguration config = new LibraryConfiguration(args[0]);
            string basedir = config["BaseDir"].Single();
            int LENGTH_LIMIT = config.LengthLimit;
            int DISC_NUM_LENGTH_LIMIT = config.DiscNumLengthLimit;
            MetadataCache cache = new MetadataCache();
            if (File.Exists(args[0] + ".cache"))
                cache.Load(args[0] + ".cache");
            cache.BeginBuildCache();
            cache.BuildCache(basedir);
            cache.EndBuildCache();
            cache.Save(args[0] + ".cache");
            int count = 0;
            foreach (var f in cache.FileCache)
            {
                count++;
                string tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + Path.GetExtension(f.Key));
                if (!f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine(count.ToString() + ") " + f.Key + " -> " + tgt);
                    Directory.CreateDirectory(Path.GetDirectoryName(tgt));
                    File.Move(f.Key, tgt);
                }
            }
            Extensions.CleanEmptyMusicFolders(new DirectoryInfo(basedir));
            Console.WriteLine();
        }
    }
}
