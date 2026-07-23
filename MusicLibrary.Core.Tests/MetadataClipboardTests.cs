using MusicFileUtilities;
using MusicLibrary.Core.Models;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MetadataClipboardTests
{
    [Fact]
    public void Known_field_round_trip_preserves_identity_order_and_newlines()
    {
        var original = new MetadataClipboardPayload(
            MetadataFieldKey.Known(TagFields.Comment),
            ["first", "line one\nline two", "third"]);

        string text = MetadataClipboardCodec.Encode(original);
        bool decoded = MetadataClipboardCodec.TryDecode(
            text,
            out MetadataClipboardPayload? result);

        Assert.True(decoded);
        Assert.Equal(original.Field, result!.Field);
        Assert.Equal(original.Values, result.Values);
        Assert.StartsWith(
            MetadataClipboardCodec.Header + "\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_field_round_trip_preserves_native_name()
    {
        var original = new MetadataClipboardPayload(
            MetadataFieldKey.Custom("DJ_SET"),
            ["Warmup", "Peak"]);

        MetadataClipboardPayload result =
            MetadataClipboardCodec.DecodeOrPlainText(
                MetadataClipboardCodec.Encode(original),
                MetadataFieldKey.Known(TagFields.Title));

        Assert.Equal("DJ_SET", result.Field.CustomName);
        Assert.Equal(["Warmup", "Peak"], result.Values);
    }

    [Fact]
    public void Plain_text_uses_selected_field_and_line_order()
    {
        MetadataClipboardPayload result =
            MetadataClipboardCodec.DecodeOrPlainText(
                "First\r\nSecond\nThird",
                MetadataFieldKey.Known(TagFields.Artist));

        Assert.Equal(TagFields.Artist, result.Field.KnownField);
        Assert.Equal(["First", "Second", "Third"], result.Values);
    }

    [Fact]
    public void Corrupt_structured_payload_is_not_treated_as_plain_text()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MetadataClipboardCodec.DecodeOrPlainText(
                MetadataClipboardCodec.Header + "\n{broken",
                MetadataFieldKey.Known(TagFields.Title)));

        Assert.Contains("malformed", error.Message);
    }
}
