using MusicFileUtilities;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.NonFree.Tests;

public sealed class LibFdkAacIntegrationTests
{
    private const string NonFreeFfmpeg = @"C:\ffmpeg\nonfree\ffmpeg.exe";

    [Fact(Timeout = 180_000)]
    [Trait("Category", "NonFreeIntegration")]
    public async Task RealLibFdkAac_Encodes256KbitCbrAndPreservesTags()
    {
        Assert.True(File.Exists(NonFreeFfmpeg), $"Required local ffmpeg was not found: {NonFreeFfmpeg}");
        string input = Path.Combine(AppContext.BaseDirectory, "TestFiles", "sample.flac");
        string output = Path.Combine(Path.GetTempPath(), $"mlt-libfdk-{Guid.NewGuid():N}.m4a");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var runner = new FfmpegRunner();
            await runner.PreflightAsync(NonFreeFfmpeg, "libfdk_aac", timeout.Token);
            await runner.EncodeAacAsync(NonFreeFfmpeg, "libfdk_aac", 256, input, output, timeout.Token);

            var media = MediaFile.GetFile(output);
            var codec = Assert.Single(media.Codecs);
            var tags = Assert.Single(media.Tags);
            Assert.Equal("AAC", codec.CodecName);
            Assert.Equal(CodecType.Lossy, codec.CodecType);
            Assert.Equal(44100u, codec.Samplerate);
            Assert.Equal(2u, codec.Channels);
            Assert.InRange(codec.AverageBitrate, 240_000u, 270_000u);
            Assert.Equal("TestTitle", tags.Title);
            Assert.Equal("TestArtist", tags.Artist);
            Assert.Equal("TestAlbum", tags.Album);
            Assert.Equal(3, tags.TrackNumber);
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }
}
