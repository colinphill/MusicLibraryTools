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

            using (var db = new MetadataDatabase("cache.db"))
            {
                var res = db.IndexFiles(new string [] { 
                    @"Z:\iTunes\HiRes\Stereo\Downloads",
                    @"Z:\iTunes\HiRes\Stereo\DVD-As",
                    @"Z:\iTunes\HiRes\Stereo\DVD-Vs",
                    @"Z:\iTunes\HiRes\Stereo\SACDs",
                    @"Z:\iTunes\FLAC",
                    @"Z:\iTunes\FLAC2",
                    @"Z:\iTunes\Purchased Sync",
                    @"Z:\iTunes\HiRes\Multi\Downloads",
                    @"Z:\iTunes\HiRes\Multi\DTS-CDs",
                    @"Z:\iTunes\HiRes\Multi\DVD-As",
                    @"Z:\iTunes\HiRes\Multi\SACDs",
                    @"Z:\iTunes\AAC\Music",
                    }, true, true);
                Console.WriteLine("Added:" + res.Added + " Modified:" + res.Modified + " Removed:" + res.Removed + " Unchanged:" + res.Unchanged);

                var cache = db.BuildCache(new string [] {
                    @"Z:\iTunes\HiRes\Stereo\Downloads",
                    @"Z:\iTunes\HiRes\Stereo\DVD-As",
                    @"Z:\iTunes\HiRes\Stereo\DVD-Vs",
                    @"Z:\iTunes\HiRes\Stereo\SACDs",
                    @"Z:\iTunes\FLAC",
                    @"Z:\iTunes\FLAC2",
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
