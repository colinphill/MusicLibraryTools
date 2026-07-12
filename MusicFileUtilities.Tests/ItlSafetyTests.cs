using System.Buffers.Binary;
using System.Text;
using iTunes.Binary;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class ItlSafetyTests
{
    [Fact]
    public void ChunkReadRejectsOverflowingHeaderLength()
    {
        byte[] data = new byte[16];
        "msdh"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), int.MaxValue);

        Assert.Throws<InvalidDataException>(() => ItlChunk.Read(data, 0));
    }

    [Fact]
    public void ChunkWalkRejectsTruncatedItem()
    {
        byte[] data = new byte[16];
        "mhit"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 20);

        Assert.Throws<InvalidDataException>(() => ItlChunk.Walk(data, 0, data.Length).ToArray());
    }

    [Fact]
    public void EnvelopeRejectsClaimedLengthThatDoesNotMatchFile()
    {
        byte[] file = CreateHeader();
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), file.Length + 1);

        Assert.Throws<InvalidDataException>(() => ItlEnvelope.Parse(file));
    }

    [Fact]
    public void SaveAtomicallyReplacesTargetAndKeepsBackup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mlt_itl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "Library.itl");
        byte[] previous = "previous library"u8.ToArray();
        File.WriteAllBytes(target, previous);

        try
        {
            byte[] header = CreateHeader();
            byte[] body = CreateBody();
            var envelope = new ItlEnvelope
            {
                Version = "1",
                LibraryPersistentId = 1,
                SectionCount = 1,
                MaxCryptSize = 0,
                FileLength = header.Length,
                RawHeader = header,
                Body = body,
            };

            ItlWriter.Save(envelope, body, target);

            Assert.Equal(previous, File.ReadAllBytes(target + ".bak"));
            Assert.Equal(body, ItlEnvelope.Load(target).Body);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static byte[] CreateHeader()
    {
        byte[] header = new byte[144];
        "hdfm"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), header.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), header.Length);
        header[16] = 1;
        header[17] = (byte)'1';
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(52), 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(92), 0);
        return header;
    }

    private static byte[] CreateBody()
    {
        byte[] body = new byte[32];
        "msdh"u8.CopyTo(body);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 16);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), 1);
        "mfdh"u8.CopyTo(body.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 16);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), body.Length + 144);
        return body;
    }
}
