using System.Buffers.Binary;
using System.Text;
using iTunes.Binary;

namespace DumpITL.Tests;

internal static class SyntheticLibrary
{
    public static byte[] CreateFile()
    {
        ItlEnvelope envelope = CreateEnvelope();
        return ItlWriter.Build(envelope, CreateBody());
    }

    public static ItlEnvelope CreateEnvelope()
    {
        byte[] header = new byte[144];
        "hdfm"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), header.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), header.Length);
        header[16] = 1;
        header[17] = (byte)'1';
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(48), 7);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(52), 0x123456789ABCDEF0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(68), 99);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(72), 99);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(76), 99);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(84), 99);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(92), 0);
        return new ItlEnvelope
        {
            Version = "1",
            LibraryPersistentId = 0x123456789ABCDEF0,
            SectionCount = 7,
            MaxCryptSize = 0,
            FileLength = header.Length,
            TrackCount = 99,
            PlaylistCount = 99,
            AlbumCount = 99,
            ArtistCount = 99,
            RawHeader = header,
            Body = [],
        };
    }

    public static byte[] CreateBody()
    {
        byte[] mfdh = Chunk("mfdh", new byte[144], headerLength: 144);
        byte[] mhgh = Chunk("mhgh", [], headerLength: 12, countAt8: 0);

        byte[] albumHeader = RecordHeader("miah", 100, id: 3);
        byte[] artistHeader = RecordHeader("miih", 100, id: 4);
        byte[] trackHeader = RecordHeader("mith", 756, id: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(trackHeader.AsSpan(220), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(trackHeader.AsSpan(480), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(trackHeader.AsSpan(ItlDocument.TrackSecondaryIdOffset), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(trackHeader.AsSpan(128), 0x1111111111111111);
        byte[] cloudHeader = (byte[])trackHeader.Clone();

        ItlField name = ItlField.CreateString((int)ItlDataType.PlaylistName, "####!####");
        byte[] nameBytes = new byte[name.Length];
        name.WriteTo(nameBytes);
        byte[] entry = new byte[84];
        "mtph"u8.CopyTo(entry);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), entry.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(16), 5);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(32), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(68), 0x4444444444444444);

        byte[] playlistHeader = RecordHeader("miph", 3500, id: 0);
        BinaryPrimitives.WriteInt32LittleEndian(playlistHeader.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(playlistHeader.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(playlistHeader.AsSpan(ItlDocument.PlaylistPersistentIdOffset), 0x5555555555555555);
        BinaryPrimitives.WriteUInt32LittleEndian(playlistHeader.AsSpan(ItlDocument.PlaylistRecordIdOffset), 6);
        byte[] playlist = JoinRecord(playlistHeader, nameBytes, entry);

        return Join(
            Section(16, mfdh),
            Section(12, mhgh),
            Section(9, List("mlah", albumHeader)),
            Section(11, List("mlih", artistHeader)),
            Section(1, List("mlth", trackHeader)),
            Section(13, List("mlth", cloudHeader)),
            Section(2, List("mlph", playlist)));
    }

    private static byte[] Section(int type, byte[] payload)
    {
        byte[] section = new byte[16 + payload.Length];
        "msdh"u8.CopyTo(section);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(4), 16);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(8), section.Length);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(12), type);
        payload.CopyTo(section, 16);
        return section;
    }

    private static byte[] List(string signature, params byte[][] records)
    {
        byte[] list = new byte[12 + records.Sum(r => r.Length)];
        Encoding.ASCII.GetBytes(signature).CopyTo(list, 0);
        BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(8), records.Length);
        int offset = 12;
        foreach (byte[] record in records) { record.CopyTo(list, offset); offset += record.Length; }
        return list;
    }

    private static byte[] RecordHeader(string signature, int length, uint id)
    {
        byte[] header = new byte[length];
        Encoding.ASCII.GetBytes(signature).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), length);
        if (signature != "miph") BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), id);
        return header;
    }

    private static byte[] JoinRecord(byte[] header, params byte[][] children)
    {
        byte[] record = Join([header, .. children]);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), record.Length);
        return record;
    }

    private static byte[] Chunk(string signature, byte[] payload, int headerLength, int? countAt8 = null)
    {
        byte[] bytes = new byte[headerLength + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), headerLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), countAt8 ?? bytes.Length);
        payload.CopyTo(bytes, headerLength);
        return bytes;
    }

    private static byte[] Join(params byte[][] arrays)
    {
        byte[] result = new byte[arrays.Sum(a => a.Length)];
        int offset = 0;
        foreach (byte[] array in arrays) { array.CopyTo(result, offset); offset += array.Length; }
        return result;
    }
}
