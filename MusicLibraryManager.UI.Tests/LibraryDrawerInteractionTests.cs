using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class LibraryDrawerInteractionTests
{
    [AvaloniaFact]
    public void Compact_inspector_supports_pointer_escape_close_and_focus_restoration()
    {
        using ServiceProvider services =
            Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Library);
            Render();

            LibraryView view =
                Assert.IsType<LibraryView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            Button toggle =
                view.FindControl<Button>(
                    "InspectorToggle")!;
            Border scrim =
                view.FindControl<Border>(
                    "InspectorScrim")!;
            Control close =
                view.FindControl<
                        SelectionInspectorView>(
                        "InspectorView")!
                    .CloseButton;
            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkspaceSplit")!;
            ContentPresenter right =
                split.FindControl<ContentPresenter>(
                    "RightPresenter")!;

            Assert.True(toggle.IsVisible);
            Assert.False(scrim.IsVisible);

            OpenDrawer(toggle);
            Assert.True(scrim.IsVisible);
            Assert.True(right.IsVisible);
            Assert.Same(
                close,
                Focused(view));

            Point scrimPoint =
                scrim.TranslatePoint(
                    new Point(
                        Math.Max(
                            2,
                            scrim.Bounds.Width / 2),
                        Math.Max(
                            2,
                            scrim.Bounds.Height / 2)),
                    window)!.Value;
            window.MouseDown(
                scrimPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            window.MouseUp(
                scrimPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            Render();

            Assert.False(scrim.IsVisible);
            Assert.False(right.IsVisible);
            Assert.True(model.IsInspectorOpen);
            Assert.Same(
                toggle,
                Focused(view));

            OpenDrawer(toggle);
            view.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
            Render();

            Assert.False(scrim.IsVisible);
            Assert.False(right.IsVisible);
            Assert.True(model.IsInspectorOpen);
            Assert.Same(
                toggle,
                Focused(view));

            OpenDrawer(toggle);
            close.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();

            Assert.False(scrim.IsVisible);
            Assert.False(right.IsVisible);
            Assert.False(model.IsInspectorOpen);
            Assert.Same(
                toggle,
                Focused(view));
        }
        finally
        {
            window.Hide();
        }
    }

    private static void OpenDrawer(
        Button toggle)
    {
        toggle.RaiseEvent(
            new RoutedEventArgs(
                Button.ClickEvent));
        Render();
    }

    private static IInputElement? Focused(
        Control control) =>
        TopLevel.GetTopLevel(control)!
            .FocusManager!
            .GetFocusedElement();

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
