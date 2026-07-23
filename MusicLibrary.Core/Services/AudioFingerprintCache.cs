using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IAudioPayloadIdentityService
{
    Task<string> ComputeAsync(
        string path,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public interface IAudioFingerprintCache
{
    Task<AudioFingerprint?> ReadAsync(
        string payloadIdentity,
        string path,
        CancellationToken ct = default);

    Task WriteAsync(
        string payloadIdentity,
        AudioFingerprint fingerprint,
        CancellationToken ct = default);
}

/// <summary>
/// Computes a tag-insensitive identity from native compressed-audio payloads.
/// Unsupported containers safely fall back to the whole file, which may cause
/// a cache miss after metadata edits but can never reuse a stale fingerprint.
/// </summary>
public sealed class AudioPayloadIdentityService(
    IMediaFormatRegistry formats) : IAudioPayloadIdentityService
{
    private const int BufferSize = 128 * 1024;

    public Task<string> ComputeAsync(
        string path,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return Task.Run(
            () => Compute(fullPath, progress, ct),
            ct);
    }

    private string Compute(
        string path,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException(
                "The audio file does not exist.", path);
        ReportByteProgress(
            progress,
            path,
            $"Checking audio payload for {Path.GetFileName(path)}",
            0,
            info.Length);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string strategy = formats.TryGetForPath(
            path, out MediaFormatDefinition? format)
            ? format.Family switch
            {
                MediaFormatFamily.Flac => HashFlac(stream, hash, progress, ct),
                MediaFormatFamily.Mp3 => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.WavPack => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.Mp4 => HashMp4(
                    stream, hash, progress, ct),
                MediaFormatFamily.Dsf => HashDsf(
                    stream, hash, progress, ct),
                MediaFormatFamily.Ogg => HashOgg(
                    stream, hash, progress, ct),
                MediaFormatFamily.Wave => HashChunkedPcm(
                    stream, hash, littleEndian: true, progress, ct),
                MediaFormatFamily.Aiff => HashChunkedPcm(
                    stream, hash, littleEndian: false, progress, ct),
                MediaFormatFamily.Aac => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.MonkeysAudio => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.Musepack => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.TrueAudio => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.Tak => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.OptimFrog => HashTaggedFrames(
                    stream, hash, progress, ct),
                MediaFormatFamily.Asf => HashAsf(
                    stream, hash, progress, ct),
                _ => HashWholeFile(stream, hash, progress, ct),
            }
            : HashWholeFile(stream, hash, progress, ct);
        string result =
            $"payload-v1:{strategy}:" +
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        ReportByteProgress(
            progress,
            path,
            $"Checked audio payload for {Path.GetFileName(path)}",
            info.Length,
            info.Length,
            OperationPhase.Completed);
        return result;
    }

    private static string HashFlac(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        Span<byte> signature = stackalloc byte[4];
        ReadExactly(stream, signature);
        if (!signature.SequenceEqual("fLaC"u8))
            return HashWholeFile(stream, hash, progress, ct, rewind: true);
        bool last;
        byte[] blockHeader = new byte[4];
        do
        {
            ReadExactly(stream, blockHeader);
            last = (blockHeader[0] & 0x80) != 0;
            int length =
                (blockHeader[1] << 16) |
                (blockHeader[2] << 8) |
                blockHeader[3];
            if (length > stream.Length - stream.Position)
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            stream.Seek(length, SeekOrigin.Current);
        }
        while (!last);
        AppendRange(
            stream, hash, stream.Position, stream.Length, progress, ct);
        return "flac-frames";
    }

    private static string HashTaggedFrames(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        long start = Id3v2End(stream);
        long end = stream.Length;
        if (end - start >= 128)
        {
            stream.Position = end - 128;
            Span<byte> marker = stackalloc byte[3];
            ReadExactly(stream, marker);
            if (marker.SequenceEqual("TAG"u8))
                end -= 128;
        }
        if (end - start >= 32)
        {
            stream.Position = end - 32;
            Span<byte> footer = stackalloc byte[32];
            ReadExactly(stream, footer);
            if (footer[..8].SequenceEqual("APETAGEX"u8))
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                    footer.Slice(12, 4));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                    footer.Slice(20, 4));
                long totalSize = size +
                    ((flags & 0x80000000u) != 0 ? 32L : 0L);
                if (size >= 32 && totalSize <= end - start)
                    end -= totalSize;
            }
        }
        if (start >= end)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        AppendRange(stream, hash, start, end, progress, ct);
        return "tagged-frames";
    }

    private static string HashAsf(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        if (stream.Length < 30)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        stream.Position = 0;
        Span<byte> header = stackalloc byte[30];
        ReadExactly(stream, header);
        ReadOnlySpan<byte> headerGuid =
        [
            0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11,
            0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c,
        ];
        if (!header[..16].SequenceEqual(headerGuid))
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        ulong headerSize =
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16, 8));
        if (headerSize < 30 || headerSize > (ulong)stream.Length)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);

        long position = checked((long)headerSize);
        Span<byte> objectHeader = stackalloc byte[24];
        ReadOnlySpan<byte> dataGuid =
        [
            0x36, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11,
            0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c,
        ];
        while (position + objectHeader.Length <= stream.Length)
        {
            stream.Position = position;
            ReadExactly(stream, objectHeader);
            ulong objectSize =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    objectHeader.Slice(16, 8));
            if (objectSize < 24 ||
                objectSize > (ulong)(stream.Length - position))
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            if (objectHeader[..16].SequenceEqual(dataGuid))
            {
                long payloadStart = position + 24;
                long payloadEnd =
                    checked(position + (long)objectSize);
                AppendLength(hash, payloadEnd - payloadStart);
                AppendRange(
                    stream,
                    hash,
                    payloadStart,
                    payloadEnd,
                    progress,
                    ct);
                return "asf-data";
            }
            position = checked(position + (long)objectSize);
        }
        return HashWholeFile(
            stream, hash, progress, ct, rewind: true);
    }

    private static long Id3v2End(FileStream stream)
    {
        if (stream.Length < 10)
            return 0;
        stream.Position = 0;
        Span<byte> header = stackalloc byte[10];
        ReadExactly(stream, header);
        if (!header[..3].SequenceEqual("ID3"u8) ||
            header[6] > 0x7f ||
            header[7] > 0x7f ||
            header[8] > 0x7f ||
            header[9] > 0x7f)
            return 0;
        int size = (header[6] << 21) |
                   (header[7] << 14) |
                   (header[8] << 7) |
                   header[9];
        long end = 10L + size + ((header[5] & 0x10) != 0 ? 10 : 0);
        return Math.Min(end, stream.Length);
    }

    private static string HashMp4(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        long position = 0;
        int mediaDataAtoms = 0;
        byte[] atomHeader = new byte[16];
        while (position + 8 <= stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            stream.Position = position;
            ReadExactly(stream, atomHeader.AsSpan(0, 8));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(
                atomHeader.AsSpan(0, 4));
            int headerSize = 8;
            if (size == 1)
            {
                ReadExactly(stream, atomHeader.AsSpan(8, 8));
                size = BinaryPrimitives.ReadUInt64BigEndian(
                    atomHeader.AsSpan(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = checked((ulong)(stream.Length - position));
            }
            if (size < (ulong)headerSize ||
                size > (ulong)(stream.Length - position))
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            if (atomHeader.AsSpan(4, 4).SequenceEqual("mdat"u8))
            {
                AppendLength(hash, checked((long)size - headerSize));
                AppendRange(
                    stream,
                    hash,
                    position + headerSize,
                    checked(position + (long)size),
                    progress,
                    ct);
                mediaDataAtoms++;
            }
            position = checked(position + (long)size);
        }
        if (mediaDataAtoms == 0)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        return "mp4-mdat";
    }

    private static string HashDsf(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        if (stream.Length < 28)
            return HashWholeFile(stream, hash, progress, ct, rewind: true);
        stream.Position = 0;
        Span<byte> header = stackalloc byte[28];
        ReadExactly(stream, header);
        if (!header[..4].SequenceEqual("DSD "u8))
            return HashWholeFile(stream, hash, progress, ct, rewind: true);
        ulong metadataOffset = BinaryPrimitives.ReadUInt64LittleEndian(
            header.Slice(20, 8));
        long end = metadataOffset is > 28 &&
                   metadataOffset <= (ulong)stream.Length
            ? checked((long)metadataOffset)
            : stream.Length;
        AppendRange(stream, hash, 28, end, progress, ct);
        return "dsf-chunks";
    }

    private static string HashOgg(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        stream.Position = 0;
        var packet = new MemoryStream();
        int packetIndex = 0;
        bool knownCommentLayout = false;
        byte[] pageHeader = new byte[27];
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadExactly(stream, pageHeader))
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            if (!pageHeader.AsSpan(0, 4).SequenceEqual("OggS"u8))
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            if ((pageHeader[5] & 0x02) != 0)
            {
                packet.SetLength(0);
                packetIndex = 0;
                knownCommentLayout = false;
            }
            int segmentCount = pageHeader[26];
            byte[] laces = new byte[segmentCount];
            ReadExactly(stream, laces);
            int payloadLength = laces.Sum(value => value);
            byte[] payload = new byte[payloadLength];
            ReadExactly(stream, payload);
            int offset = 0;
            foreach (byte lace in laces)
            {
                bool skip = knownCommentLayout && packetIndex == 1;
                if (!skip && lace > 0)
                    packet.Write(payload, offset, lace);
                offset += lace;
                if (lace == 255)
                    continue;
                if (packetIndex == 0)
                {
                    ReadOnlySpan<byte> first = packet.GetBuffer()
                        .AsSpan(0, checked((int)packet.Length));
                    knownCommentLayout =
                        first.StartsWith("\x01vorbis"u8) ||
                        first.StartsWith("OpusHead"u8) ||
                        first.StartsWith("Speex   "u8);
                }
                if (!(knownCommentLayout && packetIndex == 1))
                {
                    AppendLength(hash, packet.Length);
                    hash.AppendData(
                        packet.GetBuffer(), 0, checked((int)packet.Length));
                }
                packet.SetLength(0);
                packetIndex++;
            }
            ReportByteProgress(
                progress,
                null,
                "Checking Ogg audio packets",
                stream.Position,
                stream.Length);
        }
        if (packet.Length != 0)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        return "ogg-packets";
    }

    private static string HashChunkedPcm(
        FileStream stream,
        IncrementalHash hash,
        bool littleEndian,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        stream.Position = 0;
        Span<byte> container = stackalloc byte[12];
        if (!TryReadExactly(stream, container))
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        bool valid = littleEndian
            ? (container[..4].SequenceEqual("RIFF"u8) ||
               container[..4].SequenceEqual("RF64"u8)) &&
              container[8..].SequenceEqual("WAVE"u8)
            : container[..4].SequenceEqual("FORM"u8) &&
              (container[8..].SequenceEqual("AIFF"u8) ||
               container[8..].SequenceEqual("AIFC"u8));
        if (!valid)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);

        ulong? rf64DataSize = null;
        bool foundFormat = false;
        bool foundAudio = false;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> ds64Header = stackalloc byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadExactly(stream, chunkHeader))
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            ReadOnlySpan<byte> id = chunkHeader[..4];
            uint size32 = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..])
                : BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[4..]);
            long dataOffset = stream.Position;
            ulong size = size32;
            if (littleEndian && id.SequenceEqual("ds64"u8))
            {
                if (size32 < 28 || dataOffset + size32 > stream.Length)
                    return HashWholeFile(
                        stream, hash, progress, ct, rewind: true);
                ReadExactly(stream, ds64Header);
                rf64DataSize =
                    BinaryPrimitives.ReadUInt64LittleEndian(ds64Header[8..]);
                stream.Position = dataOffset;
            }
            else if (littleEndian &&
                     id.SequenceEqual("data"u8) &&
                     size32 == uint.MaxValue)
            {
                if (!rf64DataSize.HasValue)
                    return HashWholeFile(
                        stream, hash, progress, ct, rewind: true);
                size = rf64DataSize.Value;
            }

            if (size > long.MaxValue)
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            long end;
            long next;
            try
            {
                end = checked(dataOffset + (long)size);
                next = checked(end + ((size & 1) != 0 ? 1 : 0));
            }
            catch (OverflowException)
            {
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);
            }
            if (next > stream.Length)
                return HashWholeFile(
                    stream, hash, progress, ct, rewind: true);

            bool isFormat = littleEndian
                ? id.SequenceEqual("fmt "u8)
                : id.SequenceEqual("COMM"u8);
            bool isAudio = littleEndian
                ? id.SequenceEqual("data"u8)
                : id.SequenceEqual("SSND"u8);
            if (isFormat || isAudio)
            {
                hash.AppendData(id);
                AppendLength(hash, (long)size);
                AppendRange(
                    stream, hash, dataOffset, end, progress, ct);
                foundFormat |= isFormat;
                foundAudio |= isAudio;
            }
            stream.Position = next;
        }

        if (!foundFormat || !foundAudio)
            return HashWholeFile(
                stream, hash, progress, ct, rewind: true);
        return littleEndian ? "wave-chunks" : "aiff-chunks";
    }

    private static string HashWholeFile(
        FileStream stream,
        IncrementalHash hash,
        IProgress<OperationProgress>? progress,
        CancellationToken ct,
        bool rewind = false)
    {
        if (rewind)
        {
            hash.GetHashAndReset();
            stream.Position = 0;
        }
        AppendRange(stream, hash, stream.Position, stream.Length, progress, ct);
        return "whole-file";
    }

    private static void AppendRange(
        FileStream stream,
        IncrementalHash hash,
        long start,
        long end,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        stream.Position = start;
        byte[] buffer = new byte[BufferSize];
        long remaining = end - start;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(
                buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            remaining -= read;
            ReportByteProgress(
                progress,
                null,
                "Checking compressed audio payload",
                stream.Position,
                stream.Length);
        }
    }

    private static void ReportByteProgress(
        IProgress<OperationProgress>? progress,
        string? path,
        string message,
        long completed,
        long total,
        OperationPhase phase = OperationPhase.IndexingSources)
    {
        if (progress is null)
            return;
        int scaledTotal = total <= int.MaxValue
            ? checked((int)total)
            : int.MaxValue;
        int scaledCompleted = total <= 0
            ? 0
            : total <= int.MaxValue
                ? checked((int)Math.Clamp(completed, 0, total))
                : checked((int)Math.Round(
                    Math.Clamp((double)completed / total, 0, 1) *
                    int.MaxValue));
        progress.Report(new(
            phase,
            scaledCompleted,
            scaledTotal,
            path,
            message));
    }

    private static void AppendLength(IncrementalHash hash, long length)
    {
        Span<byte> value = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(value, length);
        hash.AppendData(value);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (!TryReadExactly(stream, buffer))
            throw new EndOfStreamException();
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }
}

public sealed class AudioFingerprintCache : IAudioFingerprintCache
{
    private const int MaximumEntries = 10_000;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public AudioFingerprintCache() : this(Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "MusicLibraryTools",
        "metadata-source-cache.db"))
    {
    }

    public AudioFingerprintCache(string databasePath) =>
        _databasePath = Path.GetFullPath(databasePath);

    public async Task<AudioFingerprint?> ReadAsync(
        string payloadIdentity,
        string path,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection =
                await OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload
                FROM AudioFingerprintCache
                WHERE payload_identity = $identity;
                """;
            command.Parameters.AddWithValue("$identity", payloadIdentity);
            string? payload = (string?)await command.ExecuteScalarAsync(ct)
                .ConfigureAwait(false);
            AudioFingerprint? cached = payload is null
                ? null
                : JsonSerializer.Deserialize<AudioFingerprint>(payload);
            return cached is null
                ? null
                : cached with { Path = Path.GetFullPath(path) };
        }
        catch (Exception error) when (
            error is SqliteException or IOException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        string payloadIdentity,
        AudioFingerprint fingerprint,
        CancellationToken ct = default)
    {
        string payload = JsonSerializer.Serialize(fingerprint);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection =
                await OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO AudioFingerprintCache(
                    payload_identity, payload, cached_utc)
                VALUES($identity, $payload, $cached)
                ON CONFLICT(payload_identity) DO UPDATE SET
                    payload = excluded.payload,
                    cached_utc = excluded.cached_utc;
                DELETE FROM AudioFingerprintCache
                WHERE payload_identity IN (
                    SELECT payload_identity
                    FROM AudioFingerprintCache
                    ORDER BY cached_utc DESC
                    LIMIT -1 OFFSET $maximum);
                """;
            command.Parameters.AddWithValue("$identity", payloadIdentity);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue(
                "$cached", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$maximum", MaximumEntries);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is SqliteException or IOException)
        {
            // Fingerprint generation remains available without the cache.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (!_initialized)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS AudioFingerprintCache(
                    payload_identity TEXT PRIMARY KEY NOT NULL,
                    payload TEXT NOT NULL,
                    cached_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AudioFingerprintCache_Cached
                    ON AudioFingerprintCache(cached_utc);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        return connection;
    }
}
