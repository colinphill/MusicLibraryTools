using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// The "check artists" tool: clusters near-duplicate artist-name spellings (e.g. "Beatles" vs
/// "The Beatles", or typos) so they can be merged, and rewrites the Artist/AlbumArtist tags of the
/// affected files to a chosen canonical name. Reimplements AnalyzeMetadata's interactive
/// checkartists, cross-platform and via the hand-written writers.
/// </summary>
public interface IArtistReconciler
{
    /// <summary>Find clusters of similar artist names (each cluster has 2+ spellings).</summary>
    IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(IReadOnlyList<TrackRecord> records, double threshold = 0.2, CancellationToken ct = default);

    /// <summary>
    /// Rewrite the Artist and/or AlbumArtist tag (whichever currently equals <paramref name="from"/>)
    /// to <paramref name="to"/> across the given files. Returns the number of files changed.
    /// </summary>
    Task<int> RenameArtistAsync(IReadOnlyList<string> paths, string from, string to,
        IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IArtistReconciler"/>
public sealed class ArtistReconciler : IArtistReconciler
{
    private readonly IMediaFileService _media;
    private readonly ITagWriteService _writer;

    public ArtistReconciler(IMediaFileService media, ITagWriteService writer)
    {
        _media = media;
        _writer = writer;
    }

    public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(IReadOnlyList<TrackRecord> records, double threshold = 0.2, CancellationToken ct = default)
    {
        // Group files by their effective album-artist spelling.
        var byName = records
            .Where(r => !string.IsNullOrWhiteSpace(r.EffectiveAlbumArtist))
            .GroupBy(r => r.EffectiveAlbumArtist)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Path).ToList());

        var names = byName.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Union-find: merge any two names within the fuzzy-distance threshold.
        var parent = new int[names.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        for (int i = 0; i < names.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < names.Count; j++)
            {
                var d = names[i].FuzzyDistance(names[j]);
                if (d > 0 && d <= threshold)
                    Union(i, j);
            }
        }

        // Assemble clusters with 2+ distinct spellings, largest variant first.
        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < names.Count; i++)
            (clusters.TryGetValue(Find(i), out var list) ? list : clusters[Find(i)] = []).Add(i);

        var groups = new List<SimilarArtistGroup>();
        foreach (var members in clusters.Values.Where(m => m.Count > 1))
        {
            var variants = members
                .Select(i => new ArtistVariant(names[i], byName[names[i]]))
                .OrderByDescending(v => v.TrackCount)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            groups.Add(new SimilarArtistGroup(variants));
        }

        return groups
            .OrderByDescending(g => g.AllPaths.Count)
            .ToList();
    }

    public async Task<int> RenameArtistAsync(IReadOnlyList<string> paths, string from, string to,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int changed = 0, done = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _media.LoadAsync(path, ct);
            if (result.Success)
            {
                var model = result.Value!;
                // Only rewrite the field(s) that actually hold the old spelling.
                var edits = new List<TagEdit>();
                if (string.Equals(model.AlbumArtist, from, StringComparison.Ordinal))
                    edits.Add(new TagEdit(TagFields.AlbumArtist, to));
                if (string.Equals(model.Artist, from, StringComparison.Ordinal))
                    edits.Add(new TagEdit(TagFields.Artist, to));

                if (edits.Count > 0)
                {
                    var write = await _writer.ApplyAsync([path], edits, ct: ct);
                    if (write.SavedCount > 0)
                        changed++;
                }
            }
            progress?.Report(++done);
        }
        return changed;
    }
}
