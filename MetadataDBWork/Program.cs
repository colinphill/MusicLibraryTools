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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            using (MetadataDatabase db = new MetadataDatabase("cache.db"))
            {
                db.IndexFiles(new [] { 
                    (@"Z:\iTunes\HiRes\Stereo\Downloads", 1),
                    (@"Z:\iTunes\HiRes\Stereo\DVD-As", 2),
                    (@"Z:\iTunes\HiRes\Stereo\DVD-Vs", 3),
                    (@"Z:\iTunes\HiRes\Stereo\SACDs", 4),
                    (@"Z:\iTunes\FLAC", 100),
                    (@"Z:\iTunes\FLAC2", 101),
                    (@"Z:\iTunes\Purchased Sync", 200),
                    (@"Z:\iTunes\HiRes\Multi\Downloads", 300),
                    (@"Z:\iTunes\HiRes\Multi\DTS-CDs", 301),
                    (@"Z:\iTunes\HiRes\Multi\DVD-As", 302),
                    (@"Z:\iTunes\HiRes\Multi\SACDs", 303),
                    (@"Z:\iTunes\AAC\Music", 1000),
                    }, true);
            }

            Console.WriteLine();


        }
    }
}
