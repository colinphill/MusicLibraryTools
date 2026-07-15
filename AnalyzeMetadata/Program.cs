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
using MetadataCaching;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Globalization;

namespace AnalyzeMetadata
{
    class Program
    {

        static bool IsFuzzy(string a, string b, float ratio)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            float len = Math.Max(a.Length, b.Length);
            int dist = a.ToLowerInvariant().EditDistance(b.ToLowerInvariant());
            return (dist / len) < ratio;
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("AnalyzeMetadata.log");

            if (args.Length == 0)
            {
                LogConsole.WriteLine("Usage: AnalyzeMetadata <libraryconfiguration.xml> [checks]");
                LogConsole.Close();
                return;
            }

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            LogConsole.WriteLine("Indexing Files...");

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(config.DatabaseFile); // TBD Dispose
            db.IndexFiles(config.IndexLocations.Select(l => new ScanRootDefinition(l.Target, l.Sets)));

            var cache = db.BuildCache(config.IndexLocations.Select(l => l.Target).Distinct());
            LogConsole.WriteLine("Total Parsed Files: " + cache.FileCache.Count);

            if (args.Skip(1).Any(s => s.ToLower() == "basecheck"))
            {
                foreach (var file in cache.FileCache.OrderBy(kv => kv.Key))
                {
                    var track = file.Value;
                    if (track.TrackTotal == 0)
                        Console.WriteLine("0 TrackTotal: " + file.Key);
                    if (track.TrackTotal is null)
                        Console.WriteLine("Missing TrackTotal: " + file.Key);
                    if ((track.TrackNumber ?? 0) == 0)
                        Console.WriteLine("0/Missing TrackNumber: " + file.Key);
                    if ((track.DiscNumber != null) || (track.DiscTotal != null))
                        Console.WriteLine($"({track.DiscNumber}/{track.DiscTotal}) Disc: {file.Key}");
                    if (file.Key.Contains('\xa0'))
                        Console.WriteLine("Contains nbsp: " + file.Key.Replace("\xa0", "{&nbsp;}"));
                }
            }

            if (args.Skip(1).Any(s => s.ToLower() == "incon"))
            {
                var dirmap = new Dictionary<string, List<KeyValuePair<string, MetadataCacheEntry>>>();
                foreach (var file in cache.FileCache)
                {
                    string path = Path.GetDirectoryName(file.Key);
                    if (!dirmap.ContainsKey(path))
                        dirmap.Add(path, new List<KeyValuePair<string, MetadataCacheEntry>>());
                    dirmap[path].Add(file);
                }
                foreach (var path in dirmap.OrderBy(kv => kv.Key))
                {
                    var albumcount = path.Value.Select(f => f.Value.Album).Distinct().ToArray();
                    if (albumcount.Length == 1)
                    {
                        var dates = path.Value.Select(f => f.Value.ReleaseDate).Distinct().ToArray();
                        /*if (dates.Length != 1)
                            Console.WriteLine("Multiple Release Dates: " + file);*/
                        var tracktotals = path.Value.Select(f => f.Value.TrackTotal).Distinct().ToArray();
                        if (tracktotals.Length != 1)
                            Console.WriteLine("Multiple Track Totals: " + path.Key);
                    }
                }
                foreach (var file in cache.FileCache.OrderBy(kv => kv.Key))
                {
                    var track = file.Value;
                    if (track.TrackTotal == 0)
                        Console.WriteLine("0 TrackTotal: " + file.Key);
                    if (track.TrackTotal is null)
                        Console.WriteLine("Missing TrackTotal: " + file.Key);
                    if ((track.TrackNumber ?? 0) == 0)
                        Console.WriteLine("0/Missing TrackNumber: " + file.Key);
                    if ((track.DiscNumber != null)||(track.DiscTotal != null))
                        Console.WriteLine($"({track.DiscNumber}/{track.DiscTotal}) Disc: {file.Key}");
                    if (file.Key.Contains('\xa0'))
                        Console.WriteLine("Contains nbsp: " + file.Key.Replace("\xa0", "{&nbsp;}"));
                 }

            }

            if (args.Skip(1).Any(s => s.ToLower() == "checksets"))
            {
                var caches = config.IndexLocations.SelectMany(l => l.Sets).Distinct().OrderBy(s => s).Select(s => (Set : s, Cache : db.BuildCache(config.IndexLocations.Where(l => l.Sets.Contains(s)).Select(l => (l.Target, l.Filter?.Split(",;|".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)))))).ToList();
                foreach (var tcache in caches)
                {
                    Console.WriteLine("Checking Cache Set: " + tcache.Set + " (" + tcache.Cache.FileCache.Count + ")");
                    foreach (var ocache in caches.Where(c => c.Set != tcache.Set))
                    {
                        var ofiles = ocache.Cache.FileCache;
                        var bag = new ConcurrentBag<(int Set, string Key)>();
                        var dupebag = new ConcurrentBag<(int Set, string Key, string[] Possibles)>();
                        var hitbag = new ConcurrentBag<(string Key1, string Key2)>();
                        Parallel.ForEach(tcache.Cache.FileCache, (file) =>
                        {
                            if (ocache.Cache.AlbumCache.ContainsKey(file.Value.Album))
                            {
                                var possibilities = ocache.Cache.AlbumCache[file.Value.Album].Select(of => KeyValuePair.Create(of, ofiles[of]));
                                possibilities = possibilities.Where(of => ((of.Value.AlbumArtist == file.Value.AlbumArtist) || (of.Value.Artist == file.Value.Artist)) && (of.Value.TrackNumber == file.Value.TrackNumber));
                                if (possibilities.Count() == 0)
                                    bag.Add((ocache.Set, file.Key));
                                if (possibilities.Count() == 1)
                                    hitbag.Add((possibilities.First().Key, file.Key));
                                if (possibilities.Count() > 1)
                                {
                                    possibilities = possibilities.Where(of => (of.Value.Title == file.Value.Title));
                                    if (possibilities.Count() == 0)
                                        bag.Add((ocache.Set, file.Key));
                                    if (possibilities.Count() == 1)
                                        hitbag.Add((possibilities.First().Key, file.Key));
                                    if (possibilities.Count() > 1)
                                        dupebag.Add((ocache.Set, file.Key, possibilities.Select(kv => kv.Key).ToArray()));
                                }
                            }
                            else
                                bag.Add((ocache.Set, file.Key));
                        });
                        foreach (var miss in bag.OrderBy(m => m.Key))
                            Console.WriteLine("Missing In Set:" + miss.Set + " - " + miss.Key);
                        foreach (var dupe in dupebag.OrderBy(m => m.Key))
                        {
                            Console.WriteLine("Dupe In Set:" + dupe.Set + " - " + dupe.Key);
                            foreach (var poss in dupe.Possibles)
                                Console.WriteLine("--> " + poss);
                        }
                        var hitdict = new Dictionary<string, List<string>>();
                        foreach (var item in hitbag)
                        {
                            if (!hitdict.ContainsKey(item.Key1))
                                hitdict.Add(item.Key1, new List<string>());
                            hitdict[item.Key1].Add(item.Key2);
                        }
                        foreach (var kv in hitdict.Where(kv => kv.Value.Count > 1))
                        {
                            Console.WriteLine("Multi Hit: " + kv.Key);
                            foreach (var val in kv.Value)
                                Console.WriteLine("--> " + val);
                        }

                    }
                }
            }

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
                                // Pure hit; nothing to report.
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

            if (args.Skip(1).Any(s => s.ToLower() == "checkcompilations"))
            {
                var compilations = cache.FileCache.Where(kv => kv.Value.Compilation).OrderBy(kv => kv.Key).ToArray();
                var files = cache.FileCache.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) && (kv.Value.AlbumArtist != kv.Value.Artist) && !kv.Value.Compilation).OrderBy(kv => kv.Key).ToArray();
                Console.WriteLine();
                
            }
#endif

            if (args.Skip(1).Any(s => s.ToLower() == "checksr"))
            {
                var files = cache.FileCache.Where(kv => (kv.Value.SampleRate > 44100)||(kv.Value.BitsPerSample > 16)).OrderBy(kv => kv.Key).ToArray();
                Console.WriteLine();

            }

            if (args.Skip(1).Any(s => s.ToLower() == "checkartists"))
            {
                int thresh = args.Skip(1).Where(s => s.ToLower().StartsWith("thresh") && int.TryParse(s.Substring(6), out int num)).Select(s => int.Parse(s.Substring(6))).DefaultIfEmpty(-1).Max();

                string[] artists = cache.Artists.Concat(cache.AlbumArtists).Distinct().ToArray();
                var distdict = new Dictionary<int, List<Tuple<string, string>>>();
                var checkdict = new Dictionary<Tuple<string, string>, bool>();
                var moddict = new Dictionary<string, (string Value, int Length)>();
                var invmod = new Dictionary<string, List<string>>();
                foreach (string a in artists)
                {
                    string ta = a.Replace("&", "and").Trim().ToLowerInvariant();
                    if (ta.StartsWith("a "))
                        ta = ta.Remove(0, 2);
                    else if (ta.StartsWith("an "))
                        ta = ta.Remove(0, 3);
                    else if (ta.StartsWith("the "))
                        ta = ta.Remove(0, 4);
                    var chars = ta.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
                    ta = new string(chars).Normalize(NormalizationForm.FormC);
                    ta = new string(ta.Where(c => char.IsLetterOrDigit(c)).ToArray());
                    moddict[a] = (ta, a.Length);
                    if (invmod.ContainsKey(ta))
                        invmod[ta].Add(a);
                    else
                        invmod.Add(ta, new List<string> { a });
                }

                var dupes = invmod.Where(kv => kv.Value.Count > 1).ToArray();


                foreach ((var key, var variations) in dupes)
                {
                    var varlist = new List<(string variation, int count)>();
                    var files = new List<(string variation, string file)>();
                    foreach (var variation in variations)
                    {
                        Console.WriteLine($"Variation: {variation}");
                        var varfiles = cache.FileCache.Where(e => (e.Value.Artist == variation) || (e.Value.AlbumArtist == variation)).Select(e => (variation, e.Key)).ToArray();
                        varlist.Add((variation, varfiles.Length));
                        files.AddRange(varfiles);
                        foreach (var p in varfiles.Select(f => Path.GetDirectoryName(f.Key)).Distinct())
                            Console.WriteLine(p);
                    }
                    Console.WriteLine();
                    if (thresh >= 0)
                    {
                        varlist = varlist.OrderByDescending(v => v.count).ToList();
                        Console.WriteLine("Select Variation:");
                        for(int i=0;i<varlist.Count;i++)
                            Console.WriteLine($"{i + 1}) {varlist[i].variation} ({varlist[i].count})");
                        Console.Write("-> ");
                        Console.Out.Flush();
                        if (int.TryParse(Console.ReadLine(), out int res) && ((res > 0) && (res <= varlist.Count)))
                        {
                            var variation = varlist[res - 1].variation;
                            var variantstochange = variations.Where(v => v != variation).ToArray();
                            foreach (var f in files.Where(f => f.variation != variation))
                            {
                                var mediaFile = MediaFile.GetFile(f.file);
                                Action<TagFields, string> setField = mediaFile switch
                                {
                                    IMetadataWriter writer => writer.SetField,
                                    VorbisComments vorbis => vorbis.SetField,
                                    _ => throw new InvalidOperationException($"Metadata is read-only for '{f.file}'.")
                                };

                                var tags = mediaFile.Tags.ToArray();
                                bool update = false;
                                if (tags.Any(t => variantstochange.Contains(t.Artist, StringComparer.OrdinalIgnoreCase)))
                                {
                                    setField(TagFields.Artist, variation);
                                    update = true;
                                }
                                if (tags.Any(t => t.HasAlbumArtist && variantstochange.Contains(t.AlbumArtist, StringComparer.OrdinalIgnoreCase)))
                                {
                                    setField(TagFields.AlbumArtist, variation);
                                    update = true;
                                }
                                if (update)
                                {
                                    Console.WriteLine($"Updating: {f}");
                                    mediaFile.SaveTags();
                                }
                            }
                        }
                    }
                }

                for (int i = 0; i < artists.Length; i++)
                {
                    string a = artists[i];
                    for (int j = i + 1; j < artists.Length; j++)
                    {
                        string b = artists[j];
                        if (checkdict.ContainsKey(new Tuple<string, string>(b, a)))
                            continue;
                        var ta = moddict[a];
                        var tb = moddict[b];
                        if (ta.Value == tb.Value)
                            continue;
                        int checklen = Math.Max(ta.Length, tb.Length);
                        int dist = ta.Value.EditDistance(tb.Value);
                        if ((100 * dist / checklen) < 10)
                        {
                            if (!distdict.ContainsKey(dist))
                                distdict.Add(dist, new List<Tuple<string, string>>());
                            distdict[dist].Add(new Tuple<string, string>(a, b));
                            //Console.WriteLine($"{ta} //// {tb}");
                        }
                        checkdict.Add(new Tuple<string, string>(a, b), true);
                    }
                }

                foreach (int dist in distdict.Keys.OrderBy(k => k))
                {
                    LogConsole.WriteLine("Distance: " + dist);
                    LogConsole.WriteLine();
                    foreach (var pair in distdict[dist])
                    {
                        LogConsole.WriteLine("First: " + pair.Item1);
                        string[] ffiles = cache.FileCache.Where(f => f.Value.Artist == pair.Item1).Select(f => f.Key).Concat(
                            cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item1).Select(f => f.Key)).Distinct().ToArray();

                        foreach (string s in ffiles.Select(f => Path.GetDirectoryName(f)).Distinct())
                            LogConsole.WriteLine(s);

                        LogConsole.WriteLine();
                        LogConsole.WriteLine("Second: " + pair.Item2);
                        
                        string [] sfiles = cache.FileCache.Where(f => f.Value.Artist == pair.Item2).Select(f => f.Key).Concat(
                            cache.FileCache.Where(f => f.Value.AlbumArtist == pair.Item2).Select(f => f.Key)).Distinct().ToArray();

                        foreach (string s in sfiles.Select(f => Path.GetDirectoryName(f)).Distinct())
                            LogConsole.WriteLine(s);

                        LogConsole.WriteLine();

                    }
                }

                LogConsole.WriteLine();
            }

        }
    }
}
