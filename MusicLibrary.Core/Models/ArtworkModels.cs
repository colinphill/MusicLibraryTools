using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>One image to embed: its picture type, MIME type, and bytes.</summary>
public sealed record ArtworkInput(ID3v2Util.APICType Type, string MimeType, byte[] Data);

/// <summary>An image prepared (encoded/resized) for embedding.</summary>
public sealed record PreparedImage(byte[] Data, string MimeType, int Width, int Height);

/// <summary>Result of an artwork operation (replace/scrub/remove).</summary>
public sealed record ArtworkOpResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Size { get; init; }

    public static ArtworkOpResult Fail(string error) => new() { Success = false, Error = error };
}
