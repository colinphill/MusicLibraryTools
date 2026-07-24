using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ReverseDeltaServiceTests
{
    private static readonly ReverseDeltaFileMetadata Metadata = new(
        new DateTime(638800000000000000, DateTimeKind.Utc),
        FileAttributes.Archive);

    public static TheoryData<string> MutationCases => new()
    {
        "insert-at-start",
        "insert-at-end",
        "delete-at-start",
        "delete-at-end",
        "replace-middle",
        "replace-both-ends",
    };

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task CreateAndRestoreHandlesShiftedAndReplacedContent(string mutation)
    {
        byte[] original = CreateDeterministicBytes(2 * 1024 * 1024 + 731, 173);
        byte[] postEdit = ApplyMutation(original, mutation);
        using var originalStream = new MemoryStream(original, writable: false);
        using var postEditStream = new MemoryStream(postEdit, writable: false);
        using var delta = new MemoryStream();
        var service = new ReverseDeltaService();

        ReverseDeltaDescriptor created = await service.CreateAsync(
            originalStream, postEditStream, delta, Metadata,
            TestContext.Current.CancellationToken);

        Assert.Equal(original.LongLength, created.OriginalLength);
        Assert.Equal(postEdit.LongLength, created.PostEditLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(original)), created.OriginalSha256);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(postEdit)), created.PostEditSha256);
        Assert.Equal(ReverseDeltaService.CurrentFormatVersion, created.FormatVersion);
        Assert.Equal(ReverseDeltaService.EncodedHeaderLength, created.HeaderLength);
        Assert.True(
            delta.Length <=
                ReverseDeltaService.MaximumEncodedLength(original.LongLength));

        delta.Position = 0;
        postEditStream.Position = 0;
        using var restored = new MemoryStream();
        ReverseDeltaDescriptor applied = await service.RestoreAsync(
            delta, postEditStream, restored, TestContext.Current.CancellationToken);

        Assert.Equal(created, applied);
        Assert.Equal(original, restored.ToArray());
    }

    [Fact]
    public async Task RepeatedBlocksCanReuseAnyIdenticalPostEditChunk()
    {
        byte[] blockA = CreateDeterministicBytes(256 * 1024, 41);
        byte[] blockB = CreateDeterministicBytes(256 * 1024, 97);
        byte[] original = Join(blockA, blockB, blockA, blockB, blockA);
        byte[] postEdit = Join(blockA, blockA, blockB, blockA, blockB, blockA);
        using var originalStream = new MemoryStream(original, writable: false);
        using var postEditStream = new MemoryStream(postEdit, writable: false);
        using var delta = new MemoryStream();
        var service = new ReverseDeltaService();

        ReverseDeltaDescriptor descriptor = await service.CreateAsync(
            originalStream, postEditStream, delta, Metadata,
            TestContext.Current.CancellationToken);
        delta.Position = 0;
        postEditStream.Position = 0;
        using var restored = new MemoryStream();
        await service.RestoreAsync(
            delta, postEditStream, restored, TestContext.Current.CancellationToken);

        Assert.Equal(original, restored.ToArray());
        Assert.True(ReverseDeltaService.IsAdaptivePayloadBeneficial(
            descriptor, original.LongLength));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(16L * 1024)]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024)]
    public void MaximumEncodedLengthIsBoundedForStreamingInputs(long originalLength)
    {
        long bound = ReverseDeltaService.MaximumEncodedLength(originalLength);

        Assert.True(bound >= ReverseDeltaService.EncodedHeaderLength + originalLength);
        long allowedOverhead = Math.Max(
            1024 * 1024,
            originalLength / 100);
        Assert.True(
            bound <= ReverseDeltaService.EncodedHeaderLength +
                originalLength +
                allowedOverhead,
            $"Bound {bound:N0} exceeded the bounded overhead for {originalLength:N0} bytes.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(16 * 1024)]
    public async Task MaximumEncodedLengthCoversSmallCopyCommandPayloads(int length)
    {
        byte[] content = CreateDeterministicBytes(length, 83);
        using var original = new MemoryStream(content, writable: false);
        using var postEdit = new MemoryStream(content, writable: false);
        using var delta = new MemoryStream();

        await new ReverseDeltaService().CreateAsync(
            original,
            postEdit,
            delta,
            Metadata,
            TestContext.Current.CancellationToken);

        Assert.True(
            delta.Length <= ReverseDeltaService.MaximumEncodedLength(length),
            $"Encoded {delta.Length:N0} bytes for a {length:N0}-byte original.");
    }

    [Fact]
    public async Task CorruptedCompressedPayloadIsRejectedBeforeRestoreWrites()
    {
        byte[] original = CreateDeterministicBytes(1024 * 1024, 19);
        byte[] postEdit = (byte[])original.Clone();
        postEdit[400_000] ^= 0x7f;
        byte[] deltaBytes = await CreateDeltaAsync(original, postEdit);
        deltaBytes[^1] ^= 0x55;
        using var delta = new MemoryStream(deltaBytes, writable: false);
        using var postEditStream = new MemoryStream(postEdit, writable: false);
        using var restored = new MemoryStream();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ReverseDeltaService().RestoreAsync(
                delta, postEditStream, restored, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, restored.Length);
    }

    [Fact]
    public async Task CopyOffsetOutsidePostEditFileIsRejected()
    {
        byte[] original = CreateDeterministicBytes(1024 * 1024, 23);
        byte[] deltaBytes = await CreateDeltaAsync(original, original);
        byte[] commands = DecompressCommands(deltaBytes);
        Assert.Equal(1, commands[0]);
        BinaryPrimitives.WriteInt64LittleEndian(
            commands.AsSpan(1, sizeof(long)), original.LongLength);
        deltaBytes = ReplaceCompressedPayload(deltaBytes, commands);
        using var delta = new MemoryStream(deltaBytes, writable: false);
        using var postEdit = new MemoryStream(original, writable: false);
        using var restored = new MemoryStream();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ReverseDeltaService().RestoreAsync(
                delta, postEdit, restored, TestContext.Current.CancellationToken));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternallyChangedPostEditBaseIsRefusedWithoutWritingOutput()
    {
        byte[] original = CreateDeterministicBytes(1024 * 1024, 31);
        byte[] postEdit = (byte[])original.Clone();
        postEdit[22] ^= 0x10;
        byte[] deltaBytes = await CreateDeltaAsync(original, postEdit);
        postEdit[^23] ^= 0x20;
        using var delta = new MemoryStream(deltaBytes, writable: false);
        using var changedBase = new MemoryStream(postEdit, writable: false);
        using var restored = new MemoryStream();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ReverseDeltaService().RestoreAsync(
                    delta, changedBase, restored, TestContext.Current.CancellationToken));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, restored.Length);
    }

    [Fact]
    public async Task CancellationStopsCreationAndDoesNotProduceValidPayload()
    {
        byte[] original = CreateDeterministicBytes(4 * 1024 * 1024, 59);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var originalStream = new MemoryStream(original, writable: false);
        using var postEdit = new CancelAfterReadStream(original, cts, cancelAfterReads: 3);
        using var delta = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ReverseDeltaService().CreateAsync(
                originalStream, postEdit, delta, Metadata, cts.Token));
        delta.Position = 0;
        await Assert.ThrowsAnyAsync<Exception>(
            () => new ReverseDeltaService().InspectAsync(
                delta, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileRestorePreservesBytesTimestampAndStandardAttributes()
    {
        using var workspace = new TempDirectory();
        string originalPath = Path.Combine(workspace.Path, "original.bin");
        string postEditPath = Path.Combine(workspace.Path, "post-edit.bin");
        string deltaPath = Path.Combine(workspace.Path, "change.rdelta");
        string restoredPath = Path.Combine(workspace.Path, "restored.bin");
        byte[] original = CreateDeterministicBytes(1024 * 1024, 67);
        byte[] postEdit = (byte[])original.Clone();
        Array.Fill(postEdit, (byte)0xA5, 1234, 1500);
        await File.WriteAllBytesAsync(
            originalPath, original, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            postEditPath, postEdit, TestContext.Current.CancellationToken);
        DateTime expectedTimestamp = new(
            638800000000000000, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(originalPath, expectedTimestamp);
        File.SetAttributes(originalPath, FileAttributes.Archive);
        var service = new ReverseDeltaService();

        ReverseDeltaDescriptor created = await service.CreateFileAsync(
            originalPath, postEditPath, deltaPath, TestContext.Current.CancellationToken);
        ReverseDeltaDescriptor restored = await service.RestoreFileAsync(
            deltaPath, postEditPath, restoredPath, TestContext.Current.CancellationToken);

        Assert.Equal(created, restored);
        Assert.Equal(
            original,
            await File.ReadAllBytesAsync(restoredPath, TestContext.Current.CancellationToken));
        Assert.Equal(created.OriginalLastWriteTimeUtc, File.GetLastWriteTimeUtc(restoredPath));
        Assert.Equal(
            created.OriginalAttributes & FileAttributes.Archive,
            File.GetAttributes(restoredPath) & FileAttributes.Archive);
    }

    [Fact]
    public async Task LargeTitleLikeEditRetainsUnderFivePercentAndOneMiB()
    {
        using var workspace = new TempDirectory();
        string originalPath = Path.Combine(workspace.Path, "large-original.bin");
        string postEditPath = Path.Combine(workspace.Path, "large-post.bin");
        string deltaPath = Path.Combine(workspace.Path, "large.rdelta");
        string restoredPath = Path.Combine(workspace.Path, "large-restored.bin");
        const long length = 64L * 1024 * 1024;
        await WriteLargeFixtureAsync(
            originalPath, length, TestContext.Current.CancellationToken);
        File.Copy(originalPath, postEditPath);
        await using (var post = new FileStream(
            postEditPath, FileMode.Open, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous))
        {
            post.Position = 257;
            await post.WriteAsync(
                "A changed title"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        var service = new ReverseDeltaService();

        ReverseDeltaDescriptor descriptor = await service.CreateFileAsync(
            originalPath, postEditPath, deltaPath, TestContext.Current.CancellationToken);
        await service.RestoreFileAsync(
            deltaPath, postEditPath, restoredPath, TestContext.Current.CancellationToken);

        long threshold = Math.Min(length / 20, 1024 * 1024);
        Assert.True(descriptor.RetainedBytes <= threshold,
            $"Retained {descriptor.RetainedBytes:N0} bytes; limit is {threshold:N0}.");
        Assert.Equal(
            await HashFileAsync(originalPath),
            await HashFileAsync(restoredPath));
    }

    [Fact]
    public async Task HeaderRepresentsLengthsBeyondThirtyTwoBits()
    {
        const long multiGigabyteLength = 5L * 1024 * 1024 * 1024;
        byte[] commands = [byte.MaxValue];
        byte[] payload = Compress(commands);
        byte[] header = CreateHeader(
            originalLength: 0,
            postEditLength: multiGigabyteLength,
            originalHash: SHA256.HashData([]),
            postEditHash: new byte[32],
            payload);
        using var delta = new MemoryStream(Join(header, payload), writable: false);

        ReverseDeltaDescriptor descriptor = await new ReverseDeltaService().InspectAsync(
            delta, TestContext.Current.CancellationToken);

        Assert.Equal(multiGigabyteLength, descriptor.PostEditLength);
        Assert.Equal(0, descriptor.OriginalLength);
    }

    [Fact]
    public async Task MultiGigabyteInputStreamsWithBoundedIndexAndCancellation()
    {
        const long multiGigabyteLength = 5L * 1024 * 1024 * 1024;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var original = new MemoryStream();
        using var postEdit = new VirtualPatternStream(
            multiGigabyteLength,
            cts,
            cancelAfterReads: 64);
        using var delta = new MemoryStream();
        var service = new ReverseDeltaService(maximumIndexedChunks: 8);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateAsync(
                original,
                postEdit,
                delta,
                Metadata,
                cts.Token));

        Assert.Equal(multiGigabyteLength, postEdit.Length);
        Assert.InRange(postEdit.Position, 1, 64L * 1024 * 1024);
        Assert.Equal(0, delta.Length);
    }

    private static async Task<byte[]> CreateDeltaAsync(byte[] original, byte[] postEdit)
    {
        using var originalStream = new MemoryStream(original, writable: false);
        using var postEditStream = new MemoryStream(postEdit, writable: false);
        using var delta = new MemoryStream();
        await new ReverseDeltaService().CreateAsync(
            originalStream, postEditStream, delta, Metadata,
            TestContext.Current.CancellationToken);
        return delta.ToArray();
    }

    private static byte[] ApplyMutation(byte[] original, string mutation)
    {
        const int amount = 12_345;
        byte[] inserted = CreateDeterministicBytes(amount, 211);
        return mutation switch
        {
            "insert-at-start" => Join(inserted, original),
            "insert-at-end" => Join(original, inserted),
            "delete-at-start" => original[amount..],
            "delete-at-end" => original[..^amount],
            "replace-middle" => Join(
                original[..700_000], inserted, original[(700_000 + amount)..]),
            "replace-both-ends" => Join(
                inserted, original[amount..^amount], inserted),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static byte[] CreateDeterministicBytes(int length, int seed)
    {
        byte[] result = new byte[length];
        var random = new Random(seed);
        random.NextBytes(result);
        return result;
    }

    private static byte[] Join(params byte[][] parts)
    {
        int length = checked(parts.Sum(part => part.Length));
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] DecompressCommands(byte[] delta)
    {
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(
            delta.AsSpan(56, sizeof(long)));
        using var input = new MemoryStream(
            delta, ReverseDeltaService.EncodedHeaderLength, checked((int)payloadLength),
            writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReplaceCompressedPayload(byte[] delta, byte[] commands)
    {
        byte[] payload = Compress(commands);
        byte[] result = new byte[ReverseDeltaService.EncodedHeaderLength + payload.Length];
        delta.AsSpan(0, ReverseDeltaService.EncodedHeaderLength).CopyTo(result);
        BinaryPrimitives.WriteInt64LittleEndian(
            result.AsSpan(56, sizeof(long)), payload.LongLength);
        SHA256.HashData(payload).CopyTo(result, 128);
        payload.CopyTo(result, ReverseDeltaService.EncodedHeaderLength);
        return result;
    }

    private static byte[] Compress(byte[] commands)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(
            output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(commands);
        }
        return output.ToArray();
    }

    private static byte[] CreateHeader(
        long originalLength,
        long postEditLength,
        byte[] originalHash,
        byte[] postEditHash,
        byte[] payload)
    {
        byte[] header = new byte[ReverseDeltaService.EncodedHeaderLength];
        "MLMRDEL1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(10, 2), ReverseDeltaService.EncodedHeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(16, 8), originalLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(24, 8), postEditLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(32, 8), Metadata.LastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(40, 4), (int)Metadata.Attributes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(44, 4), ReverseDeltaService.MinimumChunkBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(48, 4), ReverseDeltaService.TargetChunkBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(52, 4), ReverseDeltaService.MaximumChunkBytes);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(56, 8), payload.LongLength);
        originalHash.CopyTo(header, 64);
        postEditHash.CopyTo(header, 96);
        SHA256.HashData(payload).CopyTo(header, 128);
        return header;
    }

    private static async Task WriteLargeFixtureAsync(
        string path,
        long length,
        CancellationToken ct)
    {
        byte[] block = CreateDeterministicBytes(64 * 1024, 101);
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            block.Length, FileOptions.Asynchronous);
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(block.Length, remaining);
            await stream.WriteAsync(block.AsMemory(0, count), ct);
            remaining -= count;
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(
            stream, TestContext.Current.CancellationToken));
    }

    private sealed class CancelAfterReadStream : MemoryStream
    {
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfterReads;
        private int _reads;

        public CancelAfterReadStream(
            byte[] buffer,
            CancellationTokenSource cts,
            int cancelAfterReads)
            : base(buffer, writable: false)
        {
            _cts = cts;
            _cancelAfterReads = cancelAfterReads;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) >= _cancelAfterReads)
                _cts.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class VirtualPatternStream(
        long length,
        CancellationTokenSource cts,
        int cancelAfterReads) : Stream
    {
        private long _position;
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            int available = (int)Math.Min(count, length - _position);
            for (int index = 0; index < available; index++)
                buffer[offset + index] = (byte)((_position + index) * 31);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _reads) >= cancelAfterReads)
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            int available = (int)Math.Min(buffer.Length, length - _position);
            for (int index = 0; index < available; index++)
                buffer.Span[index] = (byte)((_position + index) * 31);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MusicLibrary.Core.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;
            foreach (string file in Directory.EnumerateFiles(
                Path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }
            Directory.Delete(Path, recursive: true);
        }
    }
}
