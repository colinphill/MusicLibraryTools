using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicFileUtilities;
using MusicLibraryTools;
using System.IO;

namespace AnalyzeMetadata
{
    class Program
    {
        static void Main(string[] args)
        {
            MetadataCache cache = new MetadataCache();
            if (File.Exists(@"metadata.cache"))
                cache.Load(@"metadata.cache");
#if true
            cache.BeginBuildCache();
            cache.BuildCache(@"z:\itunes\hires\stereo", false); // Avoid MCH for now
            cache.BuildCache(@"z:\itunes\lossless\music", false);
            cache.BuildCache(@"z:\itunes\lossless\purchased sync", false);
            cache.EndBuildCache();
            cache.Save(@"metadata.cache");
#endif
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
        }
    }
}
