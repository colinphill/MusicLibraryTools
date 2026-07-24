using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

/// <summary>
/// Registry-wide parser corpus contract. Format-specific suites retain deeper
/// structural edge cases; this suite ensures every parser family participates
/// in the same valid, unusual, truncated, and corrupt input gate.
/// </summary>
public sealed class ParserCorpusTests
{
    public static TheoryData<string> ParserFamilyFixtures => new()
    {
        "sample.dsf",
        "sample_aac.m4a",
        "sample.mp3",
        "sample.flac",
        "sample.ogg",
        "sample.wv",
        "sample.wav",
        "sample.aiff",
        "sample.aac",
        "sample.ape",
        "sample.mpc",
        "sample.tta",
        "sample.tak",
        "sample.ofr",
        "sample.wma",
        "sample.mka",
    };

    public static TheoryData<string> UnusualFixtures => new()
    {
        "sample_hires.flac",
        "sample_multi.flac",
        "sample_alac.m4a",
        "sample_aac.m4a",
        "sample.ofs",
        "sample.off",
        "sample.webm",
    };

    [Theory]
    [MemberData(nameof(ParserFamilyFixtures))]
    public void ValidCorpusParsesWithARecognizedCodec(string fixture)
    {
        IMediaFile media = MediaFile.GetFile(MediaFixtures.Path_(fixture));

        Assert.NotEmpty(media.Codecs);
        Assert.All(media.Codecs, codec =>
            Assert.False(string.IsNullOrWhiteSpace(codec.CodecName)));
    }

    [Theory]
    [MemberData(nameof(UnusualFixtures))]
    public void UnusualCorpusParsesWithARecognizedCodec(string fixture)
    {
        IMediaFile media = MediaFile.GetFile(MediaFixtures.Path_(fixture));

        Assert.NotEmpty(media.Codecs);
        Assert.All(media.Codecs, codec =>
            Assert.False(string.IsNullOrWhiteSpace(codec.CodecName)));
    }

    [Theory]
    [MemberData(nameof(ParserFamilyFixtures))]
    public void TruncatedCorpusIsRejectedWithADataException(string fixture)
    {
        byte[] source = File.ReadAllBytes(MediaFixtures.Path_(fixture));
        Assert.True(source.Length > 7);
        using var media = WriteCorpusFile(
            fixture,
            source.AsSpan(0, 7).ToArray());

        Exception error = Assert.ThrowsAny<Exception>(
            () => MediaFile.GetFile(media.Path));

        AssertDataException(fixture, error);
    }

    [Theory]
    [MemberData(nameof(ParserFamilyFixtures))]
    public void CorruptCorpusIsRejectedWithADataException(string fixture)
    {
        byte[] source = File.ReadAllBytes(MediaFixtures.Path_(fixture));
        byte[] corrupt = Enumerable.Repeat(
                (byte)0xa5,
                Math.Min(source.Length, 4096))
            .ToArray();
        using var media = WriteCorpusFile(fixture, corrupt);

        Exception error = Assert.ThrowsAny<Exception>(
            () => MediaFile.GetFile(media.Path));

        AssertDataException(fixture, error);
    }

    private static void AssertDataException(
        string fixture,
        Exception error) =>
        Assert.True(
            error is InvalidDataException or EndOfStreamException,
            $"{fixture} raised incidental {error.GetType().Name}: " +
            error.Message);

    private static MediaFixtures.TempMedia WriteCorpusFile(
        string fixture,
        byte[] bytes)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "mlt-corpus-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(fixture));
        File.WriteAllBytes(path, bytes);
        return new(path);
    }
}
