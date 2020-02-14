using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MusicFileUtilities;
using MusicLibraryTools;
using System.IO;
using ConsoleTools;

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
            LogConsole.SwitchFile("AnalyzeMetadata.log");

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            LogConsole.WriteLine("Indexing Files...");
            MetadataCache cache = new MetadataCache();
            if (File.Exists(args[0] + ".cache"))
                cache.Load(args[0] + ".cache");
            else
            {
                try
                {
                    cache.Load(config.ReferenceConfig + ".cache");
                }
                catch
                {

                }
            }
#if true
            cache.BeginBuildCache();
            foreach (var iloc in config.IndexLocations)
                cache.BuildCache(iloc.Target, false);
            cache.EndBuildCache();
            cache.Save(args[0] + ".cache");
#endif
            LogConsole.WriteLine("Total Parsed Files: " + cache.FileCache.Count);

            if (args.Skip(1).Any(s => s.ToLower() == "checkhires"))
            {
                var re = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);
                var hrfiles = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\stereo\")).ToArray();
                var stdfiles = cache.FileCache.Where(kv => !kv.Key.ToLower().Contains(@"\hires\")).ToArray();
                var hralbums = hrfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, kv.Value.StrippedAlbum, Path.GetDirectoryName(kv.Key))).Distinct();
                var stdalbums = stdfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, kv.Value.Album, Path.GetDirectoryName(kv.Key))).Distinct();
                int count = 0, miss = 0, hit = 0, multiple = 0;
                foreach (var album in hralbums)
                {
                    count++;
                    float fuzzyvalue = 0.5f;
                    var possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                    while ((possibilities.Length > 1) && (fuzzyvalue >= 0.1f))
                    {
                        fuzzyvalue -= 0.1f;
                        possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                    }

                    if (possibilities.Length == 1)
                    {
                        // Check for mismatched file counts 
                        int hrtracks = hrfiles.Count(kv => (kv.Value.StrippedAlbum == album.StrippedAlbum) && ((kv.Value.Artist == album.Item1) || (kv.Value.AlbumArtist == album.Item1)));
                        int stdtracks = stdfiles.Count(kv => (kv.Value.Album == possibilities[0].Album) && ((kv.Value.Artist == possibilities[0].Item1) || (kv.Value.AlbumArtist == possibilities[0].Item1)));
                        if (hrtracks < stdtracks)
                            LogConsole.WriteLine("Count Mismatch," + hrtracks + "," + stdtracks + "," + possibilities[0].Item1 + "," + possibilities[0].Item2 + "," + possibilities[0].Item3);
                        else
                        {
                            int dist1 = album.Item1.EditDistance(possibilities[0].Item1);
                            int dist2 = album.StrippedAlbum.EditDistance(possibilities[0].Album);
                            if ((dist1 != 0) || (dist2 != 0))
                            {
                                LogConsole.WriteLine("Hit," + dist1 + "," + dist2 + "," + possibilities[0].Item1 + "," + possibilities[0].Item2 + "," + possibilities[0].Item3);
                                LogConsole.WriteLine("   ," + dist1 + "," + dist2 + "," + album.Item1 + "," + album.StrippedAlbum);
                            }
                            else
                            {
                                // Pure Hit
                                /*string parentdir = Path.GetDirectoryName(possibilities[0].Item3);
                                string artdir = Path.GetFileName(parentdir);
                                LogConsole.WriteLine("PureHit," + possibilities[0].Item3 + "," + artdir);
                                string dest = Path.Combine(@"Z:\iTunes\FLAC2", artdir);
                                Directory.CreateDirectory(dest);
                                Directory.Move(possibilities[0].Item3, Path.Combine(dest, Path.GetFileName(possibilities[0].Item3)));
                                if (Directory.GetFileSystemEntries(parentdir).Length == 0)
                                {
                                    Console.WriteLine("Delete:" + parentdir);
                                    Directory.Delete(parentdir);
                                }*/

                            }
                        }
                        hit++;
                    }
                    else if (possibilities.Length == 0)
                    {
                        LogConsole.WriteLine("Miss," + album.Item1 + "," + album.Item2 + "," + fuzzyvalue);
                        miss++;
                    }
                    else
                    {
                        LogConsole.WriteLine("Multiple," + album.Item1 + "," + album.Item2);
                        multiple++;
                    }
                }
  
                LogConsole.WriteLine(count + " " + hit + " " + miss + " " + multiple);
                LogConsole.WriteLine();
            }

            if (args.Skip(1).Any(s => s.ToLower() == "checkhiresmulti"))
            {
                var re = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);
                var hrfiles = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\multi\")).ToArray();
                var stdfiles = cache.FileCache.Where(kv => !kv.Key.ToLower().Contains(@"\hires\")).ToArray();
                var hralbums = hrfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, kv.Value.StrippedAlbum, Path.GetDirectoryName(kv.Key))).Distinct();
                var stdalbums = stdfiles.Select(kv => (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.AlbumArtist : kv.Value.Artist, kv.Value.Album, Path.GetDirectoryName(kv.Key))).Distinct();
                int count = 0, miss = 0, hit = 0, multiple = 0;
                foreach (var album in hralbums)
                {
                    count++;
                    float fuzzyvalue = 0.5f;
                    var possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                    while ((possibilities.Length > 1) && (fuzzyvalue >= 0.1f))
                    {
                        fuzzyvalue -= 0.1f;
                        possibilities = stdalbums.Where(a => IsFuzzy(a.Item1, album.Item1, fuzzyvalue) && IsFuzzy(a.Item2, album.Item2, fuzzyvalue)).ToArray();
                    }

                    if (possibilities.Length == 1)
                    {
                        // Check for mismatched file counts 
                        int hrtracks = hrfiles.Count(kv => (kv.Value.StrippedAlbum == album.StrippedAlbum) && ((kv.Value.Artist == album.Item1) || (kv.Value.AlbumArtist == album.Item1)));
                        int stdtracks = stdfiles.Count(kv => (kv.Value.Album == possibilities[0].Album) && ((kv.Value.Artist == possibilities[0].Item1) || (kv.Value.AlbumArtist == possibilities[0].Item1)));
                        if (hrtracks < stdtracks)
                            LogConsole.WriteLine("Count Mismatch," + hrtracks + "," + stdtracks + "," + possibilities[0].Item1 + "," + possibilities[0].Item2 + "," + possibilities[0].Item3);
                        else
                        {
                            int dist1 = album.Item1.EditDistance(possibilities[0].Item1);
                            int dist2 = album.StrippedAlbum.EditDistance(possibilities[0].Album);
                            if ((dist1 != 0) || (dist2 != 0))
                            {
                                LogConsole.WriteLine("Hit," + dist1 + "," + dist2 + "," + possibilities[0].Item1 + "," + possibilities[0].Item2 + "," + possibilities[0].Item3);
                                LogConsole.WriteLine("   ," + dist1 + "," + dist2 + "," + album.Item1 + "," + album.StrippedAlbum);
                            }
                        }
                        hit++;
                    }
                    else if (possibilities.Length == 0)
                    {
                        LogConsole.WriteLine("Miss," + album.Item1 + "," + album.Item2 + "," + fuzzyvalue);
                        miss++;
                    }
                    else
                    {
                        LogConsole.WriteLine("Multiple," + album.Item1 + "," + album.Item2);
                        multiple++;
                    }
                }

                LogConsole.WriteLine(count + " " + hit + " " + miss + " " + multiple);
                LogConsole.WriteLine();
            }

            if (args.Skip(1).Any(s => s.ToLower() == "checklores"))
            {
                var files = cache.FileCache.Where(kv => kv.Key.ToLower().Contains(@"\hires\") && (kv.Value.SampleRate <= 48000) && (kv.Value.BitsPerSample <= 16));
                foreach (var f in files.OrderBy(kv => kv.Key))
                    LogConsole.WriteLine("(" + f.Value.SampleRate + "/" + f.Value.BitsPerSample + ") " + f.Key);
                LogConsole.WriteLine();
            }

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
            if (args.Skip(1).Any(s => s.ToLower() == "checkcompilations"))
            {
                var compilations = cache.FileCache.Where(kv => kv.Value.Compilation).OrderBy(kv => kv.Key).ToArray();
                var files = cache.FileCache.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) && (kv.Value.AlbumArtist != kv.Value.Artist) && !kv.Value.Compilation).OrderBy(kv => kv.Key).ToArray();
                Console.WriteLine();
                
            }

            if (args.Skip(1).Any(s => s.ToLower() == "checksr"))
            {
                var files = cache.FileCache.Where(kv => (kv.Value.SampleRate > 44100)||(kv.Value.BitsPerSample > 16)).OrderBy(kv => kv.Key).ToArray();
                Console.WriteLine();

            }

            if (args.Skip(1).Any(s => s.ToLower() == "checkartists"))
            {
                string[] artists = cache.Artists.Concat(cache.AlbumArtists).Distinct().ToArray();
                var distdict = new Dictionary<int, List<Tuple<string, string>>>();
                var checkdict = new Dictionary<Tuple<string, string>, bool>();
                var moddict = new Dictionary<string, (string Value, int Length)>();
                foreach (string a in artists)
                {
                    string ta = a.Replace(".", "").Replace(" ", "").ToLower();
                    if (ta.StartsWith("a "))
                        ta = ta.Remove(0, 2);
                    if (ta.StartsWith("the "))
                        ta = ta.Remove(0, 4);
                    moddict[a] = (ta, a.Length);
                }
                foreach (string a in artists)
                {
                    foreach (string b in artists)
                    {
                        if (a != b)
                        {
                            if (checkdict.ContainsKey(new Tuple<string, string>(b, a)))
                                continue;
                            var ta = moddict[a];
                            var tb = moddict[b];
                            int checklen = Math.Max(ta.Length, tb.Length);
                            int dist = ta.Value.EditDistance(tb.Value);
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
                    LogConsole.WriteLine("Distance: " + dist);
                    LogConsole.WriteLine();
                    foreach (var pair in distdict[dist])
                    {
                        LogConsole.WriteLine("First: " + pair.Item1);
                        string[] paths = cache.FileCache.Where(f => f.Value.Artist == pair.Item1).Select(f => Path.GetDirectoryName(f.Key)).Concat(
                            cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item1).Select(f => Path.GetDirectoryName(f.Key))).Distinct().ToArray();
                        foreach (string s in paths)
                            LogConsole.WriteLine(s);

                        LogConsole.WriteLine();
                        LogConsole.WriteLine("Second: " + pair.Item2);
                        paths = cache.FileCache.Where(f => f.Value.Artist == pair.Item2).Select(f => Path.GetDirectoryName(f.Key)).Concat(
                            cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item2).Select(f => Path.GetDirectoryName(f.Key))).Distinct().ToArray();
                        foreach (string s in paths)
                            LogConsole.WriteLine(s);

                        LogConsole.WriteLine();

                    }
                }

                LogConsole.WriteLine();
            }

        }
    }
}
