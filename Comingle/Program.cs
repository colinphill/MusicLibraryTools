using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicFileUtilities;
using ConsoleTools;
using MetadataCaching;

namespace Comingle
{

    public static partial class ComparisonMetrics
    {
        public static double RatcliffObershelpSimilarity(this string source, string target)
        {
            return (2 * Convert.ToDouble(source.ToLower().Intersect(target.ToLower()).Count())) / (Convert.ToDouble(source.Length + target.Length));
        }

    }
    class Program
    {
        static void Main(string[] args)
        {
            LogConsole.ConsoleVerbosity = LogVerbosity.Max;

            MetadataDatabase db = new MetadataDatabase("cache.db");
            db.IndexFiles(new string[] { @"\\ritsuko.projecteva.net\Sonos", @"\\ritsuko.projecteva.netJenny\Henny's Crappy Music" });
            var basecache = db.BuildCache(new string[] { @"\\ritsuko.projecteva.net\Sonos" });
            var comingledcache = db.BuildCache(new string[] { @"\\ritsuko.projecteva.netJenny\Henny's Crappy Music" });

            /*LogConsole.WriteLine("Checking Artists");
            foreach (string artist in comingledcache.Artists)
            {
                string matchingartist = null;
                if (basecache.ArtistCache.ContainsKey(artist))
                    matchingartist = artist;
                else
                {
                    foreach (string baseartist in basecache.Artists)
                    {
                        if (baseartist.FuzzyDistance(artist) < 0.1)
                        {
                            //LogConsole.WriteLine("Fuzzy Match - " + artist + " (" + baseartist + ")");
                            matchingartist = baseartist;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(matchingartist))
                {
                    foreach (string basefile in basecache.ArtistCache[matchingartist])
                    {
                        IMetadataProvider basemeta = basecache[basefile];
                        foreach (string file in comingledcache.ArtistCache[artist])
                        {
                            IMetadataProvider meta = comingledcache[file];
                            if ((meta.Album.FuzzyDistance(basemeta.Album) < 0.1)&&(meta.Title.FuzzyDistance(basemeta.Title) < 0.1))
                            {
                                LogConsole.WriteLine("Probable Match - " + file +  " (" + basefile + ")");
                            }

                        }
                    }
                }
            }*/

            LogConsole.WriteLine("Checking Albums");
            foreach (string album in comingledcache.Albums)
            {
                string matchingalbum = null;
                if (basecache.AlbumCache.ContainsKey(album))
                    matchingalbum = album;
                else
                {
                    foreach (string basealbum in basecache.Albums)
                    {
                        if (basealbum.FuzzyDistance(album) < 0.1)
                        {
                            //LogConsole.WriteLine("Fuzzy Match - " + artist + " (" + baseartist + ")");
                            matchingalbum = basealbum;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(matchingalbum))
                {
                    foreach (string basefile in basecache.AlbumCache[matchingalbum])
                    {
                        var basemeta = basecache[basefile];
                        foreach (string file in comingledcache.AlbumCache[album])
                        {
                            var meta = comingledcache[file];
                            if (meta.Title.FuzzyDistance(basemeta.Title) < 0.1)
                            {
                                LogConsole.WriteLine("Probable Match - " + file + " (" + basefile + ")");
                            }

                        }
                    }
                }
            }

            LogConsole.WriteLine();
        }
    }
}
