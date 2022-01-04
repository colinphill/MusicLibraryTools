using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using MetadataCaching;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Linq;

namespace MetadataDBWork
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(DateTime.Now);

            Console.WriteLine("Indexing...");

            //File.Delete("cache.db");
            using (var db = MetadataDatabase.OpenDatabase("sqlite:cache.db"))
            //using (var db = MetadataDatabase.OpenDatabase("sql:database=metadata:server=SHIGERU:utf8=true"))
            //using (var db = MetadataDatabase.OpenDatabase("sql:database=metadata2:server=(localdb)\\metadata2"))
            {
                var res = db.IndexFiles(new string[] {
                    @"Z:\iTunes\HiRes\Stereo\Downloads",
                    @"Z:\iTunes\HiRes\Stereo\DVD-As",
                    @"Z:\iTunes\HiRes\Stereo\DVD-Vs",
                    @"Z:\iTunes\HiRes\Stereo\SACDs",
                    @"Z:\iTunes\FLAC",
                    @"Z:\iTunes\FLAC2",
                    @"Z:\iTunes\FLAC3\Stereo",
                    @"Z:\iTunes\FLAC3\Multi",
                    @"Z:\iTunes\Purchased Sync",
                    @"Z:\iTunes\HiRes\Multi\Downloads",
                    @"Z:\iTunes\HiRes\Multi\DTS-CDs",
                    @"Z:\iTunes\HiRes\Multi\DVD-As",
                    @"Z:\iTunes\HiRes\Multi\SACDs",
                    @"Z:\iTunes\AAC\Music",
                    }, true);
                Console.WriteLine("Added:" + res.Added + " Modified:" + res.Modified + " Removed:" + res.Removed + " Unchanged:" + res.Unchanged);

                var cache = db.BuildCache(new string[] {
                    @"Z:\iTunes\HiRes\Stereo\Downloads",
                    @"Z:\iTunes\HiRes\Stereo\DVD-As",
                    @"Z:\iTunes\HiRes\Stereo\DVD-Vs",
                    @"Z:\iTunes\HiRes\Stereo\SACDs",
                    @"Z:\iTunes\FLAC",
                    @"Z:\iTunes\FLAC2",
                    @"Z:\iTunes\FLAC3\Stereo",
                    @"Z:\iTunes\FLAC3\Multi",
                    @"Z:\iTunes\Purchased Sync",
                    @"Z:\iTunes\HiRes\Multi\Downloads",
                    @"Z:\iTunes\HiRes\Multi\DTS-CDs",
                    @"Z:\iTunes\HiRes\Multi\DVD-As",
                    @"Z:\iTunes\HiRes\Multi\SACDs",
                    @"Z:\iTunes\AAC\Music", });
                 Console.WriteLine();
            }

            Console.WriteLine(DateTime.Now);
        }
    }
}
