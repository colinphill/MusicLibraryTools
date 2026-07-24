namespace MusicLibrary.Core.Models;

/// <summary>
/// File metadata retained with a reverse delta so an exact metadata restore can accompany the
/// byte-for-byte content restore.
/// </summary>
public sealed record ReverseDeltaFileMetadata(
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes);

/// <summary>
/// The content-defined chunking parameters encoded into a reverse-delta payload.
/// </summary>
public sealed record ReverseDeltaChunkingParameters(
    int MinimumBytes,
    int TargetBytes,
    int MaximumBytes);

/// <summary>
/// Validated metadata describing a versioned reverse-delta file.
/// </summary>
public sealed record ReverseDeltaDescriptor(
    int FormatVersion,
    int HeaderLength,
    long OriginalLength,
    long PostEditLength,
    string OriginalSha256,
    string PostEditSha256,
    DateTime OriginalLastWriteTimeUtc,
    FileAttributes OriginalAttributes,
    long CompressedPayloadLength,
    string PayloadSha256,
    ReverseDeltaChunkingParameters Chunking)
{
    /// <summary>Total bytes retained by the encoded reverse delta.</summary>
    public long RetainedBytes => checked(HeaderLength + CompressedPayloadLength);
}
