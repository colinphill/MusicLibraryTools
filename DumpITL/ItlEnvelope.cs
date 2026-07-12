using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// The outer "hdfm" envelope of an iTunes Library.itl file. Unlike everything inside it,
/// the envelope stores its integers big-endian.
/// </summary>
public sealed class ItlEnvelope
{
    /// <summary>iTunes encrypts at most this key's worth of the body; the key has never changed.</summary>
    private static readonly byte[] AesKey = "BHUILuilfghuila3"u8.ToArray();

    public required string Version { get; init; }
    public required ulong LibraryPersistentId { get; init; }
    public required int SectionCount { get; init; }
    public required int MaxCryptSize { get; init; }
    public required int FileLength { get; init; }

    /// <summary>The original envelope bytes, replayed verbatim on save with only the length patched.</summary>
    public required byte[] RawHeader { get; init; }

    /// <summary>Decrypted and inflated library payload: a chain of "msdh" sections.</summary>
    public required byte[] Body { get; init; }

    public static ItlEnvelope Load(string path) => Parse(File.ReadAllBytes(path));

    public static ItlEnvelope Parse(byte[] file)
    {
        if (file.Length < 144 || Encoding.ASCII.GetString(file, 0, 4) != "hdfm")
            throw new InvalidDataException("Not an iTunes .itl file (missing 'hdfm' signature).");

        int BE(int offset) => BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));

        int headerLength = BE(4);
        int fileLength = BE(8);
        int maxCryptSize = BE(92);
        int sectionCount = BE(48);
        ulong persistentId = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(52));

        if (headerLength < 144 || headerLength > file.Length)
            throw new InvalidDataException($"Invalid .itl header length: {headerLength}.");
        if (fileLength < headerLength || maxCryptSize < 0 || sectionCount < 0)
            throw new InvalidDataException("Invalid negative or inconsistent .itl envelope values.");
        if (fileLength != file.Length)
            throw new InvalidDataException($"Truncated .itl: header claims {fileLength} bytes, file has {file.Length}.");

        int versionLength = file[16];
        if (17 + versionLength > headerLength)
            throw new InvalidDataException("The .itl version string extends beyond the envelope header.");
        string version = Encoding.ASCII.GetString(file, 17, versionLength);

        int bodyLength = file.Length - headerLength;

        // Only a bounded prefix of the body is enciphered; the tail is stored as-is.
        int cryptLength = Math.Min(maxCryptSize, bodyLength);
        cryptLength -= cryptLength % 16;

        byte[] body = new byte[bodyLength];
        Array.Copy(file, headerLength, body, 0, bodyLength);

        if (cryptLength > 0)
        {
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = AesKey;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] plain = decryptor.TransformFinalBlock(body, 0, cryptLength);
            plain.CopyTo(body, 0);
        }

        return new ItlEnvelope
        {
            Version = version,
            LibraryPersistentId = persistentId,
            SectionCount = sectionCount,
            MaxCryptSize = maxCryptSize,
            FileLength = fileLength,
            RawHeader = file.AsSpan(0, headerLength).ToArray(),
            Body = Inflate(body),
        };
    }

    private static byte[] Inflate(byte[] body)
    {
        if (body.Length < 2 || body[0] != 0x78)
            return body;

        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(body.Length * 8);
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
