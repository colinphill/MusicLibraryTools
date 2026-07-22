using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>How the browser tree is grouped.</summary>
public enum LibraryGrouping
{
    /// <summary>AlbumArtist → Album → Track.</summary>
    AlbumArtist,
    /// <summary>Album → Track (albums flattened across artists).</summary>
    Album,
}

/// <summary>
/// An immutable projection of the SQLite metadata cache into a browser tree. Roots are either
/// <see cref="ArtistGroup"/> (AlbumArtist grouping) or <see cref="AlbumGroup"/> (Album grouping).
/// Built off the UI thread from a <c>MetadataCache</c>.
/// </summary>
public sealed record LibrarySnapshot
{
    public IReadOnlyList<object> Roots { get; init; } = [];
    public int TotalTracks { get; init; }
    public int RootCount => Roots.Count;
}

public sealed record ArtistGroup(string Name, IReadOnlyList<AlbumGroup> Albums)
{
    public int TrackCount => Albums.Sum(a => a.Tracks.Count);
}

public sealed record AlbumGroup(string Name, IReadOnlyList<TrackItem> Tracks);

public sealed record TrackItem
{
    public required string Path { get; init; }
    public string? Title { get; init; }
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public string? Grouping { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public string? CodecName { get; init; }
    public CodecType CodecType { get; init; }
    public int DurationInSeconds { get; init; }

    /// <summary>Track display: "1-02  Title" (disc-track when multi-disc) or the filename.</summary>
    public string Display
    {
        get
        {
            var num = TrackNumber is null ? "" :
                (DiscNumber is > 1 ? $"{DiscNumber}-{TrackNumber:D2}  " : $"{TrackNumber:D2}  ");
            var name = string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileName(Path) : Title;
            return num + name;
        }
    }
}
