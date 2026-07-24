using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Creates and applies bounded-memory reverse deltas. A reverse delta uses the post-edit file as
/// its base and reconstructs the exact pre-edit bytes.
/// </summary>
public interface IReverseDeltaService
{
    Task<ReverseDeltaDescriptor> CreateAsync(
        Stream original,
        Stream postEdit,
        Stream deltaOutput,
        ReverseDeltaFileMetadata originalMetadata,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> CreateFileAsync(
        string originalPath,
        string postEditPath,
        string deltaPath,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> InspectAsync(
        Stream delta,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> InspectFileAsync(
        string deltaPath,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> ValidateBaseAsync(
        Stream delta,
        Stream postEdit,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> ValidateBaseFileAsync(
        string deltaPath,
        string postEditPath,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> ValidateAsync(
        Stream delta,
        Stream postEdit,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> ValidateFileAsync(
        string deltaPath,
        string postEditPath,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> RestoreAsync(
        Stream delta,
        Stream postEdit,
        Stream originalOutput,
        CancellationToken ct = default);

    Task<ReverseDeltaDescriptor> RestoreFileAsync(
        string deltaPath,
        string postEditPath,
        string originalOutputPath,
        CancellationToken ct = default);
}

/// <summary>
/// Version-one reverse-delta encoder/decoder. Inputs are never loaded wholesale; memory is bounded
/// by the chunk buffers and the capped post-edit chunk index.
/// </summary>
public sealed class ReverseDeltaService : IReverseDeltaService
{
    public const int CurrentFormatVersion = 1;
    public const int EncodedHeaderLength = 160;
    public const int MinimumChunkBytes = 16 * 1024;
    public const int TargetChunkBytes = 64 * 1024;
    public const int MaximumChunkBytes = 256 * 1024;
    public const int DefaultMaximumIndexedChunks = 1_048_576;

    private const byte CopyCommand = 1;
    private const byte LiteralCommand = 2;
    private const byte EndCommand = byte.MaxValue;
    private const int HashLength = 32;
    private const int IoBufferSize = 64 * 1024;
    private const int PayloadLengthOffset = 56;
    private const int OriginalHashOffset = 64;
    private const int PostEditHashOffset = 96;
    private const int PayloadHashOffset = 128;
    private const FileAttributes RestorableAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Normal |
        FileAttributes.Temporary |
        FileAttributes.Offline |
        FileAttributes.NotContentIndexed;

    private static readonly byte[] Magic = "MLMRDEL1"u8.ToArray();
    private static readonly ulong[] GearTable = CreateGearTable();

    private readonly int _maximumIndexedChunks;

    public ReverseDeltaService(int maximumIndexedChunks = DefaultMaximumIndexedChunks)
    {
        if (maximumIndexedChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIndexedChunks));
        _maximumIndexedChunks = maximumIndexedChunks;
    }

    public async Task<ReverseDeltaDescriptor> CreateAsync(
        Stream original,
        Stream postEdit,
        Stream deltaOutput,
        ReverseDeltaFileMetadata originalMetadata,
        CancellationToken ct = default)
    {
        ValidateReadableSeekable(original, nameof(original));
        ValidateReadableSeekable(postEdit, nameof(postEdit));
        ArgumentNullException.ThrowIfNull(deltaOutput);
        ArgumentNullException.ThrowIfNull(originalMetadata);
        if (!deltaOutput.CanWrite || !deltaOutput.CanSeek)
            throw new ArgumentException(
                "The delta output must be writable and seekable.", nameof(deltaOutput));
        if (deltaOutput.Length != 0 || deltaOutput.Position != 0)
            throw new ArgumentException(
                "The delta output must be an empty stream positioned at zero.", nameof(deltaOutput));
        if (ReferenceEquals(original, postEdit) ||
            ReferenceEquals(original, deltaOutput) ||
            ReferenceEquals(postEdit, deltaOutput))
        {
            throw new ArgumentException("Reverse-delta streams must be distinct.");
        }

        ct.ThrowIfCancellationRequested();
        long originalLength = original.Length;
        long postEditLength = postEdit.Length;
        if (originalLength < 0 || postEditLength < 0)
            throw new InvalidDataException("A reverse-delta input reported a negative length.");

        var postIndex = new Dictionary<ChunkKey, ChunkLocation>(
            (int)Math.Min(_maximumIndexedChunks,
                Math.Max(1, Math.Min(int.MaxValue, postEditLength / TargetChunkBytes + 1))));
        byte[] postHash;
        var chunkBuffer = new byte[MaximumChunkBytes];
        using (var postHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var reader = new ContentDefinedChunkReader(postEdit);
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int length = await reader.ReadChunkAsync(chunkBuffer, ct).ConfigureAwait(false);
                if (length == 0)
                    break;

                postHasher.AppendData(chunkBuffer, 0, length);
                if (postIndex.Count < _maximumIndexedChunks)
                {
                    ChunkKey key = ChunkKey.Create(chunkBuffer.AsSpan(0, length));
                    postIndex.TryAdd(key, new ChunkLocation(offset, length));
                }

                offset = checked(offset + length);
            }

            if (offset != postEditLength || postEdit.Length != postEditLength)
                throw new IOException("The post-edit input changed while the delta was created.");
            postHash = postHasher.GetHashAndReset();
        }

        await WriteZerosAsync(deltaOutput, EncodedHeaderLength, ct).ConfigureAwait(false);
        long bodyStart = deltaOutput.Position;
        byte[] originalHash;
        byte[] payloadHash;
        long payloadLength;

        using (var payloadHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var hashingOutput = new HashingWriteStream(deltaOutput, payloadHasher))
            await using (var compressor = new BrotliStream(
                hashingOutput, CompressionLevel.Optimal, leaveOpen: true))
            {
                var commandWriter = new CommandWriter(compressor);
                var reader = new ContentDefinedChunkReader(original);
                var comparisonBuffer = new byte[IoBufferSize];
                long originalOffset = 0;
                using var originalHasher =
                    IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int length = await reader.ReadChunkAsync(chunkBuffer, ct).ConfigureAwait(false);
                    if (length == 0)
                        break;

                    originalHasher.AppendData(chunkBuffer, 0, length);
                    ChunkKey key = ChunkKey.Create(chunkBuffer.AsSpan(0, length));
                    if (postIndex.TryGetValue(key, out ChunkLocation candidate) &&
                        candidate.Length == length &&
                        await ContentEqualsAsync(
                            postEdit, candidate.Offset, chunkBuffer.AsMemory(0, length),
                            comparisonBuffer, ct).ConfigureAwait(false))
                    {
                        await commandWriter.WriteCopyAsync(
                            candidate.Offset, length, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await commandWriter.WriteLiteralAsync(
                            chunkBuffer.AsMemory(0, length), ct).ConfigureAwait(false);
                    }

                    originalOffset = checked(originalOffset + length);
                }

                if (originalOffset != originalLength || original.Length != originalLength)
                    throw new IOException("The original input changed while the delta was created.");
                originalHash = originalHasher.GetHashAndReset();
                await commandWriter.WriteEndAsync(ct).ConfigureAwait(false);
            }

            payloadLength = checked(deltaOutput.Position - bodyStart);
            payloadHash = payloadHasher.GetHashAndReset();
        }

        var descriptor = new ReverseDeltaDescriptor(
            CurrentFormatVersion,
            EncodedHeaderLength,
            originalLength,
            postEditLength,
            Convert.ToHexString(originalHash),
            Convert.ToHexString(postHash),
            NormalizeUtc(originalMetadata.LastWriteTimeUtc),
            originalMetadata.Attributes,
            payloadLength,
            Convert.ToHexString(payloadHash),
            new ReverseDeltaChunkingParameters(
                MinimumChunkBytes, TargetChunkBytes, MaximumChunkBytes));

        long end = deltaOutput.Position;
        deltaOutput.Position = 0;
        await WriteHeaderAsync(deltaOutput, descriptor, ct).ConfigureAwait(false);
        deltaOutput.Position = end;
        return descriptor;
    }

    public async Task<ReverseDeltaDescriptor> CreateFileAsync(
        string originalPath,
        string postEditPath,
        string deltaPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(postEditPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPath);
        string fullOriginal = Path.GetFullPath(originalPath);
        string fullPostEdit = Path.GetFullPath(postEditPath);
        string fullDelta = Path.GetFullPath(deltaPath);
        if (File.Exists(fullDelta))
            throw new IOException($"The reverse-delta path already exists: '{fullDelta}'.");

        var metadata = new ReverseDeltaFileMetadata(
            File.GetLastWriteTimeUtc(fullOriginal),
            File.GetAttributes(fullOriginal));
        try
        {
            await using var original = new FileStream(
                fullOriginal, FileMode.Open, FileAccess.Read, FileShare.Read,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var postEdit = new FileStream(
                fullPostEdit, FileMode.Open, FileAccess.Read, FileShare.Read,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using var output = new FileStream(
                fullDelta, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
            ReverseDeltaDescriptor descriptor = await CreateAsync(
                original, postEdit, output, metadata, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return descriptor;
        }
        catch
        {
            TryDelete(fullDelta);
            throw;
        }
    }

    public async Task<ReverseDeltaDescriptor> InspectAsync(
        Stream delta,
        CancellationToken ct = default)
    {
        ValidateReadableSeekable(delta, nameof(delta));
        ct.ThrowIfCancellationRequested();
        delta.Position = 0;
        ReverseDeltaDescriptor descriptor =
            await ReadHeaderAsync(delta, ct).ConfigureAwait(false);
        long expectedLength = checked(
            (long)descriptor.HeaderLength + descriptor.CompressedPayloadLength);
        if (delta.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The reverse delta has a truncated payload or unexpected trailing data.");
        }

        byte[] actualPayloadHash = await HashRangeAsync(
            delta, descriptor.HeaderLength, descriptor.CompressedPayloadLength, ct)
            .ConfigureAwait(false);
        byte[] expectedPayloadHash = Convert.FromHexString(descriptor.PayloadSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualPayloadHash, expectedPayloadHash))
            throw new InvalidDataException("The reverse-delta payload checksum is invalid.");
        return descriptor;
    }

    public async Task<ReverseDeltaDescriptor> InspectFileAsync(
        string deltaPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPath);
        await using var delta = new FileStream(
            Path.GetFullPath(deltaPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await InspectAsync(delta, ct).ConfigureAwait(false);
    }

    public async Task<ReverseDeltaDescriptor> ValidateBaseAsync(
        Stream delta,
        Stream postEdit,
        CancellationToken ct = default)
    {
        ValidateReadableSeekable(postEdit, nameof(postEdit));
        ReverseDeltaDescriptor descriptor =
            await InspectAsync(delta, ct).ConfigureAwait(false);
        await ValidatePostEditAsync(postEdit, descriptor, ct).ConfigureAwait(false);
        return descriptor;
    }

    public async Task<ReverseDeltaDescriptor> ValidateBaseFileAsync(
        string deltaPath,
        string postEditPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(postEditPath);
        await using var delta = new FileStream(
            Path.GetFullPath(deltaPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var postEdit = new FileStream(
            Path.GetFullPath(postEditPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ValidateBaseAsync(delta, postEdit, ct).ConfigureAwait(false);
    }

    public Task<ReverseDeltaDescriptor> ValidateAsync(
        Stream delta,
        Stream postEdit,
        CancellationToken ct = default) =>
        RestoreAsync(delta, postEdit, Stream.Null, ct);

    public async Task<ReverseDeltaDescriptor> ValidateFileAsync(
        string deltaPath,
        string postEditPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(postEditPath);
        await using var delta = new FileStream(
            Path.GetFullPath(deltaPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var postEdit = new FileStream(
            Path.GetFullPath(postEditPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            IoBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ValidateAsync(delta, postEdit, ct).ConfigureAwait(false);
    }

    public async Task<ReverseDeltaDescriptor> RestoreAsync(
        Stream delta,
        Stream postEdit,
        Stream originalOutput,
        CancellationToken ct = default)
    {
        ValidateReadableSeekable(delta, nameof(delta));
        ValidateReadableSeekable(postEdit, nameof(postEdit));
        ArgumentNullException.ThrowIfNull(originalOutput);
        if (!originalOutput.CanWrite)
            throw new ArgumentException("The restore output must be writable.", nameof(originalOutput));
        if (ReferenceEquals(delta, postEdit) ||
            ReferenceEquals(delta, originalOutput) ||
            ReferenceEquals(postEdit, originalOutput))
        {
            throw new ArgumentException("Reverse-delta streams must be distinct.");
        }

        ReverseDeltaDescriptor descriptor =
            await InspectAsync(delta, ct).ConfigureAwait(false);
        await ValidatePostEditAsync(postEdit, descriptor, ct).ConfigureAwait(false);
        delta.Position = descriptor.HeaderLength;

        await using var payload = new BoundedReadStream(
            delta, descriptor.CompressedPayloadLength);
        await using var decompressor = new BrotliStream(
            payload, CompressionMode.Decompress, leaveOpen: true);
        using var outputHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var primitive = new byte[sizeof(long) + sizeof(int)];
        var transfer = new byte[IoBufferSize];
        long written = 0;
        long commandCount = 0;
        long commandLimit = checked(descriptor.OriginalLength / MinimumChunkBytes + 2);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int command = await ReadByteAsync(decompressor, primitive, ct)
                .ConfigureAwait(false);
            if (command < 0)
                throw new InvalidDataException("The reverse-delta command stream is truncated.");
            if (command == EndCommand)
                break;
            if (++commandCount > commandLimit)
                throw new InvalidDataException("The reverse delta contains too many commands.");

            switch (command)
            {
                case CopyCommand:
                {
                    await ReadExactlyAsync(
                        decompressor, primitive.AsMemory(0, sizeof(long) + sizeof(int)), ct)
                        .ConfigureAwait(false);
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(
                        primitive.AsSpan(0, sizeof(long)));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(
                        primitive.AsSpan(sizeof(long), sizeof(int)));
                    ValidateCommandLength(length);
                    if (offset < 0 || offset > descriptor.PostEditLength - length)
                        throw new InvalidDataException(
                            "A reverse-delta copy command is outside the post-edit file.");
                    EnsureOutputLimit(written, length, descriptor.OriginalLength);
                    postEdit.Position = offset;
                    await CopyExactlyAsync(
                        postEdit, originalOutput, outputHasher, transfer, length, ct)
                        .ConfigureAwait(false);
                    written = checked(written + length);
                    break;
                }
                case LiteralCommand:
                {
                    await ReadExactlyAsync(
                        decompressor, primitive.AsMemory(0, sizeof(int)), ct)
                        .ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(
                        primitive.AsSpan(0, sizeof(int)));
                    ValidateCommandLength(length);
                    EnsureOutputLimit(written, length, descriptor.OriginalLength);
                    await CopyExactlyAsync(
                        decompressor, originalOutput, outputHasher, transfer, length, ct)
                        .ConfigureAwait(false);
                    written = checked(written + length);
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"The reverse delta contains unknown command 0x{command:X2}.");
            }
        }

        if (await ReadByteAsync(decompressor, primitive, ct).ConfigureAwait(false) >= 0)
            throw new InvalidDataException(
                "The reverse delta contains data after its end command.");
        if (written != descriptor.OriginalLength)
            throw new InvalidDataException(
                "The reverse delta did not reconstruct the declared original length.");

        byte[] actualOriginalHash = outputHasher.GetHashAndReset();
        byte[] expectedOriginalHash = Convert.FromHexString(descriptor.OriginalSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualOriginalHash, expectedOriginalHash))
            throw new InvalidDataException(
                "The reconstructed original SHA-256 hash is invalid.");
        return descriptor;
    }

    public async Task<ReverseDeltaDescriptor> RestoreFileAsync(
        string deltaPath,
        string postEditPath,
        string originalOutputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(postEditPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalOutputPath);
        string fullOutput = Path.GetFullPath(originalOutputPath);
        if (File.Exists(fullOutput))
            throw new IOException($"The restore output path already exists: '{fullOutput}'.");

        ReverseDeltaDescriptor descriptor;
        try
        {
            await using var delta = new FileStream(
                Path.GetFullPath(deltaPath), FileMode.Open, FileAccess.Read, FileShare.Read,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var postEdit = new FileStream(
                Path.GetFullPath(postEditPath), FileMode.Open, FileAccess.Read, FileShare.Read,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using (var output = new FileStream(
                fullOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                IoBufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                descriptor = await RestoreAsync(delta, postEdit, output, ct)
                    .ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(fullOutput, descriptor.OriginalLastWriteTimeUtc);
            File.SetAttributes(
                fullOutput, NormalizeRestorableAttributes(descriptor.OriginalAttributes));
            return descriptor;
        }
        catch
        {
            TryDelete(fullOutput);
            throw;
        }
    }

    /// <summary>
    /// Returns whether the compact payload and its journal entry retain fewer bytes than a full
    /// original and its journal entry.
    /// </summary>
    public static bool IsAdaptivePayloadBeneficial(
        ReverseDeltaDescriptor descriptor,
        long originalLength,
        long compactJournalOverheadBytes = 0,
        long fullJournalOverheadBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        ArgumentOutOfRangeException.ThrowIfNegative(compactJournalOverheadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(fullJournalOverheadBytes);
        return checked(descriptor.RetainedBytes + compactJournalOverheadBytes) <
               checked(originalLength + fullJournalOverheadBytes);
    }

    /// <summary>
    /// Conservative upper bound for the temporary encoded payload. The command stream can add
    /// one literal prefix per minimum-sized chunk; summing Brotli's bound over fixed-size
    /// segments is at least as conservative as encoding the same bytes as one continuous stream.
    /// </summary>
    public static long MaximumEncodedLength(long originalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        long chunkCount = originalLength == 0
            ? 0
            : checked((originalLength - 1) / MinimumChunkBytes + 1);
        // Literal commands dominate for every minimum-sized chunk. Only the final short chunk can
        // be larger as a 13-byte copy command than as a five-byte prefix plus its literal bytes;
        // reserve that maximum eight-byte difference as well.
        long commandBytes = checked(
            originalLength +
            checked(chunkCount * (1 + sizeof(int))) +
            1 +
            (originalLength == 0 ? 0 : 8));
        const int segmentBytes = 64 * 1024 * 1024;
        long compressedBound = 0;
        long remaining = commandBytes;
        while (remaining > 0)
        {
            int current = (int)Math.Min(segmentBytes, remaining);
            compressedBound = checked(
                compressedBound +
                BrotliEncoder.GetMaxCompressedLength(current));
            remaining -= current;
        }
        return checked(EncodedHeaderLength + compressedBound);
    }

    private static async Task ValidatePostEditAsync(
        Stream postEdit,
        ReverseDeltaDescriptor descriptor,
        CancellationToken ct)
    {
        if (postEdit.Length != descriptor.PostEditLength)
            throw new InvalidOperationException(
                "The post-edit file length no longer matches the reverse-delta base.");
        byte[] actualHash = await HashRangeAsync(
            postEdit, 0, descriptor.PostEditLength, ct).ConfigureAwait(false);
        byte[] expectedHash = Convert.FromHexString(descriptor.PostEditSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidOperationException(
                "The post-edit file changed after the reverse delta was created.");
        }
    }

    private static async Task<ReverseDeltaDescriptor> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct)
    {
        if (stream.Length < EncodedHeaderLength)
            throw new InvalidDataException("The reverse-delta header is truncated.");
        byte[] header = new byte[EncodedHeaderLength];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The reverse-delta magic is invalid.");

        int version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        if (version != CurrentFormatVersion)
            throw new NotSupportedException(
                $"Reverse-delta format version {version} is not supported.");
        if (headerLength != EncodedHeaderLength)
            throw new InvalidDataException("The reverse-delta header length is invalid.");
        if (flags != 0)
            throw new NotSupportedException("The reverse delta uses unsupported format flags.");

        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
        long postEditLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24, 8));
        long lastWriteTicks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32, 8));
        var attributes = (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(40, 4));
        int minimum = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(44, 4));
        int target = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48, 4));
        int maximum = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(52, 4));
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(
            PayloadLengthOffset, 8));

        if (originalLength < 0 || postEditLength < 0 || payloadLength <= 0)
            throw new InvalidDataException("The reverse-delta header contains invalid lengths.");
        if (minimum != MinimumChunkBytes ||
            target != TargetChunkBytes ||
            maximum != MaximumChunkBytes)
        {
            throw new InvalidDataException(
                "The reverse-delta chunking parameters are invalid.");
        }
        if (lastWriteTicks < DateTime.MinValue.Ticks ||
            lastWriteTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException(
                "The reverse-delta last-write timestamp is invalid.");
        }
        try
        {
            _ = checked((long)headerLength + payloadLength);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "The reverse-delta payload length is invalid.", ex);
        }

        return new ReverseDeltaDescriptor(
            version,
            headerLength,
            originalLength,
            postEditLength,
            Convert.ToHexString(header.AsSpan(OriginalHashOffset, HashLength)),
            Convert.ToHexString(header.AsSpan(PostEditHashOffset, HashLength)),
            new DateTime(lastWriteTicks, DateTimeKind.Utc),
            attributes,
            payloadLength,
            Convert.ToHexString(header.AsSpan(PayloadHashOffset, HashLength)),
            new ReverseDeltaChunkingParameters(minimum, target, maximum));
    }

    private static async Task WriteHeaderAsync(
        Stream stream,
        ReverseDeltaDescriptor descriptor,
        CancellationToken ct)
    {
        byte[] header = new byte[EncodedHeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(8, 2), checked((ushort)descriptor.FormatVersion));
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(10, 2), checked((ushort)descriptor.HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(16, 8), descriptor.OriginalLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(24, 8), descriptor.PostEditLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(32, 8), descriptor.OriginalLastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(40, 4), (int)descriptor.OriginalAttributes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(44, 4), descriptor.Chunking.MinimumBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(48, 4), descriptor.Chunking.TargetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(52, 4), descriptor.Chunking.MaximumBytes);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(PayloadLengthOffset, 8), descriptor.CompressedPayloadLength);
        Convert.FromHexString(descriptor.OriginalSha256)
            .CopyTo(header, OriginalHashOffset);
        Convert.FromHexString(descriptor.PostEditSha256)
            .CopyTo(header, PostEditHashOffset);
        Convert.FromHexString(descriptor.PayloadSha256)
            .CopyTo(header, PayloadHashOffset);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> HashRangeAsync(
        Stream stream,
        long offset,
        long length,
        CancellationToken ct)
    {
        stream.Position = offset;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[IoBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, requested), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("A hashed stream ended unexpectedly.");
            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hasher.GetHashAndReset();
    }

    private static async Task<bool> ContentEqualsAsync(
        Stream candidateStream,
        long candidateOffset,
        ReadOnlyMemory<byte> expected,
        byte[] comparisonBuffer,
        CancellationToken ct)
    {
        candidateStream.Position = candidateOffset;
        int compared = 0;
        while (compared < expected.Length)
        {
            ct.ThrowIfCancellationRequested();
            int requested = Math.Min(comparisonBuffer.Length, expected.Length - compared);
            int read = await candidateStream.ReadAsync(
                comparisonBuffer.AsMemory(0, requested), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            if (!comparisonBuffer.AsSpan(0, read)
                .SequenceEqual(expected.Span.Slice(compared, read)))
            {
                return false;
            }
            compared += read;
        }
        return true;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        IncrementalHash outputHasher,
        byte[] buffer,
        int length,
        CancellationToken ct)
    {
        int remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int requested = Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(
                buffer.AsMemory(0, requested), ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException(
                    "A reverse-delta command source ended unexpectedly.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            outputHasher.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer[readTotal..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("The reverse delta is truncated.");
            readTotal += read;
        }
    }

    private static async Task<int> ReadByteAsync(
        Stream stream,
        byte[] oneByte,
        CancellationToken ct)
    {
        int read = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct)
            .ConfigureAwait(false);
        return read == 0 ? -1 : oneByte[0];
    }

    private static async Task WriteZerosAsync(
        Stream stream,
        int count,
        CancellationToken ct)
    {
        byte[] zeros = new byte[count];
        await stream.WriteAsync(zeros, ct).ConfigureAwait(false);
    }

    private static void ValidateReadableSeekable(Stream stream, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(stream, parameterName);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException(
                "The stream must be readable and seekable.", parameterName);
    }

    private static void ValidateCommandLength(int length)
    {
        if (length <= 0 || length > MaximumChunkBytes)
            throw new InvalidDataException(
                "A reverse-delta command has an invalid length.");
    }

    private static void EnsureOutputLimit(long written, int next, long declaredLength)
    {
        if (written > declaredLength - next)
            throw new InvalidDataException(
                "A reverse-delta command exceeds the declared original length.");
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static FileAttributes NormalizeRestorableAttributes(FileAttributes attributes)
    {
        FileAttributes normalized = attributes & RestorableAttributes;
        if (normalized == 0)
            return FileAttributes.Normal;
        if (normalized != FileAttributes.Normal)
            normalized &= ~FileAttributes.Normal;
        return normalized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // A partial durable artifact is preferable to masking the original failure.
        }
    }

    private static ulong[] CreateGearTable()
    {
        var result = new ulong[256];
        ulong state = 0xD6E8FEB86659FD93UL;
        for (int i = 0; i < result.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            result[i] = value ^ (value >> 31);
        }
        return result;
    }

    private readonly record struct ChunkLocation(long Offset, int Length);

    private readonly record struct ChunkKey(ulong A, ulong B, ulong C, ulong D, int Length)
    {
        public static ChunkKey Create(ReadOnlySpan<byte> content)
        {
            Span<byte> hash = stackalloc byte[HashLength];
            SHA256.HashData(content, hash);
            return new ChunkKey(
                BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]),
                content.Length);
        }
    }

    private sealed class ContentDefinedChunkReader
    {
        private readonly Stream _input;
        private readonly byte[] _readBuffer = new byte[IoBufferSize];
        private int _readOffset;
        private int _readLength;

        public ContentDefinedChunkReader(Stream input)
        {
            _input = input;
            _input.Position = 0;
        }

        public async Task<int> ReadChunkAsync(
            byte[] chunkBuffer,
            CancellationToken ct)
        {
            int length = 0;
            ulong rolling = 0;
            while (length < MaximumChunkBytes)
            {
                if (_readOffset == _readLength)
                {
                    ct.ThrowIfCancellationRequested();
                    _readLength = await _input.ReadAsync(
                        _readBuffer, ct).ConfigureAwait(false);
                    _readOffset = 0;
                    if (_readLength == 0)
                        break;
                }

                byte value = _readBuffer[_readOffset++];
                chunkBuffer[length++] = value;
                rolling = unchecked((rolling << 1) + GearTable[value]);
                if (length >= MinimumChunkBytes &&
                    ((rolling & (TargetChunkBytes - 1)) == 0 ||
                     length == MaximumChunkBytes))
                {
                    break;
                }
            }
            return length;
        }
    }

    private sealed class CommandWriter
    {
        private readonly Stream _stream;
        private readonly byte[] _prefix = new byte[1 + sizeof(long) + sizeof(int)];

        public CommandWriter(Stream stream) => _stream = stream;

        public async Task WriteCopyAsync(
            long offset,
            int length,
            CancellationToken ct)
        {
            _prefix[0] = CopyCommand;
            BinaryPrimitives.WriteInt64LittleEndian(
                _prefix.AsSpan(1, sizeof(long)), offset);
            BinaryPrimitives.WriteInt32LittleEndian(
                _prefix.AsSpan(1 + sizeof(long), sizeof(int)), length);
            await _stream.WriteAsync(_prefix, ct).ConfigureAwait(false);
        }

        public async Task WriteLiteralAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken ct)
        {
            _prefix[0] = LiteralCommand;
            BinaryPrimitives.WriteInt32LittleEndian(
                _prefix.AsSpan(1, sizeof(int)), content.Length);
            await _stream.WriteAsync(
                _prefix.AsMemory(0, 1 + sizeof(int)), ct).ConfigureAwait(false);
            await _stream.WriteAsync(content, ct).ConfigureAwait(false);
        }

        public Task WriteEndAsync(CancellationToken ct)
        {
            _prefix[0] = EndCommand;
            return _stream.WriteAsync(_prefix.AsMemory(0, 1), ct).AsTask();
        }
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;

        public HashingWriteStream(Stream inner, IncrementalHash hash)
        {
            _inner = inner;
            _hash = hash;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _hash.AppendData(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _hash.AppendData(buffer.Span);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return WriteArrayAsync(buffer, offset, count, cancellationToken);
        }

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct)
        {
            await _inner.WriteAsync(
                buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
            _hash.AppendData(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns both the underlying stream and hash.
            base.Dispose(disposing);
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int requested = (int)Math.Min(count, _remaining);
            if (requested == 0)
                return 0;
            int read = _inner.Read(buffer, offset, requested);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int requested = (int)Math.Min(buffer.Length, _remaining);
            if (requested == 0)
                return 0;
            int read = await _inner.ReadAsync(
                buffer[..requested], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer, offset, count, cancellationToken);

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct)
        {
            int requested = (int)Math.Min(count, _remaining);
            if (requested == 0)
                return 0;
            int read = await _inner.ReadAsync(
                buffer.AsMemory(offset, requested), ct).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns the underlying delta stream.
            base.Dispose(disposing);
        }
    }
}
