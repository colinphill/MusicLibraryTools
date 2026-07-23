using MusicFileUtilities;
using System.Text;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class Id3EncodingPolicyTests
{
    [Theory]
    [InlineData(ID3TextEncodingPolicy.Latin1, 0)]
    [InlineData(ID3TextEncodingPolicy.Utf16, 1)]
    [InlineData(ID3TextEncodingPolicy.Utf8, 3)]
    public void SelectedEncodingIsWrittenForNewAndExistingText(
        ID3TextEncodingPolicy policy,
        byte expectedMarker)
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        var mp3 = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        mp3.ChangeVersion(ID3v2Version.V24);

        mp3.SetTextEncodingPolicy(policy);
        mp3.SetField(TagFields.Title, "Encoding test");

        TextFrame title = Assert.IsType<TextFrame>(
            mp3.Frames.Single(frame => frame.FrameID == "TIT2"));
        Assert.Equal(expectedMarker, title.Data[0]);
        mp3.Save();
        Assert.Equal(
            "Encoding test",
            MediaFile.GetFile(media.Path).Tags.First().Title);
    }

    [Fact]
    public void Utf8RequiresV24AndLatin1RejectsUnrepresentableText()
    {
        var tag = new ID3v2Tag();
        Assert.Throws<InvalidOperationException>(
            () => tag.SetTextEncodingPolicy(ID3TextEncodingPolicy.Utf8));

        tag.SetTextEncodingPolicy(
            ID3TextEncodingPolicy.Latin1,
            reencodeExistingFrames: false);
        Assert.Throws<EncoderFallbackException>(
            () => tag.SetField(TagFields.Title, "日本語"));
    }
}
