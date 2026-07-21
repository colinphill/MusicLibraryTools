using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class HealthResultContextMenuTests
{
    [AvaloniaFact]
    public void Context_menu_exposes_copy_and_reveal_for_the_resolved_path()
    {
        const string path = @"C:\Music\Artist\Album\Track.flac";
        var platform = new RecordingPlatformService();
        ContextMenu menu = HealthResultContextMenuFactory.Create(path, platform);
        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();

        Assert.Equal(["Copy path", "Reveal in File Explorer"],
            items.Select(item => item.Header?.ToString()));

        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(path, platform.CopiedText);
        Assert.Equal(path, platform.RevealedPath);
    }

    [AvaloniaFact]
    public void Context_menu_uses_the_right_clicked_controls_item_not_a_previous_selection()
    {
        const string selectedPath = @"C:\Music\Previously-selected.flac";
        const string clickedPath = @"C:\Music\Right-clicked.flac";
        var platform = new RecordingPlatformService();
        var selected = new TextBlock
        {
            DataContext = new TrackRecord { Path = selectedPath },
        };
        var clicked = new TextBlock
        {
            DataContext = new TrackRecord { Path = clickedPath },
        };

        Assert.NotNull(HealthResultContextMenuFactory.CreateForSource(selected, platform));
        ContextMenu menu = Assert.IsType<ContextMenu>(
            HealthResultContextMenuFactory.CreateForSource(clicked, platform));
        MenuItem copy = Assert.IsType<MenuItem>(menu.Items[0]);
        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(clickedPath, platform.CopiedText);
    }

    [AvaloniaFact]
    public void Aggregate_result_source_has_no_path_menu()
    {
        var source = new TextBlock
        {
            DataContext = new ArtistVariant("Artist",
                [@"C:\Music\One.flac", @"C:\Music\Two.flac"]),
        };

        Assert.Null(HealthResultContextMenuFactory.CreateForSource(
            source, new RecordingPlatformService()));
    }

    private sealed class RecordingPlatformService : IPlatformService
    {
        public string? CopiedText { get; private set; }
        public string? RevealedPath { get; private set; }

        public Task CopyTextAsync(string text)
        {
            CopiedText = text;
            return Task.CompletedTask;
        }

        public void RevealFile(string path) => RevealedPath = path;
    }
}
