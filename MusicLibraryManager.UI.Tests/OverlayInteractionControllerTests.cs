using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MusicLibraryManager.Controls;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class
    OverlayInteractionControllerTests
{
    [AvaloniaFact]
    public void
        Shared_overlay_controller_wraps_focus_in_both_directions_and_restores_the_launcher()
    {
        var launcher = new Button
        {
            Content = "Launcher",
        };
        var fallback = new Button
        {
            Content = "Fallback",
        };
        var first = new Button
        {
            Content = "First",
        };
        var middle = new TextBox
        {
            Text = "Middle",
        };
        var last = new Button
        {
            Content = "Last",
        };
        var surface = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    first,
                    middle,
                    last,
                },
            },
        };
        var window = new Window
        {
            Width = 500,
            Height = 400,
            Content = new StackPanel
            {
                Children =
                {
                    launcher,
                    fallback,
                    surface,
                },
            },
        };
        var controller =
            new OverlayInteractionController();
        try
        {
            window.Show();
            Render();
            launcher.Focus();
            Render();
            controller.CaptureFocus(surface);

            last.Focus();
            Render();
            Assert.True(
                OverlayInteractionController
                    .TryCycleFocus(
                        surface,
                        reverse: false));
            Assert.Same(
                first,
                window.FocusManager?
                    .GetFocusedElement());

            first.Focus();
            Render();
            Assert.True(
                OverlayInteractionController
                    .TryCycleFocus(
                        surface,
                        reverse: true));
            Assert.Same(
                last,
                window.FocusManager?
                    .GetFocusedElement());

            middle.Focus();
            Render();
            Assert.False(
                OverlayInteractionController
                    .TryCycleFocus(
                        surface,
                        reverse: false));
            Assert.Same(
                middle,
                window.FocusManager?
                    .GetFocusedElement());
            Assert.True(
                OverlayInteractionController
                    .TryCycleFocus(
                        surface,
                        reverse: false,
                        moveEveryTab: true));
            Assert.Same(
                last,
                window.FocusManager?
                    .GetFocusedElement());

            controller.RestoreFocus(fallback);
            Render();
            Assert.Same(
                launcher,
                window.FocusManager?
                    .GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void
        Shared_overlay_controller_routes_escape_and_traps_tab()
    {
        var first = new Button
        {
            Content = "First",
        };
        var last = new Button
        {
            Content = "Last",
        };
        var surface = new StackPanel
        {
            Children =
            {
                first,
                last,
            },
        };
        var window = new Window
        {
            Content = surface,
        };
        var controller =
            new OverlayInteractionController();
        bool dismissed = false;
        try
        {
            window.Show();
            Render();
            last.Focus();
            Render();
            var tab = new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.Tab,
            };
            Assert.True(
                controller.HandleKeyDown(
                    tab,
                    surface,
                    canDismiss: true,
                    () => dismissed = true));
            Assert.True(tab.Handled);
            Assert.Same(
                first,
                window.FocusManager?
                    .GetFocusedElement());
            Assert.False(dismissed);

            var escape = new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.Escape,
            };
            Assert.True(
                controller.HandleKeyDown(
                    escape,
                    surface,
                    canDismiss: true,
                    () => dismissed = true));
            Assert.True(escape.Handled);
            Assert.True(dismissed);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void
        Shared_overlay_bounds_honor_the_viewport_and_declared_range()
    {
        Assert.Equal(
            300,
            OverlayInteractionController
                .ConstrainLength(
                    280,
                    300,
                    430));
        Assert.Equal(
            404,
            OverlayInteractionController
                .ConstrainLength(
                    428,
                    300,
                    430,
                    viewportInset: 24));
        Assert.Equal(
            430,
            OverlayInteractionController
                .ConstrainLength(
                    2560,
                    300,
                    430,
                    viewportInset: 24));
        Assert.Throws<
            ArgumentOutOfRangeException>(
            () =>
                OverlayInteractionController
                    .ConstrainLength(
                        1000,
                        500,
                        430));
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
