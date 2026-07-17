using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Validates the hand-rolled container/codec parsers against real ffmpeg-encoded files.
    public class MediaCodecTests
    {
        [Theory]
        [InlineData("sample.flac", "FLAC", CodecType.Lossless)]
        [InlineData("sample.mp3", "MP3", CodecType.Lossy)]
        [InlineData("sample.ogg", "Vorbis", CodecType.Lossy)]
        [InlineData("sample_alac.m4a", "ALAC", CodecType.Lossless)]
        [InlineData("sample_aac.m4a", "AAC", CodecType.Lossy)]
        [InlineData("sample.wv", "WavPack", CodecType.Lossless)]
        public void CodecIdentityAndFormatProperties(string file, string codecName, CodecType type)
        {
            var mf = MediaFile.GetFile(MediaFixtures.Path_(file));
            var c = mf.Codecs.First();

            Assert.Equal(codecName, c.CodecName);
            Assert.Equal(type, c.CodecType);
            Assert.Equal(44100u, c.Samplerate);
            Assert.Equal(2u, c.Channels);
            Assert.Equal(16u, c.BitsPerSample);
        }

        [Theory]
        [InlineData("sample.flac")]
        [InlineData("sample.mp3")]
        [InlineData("sample_alac.m4a")]
        [InlineData("sample_aac.m4a")]
        [InlineData("sample.wv")]
        public void DurationAndBitrateAreComputed(string file)
        {
            var c = MediaFile.GetFile(MediaFixtures.Path_(file)).Codecs.First();
            // 0.3s clip -> ~22 CD frames (75/s). Just assert it parsed to something positive.
            Assert.True(c.DurationInFrames > 0, "expected a positive duration");
            Assert.True(c.AverageBitrate > 0, "expected a positive bitrate");
        }

        // Regression: sample.flac / sample.wv are sub-second, so their duration in whole
        // seconds is 0. The old bitrate math divided by that and threw DivideByZeroException.
        [Theory]
        [InlineData("sample.flac")]
        [InlineData("sample.wv")]
        public void SubSecondLosslessFilesDoNotDivideByZero(string file)
        {
            var ex = Record.Exception(() => MediaFile.GetFile(MediaFixtures.Path_(file)).Codecs.First().AverageBitrate);
            Assert.Null(ex);
        }

        [Theory]
        [InlineData("sample.flac")]
        [InlineData("sample.mp3")]
        [InlineData("sample.dsf")]
        [InlineData("sample_alac.m4a")]
        [InlineData("sample.wv")]
        public void KnownFileLengthPreservesCodecAndMetadataProjections(string file)
        {
            string path = MediaFixtures.Path_(file);
            var baseline = MediaFile.GetFile(path, readOnly: true, readArtwork: false);
            var hinted = MediaFile.GetFile(
                path,
                readOnly: true,
                readArtwork: false,
                knownLength: new System.IO.FileInfo(path).Length);
            var baselineCodec = baseline.Codecs.First();
            var hintedCodec = hinted.Codecs.First();

            Assert.Equal(baselineCodec.CodecName, hintedCodec.CodecName);
            Assert.Equal(baselineCodec.AverageBitrate, hintedCodec.AverageBitrate);
            Assert.Equal(baselineCodec.Samplerate, hintedCodec.Samplerate);
            Assert.Equal(baselineCodec.Channels, hintedCodec.Channels);
            Assert.Equal(baselineCodec.BitsPerSample, hintedCodec.BitsPerSample);
            Assert.Equal(baselineCodec.DurationInFrames, hintedCodec.DurationInFrames);
            Assert.Equal(
                baseline.Tags.First().GetKnownMetadata()
                    .OrderBy(value => value.Key).ThenBy(value => value.Value),
                hinted.Tags.First().GetKnownMetadata()
                    .OrderBy(value => value.Key).ThenBy(value => value.Value));
        }

        [Fact]
        public void UnsupportedExtensionThrows()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".xyz");
            System.IO.File.Copy(MediaFixtures.Path_("sample.flac"), tmp, true);
            try { Assert.Throws<System.ArgumentException>(() => MediaFile.GetFile(tmp)); }
            finally { System.IO.File.Delete(tmp); }
        }

        [Fact]
        public void MissingFileThrowsFileNotFound()
        {
            Assert.Throws<System.IO.FileNotFoundException>(
                () => MediaFile.GetFile(MediaFixtures.Path_("does-not-exist.flac")));
        }
    }
}
