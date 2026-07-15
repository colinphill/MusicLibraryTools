using System.Text;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>Builds cache-only matrices for album packages that violate metadata invariants.</summary>
public static class AlbumMetadataMatrixBuilder
{
    private static readonly Regex DiscFolderNumber = new(
        @"^(?:cd|disc|disk)\s*[-._ ]?\s*(?<number>\d{1,2})(?:\D|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DiscAlbumSuffix = new(
        @"^(?<album>.+?)\s+\(Disc\s+\d+\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<AlbumMetadataMatrix> Build(IReadOnlyList<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .GroupBy(record => new AlbumKey(PackageRoot(record.Path), AlbumIdentity(record)), AlbumKeyComparer.Instance)
            .Select(BuildMatrix)
            .Where(matrix => matrix.InconsistentCellCount > 0)
            .OrderBy(matrix => matrix.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(matrix => matrix.Root, PathComparer)
            .ToList();
    }

    private static AlbumMetadataMatrix BuildMatrix(IGrouping<AlbumKey, TrackRecord> album)
    {
        var records = album.ToList();
        var issues = new Dictionary<(string Path, MatrixField Field), List<string>>(new IssueKeyComparer());
        void Mark(TrackRecord record, MatrixField field, string reason)
        {
            var key = (record.Path, field);
            if (!issues.TryGetValue(key, out var reasons))
                issues[key] = reasons = [];
            if (!reasons.Contains(reason, StringComparer.Ordinal))
                reasons.Add(reason);
        }

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Artist))
                Mark(record, MatrixField.Artist, "Artist is missing.");
            if (!record.HasAlbumArtist || string.IsNullOrWhiteSpace(record.AlbumArtist))
                Mark(record, MatrixField.AlbumArtist, "Explicit Album Artist is missing.");
            if (string.IsNullOrWhiteSpace(record.Album))
                Mark(record, MatrixField.Album, "Album is missing.");
            if (string.IsNullOrWhiteSpace(record.Title))
                Mark(record, MatrixField.Title, "Title is missing.");
            if (record.TrackNumber is null or <= 0)
                Mark(record, MatrixField.TrackNumber, "Track number is missing or zero.");
            if (record.TrackTotal is null or <= 0)
                Mark(record, MatrixField.TrackTotal, "Track total is missing or zero.");
        }

        MarkDifferingText(records.Where(record => record.HasAlbumArtist),
            record => record.AlbumArtist, MatrixField.AlbumArtist,
            "Album Artist values differ within this album package.", Mark);
        MarkDifferingText(records, record => record.Album, MatrixField.Album,
            "Album values differ within this album package.", Mark);
        var datedRecords = records.Where(record => !string.IsNullOrWhiteSpace(record.ReleaseDate)).ToList();
        if (datedRecords.Count > 0)
        {
            foreach (var record in records.Where(record => string.IsNullOrWhiteSpace(record.ReleaseDate)))
                Mark(record, MatrixField.ReleaseDate, "Release date is missing while album peers have one.");
            MarkDifferingText(datedRecords, record => record.ReleaseDate, MatrixField.ReleaseDate,
                "Release dates differ within this album package.", Mark);
        }

        var effectiveDiscs = records.ToDictionary(
            record => record.Path,
            record => record.DiscNumber is > 0
                ? record.DiscNumber.Value
                : ParseDiscFolder(record.Path) ?? 1,
            PathComparer);
        foreach (var record in records)
        {
            int? folderDisc = ParseDiscFolder(record.Path);
            if (record.DiscNumber is > 0 && folderDisc is > 0 && record.DiscNumber != folderDisc)
                Mark(record, MatrixField.DiscNumber, "Disc tag conflicts with the explicit disc folder.");
        }

        var distinctDiscs = effectiveDiscs.Values.Distinct().Order().ToList();
        bool multiDisc = distinctDiscs.Count > 1 || records.Any(record => record.DiscTotal is > 1);
        if (multiDisc)
        {
            foreach (var record in records.Where(record => record.DiscNumber is null or <= 0))
                Mark(record, MatrixField.DiscNumber, "Disc number is missing for a multi-disc album.");
            if (!IsCompleteSequence(distinctDiscs))
                foreach (var record in records)
                    Mark(record, MatrixField.DiscNumber, "Disc sequence has a gap or does not start at 1.");
        }

        foreach (var disc in records.GroupBy(record => effectiveDiscs[record.Path]))
        {
            var positiveNumbers = disc.Where(record => record.TrackNumber is > 0)
                .GroupBy(record => record.TrackNumber!.Value)
                .ToList();
            foreach (var duplicate in positiveNumbers.Where(group => group.Count() > 1))
                foreach (var record in duplicate)
                    Mark(record, MatrixField.TrackNumber, "Track number is duplicated within this disc.");
            var distinctNumbers = positiveNumbers.Select(group => group.Key).Order().ToList();
            if (distinctNumbers.Count > 0 && !IsCompleteSequence(distinctNumbers))
                foreach (var record in disc.Where(record => record.TrackNumber is > 0))
                    Mark(record, MatrixField.TrackNumber, "Track sequence has a gap or does not start at 1.");

            int maxTrack = distinctNumbers.Count == 0 ? 0 : distinctNumbers[^1];
            var totals = disc.Where(record => record.TrackTotal is > 0)
                .Select(record => record.TrackTotal!.Value)
                .Distinct()
                .ToList();
            if (totals.Count > 1)
                foreach (var record in disc)
                    Mark(record, MatrixField.TrackTotal, "Track totals disagree within this disc.");
            foreach (var record in disc.Where(record => record.TrackTotal is > 0 && record.TrackTotal < maxTrack))
                Mark(record, MatrixField.TrackTotal, "Track total is smaller than a track number in this disc.");
        }

        if (multiDisc)
        {
            int maxDisc = distinctDiscs.Count == 0 ? 0 : distinctDiscs[^1];
            foreach (var record in records.Where(record => record.DiscTotal is null or <= 0))
                Mark(record, MatrixField.DiscTotal, "Disc total is missing or zero.");
            var totals = records.Where(record => record.DiscTotal is > 0)
                .Select(record => record.DiscTotal!.Value)
                .Distinct()
                .ToList();
            if (totals.Count > 1)
                foreach (var record in records)
                    Mark(record, MatrixField.DiscTotal, "Disc totals disagree within this album.");
            foreach (var record in records.Where(record => record.DiscTotal is > 0 && record.DiscTotal < maxDisc))
                Mark(record, MatrixField.DiscTotal, "Disc total is smaller than a disc number in this album.");
        }

        AnalysisMatrixCell Cell(TrackRecord record, MatrixField field, string? value)
        {
            issues.TryGetValue((record.Path, field), out var reasons);
            return new AnalysisMatrixCell(value, reasons is not null, reasons is null ? null : string.Join(" ", reasons));
        }

        var rows = records
            .OrderBy(record => effectiveDiscs[record.Path])
            .ThenBy(record => record.TrackNumber ?? int.MaxValue)
            .ThenBy(record => record.Path, PathComparer)
            .Select(record => new AlbumMetadataRow(
                record.Path,
                Cell(record, MatrixField.DiscNumber, record.DiscNumber?.ToString()),
                Cell(record, MatrixField.TrackNumber, record.TrackNumber?.ToString()),
                Cell(record, MatrixField.TrackTotal, record.TrackTotal?.ToString()),
                Cell(record, MatrixField.DiscTotal, record.DiscTotal?.ToString()),
                Cell(record, MatrixField.Artist, record.Artist),
                Cell(record, MatrixField.AlbumArtist,
                    record.HasAlbumArtist ? record.AlbumArtist : null),
                Cell(record, MatrixField.Album, record.Album),
                Cell(record, MatrixField.ReleaseDate, record.ReleaseDate),
                Cell(record, MatrixField.Title, record.Title)))
            .ToList();

        string artist = MostCommon(records
            .Select(record => record.HasAlbumArtist ? record.AlbumArtist : record.Artist));
        string albumName = StripDiscSuffix(MostCommon(records.Select(record => record.Album)));
        return new AlbumMetadataMatrix(album.Key.Root, $"{artist} — {albumName}", rows);
    }

    private static void MarkDifferingText(
        IEnumerable<TrackRecord> records,
        Func<TrackRecord, string?> selector,
        MatrixField field,
        string reason,
        Action<TrackRecord, MatrixField, string> mark)
    {
        var present = records.Where(record => !string.IsNullOrWhiteSpace(selector(record))).ToList();
        if (present.Select(record => selector(record)!).Distinct(StringComparer.Ordinal).Take(2).Count() <= 1)
            return;
        foreach (var record in present)
            mark(record, field, reason);
    }

    private static string MostCommon(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.First())
            .FirstOrDefault() ?? "(unknown)";

    private static string PackageRoot(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? "";
        return ParseDiscFolder(path) is null ? directory : Path.GetDirectoryName(directory) ?? directory;
    }

    private static int? ParseDiscFolder(string path)
    {
        string? folder = Path.GetFileName(Path.GetDirectoryName(path));
        var match = DiscFolderNumber.Match(folder ?? "");
        return match.Success && int.TryParse(match.Groups["number"].Value, out int number) && number > 0
            ? number
            : null;
    }

    private static string AlbumIdentity(TrackRecord record) =>
        CollapseWhitespace(StripDiscSuffix(record.StrippedAlbum ?? record.Album ?? "(missing)"));

    private static string StripDiscSuffix(string value)
    {
        var match = DiscAlbumSuffix.Match(value.Trim());
        return match.Success ? match.Groups["album"].Value.Trim() : value.Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static bool IsCompleteSequence(IReadOnlyList<int> sortedDistinctValues) =>
        sortedDistinctValues.Count > 0 && sortedDistinctValues[0] == 1 &&
        sortedDistinctValues[^1] == sortedDistinctValues.Count;

    private enum MatrixField
    {
        DiscNumber,
        TrackNumber,
        TrackTotal,
        DiscTotal,
        Artist,
        AlbumArtist,
        Album,
        ReleaseDate,
        Title,
    }

    private sealed record AlbumKey(string Root, string Album);

    private sealed class AlbumKeyComparer : IEqualityComparer<AlbumKey>
    {
        public static AlbumKeyComparer Instance { get; } = new();
        public bool Equals(AlbumKey? x, AlbumKey? y) => ReferenceEquals(x, y) ||
            x is not null && y is not null && PathComparer.Equals(x.Root, y.Root) &&
            StringComparer.CurrentCultureIgnoreCase.Equals(x.Album, y.Album);
        public int GetHashCode(AlbumKey value) => HashCode.Combine(
            PathComparer.GetHashCode(value.Root),
            StringComparer.CurrentCultureIgnoreCase.GetHashCode(value.Album));
    }

    private sealed class IssueKeyComparer : IEqualityComparer<(string Path, MatrixField Field)>
    {
        public bool Equals((string Path, MatrixField Field) x, (string Path, MatrixField Field) y) =>
            x.Field == y.Field && PathComparer.Equals(x.Path, y.Path);
        public int GetHashCode((string Path, MatrixField Field) value) =>
            HashCode.Combine(PathComparer.GetHashCode(value.Path), value.Field);
    }
}
