using System.Buffers.Binary;

namespace iTunes.Binary;

/// <summary>
/// Typed access to the fixed fields of a track ("mith") record. The offsets were found by
/// correlating every candidate offset against iTunes' own XML export.
/// </summary>
public static class ItlTrackFields
{
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ushort U16(ItlRecord r, int o) => BinaryPrimitives.ReadUInt16LittleEndian(r.Header.AsSpan(o));
    private static uint U32(ItlRecord r, int o) => BinaryPrimitives.ReadUInt32LittleEndian(r.Header.AsSpan(o));

    private static void SetU16(ItlRecord r, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(r.Header.AsSpan(o), (ushort)v);
    private static void SetU32(ItlRecord r, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(r.Header.AsSpan(o), v);

    public static int GetTrackId(this ItlRecord t) => (int)U32(t, 16);
    public static void SetTrackId(this ItlRecord t, int v) => SetU32(t, 16, (uint)v);

    public static ulong GetPersistentId(this ItlRecord t) => BinaryPrimitives.ReadUInt64LittleEndian(t.Header.AsSpan(128));
    public static void SetPersistentId(this ItlRecord t, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(t.Header.AsSpan(128), v);

    /// <summary>File size. iTunes keeps a 32-bit copy at +36 that truncates above 4 GiB; both are written.</summary>
    public static ulong GetSize(this ItlRecord t) => BinaryPrimitives.ReadUInt64LittleEndian(t.Header.AsSpan(324));

    public static void SetSize(this ItlRecord t, ulong v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(t.Header.AsSpan(324), v);
        SetU32(t, 36, (uint)Math.Min(v, uint.MaxValue));
    }

    public static TimeSpan GetDuration(this ItlRecord t) => TimeSpan.FromMilliseconds(U32(t, 40));
    public static void SetDuration(this ItlRecord t, TimeSpan v) => SetU32(t, 40, (uint)v.TotalMilliseconds);

    public static int GetTrackNumber(this ItlRecord t) => U16(t, 44);
    public static void SetTrackNumber(this ItlRecord t, int v) => SetU16(t, 44, v);

    public static int GetTrackCount(this ItlRecord t) => U16(t, 48);
    public static void SetTrackCount(this ItlRecord t, int v) => SetU16(t, 48, v);

    public static int GetYear(this ItlRecord t) => U16(t, 52);
    public static void SetYear(this ItlRecord t, int v) => SetU16(t, 52, v);

    public static int GetBitRate(this ItlRecord t) => U16(t, 56);
    public static void SetBitRate(this ItlRecord t, int v) => SetU16(t, 56, v);

    public static int GetPlayCount(this ItlRecord t) => (int)U32(t, 76);
    public static void SetPlayCount(this ItlRecord t, int v) => SetU32(t, 76, (uint)v);

    public static int GetBpm(this ItlRecord t) => U16(t, 164);
    public static void SetBpm(this ItlRecord t, int v) => SetU16(t, 164, v);

    public static int GetSkipCount(this ItlRecord t) => (int)U32(t, 216);
    public static void SetSkipCount(this ItlRecord t, int v) => SetU32(t, 216, (uint)v);

    public static DateTime? GetDateModified(this ItlRecord t) => ToUtc(U32(t, 32));
    public static void SetDateModified(this ItlRecord t, DateTime v) => SetU32(t, 32, ToLocalSeconds(v));

    public static DateTime? GetPlayDate(this ItlRecord t) => ToUtc(U32(t, 100));
    public static void SetPlayDate(this ItlRecord t, DateTime v) => SetU32(t, 100, ToLocalSeconds(v));

    public static DateTime? GetDateAdded(this ItlRecord t) => ToUtc(U32(t, 120));
    public static void SetDateAdded(this ItlRecord t, DateTime v) => SetU32(t, 120, ToLocalSeconds(v));

    public static DateTime? GetSkipDate(this ItlRecord t) => ToUtc(U32(t, 284));
    public static void SetSkipDate(this ItlRecord t, DateTime v) => SetU32(t, 284, ToLocalSeconds(v));

    public static int GetDiscNumber(this ItlRecord t) => U16(t, 104);
    public static void SetDiscNumber(this ItlRecord t, int v) => SetU16(t, 104, v);

    public static int GetDiscCount(this ItlRecord t) => U16(t, 106);
    public static void SetDiscCount(this ItlRecord t, int v) => SetU16(t, 106, v);

    public static int GetArtworkCount(this ItlRecord t) => U16(t, 144);
    public static void SetArtworkCount(this ItlRecord t, int v) => SetU16(t, 144, v);

    /// <summary>Signed: iTunes writes -1 for a file it cannot locate under the library folder.</summary>
    public static int GetFileFolderCount(this ItlRecord t) => BinaryPrimitives.ReadInt16LittleEndian(t.Header.AsSpan(92));

    public static int GetLibraryFolderCount(this ItlRecord t) => BinaryPrimitives.ReadInt16LittleEndian(t.Header.AsSpan(94));

    public static int GetSeason(this ItlRecord t) => (int)U32(t, 272);
    public static void SetSeason(this ItlRecord t, int v) => SetU32(t, 272, (uint)v);

    public static int GetEpisodeOrder(this ItlRecord t) => (int)U32(t, 268);
    public static void SetEpisodeOrder(this ItlRecord t, int v) => SetU32(t, 268, (uint)v);

    /// <summary>Unlike the other timestamps this one is stored in UTC, not local time.</summary>
    public static DateTime? GetReleaseDate(this ItlRecord t) =>
        U32(t, 160) == 0 ? null : MacEpoch.AddSeconds(U32(t, 160));

    public static void SetReleaseDate(this ItlRecord t, DateTime v) =>
        SetU32(t, 160, (uint)(v.ToUniversalTime() - MacEpoch).TotalSeconds);

    /// <summary>Foreign key into the album records ("miah" +16).</summary>
    public static uint GetAlbumId(this ItlRecord t) => U32(t, 220);
    public static void SetAlbumId(this ItlRecord t, uint v) => SetU32(t, 220, v);

    /// <summary>Foreign key into the artist records ("miih" +16).</summary>
    public static uint GetArtistId(this ItlRecord t) => U32(t, 480);
    public static void SetArtistId(this ItlRecord t, uint v) => SetU32(t, 480, v);

    // Flags. Each was located by finding the single bit that agrees with the XML boolean on all
    // 47,494 exported tracks.
    private static bool Bit(ItlRecord t, int offset, int bit) => ((t.Header[offset] >> bit) & 1) == 1;

    private static void SetBit(ItlRecord t, int offset, int bit, bool value) =>
        t.Header[offset] = (byte)(value ? t.Header[offset] | (1 << bit) : t.Header[offset] & ~(1 << bit));

    public static bool GetCompilation(this ItlRecord t) => Bit(t, 83, 0);
    public static void SetCompilation(this ItlRecord t, bool v) => SetBit(t, 83, 0, v);

    public static bool GetHasVideo(this ItlRecord t) => Bit(t, 233, 0);
    public static void SetHasVideo(this ItlRecord t, bool v) => SetBit(t, 233, 0, v);

    public static bool GetPartOfGaplessAlbum(this ItlRecord t) => Bit(t, 278, 0);
    public static void SetPartOfGaplessAlbum(this ItlRecord t, bool v) => SetBit(t, 278, 0, v);

    /// <summary>0 = none, 1 = explicit, 2 = clean.</summary>
    public static int GetAdvisory(this ItlRecord t) => t.Header[166];
    public static void SetAdvisory(this ItlRecord t, int v) => t.Header[166] = (byte)v;

    public static string? GetString(this ItlRecord t, ItlDataType type) => t.Field((int)type)?.Text;
    public static void SetString(this ItlRecord t, ItlDataType type, string value) => t.SetField((int)type, value);

    /// <summary>Timestamps are local-time seconds since 1904, so this machine's timezone is assumed.</summary>
    private static DateTime? ToUtc(uint seconds)
    {
        if (seconds == 0)
            return null;

        DateTime local = DateTime.SpecifyKind(MacEpoch.AddSeconds(seconds), DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return null;   // inside a daylight-saving gap
        }
    }

    private static uint ToLocalSeconds(DateTime value)
    {
        DateTime local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        return (uint)(DateTime.SpecifyKind(local, DateTimeKind.Utc) - MacEpoch).TotalSeconds;
    }
}
