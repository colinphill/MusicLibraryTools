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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
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
                Assert.Equal("Front cover artwork preview", preview.Title);
                Assert.Equal(preview.Title, AutomationProperties.GetName(preview));
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
        using var image = new SixLabors.ImageSharp.Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }
}
