using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class OperationsRecoveryLayoutTests
{
    [AvaloniaFact]
    public void Recovery_keeps_reviewed_scope_and_restore_actions_in_a_sticky_footer_at_900_by_600()
    {
        using ServiceProvider services =
            Composition.BuildServices();
        App.UseServicesForTests(services);
        OperationsViewModel model =
            services.GetRequiredService<
                OperationsViewModel>();
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            services
                .GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Operations);
            Render();

            OperationsView view =
                Assert.IsType<OperationsView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!
                        .Content);
            TabControl tabs =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<TabControl>());
            tabs.SelectedIndex = 2;
            model.RestorePreviewText =
                "Two selected recovery items are ready to restore.";
            model.ShowRestorePreview = true;
            Render();

            ScrollViewer scroll =
                Assert.IsType<ScrollViewer>(
                    view.FindControl<ScrollViewer>(
                        "RecoveryDetailScroll"));
            ItemsControl entries =
                Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>(
                        "RecoveryEntryList"));
            Border footer =
                Assert.IsType<Border>(
                    view.FindControl<Border>(
                        "RestoreStickyFooter"));
            Border scope =
                Assert.IsType<Border>(
                    view.FindControl<Border>(
                        "RestorePreviewBanner"));
            Button apply =
                Assert.IsType<Button>(
                    view.FindControl<Button>(
                        "ApplyRestoreButton"));

            Assert.Same(
                model.ApplyRestoreCommand,
                apply.Command);
            Assert.False(
                entries is ListBox);

            Rect scrollBounds =
                BoundsRelativeTo(
                    scroll,
                    window);
            Rect footerBounds =
                BoundsRelativeTo(
                    footer,
                    window);
            Rect scopeBounds =
                BoundsRelativeTo(
                    scope,
                    window);
            Rect applyBounds =
                BoundsRelativeTo(
                    apply,
                    window);

            Assert.True(
                scope.IsEffectivelyVisible);
            Assert.True(
                apply.IsEffectivelyVisible);
            Assert.True(
                scrollBounds.Bottom <=
                footerBounds.Top + 1,
                $"Scrollable recovery content {scrollBounds} overlaps sticky footer {footerBounds}.");
            Assert.True(
                scopeBounds.Bottom <=
                applyBounds.Top + 1,
                $"Reviewed scope {scopeBounds} is not adjacent to the restore action {applyBounds}.");
            AssertInViewport(
                footerBounds,
                window);
            AssertInViewport(
                scopeBounds,
                window);
            AssertInViewport(
                applyBounds,
                window);
        }
        finally
        {
            window.Hide();
        }
    }

    private static Rect BoundsRelativeTo(
        Control control,
        Visual ancestor)
    {
        Point? origin =
            control.TranslatePoint(
                default,
                ancestor);
        Assert.NotNull(origin);
        Assert.True(
            control.Bounds.Width > 0 &&
            control.Bounds.Height > 0,
            $"{control.Name ?? control.GetType().Name} has no arranged bounds.");
        return new Rect(
            origin.Value,
            control.Bounds.Size);
    }

    private static void AssertInViewport(
        Rect bounds,
        Window window) =>
        Assert.True(
            bounds.Left >= -1 &&
            bounds.Top >= -1 &&
            bounds.Right <=
            window.Bounds.Width + 1 &&
            bounds.Bottom <=
            window.Bounds.Height + 1,
            $"{bounds} is outside the effective {window.Bounds.Size} viewport.");

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
