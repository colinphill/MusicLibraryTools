using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicLibraryManager.Controls;
using System.Runtime.InteropServices;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class AppButtonStyleTests
{
    [AvaloniaFact]
    public void Semantic_button_presenter_colors_survive_interaction_and_dynamic_role_changes()
    {
        var button = new Button
        {
            Classes =
            {
                "app",
                "primary",
                "danger",
            },
            Content = "Remove",
            Width = 180,
            HorizontalAlignment =
                global::Avalonia.Layout
                    .HorizontalAlignment.Left,
            VerticalAlignment =
                global::Avalonia.Layout
                    .VerticalAlignment.Top,
            Margin = new Thickness(24),
        };
        var window = new Window
        {
            Width = 360,
            Height = 160,
            Content = button,
        };

        try
        {
            window.Show();
            Render();
            ContentPresenter presenter =
                Presenter(button);

            AssertBrush(
                presenter.Background,
                "AppDangerBrush",
                window);
            AssertBrush(
                presenter.Foreground,
                "AppDangerInkBrush",
                window);
            AssertBrush(
                presenter.BorderBrush,
                "AppDangerBrush",
                window);

            Point pointer =
                button.TranslatePoint(
                    new Point(12, 12),
                    window) ??
                throw new InvalidOperationException(
                    "The semantic button was detached.");
            window.MouseMove(
                pointer,
                RawInputModifiers.None);
            Render();
            Assert.True(button.IsPointerOver);
            AssertBrush(
                presenter.Background,
                "AppDangerHoverBrush",
                window);

            window.MouseMove(
                new Point(
                    window.Bounds.Width - 2,
                    window.Bounds.Height - 2),
                RawInputModifiers.None);
            Assert.True(
                window.FocusManager!
                    .Focus(
                        button,
                        NavigationMethod.Tab,
                        KeyModifiers.None));
            Render();
            Assert.False(button.IsPointerOver);
            Assert.Equal(
                new Thickness(2),
                presenter.BorderThickness);
            AssertBrush(
                presenter.BorderBrush,
                "AppDangerBrush",
                window);

            button.Classes.Remove(
                "primary");
            Render();
            AssertBrush(
                presenter.Background,
                "AppRaisedBrush",
                window);
            AssertBrush(
                presenter.Foreground,
                "AppDangerBrush",
                window);
            AssertBrush(
                presenter.BorderBrush,
                "AppDangerBrush",
                window);

            window.MouseMove(
                pointer,
                RawInputModifiers.None);
            window.MouseDown(
                pointer,
                MouseButton.Left,
                RawInputModifiers.None);
            Render();
            AssertBrush(
                presenter.Background,
                "AppDangerBrush",
                window);
            AssertBrush(
                presenter.Foreground,
                "AppDangerInkBrush",
                window);
            window.MouseUp(
                pointer,
                MouseButton.Left,
                RawInputModifiers.None);
            window.MouseMove(
                new Point(
                    window.Bounds.Width - 2,
                    window.Bounds.Height - 2),
                RawInputModifiers.None);
            Render();

            button.Classes.Remove(
                "danger");
            button.Classes.Add(
                "quiet");
            Assert.True(
                window.FocusManager!
                    .Focus(
                        button,
                        NavigationMethod.Tab,
                        KeyModifiers.None));
            Render();
            ISolidColorBrush quiet =
                Assert.IsAssignableFrom<
                    ISolidColorBrush>(
                    presenter.Background);
            Assert.Equal(
                0,
                quiet.Color.A);
            Assert.Equal(
                new Thickness(2),
                presenter.BorderThickness);
            AssertBrush(
                presenter.BorderBrush,
                "AppAccentBrush",
                window);

            button.Classes.Remove(
                "quiet");
            button.Classes.Add(
                "primary");
            Render();
            AssertBrush(
                presenter.Background,
                "AppAccentBrush",
                window);
            AssertBrush(
                presenter.Foreground,
                "AppAccentInkBrush",
                window);

            button.Classes.Remove(
                "primary");
            Render();
            AssertBrush(
                presenter.Background,
                "AppRaisedBrush",
                window);
            AssertBrush(
                presenter.Foreground,
                "AppTextBrush",
                window);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void More_vector_icon_renders_three_visible_dots()
    {
        var icon = new AppVectorIcon
        {
            Kind = AppVectorIconKind.More,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.Black,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
        };
        var window = new Window
        {
            Width = 40,
            Height = 40,
            Background = Brushes.White,
            Content = icon,
        };

        try
        {
            window.Show();
            Render();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using (frame)
            {
                Point origin =
                    icon.TranslatePoint(
                        default,
                        window) ??
                    throw new InvalidOperationException(
                        "The More icon was detached.");
                double scaleX =
                    frame.PixelSize.Width /
                    window.Bounds.Width;
                double scaleY =
                    frame.PixelSize.Height /
                    window.Bounds.Height;
                var iconPixels = new PixelRect(
                    (int)Math.Floor(origin.X * scaleX),
                    (int)Math.Floor(origin.Y * scaleY),
                    (int)Math.Ceiling(
                        icon.Bounds.Width * scaleX),
                    (int)Math.Ceiling(
                        icon.Bounds.Height * scaleY));

                Assert.Equal(
                    3,
                    CountDarkColumnClusters(
                        frame,
                        iconPixels));
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Invalid_textbox_template_preserves_danger_outline_while_focused()
    {
        var input = new TextBox
        {
            Classes =
            {
                "app",
                "error",
            },
            Text = "Ctrl+",
            Width = 240,
            Margin = new Thickness(24),
        };
        var window = new Window
        {
            Width = 320,
            Height = 120,
            Content = input,
        };
        ThemeVariant? previousTheme =
            Application.Current!
                .RequestedThemeVariant;

        try
        {
            window.Show();
            window.Activate();
            foreach (ThemeVariant theme in
                     new[]
                     {
                         ThemeVariant.Light,
                         ThemeVariant.Dark,
                     })
            {
                Application.Current
                    .RequestedThemeVariant =
                    theme;
                Assert.True(
                    window.FocusManager!
                        .Focus(
                            input,
                            NavigationMethod.Tab,
                            KeyModifiers.None));
                Render();

                Border templateBorder =
                    Assert.Single(
                        input
                            .GetVisualDescendants()
                            .OfType<Border>(),
                        border =>
                            border.Name ==
                            "PART_BorderElement");
                AssertBrush(
                    input.BorderBrush,
                    "AppDangerBrush",
                    window);
                AssertBrush(
                    templateBorder.BorderBrush,
                    "AppDangerBrush",
                    window);
                Assert.Equal(
                    new Thickness(2),
                    templateBorder
                        .BorderThickness);
            }
        }
        finally
        {
            window.Hide();
            Application.Current!
                .RequestedThemeVariant =
                previousTheme;
        }
    }

    private static ContentPresenter Presenter(
        Button button) =>
        Assert.Single(
            button.GetVisualDescendants()
                .OfType<ContentPresenter>());

    private static void AssertBrush(
        IBrush? actual,
        string resourceKey,
        Control resourceHost)
    {
        Assert.True(
            Application.Current!
                .TryGetResource(
                resourceKey,
                resourceHost
                    .ActualThemeVariant,
                out object? expected));
        Assert.Equal(
            expected?.ToString(),
            actual?.ToString());
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static int CountDarkColumnClusters(
        Bitmap frame,
        PixelRect bounds)
    {
        using var pixels = new WriteableBitmap(
            frame.PixelSize,
            frame.Dpi,
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using ILockedFramebuffer framebuffer =
            pixels.Lock();
        frame.CopyPixels(framebuffer);

        int byteCount =
            framebuffer.RowBytes *
            framebuffer.Size.Height;
        var bytes = new byte[byteCount];
        Marshal.Copy(
            framebuffer.Address,
            bytes,
            0,
            byteCount);

        bool inCluster = false;
        int clusters = 0;
        for (int x = bounds.X;
             x < bounds.Right;
             x++)
        {
            bool hasDarkPixel = false;
            for (int y = bounds.Y;
                 y < bounds.Bottom;
                 y++)
            {
                int offset =
                    (y * framebuffer.RowBytes) +
                    (x * 4);
                byte blue = bytes[offset];
                byte green = bytes[offset + 1];
                byte red = bytes[offset + 2];
                byte alpha = bytes[offset + 3];
                if (alpha > 127 &&
                    red < 96 &&
                    green < 96 &&
                    blue < 96)
                {
                    hasDarkPixel = true;
                    break;
                }
            }

            if (hasDarkPixel && !inCluster)
                clusters++;
            inCluster = hasDarkPixel;
        }

        return clusters;
    }
}
