using global::Avalonia;
using global::Avalonia.Automation;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class ArtworkPreviewWindow : Window
{
    private const double HorizontalChrome = 40;
    private const double VerticalChrome = 112;
    private const double WorkingAreaFraction = .9;
    private Bitmap? _bitmap;

    public ArtworkPreviewWindow()
    {
        InitializeComponent();
    }

    private ArtworkPreviewWindow(ArtworkPreviewItem item, Bitmap bitmap, Window? owner)
        : this()
    {
        _bitmap = bitmap;

        string label = string.IsNullOrWhiteSpace(item.Label)
            ? LocalizedText.Get(
                "ArtworkPreview.FallbackLabel")
            : item.Label;
        Title = LocalizedText.Format(
            "ArtworkPreview.WindowTitleFormat",
            label);
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetName(
            PreviewImage,
            LocalizedText.Format(
                "ArtworkPreview.AutomationFormat",
                label.ToLower(
                    System.Globalization.CultureInfo
                        .CurrentUICulture)));
        PreviewImage.Source = bitmap;
        PreviewDetails.Text = LocalizedText.Format(
            "ArtworkPreview.DetailsFormat",
            bitmap.PixelSize.Width,
            bitmap.PixelSize.Height,
            item.MimeType,
            FormatBytes(item.Data.LongLength));

        ApplyInitialSize(GetWorkingArea(owner));
        Closed += OnClosed;
    }

    internal PixelSize ImagePixelSize => _bitmap?.PixelSize ?? default;

    internal static bool TryCreate(
        ArtworkPreviewItem? item,
        Window? owner,
        out ArtworkPreviewWindow? window)
    {
        window = null;
        if (item?.Data is not { Length: > 0 } data)
            return false;

        Bitmap? bitmap = null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            bitmap = new Bitmap(stream);
            window = new ArtworkPreviewWindow(item, bitmap, owner);
            bitmap = null; // Ownership transferred to the window.
            return true;
        }
        catch (Exception)
        {
            bitmap?.Dispose();
            window = null;
            return false;
        }
    }

    internal static Size CalculateInitialSize(
        PixelSize imageSize,
        PixelSize workingArea,
        double scaling)
    {
        double safeScale = Math.Max(.25, scaling);
        double availableWidth = Math.Max(1, workingArea.Width / safeScale);
        double availableHeight = Math.Max(1, workingArea.Height / safeScale);
        double maximumWidth = Math.Max(1, availableWidth * WorkingAreaFraction);
        double maximumHeight = Math.Max(1, availableHeight * WorkingAreaFraction);
        double minimumWidth = Math.Min(420, maximumWidth);
        double minimumHeight = Math.Min(300, maximumHeight);

        return new Size(
            Math.Clamp(imageSize.Width / safeScale + HorizontalChrome, minimumWidth, maximumWidth),
            Math.Clamp(imageSize.Height / safeScale + VerticalChrome, minimumHeight, maximumHeight));
    }

    private (PixelSize Area, double Scaling) GetWorkingArea(Window? owner)
    {
        Screen? screen = owner is null ? null : owner.Screens.ScreenFromWindow(owner);
        screen ??= owner?.Screens.Primary;
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        return screen is null
            ? (new PixelSize(1440, 900), 1)
            : (new PixelSize(screen.WorkingArea.Width, screen.WorkingArea.Height), screen.Scaling);
    }

    private void ApplyInitialSize((PixelSize Area, double Scaling) display)
    {
        if (_bitmap is null)
            return;
        Size size = CalculateInitialSize(_bitmap.PixelSize, display.Area, display.Scaling);
        MinWidth = Math.Min(320, size.Width);
        MinHeight = Math.Min(240, size.Height);
        MaxWidth = Math.Max(MinWidth, display.Area.Width / Math.Max(.25, display.Scaling));
        MaxHeight = Math.Max(MinHeight, display.Area.Height / Math.Max(.25, display.Scaling));
        Width = size.Width;
        Height = size.Height;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Close();
        e.Handled = true;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => LocalizedText.Format(
            "ArtworkPreview.Size.MegabytesFormat",
            bytes / 1024d / 1024d),
        >= 1024 => LocalizedText.Format(
            "ArtworkPreview.Size.KilobytesFormat",
            bytes / 1024d),
        _ => LocalizedText.Format(
            "ArtworkPreview.Size.BytesFormat",
            bytes),
    };

}
