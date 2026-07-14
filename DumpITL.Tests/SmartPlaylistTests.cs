using System.Buffers.Binary;
using System.Text;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class SmartPlaylistTests
{
    [Fact]
    public void ParsesInfoAndRecursiveTypedCriteria()
    {
        byte[] info = new byte[112];
        info[0] = 1;
        info[1] = 1;
        info[2] = 1;
        info[3] = (byte)ItlSmartLimitUnit.Items;
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(4), (uint)ItlSmartSortField.Random);
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(8), 25);
        info[12] = 1;
        info[13] = 0;

        byte[] nested = Criteria(ItlSmartConjunction.Any,
            Rule(ItlSmartField.Genre, ItlSmartSign.PositiveString, ItlSmartOperator.Contains,
                Encoding.BigEndianUnicode.GetBytes("Synth Pop")),
            Rule(ItlSmartField.PlayCount, ItlSmartSign.PositiveInteger, ItlSmartOperator.GreaterThan,
                Numeric(5, 5)));
        byte[] criteria = Criteria(ItlSmartConjunction.All,
            Rule(ItlSmartField.NestedRuleSet, ItlSmartSign.PositiveInteger, ItlSmartOperator.Is, nested),
            Rule(ItlSmartField.PlaylistPersistentId, ItlSmartSign.NegativeInteger, ItlSmartOperator.Is,
                U64(0x1122334455667788)));

        ItlSmartPlaylist smart = ItlSmartPlaylist.Parse(info, criteria);

        Assert.True(smart.Info.LiveUpdating);
        Assert.True(smart.Info.MatchRules);
        Assert.True(smart.Info.HasLimit);
        Assert.True(smart.Info.CheckedOnly);
        Assert.True(smart.Info.Descending);
        Assert.Equal(ItlSmartLimitUnit.Items, smart.Info.LimitUnit);
        Assert.Equal(ItlSmartSortField.Random, smart.Info.SortField);
        Assert.Equal(25u, smart.Info.LimitSize);
        Assert.Equal(ItlSmartConjunction.All, smart.Criteria.Conjunction);
        Assert.Equal(2, smart.Criteria.Rules.Count);

        ItlSmartCriteria child = smart.Criteria.Rules[0].NestedCriteria!;
        Assert.Equal(ItlSmartConjunction.Any, child.Conjunction);
        Assert.Equal("Synth Pop", child.Rules[0].StringValue);
        Assert.Equal(ItlSmartValueKind.Integer, child.Rules[1].ValueKind);
        Assert.Equal(5, child.Rules[1].IntegerValues[0]);
        Assert.Equal(0x1122334455667788UL, smart.Criteria.Rules[1].PlaylistPersistentId);
        (byte[] encodedInfo, byte[] encodedCriteria) = smart.Encode();
        Assert.Equal(info, encodedInfo);
        Assert.Equal(criteria, encodedCriteria);
    }

    [Fact]
    public void ParsesRelativeDateAndPreservesUnknownRules()
    {
        byte[] relative = Numeric(0x2DAE2DAE, 0x2DAE2DAE);
        BinaryPrimitives.WriteInt64BigEndian(relative.AsSpan(8), -7);
        BinaryPrimitives.WriteUInt32BigEndian(relative.AsSpan(20), 86_400);
        byte[] unknown = Enumerable.Range(0, 17).Select(value => (byte)value).ToArray();
        byte[] criteria = Criteria(ItlSmartConjunction.All,
            Rule(ItlSmartField.DateAdded, ItlSmartSign.PositiveInteger, ItlSmartOperator.Within, relative),
            Rule((ItlSmartField)0xDEAD, ItlSmartSign.PositiveInteger,
                ItlSmartOperator.AllowedAndRequiredBits, unknown));

        ItlSmartPlaylist smart = ItlSmartPlaylist.Parse(new byte[112], criteria);

        ItlSmartRule date = smart.Criteria.Rules[0];
        Assert.Equal(ItlSmartValueKind.Date, date.ValueKind);
        Assert.Equal(-604_800, date.RelativeSeconds);
        Assert.Empty(date.DateValues);
        ItlSmartRule unmodeled = smart.Criteria.Rules[1];
        Assert.Equal(ItlSmartValueKind.Unknown, unmodeled.ValueKind);
        Assert.Equal(0xDEADu, unmodeled.RawField);
        Assert.Equal(unknown, unmodeled.RawValue);
        Assert.Equal(criteria, smart.EncodeCriteria());
    }

    [Fact]
    public void RejectsTruncatedCriteriaWithControlledFormatError()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ItlSmartPlaylist.Parse(new byte[112], "SLst"u8.ToArray()));
        Assert.Contains("136-byte", exception.Message);
    }

    [Fact]
    public void EditableDocumentConvertsManualPlaylistWithoutChangingItsHeader()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord playlist = document.Playlists.Single();
        ItlSmartPlaylist template = ItlSmartPlaylist.Parse(new byte[112], Criteria(ItlSmartConjunction.All));
        byte[] originalHeader = (byte[])playlist.Header.Clone();

        document.SetSmartPlaylist(playlist, template);

        Assert.Equal(originalHeader, playlist.Header);
        Assert.All(playlist.Fields.Where(field => field.Type is
                (int)ItlDataType.SmartInfo or (int)ItlDataType.SmartCriteria),
            field => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))));
        ItlSmartPlaylist smart = ItlDocument.SmartPlaylistOf(playlist)!;
        smart.Info.LiveUpdating = true;
        smart.Info.CheckedOnly = true;
        document.SetSmartPlaylist(playlist, smart);

        ItlEnvelope written = ItlEnvelope.Parse(ItlWriter.Build(document.Envelope, document.Serialize()));
        ItlRecord reparsed = ItlDocument.Parse(written).Playlists.Single();
        ItlSmartPlaylist result = ItlDocument.SmartPlaylistOf(reparsed)!;
        Assert.True(result.Info.LiveUpdating);
        Assert.True(result.Info.CheckedOnly);
        Assert.DoesNotContain(ItlDocument.Parse(written).Validate(), issue => issue.Code.StartsWith("smart."));
    }

    [Fact]
    public void FactoriesEncodeSupportedRulesIntoAParseablePlaylist()
    {
        ItlSmartCriteria criteria = ItlSmartCriteria.Create(ItlSmartConjunction.All,
            ItlSmartRule.CreateNested(ItlSmartCriteria.Create(ItlSmartConjunction.Any,
                ItlSmartRule.CreateString(ItlSmartField.Genre, ItlSmartOperator.Contains, "Country"),
                ItlSmartRule.CreateMediaKind(1))),
            ItlSmartRule.CreateInteger(ItlSmartField.PlayCount, ItlSmartOperator.GreaterThan, [5]),
            ItlSmartRule.CreateRelativeDate(ItlSmartField.DateAdded, -7 * 86_400),
            ItlSmartRule.CreatePlaylist(0x1122334455667788, negate: true));
        ItlSmartPlaylist smart = ItlSmartPlaylist.Create(criteria);
        smart.Info.HasLimit = true;
        smart.Info.LimitSize = 50;
        smart.Info.LimitUnit = ItlSmartLimitUnit.Items;

        (byte[] info, byte[] encodedCriteria) = smart.Encode();
        ItlSmartPlaylist parsed = ItlSmartPlaylist.Parse(info, encodedCriteria);

        Assert.Equal(4, parsed.Criteria.Rules.Count);
        Assert.Equal("Country", parsed.Criteria.Rules[0].NestedCriteria!.Rules[0].StringValue);
        Assert.Equal([5L, 5L, 0L], parsed.Criteria.Rules[1].IntegerValues);
        Assert.Equal(-604_800, parsed.Criteria.Rules[2].RelativeSeconds);
        Assert.Equal(0x1122334455667788UL, parsed.Criteria.Rules[3].PlaylistPersistentId);
        Assert.Equal(68, parsed.Criteria.Rules[3].RawValue.Length);
        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64BigEndian(parsed.Criteria.Rules[3].RawValue));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(parsed.Criteria.Rules[3].RawValue.AsSpan(20)));
        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64BigEndian(parsed.Criteria.Rules[3].RawValue.AsSpan(24)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(parsed.Criteria.Rules[3].RawValue.AsSpan(44)));
        Assert.True(parsed.Info.HasLimit);
        Assert.Equal(50u, parsed.Info.LimitSize);
    }

    [Fact]
    public void MediaKindFactoryPreservesDistinctExperimentalOperands()
    {
        ItlSmartRule rule = ItlSmartRule.CreateMediaKindValues(33, 32, ItlSmartOperator.AllowedAndRequiredBits);
        ItlSmartPlaylist smart = ItlSmartPlaylist.Create(
            ItlSmartCriteria.Create(ItlSmartConjunction.All, rule));

        (byte[] info, byte[] criteria) = smart.Encode();
        ItlSmartRule parsed = ItlSmartPlaylist.Parse(info, criteria).Criteria.Rules.Single();

        Assert.Equal(ItlSmartField.MediaKind, parsed.Field);
        Assert.Equal(ItlSmartOperator.AllowedAndRequiredBits, parsed.Operator);
        Assert.Equal([33L, 32L, 0L], parsed.IntegerValues);
        Assert.Equal(68, parsed.RawValue.Length);
    }

    [Fact]
    public void AppleMediaServicesVideoFactoryRoundTripsAsBoolean()
    {
        ItlSmartRule rule = ItlSmartRule.CreateBoolean(ItlSmartField.AppleMediaServicesVideo, true);
        ItlSmartPlaylist smart = ItlSmartPlaylist.Create(
            ItlSmartCriteria.Create(ItlSmartConjunction.All, rule));

        (byte[] info, byte[] criteria) = smart.Encode();
        ItlSmartPlaylist parsed = ItlSmartPlaylist.Parse(info, criteria);
        ItlSmartRule parsedRule = parsed.Criteria.Rules.Single();

        Assert.Equal(0xA4u, parsedRule.RawField);
        Assert.Equal(ItlSmartField.AppleMediaServicesVideo, parsedRule.Field);
        Assert.Equal(ItlSmartValueKind.Boolean, parsedRule.ValueKind);
        Assert.Equal([1L, 1L, 0L], parsedRule.IntegerValues);
        Assert.Equal(68, parsedRule.RawValue.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(parsedRule.RawValue.AsSpan(4)));
        Assert.Equal(criteria, parsed.EncodeCriteria());
    }

    [Fact]
    public void AddsSmartPlaylistByCloningNativeSmartTemplate()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord template = document.Playlists.Single();
        ItlRecord manual = document.AddPlaylist("Manual", template);
        ItlSmartPlaylist templateSmart = ItlSmartPlaylist.Create(ItlSmartCriteria.Create(ItlSmartConjunction.All));
        (byte[] templateInfo, byte[] templateCriteria) = templateSmart.Encode();
        int fieldEnd = template.Children.FindLastIndex(child => child is ItlField) + 1;
        template.Children.Insert(fieldEnd, ItlField.CreateBlob((int)ItlDataType.SmartCriteria, templateCriteria));
        template.Children.Insert(fieldEnd + 1, ItlField.CreateBlob((int)ItlDataType.SmartInfo, templateInfo));

        ItlSmartPlaylist desired = ItlSmartPlaylist.Create(ItlSmartCriteria.Create(
            ItlSmartConjunction.All, ItlSmartRule.CreateInteger(
                ItlSmartField.PlayCount, ItlSmartOperator.GreaterThan, [5])));
        desired.Info.HasLimit = true;
        desired.Info.LimitSize = 3;
        desired.Info.Descending = true;

        Assert.Throws<InvalidOperationException>(() =>
            document.AddSmartPlaylist("Rejected", desired, manual, []));
        int initialTrackId = template.Entries.Single().TrackId;
        ItlRecord added = document.AddSmartPlaylist("Writer Smart", desired, template, [initialTrackId]);

        Assert.Equal([initialTrackId], added.Entries.Select(entry => entry.TrackId));
        Assert.NotEqual(ItlDocument.PlaylistRecordIdOf(template), ItlDocument.PlaylistRecordIdOf(added));
        Assert.All(added.Fields.Where(field => field.Type is
                (int)ItlDataType.SmartInfo or (int)ItlDataType.SmartCriteria),
            field => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))));
        ItlSmartPlaylist result = ItlDocument.SmartPlaylistOf(added)!;
        Assert.Equal(3u, result.Info.LimitSize);
        Assert.Equal(5, result.Criteria.Rules.Single().IntegerValues[0]);
        Assert.DoesNotContain(document.Validate(), issue => issue.Code.StartsWith("smart."));
    }

    [Fact]
    public void ValidationRejectsDanglingSmartPlaylistReferences()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord playlist = document.Playlists.Single();
        ItlSmartPlaylist smart = ItlSmartPlaylist.Create(ItlSmartCriteria.Create(
            ItlSmartConjunction.All, ItlSmartRule.CreatePlaylist(0x1122334455667788)));
        (byte[] info, byte[] criteria) = smart.Encode();
        int fieldEnd = playlist.Children.FindLastIndex(child => child is ItlField) + 1;
        playlist.Children.Insert(fieldEnd, ItlField.CreateBlob((int)ItlDataType.SmartCriteria, criteria));
        playlist.Children.Insert(fieldEnd + 1, ItlField.CreateBlob((int)ItlDataType.SmartInfo, info));

        Assert.Contains(document.Validate(), issue => issue.Code == "smart.playlist-link");
    }

    private static byte[] Criteria(ItlSmartConjunction conjunction, params byte[][] rules)
    {
        byte[] result = new byte[136 + rules.Sum(rule => rule.Length)];
        "SLst"u8.CopyTo(result);
        result[5] = 1;
        result[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)rules.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), (uint)conjunction);
        int offset = 136;
        foreach (byte[] rule in rules)
        {
            rule.CopyTo(result, offset);
            offset += rule.Length;
        }
        return result;
    }

    private static byte[] Rule(ItlSmartField field, ItlSmartSign sign, ItlSmartOperator operation, byte[] value)
    {
        byte[] result = new byte[56 + value.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)field);
        result[4] = (byte)sign;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)operation);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(52), (uint)value.Length);
        value.CopyTo(result, 56);
        return result;
    }

    private static byte[] Numeric(uint first, uint second)
    {
        byte[] result = new byte[68];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), first);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28), second);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(44), 1);
        return result;
    }

    private static byte[] U64(ulong value)
    {
        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(result, value);
        return result;
    }
}
