using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IAnalysisRepairService
{
    AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records);

    Task<BatchWriteResult> ApplyAsync(
        AnalysisRepairPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds conservative cache-only repair previews, then verifies every source snapshot before
/// delegating the actual tag writes. The first repair deliberately fills only missing AlbumArtist
/// values whose directory/album peers establish one unambiguous value.
/// </summary>
public sealed class AnalysisRepairService(ITagWriteService writer) : IAnalysisRepairService
{
    public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records)
    {
        var repairs = new List<AnalysisTagRepair>();
        var albumFolders = records
            .Where(record => !string.IsNullOrWhiteSpace(record.Album))
            .GroupBy(record => (
                Directory: Path.GetDirectoryName(record.Path) ?? "",
                Album: record.Album!.Trim()), AlbumFolderComparer.Instance);

        foreach (var album in albumFolders)
        {
            var missing = album.Where(record => !record.HasAlbumArtist).ToList();
            if (missing.Count == 0)
                continue;

            var knownAlbumArtists = album
                .Where(record => record.HasAlbumArtist)
                .Select(record => record.AlbumArtist)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
            string? candidate = OneDistinctValue(knownAlbumArtists);
            string reason;
            if (candidate is not null)
            {
                reason = "Matches the album artist already used by other tracks in this folder.";
            }
            else
            {
                if (knownAlbumArtists.Count > 0)
                    continue; // Conflicting existing values are not safe to infer away.
                // With no existing album artist, a single track-artist value is safe. Varying artists
                // are likely a compilation and intentionally require human input.
                candidate = OneDistinctValue(album
                    .Select(record => record.Artist)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!));
                if (candidate is null)
                    continue;
                reason = "All tracks in this album folder use the same artist.";
            }

            foreach (var record in missing)
            {
                repairs.Add(new AnalysisTagRepair(
                    record.Path,
                    TagFields.AlbumArtist,
                    record.HasAlbumArtist ? record.AlbumArtist : null,
                    candidate,
                    reason,
                    record.Length,
                    record.LastWriteTime));
            }
        }

        return new AnalysisRepairPlan("Fill missing album artists", repairs
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    public async Task<BatchWriteResult> ApplyAsync(
        AnalysisRepairPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            return new BatchWriteResult([]);

        // Validate the entire preview before the first write. Timestamp tolerance matches the cache
        // indexer's comparison for filesystems with coarse network-share timestamp resolution.
        foreach (var repair in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(repair.Path);
            if (!file.Exists || file.Length != repair.SourceLength ||
                Math.Abs((file.LastWriteTimeUtc - repair.SourceLastWriteTimeUtc).TotalMilliseconds) > 500)
                throw new InvalidOperationException(
                    $"Source changed since the repair preview: {repair.Path}. Preview again before applying.");
        }

        var results = new List<FileWriteResult>(plan.Items.Count);
        int completed = 0;
        foreach (var group in plan.Items.GroupBy(item => (item.Field, item.After)))
        {
            ct.ThrowIfCancellationRequested();
            int completedBeforeGroup = completed;
            var groupProgress = new DelegateProgress(done => progress?.Report(completedBeforeGroup + done));
            var result = await writer.ApplyAsync(
                group.Select(item => item.Path).ToList(),
                [new TagEdit(group.Key.Field, group.Key.After)],
                groupProgress,
                ct);
            results.AddRange(result.Files);
            completed += group.Count();
            progress?.Report(completed);
        }
        return new BatchWriteResult(results);
    }

    private static string? OneDistinctValue(IEnumerable<string> values)
    {
        var distinct = values
            .Select(value => value.Trim())
            .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.Length).First())
            .Take(2)
            .ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private sealed class AlbumFolderComparer : IEqualityComparer<(string Directory, string Album)>
    {
        public static AlbumFolderComparer Instance { get; } = new();

        public bool Equals((string Directory, string Album) x, (string Directory, string Album) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Directory, y.Directory) &&
            StringComparer.CurrentCultureIgnoreCase.Equals(x.Album, y.Album);

        public int GetHashCode((string Directory, string Album) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Directory),
                StringComparer.CurrentCultureIgnoreCase.GetHashCode(value.Album));
    }

    private sealed class DelegateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
