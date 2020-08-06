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
                    (@"Z:\iTunes\HiRes\Stereo", 1),
                    (@"Z:\iTunes\FLAC", 2),
                    (@"Z:\iTunes\FLAC2", 3),
                    (@"Z:\iTunes\Purchased Sync", 4),
                    (@"Z:\iTunes\HiRes\Multi", 100),
                    (@"Z:\iTunes\AAC\Music", 10000),
                    }, true);
            }

            Console.WriteLine();


        }
    }
}
