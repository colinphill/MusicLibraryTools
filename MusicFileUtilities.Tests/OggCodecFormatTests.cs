using System.Buffers.Binary;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class OggCodecFormatTests
{
    private static readonly byte[] Cover = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(".opus", "Opus", 48000u, 2u)]
    [InlineData(".spx", "Speex", 16000u, 1u)]
    public void GeneralizedOggHandlerRoundTripsMetadataArtworkAndAudioPackets(
        string extension,
        string codecName,
        uint sampleRate,
        uint channels)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ogg_音楽_Δ_😀_{Guid.NewGuid():N}{extension}");
        WriteFixture(path, extension);
        byte[][] audioBefore = ReadPackets(path).Skip(2).ToArray();
        try
        {
            var media = Assert.IsType<OggVorbisFile>(
                MediaFile.GetFile(path, readOnly: false, readArtwork: true));
            Assert.Equal(codecName, media.CodecName);
            Assert.Equal(sampleRate, media.Samplerate);
            Assert.Equal(channels, media.Channels);

            media.SetField(
                TagFields.Title,
                "Déjà vu — 日本語 — 🦊");
            Assert.IsAssignableFrom<IArtworkWriter>(media)
                .SetFrontCover(Cover, "image/png");
            media.SaveTags();

            var reloaded = Assert.IsType<OggVorbisFile>(
                MediaFile.GetFile(path, readOnly: true, readArtwork: true));
            Assert.Equal("Déjà vu — 日本語 — 🦊", reloaded.Title);
            Assert.Equal(
                Cover,
                Assert.Single(reloaded.GetImageMetadata()).Data);

            byte[][] audioAfter = ReadPackets(path).Skip(2).ToArray();
            Assert.Equal(audioBefore.Length, audioAfter.Length);
            for (int index = 0; index < audioBefore.Length; index++)
                Assert.Equal(audioBefore[index], audioAfter[index]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(".opus")]
    [InlineData(".spx")]
    public void NewOggCodecsAdvertiseReleasedNativeCapabilities(string extension)
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.True(registry.SupportsExtension(
            extension,
            MediaFormatCapabilities.LibraryIndex |
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork |
            MediaFormatCapabilities.Remux |
            MediaFormatCapabilities.TranscodeSource));
    }

    private static void WriteFixture(string path, string extension)
    {
        byte[] identification = extension switch
        {
            ".opus" => BuildOpusHead(),
            ".spx" => BuildSpeexHeader(),
            _ => throw new ArgumentOutOfRangeException(nameof(extension)),
        };
        var comments = new VorbisComments { Vendor = "MusicLibraryTools tests" };
        comments.SetField(TagFields.Title, "Old");
        byte[] payload = comments.ToByteArray(includeart: true);
        byte[] comment = extension == ".opus"
            ? "OpusTags"u8.ToArray().Concat(payload).ToArray()
            : payload;
        byte[] audioOne = [0xA1, 0x10, 0x20, 0x30, 0x40];
        byte[] audioTwo = [0xB2, 0x50, 0x60, 0x70];
        const int serial = 0x13572468;

        using FileStream stream = File.Create(path);
        WritePage(stream, identification, headerType: 2, granule: 0, serial, sequence: 0);
        WritePage(stream, comment, headerType: 0, granule: 0, serial, sequence: 1);
        WritePage(stream, audioOne, headerType: 0, granule: 960, serial, sequence: 2);
        WritePage(stream, audioTwo, headerType: 4, granule: 1920, serial, sequence: 3);
    }

    private static byte[] BuildOpusHead()
    {
        byte[] packet = new byte[19];
        "OpusHead"u8.CopyTo(packet);
        packet[8] = 1;
        packet[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 44100);
        return packet;
    }

    private static byte[] BuildSpeexHeader()
    {
        byte[] packet = new byte[80];
        "Speex   "u8.CopyTo(packet);
        Encoding.ASCII.GetBytes("speex-1.2").CopyTo(packet, 8);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), packet.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(36), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(44), 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48), 1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(52), 28000);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(56), 320);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(64), 1);
        return packet;
    }

    private static void WritePage(
        Stream stream,
        byte[] packet,
        byte headerType,
        long granule,
        int serial,
        int sequence)
    {
        Assert.True(packet.Length < 255);
        byte[] header = new byte[27];
        "OggS"u8.CopyTo(header);
        header[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), serial);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), sequence);
        header[26] = 1;
        byte[] segments = [(byte)packet.Length];
        uint crc = UpdateCrc(0, header);
        crc = UpdateCrc(crc, segments);
        crc = UpdateCrc(crc, packet);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), crc);
        stream.Write(header);
        stream.Write(segments);
        stream.Write(packet);
    }

    private static List<byte[]> ReadPackets(string path)
    {
        var packets = new List<byte[]>();
        var current = new List<byte>();
        using FileStream stream = File.OpenRead(path);
        while (stream.Position < stream.Length)
        {
            byte[] header = new byte[27];
            stream.ReadExactly(header);
            Assert.True(header.AsSpan(0, 4).SequenceEqual("OggS"u8));
            byte[] segments = new byte[header[26]];
            stream.ReadExactly(segments);
            foreach (byte segment in segments)
            {
                byte[] data = new byte[segment];
                stream.ReadExactly(data);
                current.AddRange(data);
                if (segment < 255)
                {
                    packets.Add(current.ToArray());
                    current.Clear();
                }
            }
        }
        Assert.Empty(current);
        return packets;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000u) != 0
                    ? (crc << 1) ^ 0x04C11DB7u
                    : crc << 1;
        }
        return crc;
    }
}
