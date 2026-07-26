using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
}
