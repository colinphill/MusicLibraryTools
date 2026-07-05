using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>
/// An immutable snapshot of one music file's metadata, projected off the synchronous
/// <see cref="IMediaFile"/> objects so the rest of the app never touches the (non-thread-safe)
/// parser types directly. Built on a background thread; safe to read from the UI thread.
/// </summary>
public sealed record MediaFileModel
{
    public required string Path { get; init; }

    // The primary tag container's common fields (first tag when a file has several).
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public string? ReleaseDate { get; init; }

    /// <summary>The tag format of the primary container, e.g. "ID3v2.4", "VorbisComment", "MP4".</summary>
    public string? TagType { get; init; }

    /// <summary>Whether the primary tag container advertises write support.</summary>
    public bool IsWritable { get; init; }

    /// <summary>Every strongly-typed <see cref="TagFields"/> value present on the primary tag.</summary>
    public IReadOnlyList<TagFieldValue> KnownFields { get; init; } = [];

    /// <summary>Raw key/value text frames on the primary tag (format-native keys).</summary>
    public IReadOnlyList<TextField> TextFields { get; init; } = [];

    /// <summary>Embedded artwork (may be empty).</summary>
    public IReadOnlyList<ArtworkModel> Artwork { get; init; } = [];

    /// <summary>Audio codec properties (first codec).</summary>
    public CodecModel? Codec { get; init; }
}

public sealed record TagFieldValue(TagFields Field, string Value);

public sealed record TextField(string Key, string Value);

public sealed record ArtworkModel
{
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? ImageType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Size { get; init; }
    public string? Hash { get; init; }
    public required byte[] Data { get; init; }
}

public sealed record CodecModel
{
    public string? CodecName { get; init; }
    public CodecType CodecType { get; init; }
    public uint AverageBitrate { get; init; }
    public uint MaxBitrate { get; init; }
    public uint BitsPerSample { get; init; }
    public uint Samplerate { get; init; }
    public uint Channels { get; init; }
    public uint DurationInSeconds { get; init; }
}
