using System.Globalization;
using System.Text;
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

    /// <summary>Find clusters while reporting both the track scan and pairwise comparison phases.</summary>
    IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
        IReadOnlyList<TrackRecord> records,
        double threshold,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default) =>
        FindSimilarArtists(records, threshold, ct);

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
        => FindSimilarArtists(records, threshold, null, ct);

    public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
        IReadOnlyList<TrackRecord> records,
        double threshold,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default)
    {
        // AnalyzeMetadata historically compared the union of Artist and AlbumArtist spellings.
        // Keep each file once per spelling so a differing track artist is not hidden merely because
        // the file also has an album artist.
        var byNameEntries = new Dictionary<string,
            (List<string> Paths, HashSet<string> Seen)>(StringComparer.Ordinal);
        progress?.Report(new(0, records.Count, "tracks", "Reading artist names"));
        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            ct.ThrowIfCancellationRequested();
            TrackRecord record = records[recordIndex];
            foreach (string name in new[] { record.Artist, record.AlbumArtist }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Select(value => value!)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!byNameEntries.TryGetValue(name, out var entry))
                {
                    entry = ([], new(StringComparer.OrdinalIgnoreCase));
                    byNameEntries[name] = entry;
                }
                if (entry.Seen.Add(record.Path))
                    entry.Paths.Add(record.Path);
            }
            int completed = recordIndex + 1;
            if ((completed & 127) == 0 || completed == records.Count)
                progress?.Report(new(completed, records.Count, "tracks",
                    "Reading artist names", record.Path));
        }
        var byName = byNameEntries.ToDictionary(
            pair => pair.Key, pair => pair.Value.Paths, StringComparer.Ordinal);

        var names = byName.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Union-find over the spellings.
        var parent = new int[names.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        var canonical = names.Select(Canonicalize).ToArray();

        // Variations (AnalyzeMetadata's checkartists, lines 347-451): names that collapse to the same
        // canonical form — case, punctuation, diacritics, a leading article, and "&"/"and" ignored —
        // are the same artist.
        var firstByCanonical = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            if (canonical[i].Length == 0)
                continue;
            if (firstByCanonical.TryGetValue(canonical[i], out var first))
                Union(first, i);
            else
                firstByCanonical[canonical[i]] = i;
        }

        // Fuzzy (AnalyzeMetadata's checkartists, lines 453-500): the O(n²) pairwise pass — edit distance
        // between the canonical forms as a fraction of the longer *original* name, under the threshold.
        // Exact-canonical pairs are skipped (already merged by the variations pass above).
        long totalComparisons = (long)names.Count * (names.Count - 1) / 2;
        long completedComparisons = 0;
        long reportInterval = Math.Max(1, totalComparisons / 1000);
        long nextReport = reportInterval;
        progress?.Report(new(0, totalComparisons, "artist-name comparisons",
            "Comparing artist names"));
        for (int i = 0; i < names.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < names.Count; j++)
            {
                if (canonical[i] == canonical[j])
                    continue;
                int checkLen = Math.Max(names[i].Length, names[j].Length);
                if (checkLen == 0)
                    continue;
                int dist = canonical[i].EditDistance(canonical[j]);
                if ((double)dist / checkLen <= threshold)
                    Union(i, j);
            }
            completedComparisons += names.Count - i - 1;
            if (completedComparisons >= nextReport || completedComparisons == totalComparisons)
            {
                progress?.Report(new(completedComparisons, totalComparisons,
                    "artist-name comparisons", "Comparing artist names", names[i]));
                nextReport = completedComparisons + reportInterval;
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

    // Reduce an artist name to a canonical key: "&"→"and", lowercased, a leading article dropped,
    // diacritics removed, and everything but letters/digits stripped. Two names with the same key are
    // spelling variations of one artist (e.g. "The Beatles" / "Beatles" / "Beatlés").
    private static string Canonicalize(string name)
    {
        var s = name.Replace("&", "and").ToLowerInvariant().Trim();

        // Drop a leading article while the separating space still exists.
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        else if (s.StartsWith("an ", StringComparison.Ordinal)) s = s[3..];
        else if (s.StartsWith("a ", StringComparison.Ordinal)) s = s[2..];

        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    public async Task<int> RenameArtistAsync(IReadOnlyList<string> paths, string from, string to,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        int changed = 0, done = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _media.LoadAsync(path, includeArtwork: false, ct);
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
