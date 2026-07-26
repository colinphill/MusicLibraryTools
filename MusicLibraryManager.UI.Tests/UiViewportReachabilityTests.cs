using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class
    UiViewportReachabilityTests
{
    [AvaloniaFact]
    public void Fully_visible_action_is_accepted_without_scrolling()
    {
        Button action = new()
        {
            Name = "VisibleAction",
            Content = "Preview",
            Width = 120,
            Height = 36,
        };
        Border viewport = new()
        {
            Name = "Viewport",
            Child = action,
        };
        Window window =
            CreateWindow(
                viewport);

        try
        {
            window.Show();
            Render();

            UiActionReachabilityResult result =
                UiViewportReachability
                    .VerifyAction(
                        viewport,
                        action,
                        Render);

            Assert.True(
                result.IsReachable,
                result.Detail);
            Assert.True(
                result.WasInitiallyVisible);
            Assert.False(
                result.UsedVerticalScrolling);
            Assert.True(
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        viewport,
                        action,
                        out _,
                        out string detail),
                detail);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Offscreen_action_is_verified_through_its_scroll_owner_and_offset_is_restored()
    {
        Button action = new()
        {
            Name = "ReachableAction",
            Content = "Apply",
            Height = 36,
        };
        StackPanel content = new()
        {
            Spacing = 8,
        };
        content.Children.Add(
            new Border
            {
                Height = 320,
            });
        content.Children.Add(
            action);
        ScrollViewer scroll = new()
        {
            Name = "EditorScroll",
            VerticalScrollBarVisibility =
                ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility =
                ScrollBarVisibility.Disabled,
            Content = content,
        };
        Border viewport = new()
        {
            Name = "Viewport",
            Child = scroll,
        };
        Window window =
            CreateWindow(
                viewport);

        try
        {
            window.Show();
            Render();
            scroll.Offset =
                new Vector(
                    0,
                    24);
            Render();
            Vector originalOffset =
                scroll.Offset;
            Assert.Equal(
                24,
                originalOffset.Y);
            Assert.False(
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        viewport,
                        action,
                        out _,
                        out _));

            UiActionReachabilityResult result =
                UiViewportReachability
                    .VerifyAction(
                        viewport,
                        action,
                        Render);

            Assert.True(
                result.IsReachable,
                result.Detail);
            Assert.False(
                result.WasInitiallyVisible);
            Assert.True(
                result.UsedVerticalScrolling);
            Assert.Equal(
                originalOffset,
                scroll.Offset);
            Assert.False(
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        viewport,
                        action,
                        out _,
                        out _));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Offscreen_action_without_a_scroll_owner_is_rejected()
    {
        Button action = new()
        {
            Name = "UnreachableAction",
            Content = "Delete",
            Width = 120,
            Height = 36,
        };
        Canvas.SetTop(
            action,
            320);
        Canvas canvas = new();
        canvas.Children.Add(
            action);
        Border viewport = new()
        {
            Name = "Viewport",
            ClipToBounds = true,
            Child = canvas,
        };
        Window window =
            CreateWindow(
                viewport);

        try
        {
            window.Show();
            Render();

            UiActionReachabilityResult result =
                UiViewportReachability
                    .VerifyAction(
                        viewport,
                        action,
                        Render);

            Assert.False(
                result.IsReachable);
            Assert.False(
                result.WasInitiallyVisible);
            Assert.False(
                result.UsedVerticalScrolling);
            Assert.Contains(
                "No usable vertical ScrollViewer",
                result.Detail,
                StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow(
        Control content) =>
        new()
        {
            Width = 320,
            Height = 160,
            Content = content,
        };

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
