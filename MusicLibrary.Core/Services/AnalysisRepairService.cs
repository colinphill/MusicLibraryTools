using MusicFileUtilities;
using MusicLibrary.Core.Models;
using System.Text.RegularExpressions;

namespace MusicLibrary.Core.Services;

public interface IAnalysisRepairService
{
    AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records);

    AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records);

    AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records);

    AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records);

    IReadOnlyList<AnalysisTagConflict> FindAlbumArtistConflicts(IReadOnlyList<TrackRecord> records);

    AnalysisRepairPlan PreviewConflictRepairs(IReadOnlyList<AnalysisConflictResolution> resolutions);

    Task<BatchWriteResult> ApplyAsync(
        AnalysisRepairPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds conservative cache-only repair previews, then verifies every source snapshot before
/// delegating the actual tag writes. Repairs are proposed only when album peers, calibrated file
/// names, or explicit disc folders establish one unambiguous value.
/// </summary>
public sealed class AnalysisRepairService(ITagWriteService writer) : IAnalysisRepairService
{
    private static readonly TagFields[] PeerTextFields =
        [TagFields.Artist, TagFields.AlbumArtist, TagFields.Album];
    private static readonly Regex LeadingTrackNumber = new(
        @"^(?<number>\d{1,3})(?:\s*[-._]\s*|\s+|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DiscFolderNumber = new(
        @"^(?:cd|disc|disk)\s*[-._ ]?\s*(?<number>\d{1,2})(?:\D|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var repairs = PreviewMissingAlbumArtists(records).Items
            .Concat(PreviewNumberingAndTotals(records).Items)
            .Concat(PreviewTextNormalization(records).Items)
            .GroupBy(repair => (repair.Path, repair.Field), PathFieldComparer.Instance)
            .Select(group => group.First())
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repair => repair.Field)
            .ToList();
        return new AnalysisRepairPlan("Safe metadata repairs", repairs);
    }

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

    public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var repairs = new List<AnalysisTagRepair>();

        foreach (var album in AlbumFolders(records))
        {
            foreach (var disc in album.GroupBy(record => record.DiscNumber is > 0 ? record.DiscNumber.Value : 1))
                PreviewTrackNumbering(disc.ToList(), repairs);
        }

        PreviewDiscNumbering(records, repairs);
        return new AnalysisRepairPlan("Repair numbering and totals", repairs
            .GroupBy(repair => (repair.Path, repair.Field), PathFieldComparer.Instance)
            .Select(group => group.First())
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repair => repair.Field)
            .ToList());
    }

    public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var repairs = new Dictionary<(string Path, TagFields Field), AnalysisTagRepair>(PathFieldComparer.Instance);

        // Edge whitespace is independently wrong and needs no peer inference. Titles are limited to
        // this rule because unrelated tracks can legitimately differ only by case.
        foreach (var record in records)
        {
            foreach (var field in PeerTextFields.Append(TagFields.Title))
            {
                string? value = TextValue(record, field);
                if (string.IsNullOrEmpty(value))
                    continue;
                string trimmed = value.Trim();
                if (trimmed.Length > 0 && !StringComparer.Ordinal.Equals(value, trimmed))
                    repairs[(record.Path, field)] = Repair(record, field, value, trimmed,
                        "Removes leading or trailing whitespace from this tag value.");
            }
        }

        // Within each physical album folder, resolve only whitespace/case variants of the same
        // value, and only when one clean spelling has a strict peer majority. This never chooses
        // between genuinely different artist/album values or propagates a repeated-space spelling.
        foreach (var folder in records.GroupBy(
                     record => Path.GetDirectoryName(record.Path) ?? "",
                     StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in PeerTextFields)
            {
                var entries = folder
                    .Select(record => (Record: record, Value: TextValue(record, field)))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                    .Select(entry => (entry.Record, Value: entry.Value!));
                foreach (var equivalent in entries.GroupBy(
                             entry => CollapseWhitespace(entry.Value),
                             StringComparer.CurrentCultureIgnoreCase))
                {
                    var spellings = equivalent
                        .GroupBy(entry => entry.Value.Trim(), StringComparer.Ordinal)
                        .Select(group => new { Value = group.Key, Count = group.Count() })
                        .OrderByDescending(candidate => candidate.Count)
                        .ThenBy(candidate => candidate.Value, StringComparer.CurrentCulture)
                        .ToList();
                    if (spellings.Count == 0 || spellings[0].Count * 2 <= equivalent.Count() ||
                        !StringComparer.Ordinal.Equals(spellings[0].Value, CollapseWhitespace(spellings[0].Value)))
                        continue;

                    string canonical = spellings[0].Value;
                    foreach (var entry in equivalent.Where(entry =>
                                 !StringComparer.Ordinal.Equals(entry.Value, canonical)))
                    {
                        repairs[(entry.Record.Path, field)] = Repair(
                            entry.Record, field, entry.Value, canonical,
                            "Matches the clean spelling used by a strict majority of peers in this folder.");
                    }
                }
            }
        }

        return new AnalysisRepairPlan("Normalize metadata text", repairs.Values
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repair => repair.Field)
            .ToList());
    }

    public IReadOnlyList<AnalysisTagConflict> FindAlbumArtistConflicts(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var conflicts = new List<AnalysisTagConflict>();

        foreach (var album in records
                     .Where(record => !string.IsNullOrWhiteSpace(record.Album))
                     .GroupBy(record => (
                         Directory: AlbumPackageRoot(record.Path),
                         Album: record.Album!.Trim()), AlbumFolderComparer.Instance))
        {
            var options = album
                .Where(record => record.HasAlbumArtist && !string.IsNullOrWhiteSpace(record.AlbumArtist))
                .Select(record => record.AlbumArtist!.Trim())
                .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new AnalysisConflictOption(
                    group.GroupBy(value => value, StringComparer.Ordinal)
                        .OrderByDescending(spelling => spelling.Count())
                        .ThenBy(spelling => spelling.Key, StringComparer.CurrentCulture)
                        .First().Key,
                    group.Count()))
                .OrderByDescending(option => option.FileCount)
                .ThenBy(option => option.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (options.Count < 2)
                continue;

            var targets = album
                .Select(record => new AnalysisConflictTarget(
                    record.Path,
                    record.HasAlbumArtist ? record.AlbumArtist : null,
                    record.Length,
                    record.LastWriteTime))
                .OrderBy(target => target.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            conflicts.Add(new AnalysisTagConflict(
                album.Key.Album,
                album.Key.Directory,
                TagFields.AlbumArtist,
                options,
                targets));
        }

        return conflicts
            .OrderBy(conflict => conflict.Directory, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(conflict => conflict.Album, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public AnalysisRepairPlan PreviewConflictRepairs(IReadOnlyList<AnalysisConflictResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        var repairs = new List<AnalysisTagRepair>();
        foreach (var resolution in resolutions)
        {
            ArgumentNullException.ThrowIfNull(resolution.Conflict);
            string selected = resolution.SelectedValue?.Trim() ?? "";
            var option = resolution.Conflict.Options.FirstOrDefault(candidate =>
                StringComparer.CurrentCultureIgnoreCase.Equals(candidate.Value, selected));
            if (option is null)
                throw new ArgumentException(
                    $"'{resolution.SelectedValue}' is not an existing value for {resolution.Conflict.Album}.",
                    nameof(resolutions));

            string canonical = option.Value;
            string reason = $"User selected this canonical value from {resolution.Conflict.Options.Count} existing album-artist values.";
            foreach (var target in resolution.Conflict.Targets.Where(target =>
                         !StringComparer.Ordinal.Equals(target.Before, canonical)))
            {
                repairs.Add(new AnalysisTagRepair(
                    target.Path,
                    resolution.Conflict.Field,
                    target.Before,
                    canonical,
                    reason,
                    target.SourceLength,
                    target.SourceLastWriteTimeUtc));
            }
        }

        return new AnalysisRepairPlan("Resolve album artist conflicts", repairs
            .GroupBy(repair => (repair.Path, repair.Field), PathFieldComparer.Instance)
            .Select(group => group.Single())
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    private static void PreviewTrackNumbering(
        IReadOnlyList<TrackRecord> disc,
        List<AnalysisTagRepair> repairs)
    {
        if (disc.Count == 0)
            return;

        var parsed = disc.ToDictionary(
            record => record.Path,
            record => ParseLeadingNumber(Path.GetFileNameWithoutExtension(record.Path)),
            StringComparer.OrdinalIgnoreCase);
        var known = disc.Where(record => record.TrackNumber is > 0).ToList();
        bool calibratedNames = known.Count > 0 &&
            known.All(record => parsed[record.Path] == record.TrackNumber) &&
            parsed.Values.All(value => value is > 0) &&
            parsed.Values.Select(value => value!.Value).Distinct().Count() == disc.Count;

        var effectiveNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in disc)
        {
            if (record.TrackNumber is > 0)
            {
                effectiveNumbers[record.Path] = record.TrackNumber.Value;
                continue;
            }

            if (!calibratedNames || parsed[record.Path] is not int candidate)
                continue;
            effectiveNumbers[record.Path] = candidate;
            repairs.Add(Repair(record, TagFields.TrackNumber, NumberBefore(record.TrackNumber),
                candidate.ToString(),
                "The leading filename number matches every numbered peer in this disc folder."));
        }

        if (effectiveNumbers.Count != disc.Count)
            return;

        int maxTrack = effectiveNumbers.Values.Max();
        bool completeSequence = disc.Count >= 2 &&
            effectiveNumbers.Values.Distinct().Count() == disc.Count &&
            maxTrack == disc.Count && effectiveNumbers.Values.Min() == 1;
        var positiveTotals = disc.Where(record => record.TrackTotal is > 0)
            .Select(record => record.TrackTotal!.Value)
            .Distinct()
            .ToList();
        var validTotals = positiveTotals.Where(total => total >= maxTrack).Distinct().ToList();

        int? total = validTotals.Count == 1
            ? validTotals[0]
            : validTotals.Count == 0 && completeSequence
                ? maxTrack
                : null;
        if (total is null)
            return;

        string reason = positiveTotals.Contains(total.Value)
            ? "Matches the only peer track total that can contain every numbered track."
            : "Tracks form one complete, unique 1–N sequence in this disc folder.";
        foreach (var record in disc.Where(record => record.TrackTotal is null or <= 0 || record.TrackTotal < maxTrack))
        {
            repairs.Add(Repair(record, TagFields.TotalTracks, NumberBefore(record.TrackTotal),
                total.Value.ToString(), reason));
        }
    }

    private static void PreviewDiscNumbering(
        IReadOnlyList<TrackRecord> records,
        List<AnalysisTagRepair> repairs)
    {
        var packages = records
            .Where(record => !string.IsNullOrWhiteSpace(record.Album))
            .GroupBy(record => (
                Directory: AlbumPackageRoot(record.Path),
                Album: record.Album!.Trim()), AlbumFolderComparer.Instance);

        foreach (var package in packages)
        {
            var entries = package.Select(record => new
            {
                Record = record,
                FolderDisc = ParseDiscFolder(Path.GetFileName(Path.GetDirectoryName(record.Path))),
            }).ToList();
            var folderDiscs = entries.Where(entry => entry.FolderDisc is > 0)
                .Select(entry => entry.FolderDisc!.Value)
                .Distinct()
                .Order()
                .ToList();
            bool completeFolderSet = folderDiscs.Count >= 2 && IsCompleteSequence(folderDiscs) &&
                entries.Where(entry => entry.Record.DiscNumber is > 0 && entry.FolderDisc is > 0)
                    .All(entry => entry.Record.DiscNumber == entry.FolderDisc);

            var effectiveDiscs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Record.DiscNumber is > 0)
                {
                    effectiveDiscs[entry.Record.Path] = entry.Record.DiscNumber.Value;
                    continue;
                }

                if (!completeFolderSet || entry.FolderDisc is not int candidate)
                    continue;
                effectiveDiscs[entry.Record.Path] = candidate;
                repairs.Add(Repair(entry.Record, TagFields.DiscNumber,
                    NumberBefore(entry.Record.DiscNumber), candidate.ToString(),
                    "The file is inside a complete set of explicitly numbered disc folders."));
            }

            if (effectiveDiscs.Count != entries.Count)
                continue;
            var discNumbers = effectiveDiscs.Values.Distinct().Order().ToList();
            if (discNumbers.Count < 2 || !IsCompleteSequence(discNumbers))
                continue;

            int maxDisc = discNumbers[^1];
            var positiveTotals = entries.Where(entry => entry.Record.DiscTotal is > 0)
                .Select(entry => entry.Record.DiscTotal!.Value)
                .Distinct()
                .ToList();
            var validTotals = positiveTotals.Where(total => total >= maxDisc).Distinct().ToList();
            int? total = validTotals.Count == 1
                ? validTotals[0]
                : validTotals.Count == 0 && (completeFolderSet || discNumbers.Count == maxDisc)
                    ? maxDisc
                    : null;
            if (total is null)
                continue;

            string reason = positiveTotals.Contains(total.Value)
                ? "Matches the only peer disc total that can contain every numbered disc."
                : "The album has one complete, unique 1–N disc sequence.";
            foreach (var entry in entries.Where(entry =>
                         entry.Record.DiscTotal is null or <= 0 || entry.Record.DiscTotal < maxDisc))
            {
                repairs.Add(Repair(entry.Record, TagFields.TotalDiscs,
                    NumberBefore(entry.Record.DiscTotal), total.Value.ToString(), reason));
            }
        }
    }

    public async Task<BatchWriteResult> ApplyAsync(
        AnalysisRepairPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            return new BatchWriteResult([]);

        var files = plan.Items
            .GroupBy(repair => repair.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var snapshots = group.Select(repair => (repair.SourceLength, repair.SourceLastWriteTimeUtc))
                    .Distinct()
                    .ToList();
                if (snapshots.Count != 1)
                    throw new InvalidOperationException($"Repair preview has inconsistent source snapshots: {group.Key}");
                var edits = group
                    .GroupBy(repair => repair.Field)
                    .Select(fieldGroup =>
                    {
                        var values = fieldGroup.Select(repair => repair.After).Distinct(StringComparer.Ordinal).ToList();
                        if (values.Count != 1)
                            throw new InvalidOperationException(
                                $"Repair preview has conflicting {fieldGroup.Key} values: {group.Key}");
                        return new TagEdit(fieldGroup.Key, values[0]);
                    })
                    .OrderBy(edit => edit.Field)
                    .ToList();
                return new FileRepair(group.Key, snapshots[0].SourceLength,
                    snapshots[0].SourceLastWriteTimeUtc, edits);
            })
            .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Validate the entire preview before the first write. Timestamp tolerance matches the cache
        // indexer's comparison for filesystems with coarse network-share timestamp resolution.
        foreach (var repair in files)
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(repair.Path);
            if (!file.Exists || file.Length != repair.SourceLength ||
                Math.Abs((file.LastWriteTimeUtc - repair.SourceLastWriteTimeUtc).TotalMilliseconds) > 500)
                throw new InvalidOperationException(
                    $"Source changed since the repair preview: {repair.Path}. Preview again before applying.");
        }

        var results = new List<FileWriteResult>(files.Count);
        int completed = 0;
        foreach (var group in files.GroupBy(file => file.Edits, TagEditListComparer.Instance))
        {
            ct.ThrowIfCancellationRequested();
            int completedBeforeGroup = completed;
            var groupProgress = new DelegateProgress(done => progress?.Report(completedBeforeGroup + done));
            var result = await writer.ApplyAsync(
                group.Select(file => file.Path).ToList(),
                group.Key,
                groupProgress,
                ct);
            results.AddRange(result.Files);
            completed += group.Count();
            progress?.Report(completed);
        }
        return new BatchWriteResult(results);
    }

    private static IEnumerable<IGrouping<(string Directory, string Album), TrackRecord>> AlbumFolders(
        IReadOnlyList<TrackRecord> records) =>
        records
            .Where(record => !string.IsNullOrWhiteSpace(record.Album))
            .GroupBy(record => (
                Directory: Path.GetDirectoryName(record.Path) ?? "",
                Album: record.Album!.Trim()), AlbumFolderComparer.Instance);

    private static AnalysisTagRepair Repair(
        TrackRecord record,
        TagFields field,
        string? before,
        string after,
        string reason) =>
        new(record.Path, field, before, after, reason, record.Length, record.LastWriteTime);

    private static string? NumberBefore(int? value) => value?.ToString();

    private static string? TextValue(TrackRecord record, TagFields field) => field switch
    {
        TagFields.Artist => record.Artist,
        TagFields.AlbumArtist => record.HasAlbumArtist ? record.AlbumArtist : null,
        TagFields.Album => record.Album,
        TagFields.Title => record.Title,
        _ => null,
    };

    private static string CollapseWhitespace(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
                result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static int? ParseLeadingNumber(string? name)
    {
        var match = LeadingTrackNumber.Match(name ?? "");
        return match.Success && int.TryParse(match.Groups["number"].Value, out int number) && number > 0
            ? number
            : null;
    }

    private static int? ParseDiscFolder(string? name)
    {
        var match = DiscFolderNumber.Match(name ?? "");
        return match.Success && int.TryParse(match.Groups["number"].Value, out int number) && number > 0
            ? number
            : null;
    }

    private static string AlbumPackageRoot(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? "";
        return ParseDiscFolder(Path.GetFileName(directory)) is null
            ? directory
            : Path.GetDirectoryName(directory) ?? directory;
    }

    private static bool IsCompleteSequence(IReadOnlyList<int> sortedDistinctValues) =>
        sortedDistinctValues.Count > 0 && sortedDistinctValues[0] == 1 &&
        sortedDistinctValues[^1] == sortedDistinctValues.Count;

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

    private sealed class PathFieldComparer : IEqualityComparer<(string Path, TagFields Field)>
    {
        public static PathFieldComparer Instance { get; } = new();

        public bool Equals((string Path, TagFields Field) x, (string Path, TagFields Field) y) =>
            x.Field == y.Field && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode((string Path, TagFields Field) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path), value.Field);
    }

    private sealed class TagEditListComparer : IEqualityComparer<IReadOnlyList<TagEdit>>
    {
        public static TagEditListComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<TagEdit>? x, IReadOnlyList<TagEdit>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            for (int index = 0; index < x.Count; index++)
                if (x[index].Field != y[index].Field ||
                    !StringComparer.Ordinal.Equals(x[index].Value, y[index].Value))
                    return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<TagEdit> edits)
        {
            var hash = new HashCode();
            foreach (var edit in edits)
            {
                hash.Add(edit.Field);
                hash.Add(edit.Value, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }
    }

    private sealed record FileRepair(
        string Path,
        long SourceLength,
        DateTime SourceLastWriteTimeUtc,
        IReadOnlyList<TagEdit> Edits);

    private sealed class DelegateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
