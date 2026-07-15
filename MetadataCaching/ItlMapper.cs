#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iTunes.Binary;
using MusicFileUtilities;

namespace MetadataCaching;

/// <summary>Maps binary iTunes-library track ids to matching files in a metadata cache.</summary>
public sealed class ItlMapper
{
    private readonly Dictionary<int, string> map_ = [];
    private readonly HashSet<int> missing_ = [];

    public string this[int id] => map_[id];
    public IReadOnlyCollection<int> MissingTracks => missing_;

    public ItlMapper(ItlLibrary library, MetadataCache cache)
    {
        static (string Artist, string Album) Key(string? artist, string? album) =>
            ((artist ?? string.Empty).Trim().ToUpperInvariant(),
             (album ?? string.Empty).Trim().ToUpperInvariant());

        var byArtistAlbum = new Dictionary<(string Artist, string Album),
            List<KeyValuePair<string, MetadataCacheEntry>>>();
        foreach (KeyValuePair<string, MetadataCacheEntry> cached in cache.FileCache)
        {
            void Add(string? artist)
            {
                var key = Key(artist, cached.Value.StrippedAlbum);
                if (!byArtistAlbum.TryGetValue(key, out List<KeyValuePair<string, MetadataCacheEntry>>? entries))
                    byArtistAlbum.Add(key, entries = []);
                entries.Add(cached);
            }

            Add(cached.Value.Artist);
            if (!string.IsNullOrWhiteSpace(cached.Value.AlbumArtist) &&
                !string.Equals(cached.Value.AlbumArtist, cached.Value.Artist,
                    StringComparison.OrdinalIgnoreCase))
                Add(cached.Value.AlbumArtist);
        }

        ItlPlaylist master = library.Playlists.Single(playlist => playlist.IsMaster);
        Dictionary<int, ItlTrack> tracks = library.Tracks.ToDictionary(track => track.Id);
        foreach (int item in master.TrackIds)
        {
            ItlTrack track = tracks[item];
            string kind = track.Kind ?? string.Empty;
            string? localPath = track.LocalPath;
            if (track.HasVideo || string.IsNullOrWhiteSpace(localPath) ||
                kind.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("book", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("audible", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("document", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("app", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("tone", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                IEnumerable<KeyValuePair<string, MetadataCacheEntry>> Candidates(string? artist, string? album) =>
                    byArtistAlbum.TryGetValue(Key(artist, album), out var entries) ? entries : [];

                KeyValuePair<string, MetadataCacheEntry>[] newFiles = [.. Candidates(track.Artist, track.Album)
                    .Concat(string.IsNullOrWhiteSpace(track.AlbumArtist)
                        ? []
                        : Candidates(track.AlbumArtist, track.Album))
                    .Distinct()];

                if (newFiles.Length == 0)
                {
                    IMetadataProvider provider = MediaFile.GetFile(localPath, readOnly: true).Tags.First();
                    newFiles = [.. Candidates(provider.Artist, provider.Album)
                        .Concat(string.IsNullOrWhiteSpace(provider.AlbumArtist)
                            ? []
                            : Candidates(provider.AlbumArtist, provider.Album))
                        .Distinct()];
                }

                if (newFiles.Any(candidate => candidate.Value.TrackNumber == track.TrackNumber))
                    newFiles = [.. newFiles.Where(candidate => candidate.Value.TrackNumber == track.TrackNumber)];

                if (track.DiscNumber != 0 && newFiles.Any(candidate => candidate.Value.DiscNumber == track.DiscNumber))
                    newFiles = [.. newFiles.Where(candidate => candidate.Value.DiscNumber == track.DiscNumber)];

                if (newFiles.Any(candidate => string.Equals(candidate.Value.Title, track.Title,
                        StringComparison.InvariantCultureIgnoreCase)))
                    newFiles = [.. newFiles.Where(candidate => string.Equals(candidate.Value.Title, track.Title,
                        StringComparison.InvariantCultureIgnoreCase))];

                map_[item] = newFiles
                    .OrderBy(candidate => Path.GetFileName(localPath)
                        .FuzzyDistance(Path.GetFileName(candidate.Key)))
                    .ThenByDescending(candidate => candidate.Value.SampleRate)
                    .ThenByDescending(candidate => candidate.Value.BitsPerSample)
                    .First().Key;
            }
            catch
            {
                missing_.Add(item);
            }
        }
    }
}
