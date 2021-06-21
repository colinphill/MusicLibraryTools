using MusicFileUtilities;
using System.IO;
using iTunes;
using System.Linq;
using System.Collections.Generic;
using System;

namespace MetadataCaching
{

    public class iTunesMapper
    {
        private Dictionary<int, string> map_ = new Dictionary<int, string>();
        private HashSet<int> missing_ = new HashSet<int>();

        public string this[int id]
        {
            get
            {
                return map_[id];
            }
        }

        public IReadOnlyCollection<int> MissingTracks
        {
            get
            {
                return missing_;
            }
        }

        public iTunesMapper(iTunesLibrary library, MetadataCache cache)
        {
            var aapairs = cache.FileCache.Select(kv => (kv.Value.Artist, kv.Value.StrippedAlbum)).Concat(
               cache.FileCache.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.AlbumArtist)).Select(kv => (kv.Value.AlbumArtist, kv.Value.StrippedAlbum))).Distinct();
            var aadict = aapairs.ToDictionary(k => k, k => new List<KeyValuePair<string, MetadataCacheEntry>>());

            foreach (var kv in cache.FileCache)
            {
                if ((!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist)) && (aadict.ContainsKey((kv.Value.AlbumArtist, kv.Value.StrippedAlbum))))
                    aadict[(kv.Value.AlbumArtist, kv.Value.StrippedAlbum)].Add(kv);
                else
                    aadict[(kv.Value.Artist, kv.Value.StrippedAlbum)].Add(kv);
            }

            var pl = library.Playlists.First(p => string.Equals(p.Value.Title, "library", System.StringComparison.OrdinalIgnoreCase)).Value;

            foreach (int item in pl.Items)
            {
                string filepath = null;

                iTunesTrack track = library.Tracks[item];
                if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                    track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                    continue;

                try
                {
                    KeyValuePair<string, MetadataCacheEntry>[] newfiles = new KeyValuePair<string, MetadataCacheEntry>[0];
                    bool hasaa = aadict.ContainsKey((track.Artist, track.Album));
                    bool hasaaa = (!string.IsNullOrWhiteSpace(track.AlbumArtist)) && (aadict.ContainsKey((track.AlbumArtist, track.Album)));

                    if (hasaa)
                        newfiles = newfiles.Concat(aadict[(track.Artist, track.Album)].Where(kv => kv.Value.TrackNumber == track.TrackNumber)).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                    if (hasaaa)
                        newfiles = newfiles.Concat(aadict[(track.AlbumArtist, track.Album)].Where(kv => kv.Value.TrackNumber == track.TrackNumber)).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                    if (newfiles.Length == 0)
                    {
                        if (hasaa)
                            newfiles = newfiles.Concat(aadict[(track.Artist, track.Album)].Where(kv => kv.Value.Title.Equals(track.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        if (hasaaa)
                            newfiles = newfiles.Concat(aadict[(track.AlbumArtist, track.Album)].Where(kv => kv.Value.Title.Equals(track.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                    }

                    if (newfiles.Length == 0)
                    {
                        IMetadataProvider provider = Metadata.GetProvider(track.LocalLocation);
                        hasaa = aadict.ContainsKey((provider.Artist, provider.Album));
                        try
                        {
                            hasaaa = (!string.IsNullOrWhiteSpace(provider.AlbumArtist)) && (aadict.ContainsKey((provider.AlbumArtist, provider.Album)));
                        }
                        catch
                        {
                            hasaaa = false;
                        }
                        if (hasaa)
                            newfiles = newfiles.Concat(aadict[(provider.Artist, provider.Album)].Where(kv => kv.Value.Title.Equals(provider.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        if (hasaaa)
                            newfiles = newfiles.Concat(aadict[(provider.AlbumArtist, provider.Album)].Where(kv => kv.Value.Title.Equals(provider.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                    }

                    if (newfiles.Count(kv => kv.Value.TrackNumber == track.TrackNumber) > 0)
                        newfiles = newfiles.Where(kv => kv.Value.TrackNumber == track.TrackNumber).ToArray();

                    if (newfiles.Count(kv => string.Equals(kv.Value.Title, track.Title, StringComparison.InvariantCultureIgnoreCase)) > 0)
                        newfiles = newfiles.Where(kv => string.Equals(kv.Value.Title, track.Title, StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    newfiles = newfiles.OrderBy(kv => Path.GetFileName(track.LocalLocation).FuzzyDistance(Path.GetFileName(kv.Key))).ToArray();

                    if (newfiles.Length == 0)
                    {
                        ;

                    }

                    filepath = newfiles[0].Key;
                    map_.Add(item, filepath);
                }
                catch
                {
                    if (!missing_.Contains(item))
                        missing_.Add(item);
                }
            }
        }

    }


}