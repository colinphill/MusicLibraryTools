using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;

namespace SortDownloads
{
    class Program
    {
        const string FLAC_DIR = @"Z:\ITunes\FLAC\Sorted";
        const string FLAC2_DIR = @"Z:\iTunes\FLAC2\Sorted";
        const string HiRes_DIR = @"Z:\iTunes\HiRes\Stereo\Downloads\Sorted";

        static void Main(string[] args)
        {
            Directory.CreateDirectory(FLAC_DIR);
            Directory.CreateDirectory(FLAC2_DIR);
            Directory.CreateDirectory(HiRes_DIR);

            string basedir = (args.Length > 0) ? args[0] : @"Z:\iTunes\Sort";

            var di = new DirectoryInfo(basedir);
            var files = di.GetFileSystemInfos("*.*", SearchOption.AllDirectories);
            var buckets = new Dictionary<string, List<string>>();

            foreach (var file in files)
            {
                if ((file.Attributes & FileAttributes.Directory) == 0)
                {
                    string fn = file.Name.ToLower();
                    if (!buckets.ContainsKey(fn))
                        buckets.Add(fn, new List<string>());
                    buckets[fn].Add(file.FullName);
                }
            }

            foreach (var bucket in buckets)
            {
                if (bucket.Value.Count > 1)
                {
                    string dest = Path.Combine(FLAC_DIR, bucket.Key);
                    while (File.Exists(dest))
                    {

                    }
                    File.Copy(bucket.Value[0], dest);
                }
                else
                {
                    string dest = Path.Combine(FLAC2_DIR, bucket.Key);
                    if (bucket.Key.Contains("-smr"))
                        dest = Path.Combine(HiRes_DIR, bucket.Key);
                    while (File.Exists(dest))
                    {

                    }
                    File.Copy(bucket.Value[0], dest);
                }
            }

            Console.WriteLine();

        }
    }
}
