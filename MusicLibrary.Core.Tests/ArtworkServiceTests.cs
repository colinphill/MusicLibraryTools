using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class ArtworkServiceTests
{
    private readonly ArtworkService _art = new();
    private readonly MediaFileService _reader = new();

    private static string MakePng(int w, int h)
    {
        var path = Path.Combine(Path.GetTempPath(), "img_" + Guid.NewGuid().ToString("N") + ".png");
        using var image = new Image<Rgba32>(w, h);
        image.Mutate(x => x.BackgroundColor(Color.Red));
        image.Save(path, new PngEncoder());
        return path;
    }

    private static string MakeQuadrantPng(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), "img_" + Guid.NewGuid().ToString("N") + ".png");
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                    row[x] = (x < width / 2, y < height / 2) switch
                    {
                        (true, true) => new Rgba32(255, 0, 0),
                        (false, true) => new Rgba32(0, 255, 0),
                        (true, false) => new Rgba32(0, 0, 255),
                        _ => new Rgba32(255, 255, 0),
                    };
            }
        });
        image.Save(path, new PngEncoder());
        return path;
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    public async Task SetCover_EmbedsResizedArtwork(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        var png = MakePng(500, 500);
        try
        {
            var result = await _art.SetCoverFromFileAsync(media.Path, png, maxDimension: 300);
            Assert.True(result.Success, result.Error);

            var reload = await _reader.LoadAsync(media.Path);
            Assert.True(reload.Success, reload.Error);
            var cover = Assert.Single(reload.Value!.Artwork);
            Assert.Equal("image/jpeg", cover.ImageType);
            Assert.True(cover.Data.Length > 0);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task Scrub_DownscalesExistingCover()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var png = MakePng(600, 600);
        try
        {
            await _art.SetCoverFromFileAsync(media.Path, png); // embed full-size
            var scrub = await _art.ScrubAsync(media.Path, maxDimension: 120);
            Assert.True(scrub.Success, scrub.Error);
            Assert.True(scrub.Width <= 120 && scrub.Height <= 120);

            var reload = await _reader.LoadAsync(media.Path);
            var cover = Assert.Single(reload.Value!.Artwork);
            Assert.True(cover.Width <= 120);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task SaveImages_EmbedsMultipleTypedCovers()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var frontPng = MakePng(300, 300);
        var backPng = MakePng(280, 280);
        try
        {
            var front = await _art.PrepareFromFileAsync(frontPng, 250);
            var back = await _art.PrepareFromFileAsync(backPng, 250);
            var inputs = new[]
            {
                new ArtworkInput(ID3v2Util.APICType.FrontCover, front!.MimeType, front.Data),
                new ArtworkInput(ID3v2Util.APICType.BackCover, back!.MimeType, back.Data),
            };

            var result = await _art.SaveImagesAsync(media.Path, inputs);
            Assert.True(result.Success, result.Error);

            var reload = await _reader.LoadAsync(media.Path);
            Assert.Equal(2, reload.Value!.Artwork.Count);
            var categories = reload.Value!.Artwork.Select(a => a.Category).ToList();
            Assert.Contains(categories, c => c!.Contains("Front", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(categories, c => c!.Contains("Back", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(frontPng);
            File.Delete(backPng);
        }
    }

    [Theory]
    [InlineData(640, 320, 200, 100)]
    [InlineData(320, 640, 100, 200)]
    public async Task Scrub_FitsArtworkInsideBoundingBoxWithoutStretchingOrCropping(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string png = MakeQuadrantPng(sourceWidth, sourceHeight);
        try
        {
            ArtworkOpResult embedded = await _art.SetCoverFromFileAsync(media.Path, png);
            Assert.True(embedded.Success, embedded.Error);

            ArtworkOpResult scrubbed = await _art.ScrubAsync(media.Path, maxDimension: 200, quality: 95);
            Assert.True(scrubbed.Success, scrubbed.Error);
            Assert.Equal(expectedWidth, scrubbed.Width);
            Assert.Equal(expectedHeight, scrubbed.Height);
            Assert.True(scrubbed.Width <= 200 && scrubbed.Height <= 200);

            MediaFileModel reloaded = (await _reader.LoadAsync(media.Path)).Value!;
            ArtworkModel cover = Assert.Single(reloaded.Artwork);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(cover.Data);
            Assert.Equal(expectedWidth, decoded.Width);
            Assert.Equal(expectedHeight, decoded.Height);

            // Sample well inside each quadrant. Retaining all four markers proves the transform
            // fitted the whole source rather than cropping it; the expected non-square dimensions
            // prove it was not stretched to the square bound.
            AssertMostlyRed(decoded[decoded.Width / 4, decoded.Height / 4]);
            AssertMostlyGreen(decoded[decoded.Width * 3 / 4, decoded.Height / 4]);
            AssertMostlyBlue(decoded[decoded.Width / 4, decoded.Height * 3 / 4]);
            AssertMostlyYellow(decoded[decoded.Width * 3 / 4, decoded.Height * 3 / 4]);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task PrepareArtwork_DoesNotEnlargeImagesAlreadyInsideBoundingBox()
    {
        string png = MakeQuadrantPng(120, 60);
        try
        {
            PreparedImage? prepared = await _art.PrepareFromFileAsync(png, maxDimension: 200);

            Assert.NotNull(prepared);
            Assert.Equal(120, prepared.Width);
            Assert.Equal(60, prepared.Height);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task SaveImages_PreservesPictureDescription()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var png = MakePng(100, 100);
        try
        {
            var prepared = await _art.PrepareFromFileAsync(png);
            var result = await _art.SaveImagesAsync(media.Path,
            [
                new ArtworkInput(
                    ID3v2Util.APICType.FrontCover,
                    prepared!.MimeType,
                    prepared.Data,
                    "Original scan")
            ]);

            Assert.True(result.Success, result.Error);
            var cover = Assert.Single((await _reader.LoadAsync(media.Path)).Value!.Artwork);
            Assert.Equal("Original scan", cover.Description);

            // Callers compiled against the original three-field ArtworkInput API do not supply a
            // description. Re-saving those same bytes must not erase the existing description.
            var resave = await _art.SaveImagesAsync(media.Path,
            [
                new ArtworkInput(ID3v2Util.APICType.FrontCover, cover.ImageType!, cover.Data)
            ]);
            Assert.True(resave.Success, resave.Error);
            Assert.Equal("Original scan",
                Assert.Single((await _reader.LoadAsync(media.Path)).Value!.Artwork).Description);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task SuccessfulArtworkWrite_ReportsCacheFailureWithoutBecomingDiskFailure()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var reindex = new ThrowingReindexService();
        var service = new ArtworkService(reindex);

        var result = await service.RemoveAsync(media.Path);

        Assert.True(result.Success);
        Assert.Contains("cache unavailable", result.CacheError);
        Assert.False(reindex.ReceivedToken.CanBeCanceled);
    }

    [Fact]
    public async Task Remove_ClearsArtwork()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var png = MakePng(200, 200);
        try
        {
            await _art.SetCoverFromFileAsync(media.Path, png);
            var removed = await _art.RemoveAsync(media.Path);
            Assert.True(removed.Success, removed.Error);

            var reload = await _reader.LoadAsync(media.Path);
            Assert.Empty(reload.Value!.Artwork);
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    [InlineData("sample.dsf")]
    public void SupportsWrite_TrueForEveryTaggableFormat(string fixture)
    {
        Assert.True(_art.SupportsWrite(MediaFixtures.Path_(fixture)));
    }

    [Fact]
    public async Task Mp4_And_WavPack_RoundTripAndRemove()
    {
        foreach (var fixture in new[] { "sample_alac.m4a", "sample.wv" })
        {
            using var media = MediaFixtures.Copy(fixture);
            var png = MakePng(400, 400);
            try
            {
                var set = await _art.SetCoverFromFileAsync(media.Path, png, maxDimension: 250);
                Assert.True(set.Success, $"{fixture}: {set.Error}");

                var reload = await _reader.LoadAsync(media.Path);
                var cover = Assert.Single(reload.Value!.Artwork);
                Assert.Equal("image/jpeg", cover.ImageType);

                var removed = await _art.RemoveAsync(media.Path);
                Assert.True(removed.Success, $"{fixture}: {removed.Error}");
                var reload2 = await _reader.LoadAsync(media.Path);
                Assert.Empty(reload2.Value!.Artwork);
            }
            finally
            {
                File.Delete(png);
            }
        }
    }

    private sealed class ThrowingReindexService : IReindexService
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            ReceivedToken = ct;
            throw new InvalidOperationException("cache unavailable");
        }
    }

    private static void AssertMostlyRed(Rgba32 pixel)
    {
        Assert.True(pixel.R > 160 && pixel.G < 100 && pixel.B < 100, $"Expected red, got {pixel}.");
    }

    private static void AssertMostlyGreen(Rgba32 pixel)
    {
        Assert.True(pixel.G > 160 && pixel.R < 100 && pixel.B < 100, $"Expected green, got {pixel}.");
    }

    private static void AssertMostlyBlue(Rgba32 pixel)
    {
        Assert.True(pixel.B > 160 && pixel.R < 100 && pixel.G < 100, $"Expected blue, got {pixel}.");
    }

    private static void AssertMostlyYellow(Rgba32 pixel)
    {
        Assert.True(pixel.R > 160 && pixel.G > 160 && pixel.B < 100, $"Expected yellow, got {pixel}.");
    }
}
