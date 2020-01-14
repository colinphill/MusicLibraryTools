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
            /*cache.BeginBuildCache();
            cache.BuildCache(@"z:\itunes\hires", false);
            cache.BuildCache(@"z:\itunes\lossless\music", false);
            cache.BuildCache(@"z:\itunes\lossless\purchased sync", false);
            cache.EndBuildCache();
            cache.Save(@"metadata.cache");*/
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
                        string tb = b.Replace(".", "").Replace(" ", "").ToLower();
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
            
            Console.WriteLine();
        }
    }
}
