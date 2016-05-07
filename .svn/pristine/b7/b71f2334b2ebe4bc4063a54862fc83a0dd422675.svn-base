using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicFileUtilities;
using ConsoleTools;

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
            MetadataCache basecache = new MetadataCache();
            basecache.Load(@"c:\LibraryCaches\Colin");
            //basecache.BuildCache(@"\\melchior\Sonos");
            //basecache.Save(@"c:\LibraryCaches\Colin");

            MetadataCache comingledcache = new MetadataCache();
            comingledcache.Load(@"c:\LibraryCaches\Jenny");
            comingledcache.BuildCache(@"\\melchior\Jenny\Jenny's Crappy Music");
            comingledcache.Save(@"c:\LibraryCaches\Jenny");

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
                        IMetadataProvider basemeta = basecache[basefile];
                        foreach (string file in comingledcache.AlbumCache[album])
                        {
                            IMetadataProvider meta = comingledcache[file];
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
