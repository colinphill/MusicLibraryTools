using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
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
        const int entryCount = 2_000;
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
            var selectedRun =
                new OperationRunViewModel(
                    new(
                        "Recovery layout test",
                        OperationJournalKind.Other,
                        OperationJournalState.Completed,
                        Path.Combine(
                            Path.GetTempPath(),
                            "recovery-layout-run"),
                        null,
                        DateTimeOffset.UtcNow,
                        entryCount));
            model.Runs.Add(
                selectedRun);
            model.SelectedRun =
                selectedRun;
            string root = Path.Combine(
                Path.GetTempPath(),
                "recovery-layout-root");
            OperationFileEntry[] fixtureEntries =
                Enumerable.Range(
                        0,
                        entryCount)
                    .Select(index =>
                    {
                        string file =
                            Path.Combine(
                                root,
                                $"track-{index:0000}.flac");
                        return new OperationFileEntry(
                            file,
                            file + ".recovery",
                            Path.GetFileName(file),
                            OperationEntryKind
                                .Quarantined,
                            Exists: true,
                            IsDirectory: false,
                            RecoveryPayloadKind
                                .FullOriginal,
                            RetainedBytes: 4096);
                    })
                    .ToArray();
            model.RootNodes.Add(
                OperationEntryNodeViewModel.Build(
                    new(
                        root,
                        fixtureEntries,
                        [])));
            Assert.Equal(
                entryCount,
                model.RecoveryEntryNodes.Count);
            Assert.All(
                model.RecoveryEntryNodes,
                entry =>
                {
                    Assert.True(entry.HasEntry);
                    Assert.True(entry.CanRestore);
                });
            model.RecoveryEntryNodes[0]
                .IsSelected = true;
            Assert.True(
                model.PreviewRestoreCommand
                    .CanExecute(null));
            model.SelectAllRestorableCommand
                .Execute(null);
            Assert.All(
                model.RecoveryEntryNodes,
                entry =>
                    Assert.True(entry.IsSelected));
            model.RestorePreviewText =
                $"{entryCount:N0} selected recovery items are ready to restore.";
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
            Assert.Equal(
                entryCount,
                entries.ItemCount);
            var entriesPanel =
                Assert.IsType<
                    VirtualizingStackPanel>(
                    entries.ItemsPanelRoot);
            Assert.InRange(
                entriesPanel.Children.Count,
                1,
                40);
            Assert.True(
                entriesPanel.Children.Count <
                entryCount,
                $"Recovery virtualization realized every entry: children={entriesPanel.Children.Count}, entries={entryCount}, extent={scroll.Extent}, viewport={scroll.Viewport}.");
            CheckBox firstEntrySelection =
                Assert.Single(
                    entries
                        .GetVisualDescendants()
                        .OfType<CheckBox>(),
                    checkBox =>
                        ReferenceEquals(
                            checkBox.DataContext,
                            model.RecoveryEntryNodes[0]));
            Assert.True(
                firstEntrySelection
                    .IsEffectivelyEnabled);
            Assert.True(
                firstEntrySelection.IsChecked);
            ScrollViewer[] nestedScrollOwners =
            [
                .. scroll
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Where(nested =>
                        !nested
                            .GetVisualAncestors()
                            .OfType<TextBox>()
                            .Any()),
            ];
            Assert.True(
                nestedScrollOwners.Length == 0,
                "Recovery detail contains nested scrolling: " +
                string.Join(
                    "; ",
                    nestedScrollOwners.Select(
                        nested =>
                            string.Join(
                                " > ",
                                nested
                                    .GetVisualAncestors()
                                    .OfType<Control>()
                                    .Reverse()
                                    .Select(control =>
                                        control.Name ??
                                        control.GetType()
                                            .Name)))));
            Assert.True(
                scroll.Extent.Height >
                scroll.Viewport.Height,
                $"The populated recovery surface did not overflow: {scroll.Extent.Height:0.0}/{scroll.Viewport.Height:0.0}.");

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
            CaptureConfiguredRecoveryState(
                window);

            scroll.Offset = new Vector(
                0,
                scroll.Extent.Height -
                scroll.Viewport.Height);
            Render();

            Assert.True(
                scroll.Offset.Y > 0);
            Assert.InRange(
                entriesPanel.Children.Count,
                1,
                40);
            Assert.True(
                entriesPanel.Children.Count <
                entryCount,
                $"Recovery virtualization realized every entry after scrolling: children={entriesPanel.Children.Count}, entries={entryCount}, extent={scroll.Extent}, viewport={scroll.Viewport}.");
            Assert.True(
                entriesPanel.FirstRealizedIndex > 0,
                $"Recovery virtualization did not advance after scrolling: first={entriesPanel.FirstRealizedIndex}, last={entriesPanel.LastRealizedIndex}, offset={scroll.Offset}, extent={scroll.Extent}, viewport={scroll.Viewport}.");
            Rect scrolledFooterBounds =
                BoundsRelativeTo(
                    footer,
                    window);
            Rect scrolledScopeBounds =
                BoundsRelativeTo(
                    scope,
                    window);
            Assert.InRange(
                Math.Abs(
                    scrolledFooterBounds.Top -
                    footerBounds.Top),
                0,
                0.5);
            Assert.InRange(
                Math.Abs(
                    scrolledScopeBounds.Top -
                    scopeBounds.Top),
                0,
                0.5);
            AssertInViewport(
                scrolledFooterBounds,
                window);
            AssertInViewport(
                scrolledScopeBounds,
                window);
        }
        finally
        {
            window.Hide();
        }
    }

    private static void CaptureConfiguredRecoveryState(
        MainWindow window)
    {
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(
                captureDirectory))
        {
            return;
        }

        window.InvalidateVisual();
        Render();
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            900,
            frame.PixelSize.Width);
        Assert.Equal(
            600,
            frame.PixelSize.Height);
        Directory.CreateDirectory(
            captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                "configured-operations-recovery-900x600.png"),
            PngBitmapEncoderOptions.Default);
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
