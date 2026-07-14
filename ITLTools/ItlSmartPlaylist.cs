using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

public enum ItlSmartConjunction : uint
{
    All = 0,
    Any = 1,
}

public enum ItlSmartSign : byte
{
    PositiveInteger = 0,
    PositiveString = 1,
    NegativeInteger = 2,
    NegativeString = 3,
}

public enum ItlSmartOperator : ushort
{
    Other = 0,
    Is = 0x0001,
    Contains = 0x0002,
    StartsWith = 0x0004,
    EndsWith = 0x0008,
    GreaterThan = 0x0010,
    GreaterThanOrEqual = 0x0020,
    LessThan = 0x0040,
    LessThanOrEqual = 0x0080,
    Between = 0x0100,
    Within = 0x0200,
    BinaryAnd = 0x0400,
    /// <summary>The value must use only the first mask's bits and intersect the second mask.</summary>
    AllowedAndRequiredBits = 0x0800,
}

public enum ItlSmartField : uint
{
    NestedRuleSet = 0x00,
    Name = 0x02,
    Album = 0x03,
    Artist = 0x04,
    BitRate = 0x05,
    SampleRate = 0x06,
    Year = 0x07,
    Genre = 0x08,
    Kind = 0x09,
    DateModified = 0x0A,
    TrackNumber = 0x0B,
    Size = 0x0C,
    Duration = 0x0D,
    Comments = 0x0E,
    DateAdded = 0x10,
    Composer = 0x12,
    PlayCount = 0x16,
    PlayDate = 0x17,
    DiscNumber = 0x18,
    Rating = 0x19,
    Disabled = 0x1D,
    Compilation = 0x1F,
    Bpm = 0x23,
    HasArtwork = 0x25,
    Grouping = 0x27,
    PlaylistPersistentId = 0x28,
    Purchased = 0x29,
    Description = 0x36,
    Category = 0x37,
    Podcast = 0x39,
    MediaKind = 0x3C,
    Series = 0x3E,
    Season = 0x3F,
    SkipCount = 0x44,
    SkipDate = 0x45,
    AlbumArtist = 0x47,
    SortName = 0x4E,
    SortAlbum = 0x4F,
    SortAlbumArtist = 0x51,
    SortComposer = 0x52,
    SortSeries = 0x53,
    VideoRating = 0x59,
    AlbumRating = 0x5A,
    Location = 0x85,
    CloudStatus = 0x86,
    Love = 0x9A,
}

public enum ItlSmartValueKind
{
    Unknown,
    NestedRuleSet,
    String,
    Integer,
    Boolean,
    Date,
    MediaKind,
    Playlist,
    Love,
    CloudStatus,
    Location,
}

public enum ItlSmartLimitUnit : byte
{
    Minutes = 1,
    Megabytes = 2,
    Items = 3,
    Hours = 4,
    Gigabytes = 5,
}

public enum ItlSmartSortField : uint
{
    LowestRating = 0x01,
    Random = 0x02,
    Name = 0x05,
    Album = 0x06,
    Artist = 0x07,
    Genre = 0x09,
    DateAdded = 0x15,
    PlayCount = 0x19,
    PlayDate = 0x1A,
    Rating = 0x1C,
}

public sealed class ItlSmartPlaylistInfo
{
    public bool LiveUpdating { get; set; }
    public bool MatchRules { get; set; }
    public bool HasLimit { get; set; }
    public ItlSmartLimitUnit LimitUnit { get; set; }
    public ItlSmartSortField SortField { get; set; }
    public uint LimitSize { get; set; }
    public bool CheckedOnly { get; set; }
    public bool Descending { get; set; }
    public required byte[] Raw { get; init; }
}

public sealed class ItlSmartRule
{
    public ItlSmartField Field { get; set; }
    public uint RawField => (uint)Field;
    public ItlSmartSign Sign { get; set; }
    public ItlSmartOperator Operator { get; set; }
    public byte HeaderUnknown { get; init; }
    public required byte[] HeaderPadding { get; init; }
    public ItlSmartValueKind ValueKind { get; set; }
    public string? StringValue { get; set; }
    public List<long> IntegerValues { get; set; } = [];
    public List<DateTime> DateValues { get; set; } = [];
    public long RelativeSeconds { get; set; }
    public ulong? PlaylistPersistentId { get; set; }
    public ItlSmartCriteria? NestedCriteria { get; set; }
    public required byte[] RawValue { get; init; }

    public static ItlSmartRule CreateString(ItlSmartField field, ItlSmartOperator operation, string value,
        bool negate = false) => new()
    {
        Field = field,
        Sign = negate ? ItlSmartSign.NegativeString : ItlSmartSign.PositiveString,
        Operator = operation,
        HeaderPadding = new byte[44],
        ValueKind = ItlSmartValueKind.String,
        StringValue = value,
        RawValue = [],
    };

    public static ItlSmartRule CreateInteger(ItlSmartField field, ItlSmartOperator operation,
        IEnumerable<long> values, bool negate = false) => CreateNumeric(
        field, operation, ItlSmartValueKind.Integer, values, negate);

    public static ItlSmartRule CreateBoolean(ItlSmartField field, bool value, bool negate = false) => CreateNumeric(
        field, ItlSmartOperator.Is, ItlSmartValueKind.Boolean, [value ? 1 : 0, value ? 1 : 0, 0], negate);

    public static ItlSmartRule CreateMediaKind(long mask, ItlSmartOperator operation = ItlSmartOperator.Is,
        bool negate = false) => CreateNumeric(
        ItlSmartField.MediaKind, operation, ItlSmartValueKind.MediaKind, [mask, mask, 0], negate);

    /// <summary>
    /// Creates a media-kind comparison with separate allowed and required masks. For
    /// <see cref="ItlSmartOperator.AllowedAndRequiredBits"/>, a value must contain no bits outside
    /// <paramref name="allowedMask"/> and must intersect <paramref name="requiredMask"/>.
    /// </summary>
    public static ItlSmartRule CreateMediaKindValues(long allowedMask, long requiredMask, ItlSmartOperator operation,
        bool negate = false) => CreateNumeric(
        ItlSmartField.MediaKind, operation, ItlSmartValueKind.MediaKind, [allowedMask, requiredMask, 0], negate);

    public static ItlSmartRule CreateLocation(long value, bool negate = false) => CreateNumeric(
        ItlSmartField.Location, ItlSmartOperator.BinaryAnd, ItlSmartValueKind.Location, [value, value, 0], negate);

    public static ItlSmartRule CreateLove(long value, bool negate = false) => CreateNumeric(
        ItlSmartField.Love, ItlSmartOperator.Is, ItlSmartValueKind.Love, [value, value, 0], negate);

    public static ItlSmartRule CreateCloudStatus(long value, bool negate = false) => CreateNumeric(
        ItlSmartField.CloudStatus, ItlSmartOperator.Is, ItlSmartValueKind.CloudStatus, [value, value, 0], negate);

    public static ItlSmartRule CreateRelativeDate(ItlSmartField field, long relativeSeconds, bool negate = false)
    {
        byte[] raw = new byte[68];
        foreach (int offset in new[] { 0, 4, 24, 28 })
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(offset), 0x2DAE2DAE);
        return new ItlSmartRule
        {
            Field = field,
            Sign = negate ? ItlSmartSign.NegativeInteger : ItlSmartSign.PositiveInteger,
            Operator = ItlSmartOperator.Within,
            HeaderPadding = new byte[44],
            ValueKind = ItlSmartValueKind.Date,
            IntegerValues = [0x2DAE2DAE, 0x2DAE2DAE],
            RelativeSeconds = relativeSeconds,
            RawValue = raw,
        };
    }

    public static ItlSmartRule CreateDate(ItlSmartField field, ItlSmartOperator operation,
        IEnumerable<DateTime> values, bool negate = false) => new()
    {
        Field = field,
        Sign = negate ? ItlSmartSign.NegativeInteger : ItlSmartSign.PositiveInteger,
        Operator = operation,
        HeaderPadding = new byte[44],
        ValueKind = ItlSmartValueKind.Date,
        DateValues = [.. values],
        RawValue = new byte[68],
    };

    public static ItlSmartRule CreatePlaylist(ulong persistentId, bool negate = false)
    {
        byte[] value = new byte[68];
        BinaryPrimitives.WriteUInt64BigEndian(value, persistentId);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(24), persistentId);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(44), 1);
        return new ItlSmartRule
        {
            Field = ItlSmartField.PlaylistPersistentId,
            Sign = negate ? ItlSmartSign.NegativeInteger : ItlSmartSign.PositiveInteger,
            Operator = ItlSmartOperator.Is,
            HeaderPadding = new byte[44],
            ValueKind = ItlSmartValueKind.Playlist,
            PlaylistPersistentId = persistentId,
            RawValue = value,
        };
    }

    public static ItlSmartRule CreateNested(ItlSmartCriteria criteria)
    {
        byte[] padding = new byte[44];
        padding[0] = 1;
        return new ItlSmartRule
        {
            Field = ItlSmartField.NestedRuleSet,
            Sign = ItlSmartSign.PositiveInteger,
            Operator = ItlSmartOperator.Is,
            HeaderPadding = padding,
            ValueKind = ItlSmartValueKind.NestedRuleSet,
            NestedCriteria = criteria,
            RawValue = [],
        };
    }

    private static ItlSmartRule CreateNumeric(ItlSmartField field, ItlSmartOperator operation,
        ItlSmartValueKind kind, IEnumerable<long> values, bool negate)
    {
        long[] supplied = [.. values];
        if (supplied.Length == 0)
            throw new ArgumentException("A numeric smart rule requires at least one value.", nameof(values));
        long[] encoded = supplied.Length switch
        {
            1 => [supplied[0], supplied[0], 0],
            2 => [supplied[0], supplied[1], 0],
            _ => supplied,
        };
        return new ItlSmartRule
        {
            Field = field,
            Sign = negate ? ItlSmartSign.NegativeInteger : ItlSmartSign.PositiveInteger,
            Operator = operation,
            HeaderPadding = new byte[44],
            ValueKind = kind,
            IntegerValues = [.. encoded],
            RawValue = new byte[68],
        };
    }
}

public sealed class ItlSmartCriteria
{
    public ItlSmartConjunction Conjunction { get; set; }
    public required List<ItlSmartRule> Rules { get; init; }
    public required byte[] HeaderPrefix { get; init; }
    public required byte[] HeaderPadding { get; init; }
    public required byte[] Raw { get; init; }

    public static ItlSmartCriteria Create(ItlSmartConjunction conjunction, params ItlSmartRule[] rules) => new()
    {
        Conjunction = conjunction,
        Rules = [.. rules],
        HeaderPrefix = [.. "SLst"u8, 0, 1, 0, 1],
        HeaderPadding = new byte[120],
        Raw = [],
    };
}

public sealed class ItlSmartPlaylist
{
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public required ItlSmartPlaylistInfo Info { get; init; }
    public required ItlSmartCriteria Criteria { get; init; }

    public static ItlSmartPlaylist Create(ItlSmartCriteria criteria) => new()
    {
        Info = new ItlSmartPlaylistInfo
        {
            LiveUpdating = true,
            MatchRules = true,
            LimitUnit = ItlSmartLimitUnit.Items,
            SortField = ItlSmartSortField.Random,
            Raw = new byte[112],
        },
        Criteria = criteria,
    };

    public (byte[] Info, byte[] Criteria) Encode() => (EncodeInfo(), EncodeCriteria(Criteria));

    public byte[] EncodeInfo()
    {
        byte[] result = (byte[])Info.Raw.Clone();
        if (result.Length != 112)
            throw new InvalidOperationException($"Smart Info template is {result.Length} bytes; expected 112.");
        result[0] = Info.LiveUpdating ? (byte)1 : (byte)0;
        result[1] = Info.MatchRules ? (byte)1 : (byte)0;
        result[2] = Info.HasLimit ? (byte)1 : (byte)0;
        result[3] = (byte)Info.LimitUnit;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)Info.SortField);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), Info.LimitSize);
        result[12] = Info.CheckedOnly ? (byte)1 : (byte)0;
        result[13] = Info.Descending ? (byte)0 : (byte)1;
        return result;
    }

    public byte[] EncodeCriteria() => EncodeCriteria(Criteria);

    private static byte[] EncodeCriteria(ItlSmartCriteria criteria)
    {
        if (criteria.HeaderPrefix.Length != 8 || criteria.HeaderPadding.Length != 120)
            throw new InvalidOperationException("Smart Criteria must retain its 8-byte prefix and 120-byte header padding.");

        var encodedRules = criteria.Rules.Select(EncodeRule).ToArray();
        byte[] result = new byte[136 + encodedRules.Sum(rule => rule.Length)];
        criteria.HeaderPrefix.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)criteria.Rules.Count);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), (uint)criteria.Conjunction);
        criteria.HeaderPadding.CopyTo(result, 16);
        int offset = 136;
        foreach (byte[] rule in encodedRules)
        {
            rule.CopyTo(result, offset);
            offset += rule.Length;
        }
        return result;
    }

    private static byte[] EncodeRule(ItlSmartRule rule)
    {
        if (rule.HeaderPadding.Length != 44)
            throw new InvalidOperationException("Smart rule must retain its 44-byte header padding.");

        byte[] value = EncodeRuleValue(rule);
        byte[] result = new byte[56 + value.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, rule.RawField);
        result[4] = (byte)rule.Sign;
        result[5] = rule.HeaderUnknown;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)rule.Operator);
        rule.HeaderPadding.CopyTo(result, 8);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(52), (uint)value.Length);
        value.CopyTo(result, 56);
        return result;
    }

    private static byte[] EncodeRuleValue(ItlSmartRule rule)
    {
        switch (rule.ValueKind)
        {
            case ItlSmartValueKind.NestedRuleSet:
                return rule.NestedCriteria is null
                    ? throw new InvalidOperationException("Nested smart rule has no child criteria.")
                    : EncodeCriteria(rule.NestedCriteria);
            case ItlSmartValueKind.String:
                return Encoding.BigEndianUnicode.GetBytes(rule.StringValue ?? "");
            case ItlSmartValueKind.Playlist:
                if (!rule.PlaylistPersistentId.HasValue)
                    throw new InvalidOperationException("Smart playlist-reference rule has no persistent ID.");
                byte[] id = rule.RawValue.Length >= 8 ? (byte[])rule.RawValue.Clone() : new byte[68];
                BinaryPrimitives.WriteUInt64BigEndian(id, rule.PlaylistPersistentId.Value);
                if (id.Length >= 68)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(20), 1);
                    BinaryPrimitives.WriteUInt64BigEndian(id.AsSpan(24), rule.PlaylistPersistentId.Value);
                    BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(44), 1);
                }
                return id;
            case ItlSmartValueKind.Unknown:
                return (byte[])rule.RawValue.Clone();
            default:
                return EncodeNumeric(rule);
        }
    }

    private static byte[] EncodeNumeric(ItlSmartRule rule)
    {
        byte[] result = rule.RawValue.Length >= 68 ? (byte[])rule.RawValue.Clone() : new byte[68];
        if (rule.ValueKind == ItlSmartValueKind.Date && rule.RelativeSeconds != 0)
        {
            long unit = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(20));
            if (unit <= 0 || rule.RelativeSeconds % unit != 0)
                unit = RelativeUnit(rule.RelativeSeconds);
            BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(8), rule.RelativeSeconds / unit);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), checked((uint)unit));
            return result;
        }

        IReadOnlyList<long> values;
        if (rule.ValueKind == ItlSmartValueKind.Date && rule.DateValues.Count > 0)
            values = rule.DateValues.Select(date => checked((long)(date.ToUniversalTime() - MacEpoch).TotalSeconds)).ToArray();
        else
            values = rule.IntegerValues;
        if (values.Count == 0)
            throw new InvalidOperationException($"Smart {rule.ValueKind} rule has no numeric value.");

        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), checked((uint)values[0]));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), values.Count > 1 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28), values.Count > 1 ? checked((uint)values[1]) : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(44), values.Count > 2 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(52), values.Count > 2 ? checked((uint)values[2]) : 0u);
        return result;

        static long RelativeUnit(long seconds)
        {
            long absolute = Math.Abs(seconds);
            foreach (long unit in new long[] { 365 * 86_400L, 30 * 86_400L, 7 * 86_400L, 86_400, 3_600, 60, 1 })
                if (absolute % unit == 0) return unit;
            return 1;
        }
    }

    public static ItlSmartPlaylist Parse(byte[] info, byte[] criteria)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(criteria);
        if (info.Length != 112)
            throw new InvalidDataException($"Smart Info is {info.Length} bytes; expected 112.");

        var parsedInfo = new ItlSmartPlaylistInfo
        {
            LiveUpdating = info[0] != 0,
            MatchRules = info[1] != 0,
            HasLimit = info[2] != 0,
            LimitUnit = (ItlSmartLimitUnit)info[3],
            SortField = (ItlSmartSortField)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(4)),
            LimitSize = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(8)),
            CheckedOnly = info[12] != 0,
            Descending = info[13] == 0,
            Raw = (byte[])info.Clone(),
        };

        return new ItlSmartPlaylist { Info = parsedInfo, Criteria = ParseCriteria(criteria, 0, out int read, true) };

        static ItlSmartCriteria ParseCriteria(byte[] bytes, int offset, out int consumed, bool requireAll)
        {
            const int headerLength = 136;
            if (offset < 0 || bytes.Length - offset < headerLength)
                throw new InvalidDataException("Smart Criteria ends inside its 136-byte rule-set header.");
            ReadOnlySpan<byte> header = bytes.AsSpan(offset, headerLength);
            if (!header[..4].SequenceEqual("SLst"u8))
                throw new InvalidDataException($"Smart Criteria at +{offset} does not start with 'SLst'.");

            uint count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            uint conjunction = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            if (count > 100_000)
                throw new InvalidDataException($"Smart Criteria declares an unreasonable {count} rules.");

            int position = checked(offset + headerLength);
            var rules = new List<ItlSmartRule>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                if (bytes.Length - position < 56)
                    throw new InvalidDataException($"Smart rule {index} ends inside its 56-byte header.");
                ReadOnlySpan<byte> ruleHeader = bytes.AsSpan(position, 56);
                uint field = BinaryPrimitives.ReadUInt32BigEndian(ruleHeader);
                byte sign = ruleHeader[4];
                byte unknown = ruleHeader[5];
                ushort operation = BinaryPrimitives.ReadUInt16BigEndian(ruleHeader[6..]);
                uint valueLength = BinaryPrimitives.ReadUInt32BigEndian(ruleHeader[52..]);
                position += 56;
                if (valueLength > int.MaxValue || bytes.Length - position < (int)valueLength)
                    throw new InvalidDataException($"Smart rule {index} declares {valueLength} value bytes past the blob end.");

                byte[] rawValue = bytes.AsSpan(position, (int)valueLength).ToArray();
                position += (int)valueLength;
                ItlSmartValueKind kind = KindOf((ItlSmartField)field);
                string? stringValue = null;
                IReadOnlyList<long> integers = [];
                IReadOnlyList<DateTime> dates = [];
                long relativeSeconds = 0;
                ulong? playlistId = null;
                ItlSmartCriteria? nested = null;

                switch (kind)
                {
                    case ItlSmartValueKind.NestedRuleSet:
                        nested = ParseCriteria(rawValue, 0, out int nestedRead, true);
                        if (nestedRead != rawValue.Length)
                            throw new InvalidDataException($"Nested smart rule set consumed {nestedRead} of {rawValue.Length} bytes.");
                        break;
                    case ItlSmartValueKind.String:
                        if ((rawValue.Length & 1) != 0)
                            throw new InvalidDataException($"Smart string rule {index} has odd byte length {rawValue.Length}.");
                        stringValue = Encoding.BigEndianUnicode.GetString(rawValue);
                        break;
                    case ItlSmartValueKind.Playlist:
                        if (rawValue.Length < 8)
                            throw new InvalidDataException($"Smart playlist-reference rule {index} is shorter than 8 bytes.");
                        playlistId = BinaryPrimitives.ReadUInt64BigEndian(rawValue);
                        break;
                    case ItlSmartValueKind.Integer:
                    case ItlSmartValueKind.Boolean:
                    case ItlSmartValueKind.Date:
                    case ItlSmartValueKind.MediaKind:
                    case ItlSmartValueKind.Love:
                    case ItlSmartValueKind.CloudStatus:
                    case ItlSmartValueKind.Location:
                        ParseNumeric(rawValue, kind, out integers, out dates, out relativeSeconds);
                        break;
                }

                rules.Add(new ItlSmartRule
                {
                    Field = (ItlSmartField)field,
                    Sign = (ItlSmartSign)sign,
                    Operator = (ItlSmartOperator)operation,
                    HeaderUnknown = unknown,
                    HeaderPadding = ruleHeader[8..52].ToArray(),
                    ValueKind = kind,
                    StringValue = stringValue,
                    IntegerValues = [.. integers],
                    DateValues = [.. dates],
                    RelativeSeconds = relativeSeconds,
                    PlaylistPersistentId = playlistId,
                    NestedCriteria = nested,
                    RawValue = rawValue,
                });
            }

            consumed = position - offset;
            if (requireAll && offset + consumed != bytes.Length)
                throw new InvalidDataException($"Smart Criteria has {bytes.Length - offset - consumed} trailing bytes.");
            return new ItlSmartCriteria
            {
                Conjunction = (ItlSmartConjunction)conjunction,
                Rules = rules,
                HeaderPrefix = header[..8].ToArray(),
                HeaderPadding = header[16..].ToArray(),
                Raw = bytes.AsSpan(offset, consumed).ToArray(),
            };
        }

        static void ParseNumeric(byte[] value, ItlSmartValueKind kind,
            out IReadOnlyList<long> integers, out IReadOnlyList<DateTime> dates, out long relativeSeconds)
        {
            if (value.Length < 68)
                throw new InvalidDataException($"Smart {kind} rule value is {value.Length} bytes; expected at least 68.");

            uint first = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(4));
            long relative = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(8));
            uint hasSecond = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(20));
            uint second = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(28));
            uint hasThird = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(44));
            uint third = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(52));
            var numeric = new List<long> { first };
            if (hasSecond != 0) numeric.Add(second);
            if (hasThird != 0) numeric.Add(third);
            integers = numeric;
            relativeSeconds = checked(relative * (long)hasSecond);

            if (kind != ItlSmartValueKind.Date)
            {
                dates = [];
                return;
            }
            dates = numeric.Where(item => item != 0 && item != 0x2DAE2DAE)
                .Select(item => MacEpoch.AddSeconds(item)).ToArray();
        }

        static ItlSmartValueKind KindOf(ItlSmartField field) => field switch
        {
            ItlSmartField.NestedRuleSet => ItlSmartValueKind.NestedRuleSet,
            ItlSmartField.Album or ItlSmartField.AlbumArtist or ItlSmartField.Artist or
                ItlSmartField.Category or ItlSmartField.Comments or ItlSmartField.Composer or
                ItlSmartField.Description or ItlSmartField.Genre or ItlSmartField.Grouping or
                ItlSmartField.Kind or ItlSmartField.Name or ItlSmartField.Series or
                ItlSmartField.SortName or ItlSmartField.SortAlbum or ItlSmartField.SortAlbumArtist or
                ItlSmartField.SortComposer or ItlSmartField.SortSeries or ItlSmartField.VideoRating
                => ItlSmartValueKind.String,
            ItlSmartField.Bpm or ItlSmartField.BitRate or ItlSmartField.DiscNumber or
                ItlSmartField.PlayCount or ItlSmartField.Rating or ItlSmartField.Podcast or
                ItlSmartField.SampleRate or ItlSmartField.Season or ItlSmartField.Size or
                ItlSmartField.SkipCount or ItlSmartField.Duration or ItlSmartField.TrackNumber or
                ItlSmartField.Year or ItlSmartField.AlbumRating => ItlSmartValueKind.Integer,
            ItlSmartField.Compilation or ItlSmartField.HasArtwork or ItlSmartField.Purchased or
                ItlSmartField.Disabled => ItlSmartValueKind.Boolean,
            ItlSmartField.DateAdded or ItlSmartField.DateModified or ItlSmartField.PlayDate or
                ItlSmartField.SkipDate => ItlSmartValueKind.Date,
            ItlSmartField.MediaKind => ItlSmartValueKind.MediaKind,
            ItlSmartField.PlaylistPersistentId => ItlSmartValueKind.Playlist,
            ItlSmartField.Love => ItlSmartValueKind.Love,
            ItlSmartField.CloudStatus => ItlSmartValueKind.CloudStatus,
            ItlSmartField.Location => ItlSmartValueKind.Location,
            _ => ItlSmartValueKind.Unknown,
        };
    }
}
