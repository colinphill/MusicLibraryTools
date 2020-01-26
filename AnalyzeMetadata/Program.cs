using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MusicFileUtilities;
using MusicLibraryTools;
using System.IO;

namespace AnalyzeMetadata
{
    class Program
    {

        static bool IsFuzzy(string a, string b, float ratio)
        {
            float len = Math.Max(a.Length, b.Length);
            int dist = a.ToLower().EditDistance(b.ToLower());
            return (dist / len) < ratio;
        }

        static void Main(string[] args)
        {
            MetadataCache cache = new MetadataCache();
            if (File.Exists(@"metadata.cache"))
                cache.Load(@"metadata.cache");
#if true
            cache.BeginBuildCache();
            cache.BuildCache(@"z:\itunes\hires\stereo", false); // Avoid MCH for now
            cache.BuildCache(@"z:\itunes\hires\multi", false); // Avoid MCH for now
            cache.BuildCache(@"z:\itunes\lossless\music", false);
            cache.BuildCache(@"z:\itunes\lossless\purchased sync", false);
            cache.EndBuildCache();
            cache.Save(@"metadata.cache");
#endif

#if true
            var re = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);
            var hrfiles = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\")).ToArray();
            var stdfiles = cache.FileCache.Where(kv => !kv.Key.ToLower().Contains(@"\hires\")).ToArray();
            var hralbums = hrfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, re.Replace(kv.Value.Album, ""))).Distinct();
            var stdalbums = stdfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, kv.Value.Album, Path.GetDirectoryName(kv.Key))).Distinct();
            int count = 0, miss = 0, hit = 0, multiple = 0;
            foreach (var album in hralbums)
            {
                count++;
                float fuzzyvalue = 0.5f;
                var possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                while ((possibilities.Length > 1)&&(fuzzyvalue >= 0.1f))
                {
                    fuzzyvalue -= 0.1f;
                    possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                }

                if (possibilities.Length == 1)
                {
                    //Console.WriteLine("Hit," + possibilities[0].Item1 + "," + possibilities[0].Item2 + "," + possibilities[0].Item3);
                    hit++;
                }
                else if (possibilities.Length == 0)
                {
                    Console.WriteLine("Miss," + album.Item1 + "," + album.Item2 + "," + fuzzyvalue);
                    miss++;
                }
                else
                {
                    Console.WriteLine("Multiple," + album.Item1 + "," + album.Item2);
                    multiple++;
                }
            }

            Console.WriteLine(count + " " + hit + " " + miss + " " + multiple);
            Console.WriteLine();
#endif

#if false
            var files = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\") && (kv.Value.SampleRate <= 48000) && (kv.Value.BitsPerSample <= 16));
            Console.WriteLine();
#endif

#if false
            var re = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res)\)$", RegexOptions.IgnoreCase);

            int count = 0, miss = 0, hit = 0, multiple = 0;

            var hrfiles = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\")).ToArray();
            var stdfiles = cache.FileCache.Where(kv =>!kv.Key.ToLower().Contains(@"\hires\")).ToArray();
            foreach (var file in hrfiles)
            {
                count++;
                string modalbum = re.Replace(file.Value.Album, "").ToLower();
                var possibilities = stdfiles.Where(kv => IsFuzzy(kv.Value.Album, modalbum, 0.2f) && IsFuzzy(kv.Value.Title, file.Value.Title, 0.2f) && (kv.Value.TrackNumber == file.Value.TrackNumber) &&
                    (IsFuzzy(kv.Value.Artist, file.Value.Artist.ToLower(), 0.2f) || IsFuzzy(kv.Value.AlbumArtist, file.Value.AlbumArtist.ToLower(), 0.2f))).ToArray();
                if (possibilities.Length == 1)
                {
                    //Console.WriteLine("Hit," + file.Key);
                    hit++;
                }
                else if (possibilities.Length == 0)
                {
                    Console.WriteLine("Miss," + file.Key);
                    miss++;
                }
                else
                {
                    Console.WriteLine("Multiple," + file.Key);
                    multiple++;
                }

            }

            Console.WriteLine(count + " " + hit + " " + miss + " " + multiple);
            Console.WriteLine();
#endif

#if false
            string [] artists = cache.Artists.Concat(cache.AlbumArtists).Distinct().ToArray();
            var distdict = new Dictionary<int, List<Tuple<string, string>>>();
            var checkdict = new Dictionary<Tuple<string, string>, bool>();
            foreach (string a in artists)
            {
                foreach (string b in artists)
                {
                    if (a != b)
                    {
                        if (checkdict.ContainsKey(new Tuple<string, string>(b, a)))
                            continue;
                        int checklen = Math.Max(a.Length, b.Length);
                        string ta = a.Replace(".", "").Replace(" ", "").ToLower();
                        if (ta.StartsWith("a "))
                            ta = ta.Remove(0, 2);
                        if (ta.StartsWith("the "))
                            ta = ta.Remove(0, 4);
                        string tb = b.Replace(".", "").Replace(" ", "").ToLower();
                        if (tb.StartsWith("a "))
                            tb = tb.Remove(0, 2);
                        if (tb.StartsWith("the "))
                            tb = tb.Remove(0, 4);
                        int dist = ta.EditDistance(tb);
                        if ((100 * dist / checklen) < 10)
                        {
                            if (!distdict.ContainsKey(dist))
                                distdict.Add(dist, new List<Tuple<string, string>>());
                            distdict[dist].Add(new Tuple<string, string>(a, b));
                        }
                        checkdict.Add(new Tuple<string, string>(a, b), true);
                    }
                }
            }

            foreach (int dist in distdict.Keys.OrderBy(k => k))
            {
                Console.WriteLine("Distance: " + dist);
                Console.WriteLine();
                foreach (var pair in distdict[dist])
                {
                    Console.WriteLine("First: " + pair.Item1);
                    string[] paths = cache.FileCache.Where(f => f.Value.Artist == pair.Item1).Select(f => Path.GetDirectoryName(f.Key)).Concat(
                        cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item1).Select(f => Path.GetDirectoryName(f.Key))).Distinct().ToArray();
                    foreach (string s in paths)
                        Console.WriteLine(s);

                    Console.WriteLine();
                    Console.WriteLine("Second: " + pair.Item2);
                    paths = cache.FileCache.Where(f => f.Value.Artist == pair.Item2).Select(f => Path.GetDirectoryName(f.Key)).Concat(
                        cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item2).Select(f => Path.GetDirectoryName(f.Key))).Distinct().ToArray();
                    foreach (string s in paths)
                        Console.WriteLine(s);

                    Console.WriteLine();

                }
            }
            
            Console.WriteLine();
#endif

        }
}
}
