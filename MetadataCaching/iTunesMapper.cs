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
            static (string Artist, string Album) Key(string artist, string album) =>
                ((artist ?? string.Empty).Trim().ToUpperInvariant(), (album ?? string.Empty).Trim().ToUpperInvariant());

            var aadict = new Dictionary<(string Artist, string Album), List<KeyValuePair<string, MetadataCacheEntry>>>();
            foreach (var kv in cache.FileCache)
            {
                void Add(string artist)
                {
                    var key = Key(artist, kv.Value.StrippedAlbum);
                    if (!aadict.TryGetValue(key, out var entries))
                        aadict.Add(key, entries = new List<KeyValuePair<string, MetadataCacheEntry>>());
                    entries.Add(kv);
                }

                Add(kv.Value.Artist);
                if (!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) &&
                    !string.Equals(kv.Value.AlbumArtist, kv.Value.Artist, StringComparison.OrdinalIgnoreCase))
                    Add(kv.Value.AlbumArtist);
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
                    IEnumerable<KeyValuePair<string, MetadataCacheEntry>> Candidates(string artist, string album) =>
                        aadict.TryGetValue(Key(artist, album), out var entries) ? entries : [];

                    var newfiles = Candidates(track.Artist, track.Album)
                        .Concat(string.IsNullOrWhiteSpace(track.AlbumArtist) ? [] : Candidates(track.AlbumArtist, track.Album))
                        .Distinct()
                        .ToArray();

                    if (newfiles.Length == 0)
                    {
                        IMetadataProvider provider = MediaFile.GetFile(track.LocalLocation).Tags.First();
                        newfiles = Candidates(provider.Artist, provider.Album)
                            .Concat(string.IsNullOrWhiteSpace(provider.AlbumArtist) ? [] : Candidates(provider.AlbumArtist, provider.Album))
                            .Distinct()
                            .ToArray();
                    }

                    if (newfiles.Count(kv => kv.Value.TrackNumber == track.TrackNumber) > 0)
                        newfiles = newfiles.Where(kv => kv.Value.TrackNumber == track.TrackNumber).ToArray();

                    if (track.DiscNumber is not null && newfiles.Count(kv => kv.Value.DiscNumber == track.DiscNumber) > 0)
                        newfiles = newfiles.Where(kv => kv.Value.DiscNumber == track.DiscNumber).ToArray();

                    if (newfiles.Count(kv => string.Equals(kv.Value.Title, track.Title, StringComparison.InvariantCultureIgnoreCase)) > 0)
                        newfiles = newfiles.Where(kv => string.Equals(kv.Value.Title, track.Title, StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    newfiles = newfiles
                        .OrderBy(kv => Path.GetFileName(track.LocalLocation).FuzzyDistance(Path.GetFileName(kv.Key)))
                        .ThenByDescending(kv => kv.Value.SampleRate)
                        .ThenByDescending(kv => kv.Value.BitsPerSample)
                        .ToArray();

                    filepath = newfiles[0].Key;
                    map_[item] = filepath;
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
