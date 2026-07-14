using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// Re-encodes an inflated library body back into a valid .itl file: deflate, encrypt the bounded
/// prefix, and replay the original envelope with the lengths patched.
/// </summary>
public static class ItlWriter
{
    private static readonly byte[] AesKey = "BHUILuilfghuila3"u8.ToArray();
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Offset of the total-length word inside the internal "mfdh" copy of the envelope.</summary>
    private const int MfdhTotalLengthOffset = 8;

    private readonly record struct Aggregates(int Sections, int Tracks, int Playlists, int Albums, int Artists);

    public static void Save(ItlEnvelope envelope, byte[] body, string path)
    {
        byte[] data = Build(envelope, body);
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        string backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static byte[] Build(ItlEnvelope envelope, byte[] body)
    {
        int headerLength = envelope.RawHeader.Length;
        byte[] bodyCopy = (byte[])body.Clone();
        byte[] headerCopy = (byte[])envelope.RawHeader.Clone();
        EnsurePlaybackStateUnchanged(envelope.Body, bodyCopy);
        Aggregates aggregates = ReadAggregates(bodyCopy);
        bool bodyChanged = !body.AsSpan().SequenceEqual(envelope.Body);
        uint modifiedDate = bodyChanged
            ? checked((uint)(DateTime.UtcNow - MacEpoch).TotalSeconds)
            : envelope.ModifiedDateSeconds;

        // The internal envelope copy records the *uncompressed* total length. iTunes reads it back,
        // so it has to track the body we are about to write.
        PatchMfdh(bodyCopy, headerLength, aggregates, bodyChanged ? modifiedDate : null);

        byte[] compressed = Deflate(bodyCopy);

        int cryptLength = Math.Min(envelope.MaxCryptSize, compressed.Length);
        cryptLength -= cryptLength % 16;

        if (cryptLength > 0)
        {
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = AesKey;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] cipher = encryptor.TransformFinalBlock(compressed, 0, cryptLength);
            cipher.CopyTo(compressed, 0);
        }

        byte[] file = new byte[headerLength + compressed.Length];
        headerCopy.CopyTo(file, 0);
        compressed.CopyTo(file, headerLength);

        // The envelope is the one big-endian structure in the file.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), file.Length);
        PatchEnvelopeAggregates(file, aggregates);
        if (bodyChanged)
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(112), modifiedDate);

        return file;
    }

    private static void EnsurePlaybackStateUnchanged(byte[] originalBody, byte[] candidateBody)
    {
        byte[][] original = ReadPlaybackStateChunks(originalBody);
        byte[][] candidate = ReadPlaybackStateChunks(candidateBody);
        if (original.Length == candidate.Length &&
            original.Zip(candidate).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)))
            return;

        throw new InvalidOperationException(
            "Type-514 playback state cannot be added, removed, or edited because its +108/+124 integrity token is not reproducible.");
    }

    private static byte[][] ReadPlaybackStateChunks(byte[] body)
    {
        if (body.Length == 0) return [];

        var result = new List<byte[]>();
        foreach (ItlChunk section in ItlChunk.Walk(body, 0, body.Length))
        {
            if (section.Type != 12 || section.BodyLength < 12) continue;
            ItlChunk mhgh = ItlChunk.Read(body, section.BodyOffset);
            if (mhgh.Signature != "mhgh") continue;
            foreach (ItlChunk child in ItlChunk.Walk(body, mhgh.HeaderEnd, section.EndOffset))
            {
                if (child.Signature == "mhoh" && child.Type == (int)ItlDataType.PlaybackStatePlist)
                    result.Add(body.AsSpan(child.Offset, child.TotalLength).ToArray());
            }
        }
        return [.. result];
    }

    /// <summary>Rewrites the total length inside the first section's "mfdh" record.</summary>
    private static void PatchMfdh(byte[] body, int headerLength, Aggregates aggregates, uint? modifiedDate)
    {
        ItlChunk section = ItlChunk.Read(body, 0);
        int mfdh = section.BodyOffset;

        if (Encoding.ASCII.GetString(body, mfdh, 4) != "mfdh")
            throw new InvalidDataException("First section does not contain the expected 'mfdh' record.");

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(mfdh + MfdhTotalLengthOffset), headerLength + body.Length);

        int mfdhHeaderLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(mfdh + 4));
        if (mfdhHeaderLength >= 88)
        {
            WriteLittle(body, mfdh + 48, aggregates.Sections);
            WriteLittle(body, mfdh + 68, aggregates.Tracks);
            WriteLittle(body, mfdh + 72, aggregates.Playlists);
            WriteLittle(body, mfdh + 76, aggregates.Albums);
            WriteLittle(body, mfdh + 84, aggregates.Artists);
        }
        if (modifiedDate.HasValue && mfdhHeaderLength >= 116)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(mfdh + 112), modifiedDate.Value);
    }

    private static Aggregates ReadAggregates(byte[] body)
    {
        int sections = 0, tracks = 0, playlists = 0, albums = 0, artists = 0;
        foreach (ItlChunk section in ItlChunk.Walk(body, 0, body.Length))
        {
            sections++;
            if (section.Type is not (1 or 2 or 9 or 11) || section.BodyLength < 12)
                continue;

            ItlChunk list = ItlChunk.Read(body, section.BodyOffset);
            switch (section.Type, list.Signature)
            {
                case (1, "mlth"): tracks = list.ItemCount; break;
                case (2, "mlph"): playlists = list.ItemCount; break;
                case (9, "mlah"): albums = list.ItemCount; break;
                case (11, "mlih"): artists = list.ItemCount; break;
            }
        }
        return new Aggregates(sections, tracks, playlists, albums, artists);
    }

    private static void PatchEnvelopeAggregates(byte[] file, Aggregates aggregates)
    {
        WriteBig(file, 48, aggregates.Sections);
        WriteBig(file, 68, aggregates.Tracks);
        WriteBig(file, 72, aggregates.Playlists);
        WriteBig(file, 76, aggregates.Albums);
        WriteBig(file, 84, aggregates.Artists);
    }

    private static void WriteLittle(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteBig(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);

    private static byte[] Deflate(byte[] body)
    {
        using var output = new MemoryStream(body.Length / 8);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(body);
        return output.ToArray();
    }
}
