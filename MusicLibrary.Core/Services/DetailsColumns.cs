using System.Collections;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Definition of one selectable details-grid column. <see cref="Get"/> is the displayed text;
/// <see cref="SortKey"/> is an optional typed value so numeric/date columns sort by magnitude
/// rather than by their formatted string (e.g. "44,100 Hz").
/// </summary>
public sealed record DetailsColumn(string Key, string Header, Func<TrackRecord, string> Get, Func<TrackRecord, IComparable?>? SortKey = null);

/// <summary>
/// The catalog of columns the details grid can show: metadata fields, audio parameters, and file
/// info. Ordered as they appear in the column chooser.
/// </summary>
public static class DetailsColumns
{
    public static readonly IReadOnlyList<DetailsColumn> All =
    [
        new("Title", "Title", r => r.Title ?? ""),
        new("Artist", "Artist", r => r.Artist ?? ""),
        new("AlbumArtist", "Album Artist", r => r.AlbumArtist ?? ""),
        new("Album", "Album", r => r.Album ?? ""),
        new("Track", "Track", r => Num(r.TrackNumber), r => r.TrackNumber ?? -1),
        new("TrackTotal", "Tracks", r => Num(r.TrackTotal), r => r.TrackTotal ?? -1),
        new("Disc", "Disc", r => Num(r.DiscNumber), r => r.DiscNumber ?? -1),
        new("DiscTotal", "Discs", r => Num(r.DiscTotal), r => r.DiscTotal ?? -1),
        new("Date", "Date", r => r.ReleaseDate ?? ""),
        new("Codec", "Codec", r => r.CodecName ?? ""),
        new("Type", "Type", r => r.CodecType.ToString()),
        new("SampleRate", "Sample Rate", r => r.SampleRate == 0 ? "" : $"{r.SampleRate:N0} Hz", r => r.SampleRate),
        new("Bits", "Bits", r => r.BitsPerSample == 0 ? "" : r.BitsPerSample.ToString(), r => r.BitsPerSample),
        new("Bitrate", "Bitrate", r => r.AverageBitRate == 0 ? "" : $"{r.AverageBitRate:N0} kbps", r => r.AverageBitRate),
        new("Channels", "Channels", r => r.Channels == 0 ? "" : r.Channels.ToString(), r => r.Channels),
        new("Duration", "Duration", r => Duration(r.DurationInSeconds), r => r.DurationInSeconds),
        new("Modified", "Date Modified", r => r.LastWriteTime == default ? "" : r.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), r => r.LastWriteTime),
        new("Path", "Path", r => r.Path),
    ];

    /// <summary>Columns shown by default before the user customizes.</summary>
    public static readonly IReadOnlySet<string> DefaultVisible =
        new HashSet<string> { "Title", "Artist", "Album", "Track", "Codec", "Duration", "Path" };

    private static readonly Dictionary<string, DetailsColumn> ByKey = All.ToDictionary(c => c.Key);

    public static DetailsColumn Get(string key) => ByKey[key];

    private static string Num(int? n) => n?.ToString() ?? "";

    private static string Duration(int seconds)
    {
        if (seconds <= 0)
            return "";
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }
}

/// <summary>
/// A grid row wrapping a <see cref="TrackRecord"/>. Column values are exposed via a string indexer
/// (so a DataGridTextColumn can bind "[Key]"), and a precomputed <see cref="SearchText"/> holds the
/// concatenation of the visible columns for fast realtime filtering.
/// </summary>
public sealed class DetailsRow
{
    private readonly TrackRecord _record;

    public DetailsRow(TrackRecord record) => _record = record;

    public string Path => _record.Path;

    public string this[string key] => DetailsColumns.Get(key).Get(_record);

    /// <summary>Typed value for sorting, or null to fall back to the displayed string.</summary>
    public IComparable? SortValue(string key) => DetailsColumns.Get(key).SortKey?.Invoke(_record);

    public string SearchText { get; private set; } = "";

    /// <summary>Recompute the search text from the currently visible columns.</summary>
    public void RebuildSearchText(IReadOnlyList<string> visibleKeys)
    {
        SearchText = string.Join('\n', visibleKeys.Select(k => this[k]));
    }
}

/// <summary>
/// Sorts <see cref="DetailsRow"/>s by one column: by the column's typed <c>SortKey</c> when present
/// (numeric/date magnitude), otherwise a culture-insensitive string compare of the displayed text.
/// Assigned to a DataGrid column's CustomSortComparer.
/// </summary>
public sealed class DetailsRowComparer : IComparer
{
    private readonly string _key;
    public DetailsRowComparer(string key) => _key = key;

    public int Compare(object? x, object? y)
    {
        if (x is not DetailsRow a || y is not DetailsRow b)
            return 0;

        var ka = a.SortValue(_key);
        var kb = b.SortValue(_key);
        if (ka is not null && kb is not null)
            return ka.CompareTo(kb);

        return string.Compare(a[_key], b[_key], StringComparison.CurrentCultureIgnoreCase);
    }
}
