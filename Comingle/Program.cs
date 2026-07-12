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

    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: Comingle <cache.db> <base-library> <incoming-library>");
                return;
            }

            LogConsole.ConsoleVerbosity = LogVerbosity.Max;

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(args[0]);
            db.IndexFiles(new[] { args[1], args[2] });
            var basecache = db.BuildCache(new[] { args[1] });
            var comingledcache = db.BuildCache(new[] { args[2] });

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
