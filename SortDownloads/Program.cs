using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using MusicFileUtilities;

namespace SortDownloads
{
    class Program
    {
        const string FLAC_DIR = @"Z:\ITunes\FLAC\Sorted";
        const string FLAC2_DIR = @"Z:\iTunes\FLAC2\Sorted";
        const string HiRes_DIR = @"Z:\iTunes\HiRes\Stereo\PCM\Sorted";

        static string GetFileName(string desired)
        {
            if (!File.Exists(desired))
                return desired;
            string dir = Path.GetDirectoryName(desired);
            string file = Path.GetFileNameWithoutExtension(desired);
            string ext = Path.GetExtension(desired);
            for(int i=1; ;i++)
            {
                string fn = Path.Combine(dir, file) + i.ToString() + ext;
                if (!File.Exists(fn))
                    return fn;
            }
        }

        static void Main(string[] args)
        {
            Directory.CreateDirectory(FLAC_DIR);
            Directory.CreateDirectory(FLAC2_DIR);
            Directory.CreateDirectory(HiRes_DIR);

            string basedir = (args.Length > 0) ? args[0] : @"Z:\iTunes\Sort";

            var di = new DirectoryInfo(basedir);
            var files = di.GetFileSystemInfos("*.*", SearchOption.AllDirectories);
            var buckets = new Dictionary<string, List<(string Path, ICodecProvider Codec)>>();

            foreach (var file in files.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)))
            {
                try
                {
                    var filename = file.FullName;
                    var mediafile = MediaFile.GetFile(filename);
                    var tag = mediafile.Tags.First();
                    var codec = mediafile.Codecs.First();
                    if (codec.CodecType == CodecType.Lossy)
                    {
                        Console.WriteLine($"Skipping Lossy File: {filename}");
                        continue;
                    }
                    var name = $"{tag.TrackNumber ?? 0} {tag.AlbumArtist} {tag.Album} {tag.Title}".ToLower();
                    if (!buckets.ContainsKey(name))
                        buckets.Add(name, new List<(string, ICodecProvider)>());
                    buckets[name].Add((filename, codec));
                }
                catch
                {

                }
            }

            foreach (var bucket in buckets)
            {
                if (bucket.Value.Select(i => i.Codec.AverageBitrate).Distinct().Count() == 1)
                {
                    File.Move(bucket.Value[0].Path, GetFileName(Path.Combine(FLAC_DIR, Path.GetFileName(bucket.Value[0].Path))));
                    bucket.Value.Skip(1).ToList().ForEach(p => File.Delete(p.Path));
                }
                else
                {
                    var ordered = bucket.Value.OrderBy(i => i.Codec.AverageBitrate).ToArray();
                    // Keep the lowest-bitrate copy as the standard FLAC and the highest as HiRes;
                    // delete any intermediate duplicates so nothing is left behind in the source.
                    File.Move(ordered[0].Path, GetFileName(Path.Combine(FLAC2_DIR, Path.GetFileName(ordered[0].Path))));
                    File.Move(ordered[^1].Path, GetFileName(Path.Combine(HiRes_DIR, Path.GetFileName(ordered[^1].Path))));
                    foreach (var extra in ordered.Skip(1).Take(ordered.Length - 2))
                        File.Delete(extra.Path);
                }
            }

            MetadataExtensions.CleanEmptyMusicFolders(di, true);

            Console.WriteLine();

        }
    }
}
