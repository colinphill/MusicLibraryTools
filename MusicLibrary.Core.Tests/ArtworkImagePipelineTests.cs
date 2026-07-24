using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using SkiaSharp;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkImagePipelineTests
{
    private static readonly SKColor A =
        SKColors.Red;
    private static readonly SKColor B =
        SKColors.Lime;
    private static readonly SKColor C =
        SKColors.Blue;
    private static readonly SKColor D =
        SKColors.Yellow;
    private static readonly SKColor E =
        SKColors.Magenta;
    private static readonly SKColor F =
        SKColors.Cyan;

    public static IEnumerable<object[]> PreserveSourceCases()
    {
        yield return
        [
            "PNG",
            TestImageFactory.Png(9, 5, SKColors.CornflowerBlue),
            "image/png",
            ".png",
            9,
            5,
        ];
        yield return
        [
            "JPEG",
            TestImageFactory.Jpeg(10, 6, SKColors.Orange),
            "image/jpeg",
            ".jpg",
            10,
            6,
        ];
        yield return
        [
            "WebP",
            TestImageFactory.Webp(12, 7, SKColors.Purple),
            "image/webp",
            ".webp",
            12,
            7,
        ];
        yield return
        [
            "GIF",
            TestImageFactory.StaticGif2x1(),
            "image/gif",
            ".gif",
            2,
            1,
        ];
        yield return
        [
            "BMP",
            TestImageFactory.Bmp(8, 3),
            "image/bmp",
            ".bmp",
            8,
            3,
        ];
        yield return
        [
            "TIFF",
            TestImageFactory.BaselineTiff(7, 4),
            "image/tiff",
            ".tiff",
            7,
            4,
        ];
    }

    public static IEnumerable<object[]> OrientationCases()
    {
        yield return
        [
            SKEncodedOrigin.TopLeft,
            3,
            2,
            new[] { A, B, C, D, E, F },
        ];
        yield return
        [
            SKEncodedOrigin.TopRight,
            3,
            2,
            new[] { C, B, A, F, E, D },
        ];
        yield return
        [
            SKEncodedOrigin.BottomRight,
            3,
            2,
            new[] { F, E, D, C, B, A },
        ];
        yield return
        [
            SKEncodedOrigin.BottomLeft,
            3,
            2,
            new[] { D, E, F, A, B, C },
        ];
        yield return
        [
            SKEncodedOrigin.LeftTop,
            2,
            3,
            new[] { A, D, B, E, C, F },
        ];
        yield return
        [
            SKEncodedOrigin.RightTop,
            2,
            3,
            new[] { D, A, E, B, F, C },
        ];
        yield return
        [
            SKEncodedOrigin.RightBottom,
            2,
            3,
            new[] { F, C, E, B, D, A },
        ];
        yield return
        [
            SKEncodedOrigin.LeftBottom,
            2,
            3,
            new[] { C, F, B, E, A, D },
        ];
    }

    [Theory]
    [MemberData(nameof(PreserveSourceCases))]
    public void PreserveSource_without_resize_is_byte_exact(
        string caseName,
        byte[] source,
        string mimeType,
        string extension,
        int width,
        int height)
    {
        _ = caseName;

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                mimeType,
                Policy(
                    LibraryArtworkEncoding.PreserveSource),
                requestedMaximumDimension: 0);

        Assert.Equal(source, prepared.Data);
        Assert.NotSame(source, prepared.Data);
        Assert.Equal(mimeType, prepared.MimeType);
        Assert.Equal(extension, prepared.Extension);
        Assert.Equal(width, prepared.Width);
        Assert.Equal(height, prepared.Height);
    }

    [Fact]
    public void PreserveSource_uses_detected_format_when_declared_MIME_is_wrong()
    {
        byte[] source =
            TestImageFactory.Png(
                9,
                5,
                SKColors.CornflowerBlue);

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                "image/jpeg",
                Policy(
                    LibraryArtworkEncoding.PreserveSource),
                requestedMaximumDimension: 0);

        Assert.Equal(source, prepared.Data);
        Assert.Equal("image/png", prepared.MimeType);
        Assert.Equal(".png", prepared.Extension);
    }

    [Theory]
    [MemberData(nameof(OrientationCases))]
    public void Every_encoded_origin_maps_pixels_to_top_left(
        SKEncodedOrigin origin,
        int expectedWidth,
        int expectedHeight,
        SKColor[] expectedPixels)
    {
        using SKBitmap source =
            CreateOrientationSource();
        using SKBitmap? normalized =
            ArtworkImageProcessor.NormalizeOrientation(
                source,
                origin);
        SKBitmap actual =
            normalized ?? source;

        Assert.Equal(
            expectedWidth,
            actual.Width);
        Assert.Equal(
            expectedHeight,
            actual.Height);
        Assert.Equal(
            origin == SKEncodedOrigin.TopLeft,
            normalized is null);
        var actualPixels =
            new List<SKColor>(
                expectedPixels.Length);
        for (var y = 0;
             y < actual.Height;
             y++)
        {
            for (var x = 0;
                 x < actual.Width;
                 x++)
            {
                actualPixels.Add(
                    actual.GetPixel(
                        x,
                        y));
            }
        }

        Assert.Equal(
            expectedPixels,
            actualPixels);
    }

    [Theory]
    [InlineData(101, 51, 50, 50, 25)]
    [InlineData(51, 101, 50, 25, 50)]
    [InlineData(4096, 1, 64, 64, 1)]
    [InlineData(1, 4096, 64, 1, 64)]
    [InlineData(37, 19, 64, 37, 19)]
    public void Resize_uses_stable_odd_and_extreme_aspect_dimensions(
        int sourceWidth,
        int sourceHeight,
        int maximumDimension,
        int expectedWidth,
        int expectedHeight)
    {
        byte[] source = TestImageFactory.Png(
            sourceWidth,
            sourceHeight,
            SKColors.Teal);

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                "image/png",
                Policy(
                    LibraryArtworkEncoding.Jpeg),
                maximumDimension);

        Assert.Equal("image/jpeg", prepared.MimeType);
        Assert.Equal(".jpg", prepared.Extension);
        Assert.Equal(expectedWidth, prepared.Width);
        Assert.Equal(expectedHeight, prepared.Height);
        AssertJpeg(prepared.Data);
        Assert.Equal(
            (expectedWidth, expectedHeight),
            TestImageFactory.Dimensions(
                prepared.Data));
    }

    [Fact]
    public void Explicit_JPEG_and_PNG_encodings_match_their_descriptors()
    {
        byte[] source =
            TestImageFactory.QuadrantPng(
                321,
                161);

        ArtworkService.PreparedArtwork jpeg =
            ArtworkService.PrepareArtwork(
                source,
                "image/png",
                Policy(
                    LibraryArtworkEncoding.Jpeg),
                requestedMaximumDimension: 100);
        ArtworkService.PreparedArtwork png =
            ArtworkService.PrepareArtwork(
                source,
                "image/png",
                Policy(
                    LibraryArtworkEncoding.Png),
                requestedMaximumDimension: 100);

        Assert.Equal("image/jpeg", jpeg.MimeType);
        Assert.Equal(".jpg", jpeg.Extension);
        Assert.Equal((100, 50), (jpeg.Width, jpeg.Height));
        AssertJpeg(jpeg.Data);
        Assert.Equal(
            (100, 50),
            TestImageFactory.Dimensions(
                jpeg.Data));

        Assert.Equal("image/png", png.MimeType);
        Assert.Equal(".png", png.Extension);
        Assert.Equal((100, 50), (png.Width, png.Height));
        AssertPng(png.Data);
        Assert.Equal(
            (100, 50),
            TestImageFactory.Dimensions(
                png.Data));
    }

    [Fact]
    public void JPEG_transcode_composites_transparency_onto_white()
    {
        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                TestImageFactory.AlphaBandsPng(),
                "image/png",
                Policy(
                    LibraryArtworkEncoding.Jpeg),
                requestedMaximumDimension: 0);

        AssertJpeg(prepared.Data);
        using SKBitmap decoded =
            TestImageFactory.Decode(
                prepared.Data);
        SKColor transparentBand =
            decoded.GetPixel(
                16,
                16);
        SKColor halfRedBand =
            decoded.GetPixel(
                48,
                16);
        SKColor opaqueBlueBand =
            decoded.GetPixel(
                80,
                16);

        Assert.True(
            transparentBand.Red > 235 &&
            transparentBand.Green > 235 &&
            transparentBand.Blue > 235,
            $"Expected a white matte, got {transparentBand}.");
        Assert.True(
            halfRedBand.Red > 235 &&
            halfRedBand.Green is > 105 and < 155 &&
            halfRedBand.Blue is > 105 and < 155,
            $"Expected half-red over white, got {halfRedBand}.");
        Assert.True(
            opaqueBlueBand.Blue > 220 &&
            opaqueBlueBand.Red < 40 &&
            opaqueBlueBand.Green < 40,
            $"Expected opaque blue, got {opaqueBlueBand}.");
    }

    [Fact]
    public void PreserveSource_resizes_WebP_as_WebP()
    {
        byte[] source = TestImageFactory.Webp(
            80,
            40,
            SKColors.Goldenrod);

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                "image/webp",
                Policy(
                    LibraryArtworkEncoding.PreserveSource),
                requestedMaximumDimension: 20);

        Assert.Equal("image/webp", prepared.MimeType);
        Assert.Equal(".webp", prepared.Extension);
        Assert.Equal((20, 10), (prepared.Width, prepared.Height));
        AssertWebp(prepared.Data);
        Assert.Equal(
            (20, 10),
            TestImageFactory.Dimensions(
                prepared.Data));
    }

    [Theory]
    [InlineData("GIF")]
    [InlineData("BMP")]
    public void PreserveSource_resized_legacy_formats_use_PNG_fallback(
        string format)
    {
        byte[] source = format == "GIF"
            ? TestImageFactory.StaticGif2x1()
            : TestImageFactory.Bmp(4, 2);
        string mimeType = format == "GIF"
            ? "image/gif"
            : "image/bmp";

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                mimeType,
                Policy(
                    LibraryArtworkEncoding.PreserveSource),
                requestedMaximumDimension: 1);

        Assert.Equal("image/png", prepared.MimeType);
        Assert.Equal(".png", prepared.Extension);
        Assert.Equal((1, 1), (prepared.Width, prepared.Height));
        AssertPng(prepared.Data);
        Assert.Equal(
            (1, 1),
            TestImageFactory.Dimensions(
                prepared.Data));
    }

    [Fact]
    public void PreserveSource_refuses_TIFF_when_a_resize_is_required()
    {
        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                ArtworkService.PrepareArtwork(
                    TestImageFactory.BaselineTiff(8, 4),
                    "image/tiff",
                    Policy(
                        LibraryArtworkEncoding.PreserveSource),
                    requestedMaximumDimension: 2));

        Assert.Contains(
            "cannot resize",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreserveSource_rejects_a_truncated_TIFF_pixel_payload()
    {
        byte[] complete =
            TestImageFactory.BaselineTiff(
                8,
                4);
        byte[] truncated =
            complete[..^1];

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                ArtworkService.PrepareArtwork(
                    truncated,
                    "image/tiff",
                    Policy(
                        LibraryArtworkEncoding.PreserveSource),
                    requestedMaximumDimension: 0));

        Assert.Contains(
            "pixel payload",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreserveSource_refuses_to_flatten_animated_artwork_during_resize()
    {
        byte[] source =
            TestImageFactory.AnimatedGif2x1();
        using ArtworkImageProcessor.DecodedArtwork decoded =
            ArtworkImageProcessor.Decode(source);
        Assert.Equal(2, decoded.FrameCount);

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                ArtworkService.PrepareArtwork(
                    source,
                    "image/gif",
                    Policy(
                        LibraryArtworkEncoding.PreserveSource),
                    requestedMaximumDimension: 1));

        Assert.Contains(
            "Animated artwork",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transcoding_normalizes_EXIF_orientation_into_pixels()
    {
        byte[] source =
            TestImageFactory.Orientation6Jpeg();

        ArtworkService.PreparedArtwork prepared =
            ArtworkService.PrepareArtwork(
                source,
                "image/jpeg",
                Policy(
                    LibraryArtworkEncoding.Png),
                requestedMaximumDimension: 0);

        Assert.Equal((2, 4), (prepared.Width, prepared.Height));
        AssertPng(prepared.Data);
        using SKBitmap decoded =
            TestImageFactory.Decode(
                prepared.Data);
        AssertMostlyRed(
            decoded.GetPixel(0, 0));
        AssertMostlyBlue(
            decoded.GetPixel(
                0,
                decoded.Height - 1));
    }

    [Fact]
    public async Task Corrupt_input_is_rejected_without_a_partial_image()
    {
        byte[] corrupt =
        [
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00,
        ];
        var service =
            new ArtworkService();

        PreparedImage? result =
            await service.PrepareFromBytesAsync(
                corrupt,
                maxDimension: 64);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    public async Task Truncated_encoded_input_is_rejected(
        string format)
    {
        byte[] complete = format == "jpeg"
            ? TestImageFactory.Jpeg(
                96,
                48,
                SKColors.Coral)
            : TestImageFactory.Png(
                96,
                48,
                SKColors.Coral);
        byte[] truncated =
            complete[..(complete.Length / 2)];
        var service =
            new ArtworkService();

        PreparedImage? result =
            await service.PrepareFromBytesAsync(
                truncated,
                maxDimension: 64);

        Assert.Null(result);
    }

    [Fact]
    public void Concurrent_transforms_are_isolated_and_disposable()
    {
        byte[] source =
            TestImageFactory.QuadrantPng(
                257,
                129);

        Parallel.For(
            0,
            128,
            index =>
            {
                LibraryArtworkEncoding encoding =
                    index % 2 == 0
                        ? LibraryArtworkEncoding.Jpeg
                        : LibraryArtworkEncoding.Png;
                ArtworkService.PreparedArtwork prepared =
                    ArtworkService.PrepareArtwork(
                        source,
                        "image/png",
                        Policy(encoding),
                        requestedMaximumDimension:
                        64);

                Assert.Equal(
                    (64, 32),
                    (prepared.Width,
                        prepared.Height));
                Assert.Equal(
                    (64, 32),
                    TestImageFactory.Dimensions(
                        prepared.Data));
                if (encoding ==
                    LibraryArtworkEncoding.Jpeg)
                    AssertJpeg(prepared.Data);
                else
                    AssertPng(prepared.Data);
            });
    }

    private static LibraryArtworkPolicy Policy(
        LibraryArtworkEncoding encoding) =>
        new(
            LibraryArtworkStorage.Sidecar,
            LibraryArtworkRoleSelection.AllRoles,
            encoding,
            MaximumDimension: 0,
            MaximumEncodedBytes: 0,
            JpegQuality: 88,
            SidecarFileNameTemplate:
            "{Role}{Extension}");

    private static SKBitmap CreateOrientationSource()
    {
        var bitmap =
            new SKBitmap(
                3,
                2,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
        SKColor[] pixels =
        [
            A, B, C,
            D, E, F,
        ];
        for (var index = 0;
             index < pixels.Length;
             index++)
        {
            bitmap.SetPixel(
                index % bitmap.Width,
                index / bitmap.Width,
                pixels[index]);
        }

        return bitmap;
    }

    private static void AssertJpeg(
        byte[] data)
    {
        Assert.True(data.Length >= 4);
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xD8, data[1]);
        Assert.Equal(0xFF, data[^2]);
        Assert.Equal(0xD9, data[^1]);
    }

    private static void AssertPng(
        byte[] data)
    {
        ReadOnlySpan<byte> signature =
        [
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A,
        ];
        Assert.True(
            data.AsSpan().StartsWith(
                signature));
    }

    private static void AssertWebp(
        byte[] data)
    {
        Assert.True(data.Length >= 12);
        Assert.Equal(
            "RIFF",
            System.Text.Encoding.ASCII.GetString(
                data,
                0,
                4));
        Assert.Equal(
            "WEBP",
            System.Text.Encoding.ASCII.GetString(
                data,
                8,
                4));
    }

    private static void AssertMostlyRed(
        SKColor color) =>
        Assert.True(
            color.Red > 150 &&
            color.Green < 110 &&
            color.Blue < 110,
            $"Expected red, got {color}.");

    private static void AssertMostlyBlue(
        SKColor color) =>
        Assert.True(
            color.Blue > 150 &&
            color.Red < 110 &&
            color.Green < 110,
            $"Expected blue, got {color}.");
}
