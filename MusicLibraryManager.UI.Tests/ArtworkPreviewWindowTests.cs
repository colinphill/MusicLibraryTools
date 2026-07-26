using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MusicFileUtilities;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using SkiaSharp;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class ArtworkPreviewWindowTests
{
    [AvaloniaFact]
    public void Preview_decodes_original_landscape_and_portrait_pixels_without_stretching()
    {
        foreach ((int width, int height) in new[] { (640, 320), (320, 640) })
        {
            byte[] data = CreatePng(width, height);
            var item = new ArtworkPreviewItem(
                source: null,
                ID3v2Util.APICType.FrontCover,
                "image/png",
                data,
                $"image/png · {width} x {height}",
                description: null);

            Assert.True(ArtworkPreviewWindow.TryCreate(item, owner: null, out ArtworkPreviewWindow? preview));
            Assert.NotNull(preview);
            try
            {
                Assert.Equal(new PixelSize(width, height), preview.ImagePixelSize);
                Avalonia.Controls.Image image = preview.FindControl<Avalonia.Controls.Image>("PreviewImage")!;
                Bitmap bitmap = Assert.IsType<Bitmap>(image.Source);
                Assert.Equal(new PixelSize(width, height), bitmap.PixelSize);
                Assert.Equal(Stretch.None, image.Stretch);
                Assert.Equal("Artwork preview: Front cover", preview.Title);
                Assert.Equal(preview.Title, AutomationProperties.GetName(preview));
                Assert.Equal(
                    "Full-resolution artwork: Front cover",
                    AutomationProperties.GetName(image));
            }
            finally
            {
                preview.Show();
                Dispatcher.UIThread.RunJobs();
                preview.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    [AvaloniaFact]
    public void Preview_uses_the_localized_fallback_without_forcing_label_casing()
    {
        var item = new ArtworkPreviewItem(
            source: null,
            ID3v2Util.APICType.FrontCover,
            "image/png",
            CreatePng(32, 32),
            "image/png · 32 x 32",
            description: null);
        item.RefreshLocalizedText(_ => " ");

        Assert.True(
            ArtworkPreviewWindow.TryCreate(
                item,
                owner: null,
                out ArtworkPreviewWindow? preview));
        Assert.NotNull(preview);
        try
        {
            Avalonia.Controls.Image image =
                preview.FindControl<Avalonia.Controls.Image>(
                    "PreviewImage")!;
            Assert.Equal(
                "Artwork preview: Artwork",
                preview.Title);
            Assert.Equal(
                "Full-resolution artwork: Artwork",
                AutomationProperties.GetName(image));
        }
        finally
        {
            preview.Show();
            Dispatcher.UIThread.RunJobs();
            preview.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Theory]
    [InlineData(1600, 800, 1920, 1080, 1, 1640, 912)]
    [InlineData(800, 1600, 1920, 1080, 1, 840, 972)]
    [InlineData(2400, 1200, 3840, 2160, 2, 1240, 712)]
    [InlineData(100, 50, 1920, 1080, 1, 420, 300)]
    public void Initial_size_handles_landscape_portrait_and_high_dpi(
        int imageWidth,
        int imageHeight,
        int workingWidth,
        int workingHeight,
        double scaling,
        double expectedWidth,
        double expectedHeight)
    {
        Avalonia.Size result = ArtworkPreviewWindow.CalculateInitialSize(
            new PixelSize(imageWidth, imageHeight),
            new PixelSize(workingWidth, workingHeight),
            scaling);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.True(result.Width <= workingWidth / scaling);
        Assert.True(result.Height <= workingHeight / scaling);
    }

    [AvaloniaFact]
    public void Missing_or_invalid_image_data_does_not_create_a_window()
    {
        var empty = new ArtworkPreviewItem(null, ID3v2Util.APICType.FrontCover,
            "image/jpeg", [], "Empty", null);
        var invalid = new ArtworkPreviewItem(null, ID3v2Util.APICType.FrontCover,
            "image/jpeg", [1, 2, 3, 4], "Invalid", null);

        Assert.False(ArtworkPreviewWindow.TryCreate(empty, owner: null, out ArtworkPreviewWindow? emptyWindow));
        Assert.Null(emptyWindow);
        Assert.False(ArtworkPreviewWindow.TryCreate(invalid, owner: null, out ArtworkPreviewWindow? invalidWindow));
        Assert.Null(invalidWindow);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data =
            image.Encode(
                SKEncodedImageFormat.Png,
                100);
        return data.ToArray();
    }
}
