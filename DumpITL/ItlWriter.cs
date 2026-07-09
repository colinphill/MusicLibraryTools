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

    /// <summary>Offset of the total-length word inside the internal "mfdh" copy of the envelope.</summary>
    private const int MfdhTotalLengthOffset = 8;

    public static void Save(ItlEnvelope envelope, byte[] body, string path)
        => File.WriteAllBytes(path, Build(envelope, body));

    public static byte[] Build(ItlEnvelope envelope, byte[] body)
    {
        int headerLength = envelope.RawHeader.Length;

        // The internal envelope copy records the *uncompressed* total length. iTunes reads it back,
        // so it has to track the body we are about to write.
        PatchMfdh(body, headerLength);

        byte[] compressed = Deflate(body);

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
        envelope.RawHeader.CopyTo(file, 0);
        compressed.CopyTo(file, headerLength);

        // The envelope is the one big-endian structure in the file.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), file.Length);

        return file;
    }

    /// <summary>Rewrites the total length inside the first section's "mfdh" record.</summary>
    private static void PatchMfdh(byte[] body, int headerLength)
    {
        ItlChunk section = ItlChunk.Read(body, 0);
        int mfdh = section.BodyOffset;

        if (Encoding.ASCII.GetString(body, mfdh, 4) != "mfdh")
            throw new InvalidDataException("First section does not contain the expected 'mfdh' record.");

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(mfdh + MfdhTotalLengthOffset), headerLength + body.Length);
    }

    private static byte[] Deflate(byte[] body)
    {
        using var output = new MemoryStream(body.Length / 8);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(body);
        return output.ToArray();
    }
}
