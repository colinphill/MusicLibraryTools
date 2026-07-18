using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class UiControlTests
{
    [AvaloniaFact]
    public void Data_grid_preserves_app_metrics_and_column_contract()
    {
        var grid = new AppDataGrid();
        grid.ConfigureColumns([
            new AppGridColumnDefinition("Title", "Title", "Title", 280, 140),
            new AppGridColumnDefinition("Hidden", "Hidden", "Hidden", 120, 80, false),
            new AppGridColumnDefinition("Path", "Path", "Path", 420, 180),
        ]);

        Assert.Equal(38, grid.RowHeight);
        Assert.Equal(39, grid.ColumnHeaderHeight);
        Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
        Assert.Equal(2, grid.Columns.Count);
        Assert.Equal("Title", grid.KeyFor(grid.Columns[0]));
        Assert.Equal(280, grid.CaptureColumnLayout()[0].Width);
    }

    [AvaloniaFact]
    public void Page_header_exposes_native_title_subtitle_and_actions()
    {
        var action = new Button { Content = "Run" };
        var header = new PageHeader
        {
            Title = "Health",
            Subtitle = "Audit the collection.",
            Actions = action,
        };

        Assert.Equal("Health", header.Title);
        Assert.Equal("Audit the collection.", header.Subtitle);
        Assert.Same(action, header.Actions);
    }

    [AvaloniaFact]
    public void App_buttons_center_content_while_navigation_remains_stretched()
    {
        var button = new Button { Content = "Run" };
        button.Classes.Add("app");
        var navigation = new Button { Content = "Library" };
        navigation.Classes.Add("nav");
        var window = new Window { Content = new StackPanel { Children = { button, navigation } } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, button.HorizontalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, button.VerticalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, navigation.HorizontalContentAlignment);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Split_width_persists_when_the_view_is_recreated()
    {
        var settings = new FakeSettings();
        var state = new SplitStateService(settings);
        state.Save("round-trip", 360);

        var split = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var window = new Window { Width = 900, Height = 600, Content = split };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(360, split.CurrentLeftWidth);

            split.CommitLeftWidth(384);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, state.Load("round-trip"));
        }
        finally
        {
            window.Hide();
        }

        var restored = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var restoredWindow = new Window { Width = 900, Height = 600, Content = restored };
        try
        {
            restoredWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, restored.CurrentLeftWidth);
        }
        finally
        {
            restoredWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void Light_and_dark_palette_tokens_match_the_app_contract()
    {
        Assert.True(Application.Current!.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out object? darkAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out object? darkCanvas));
        Assert.True(Application.Current.TryGetResource("AppAccentBrush", ThemeVariant.Light, out object? lightAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Light, out object? lightCanvas));

        Assert.Equal(Color.Parse("#2CC7BC"), Assert.IsType<SolidColorBrush>(darkAccent).Color);
        Assert.Equal(Color.Parse("#0D1417"), Assert.IsType<SolidColorBrush>(darkCanvas).Color);
        Assert.Equal(Color.Parse("#087F8C"), Assert.IsType<SolidColorBrush>(lightAccent).Color);
        Assert.Equal(Color.Parse("#EEF4F3"), Assert.IsType<SolidColorBrush>(lightCanvas).Color);
    }

    [AvaloniaFact]
    public void Native_shell_constructs_and_routes_every_destination()
    {
        using ServiceProvider services = Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        ContentControl host = window.FindControl<ContentControl>("ContentHost")!;
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        var destinations = new (ShellDestination Destination, Type View)[]
        {
            (ShellDestination.Home, typeof(HomeView)),
            (ShellDestination.Library, typeof(LibraryView)),
            (ShellDestination.Health, typeof(HealthView)),
            (ShellDestination.Ingest, typeof(IngestView)),
            (ShellDestination.Organize, typeof(OrganizeView)),
            (ShellDestination.Operations, typeof(OperationsView)),
            (ShellDestination.Settings, typeof(SettingsView)),
        };

        foreach ((ShellDestination destination, Type view) in destinations)
        {
            navigation.Navigate(destination);
            Assert.IsType(view, host.Content);
            if (host.Content is LibraryView library)
            {
                AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                Assert.Equal(8, grid.Columns.Count);
                Assert.Equal("Artwork", grid.KeyFor(grid.Columns[0]));
                Popup columnPopover = library.FindControl<Popup>("ColumnPopover")!;
                Button columnsButton = library.FindControl<Button>("ColumnsButton")!;
                Button closeColumnsButton = library.FindControl<Button>("CloseColumnsButton")!;
                Assert.True(columnPopover.IsLightDismissEnabled);
                columnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(columnPopover.IsOpen);
                closeColumnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(columnPopover.IsOpen);
            }
        }
    }

    [AvaloniaFact]
    public void Every_destination_completes_a_headless_1440_by_900_render_pass()
    {
        using ServiceProvider services = Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        bool isCapturing = !string.IsNullOrWhiteSpace(captureDirectory);
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        if (isCapturing)
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 900;
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        if (isCapturing)
            Directory.CreateDirectory(captureDirectory!);

        Button homeNavigation = window.FindControl<Button>("HomeNav")!;
        Grid navigationContent = Assert.IsType<Grid>(homeNavigation.Content);
        Viewbox navigationIcon = navigationContent.Children.OfType<Viewbox>()
            .Single(icon => icon.Classes.Contains("nav-icon"));
        TextBlock navigationLabel = navigationContent.Children.OfType<TextBlock>()
            .Single(text => text.Classes.Contains("nav-label"));
        Assert.Equal(44, homeNavigation.Bounds.Height);
        Assert.True(homeNavigation.Bounds.Width >= 190,
            $"Navigation selection did not fill the rail. Width={homeNavigation.Bounds.Width:0}");
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, homeNavigation.VerticalContentAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationContent.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationIcon.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationLabel.VerticalAlignment);
        Assert.Equal(20, navigationIcon.Width);
        Assert.Equal(20, navigationIcon.Height);
        Assert.Equal(15, navigationLabel.FontSize);
        Border navigationMarker = navigationContent.Children.OfType<Border>()
            .Single(marker => marker.Classes.Contains("nav-marker"));
        Assert.Equal(1, navigationMarker.Opacity);
        Assert.Equal(3, navigationMarker.Width);
        Assert.Equal(22, navigationMarker.Height);
        Assert.Equal(
            global::Avalonia.Layout.VerticalAlignment.Center,
            window.FindControl<TextBox>("SearchBox")!.VerticalContentAlignment);
        foreach (string captionName in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
        {
            Button caption = window.FindControl<Button>(captionName)!;
            Viewbox icon = Assert.IsType<Viewbox>(caption.Content);
            ContentPresenter presenter = caption.GetVisualDescendants().OfType<ContentPresenter>().Single();
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, caption.HorizontalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, caption.VerticalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, presenter.HorizontalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, presenter.VerticalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, icon.HorizontalAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, icon.VerticalAlignment);
            Assert.Equal(20, icon.Width);
            Assert.Equal(20, icon.Height);
        }

        try
        {
            foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
            {
                navigation.Navigate(destination);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Control activeView = Assert.IsAssignableFrom<Control>(window.FindControl<ContentControl>("ContentHost")!.Content);
                Assert.True(activeView.Bounds.Width >= 1150,
                    $"{destination} did not fill the content host. Width={activeView.Bounds.Width:0}");
                if (destination == ShellDestination.Library)
                {
                    LibraryView library = Assert.IsType<LibraryView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                    if (!isCapturing)
                    {
                        grid.ItemsSource = new[]
                        {
                            new LibraryRow(new TrackRecord
                            {
                                Path = @"C:\Music\Wild Fire.flac",
                                Title = "Wild Fire",
                                Artist = "The Avalonians",
                            }),
                        };
                    }
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    PersistedSplitView split = library.FindControl<PersistedSplitView>("WorkspaceSplit")!;
                    ContentPresenter leftPresenter = split.FindControl<ContentPresenter>("LeftPresenter")!;
                    string bounds = string.Join(" -> ", new Control[] { grid }.Concat(grid.GetVisualAncestors().OfType<Control>())
                        .Select(control => $"{control.GetType().Name}:{control.Bounds.Width:0}x{control.Bounds.Height:0}"));
                    Assert.True(grid.Bounds.Width >= 500, $"Library grid width collapsed. Left={split.Left?.GetType().Name ?? "null"}; Presented={leftPresenter.Content?.GetType().Name ?? "null"}; Split={split.Bounds.Width:0}x{split.Bounds.Height:0}; Presenter={leftPresenter.Bounds.Width:0}x{leftPresenter.Bounds.Height:0}; {bounds}");
                    Assert.True(grid.Bounds.Height >= 300, $"Library grid height collapsed. {bounds}");
                    Assert.True(split.FindControl<ContentPresenter>("RightPresenter")!.Bounds.Width >= 190,
                        "Library inspector collapsed below its minimum width.");
                    Assert.Equal(8, grid.Columns.Count);
                    Assert.Contains(grid.GetVisualDescendants(), visual => visual.GetType().Name == "DataGridColumnHeader");
                    if (!isCapturing)
                        Assert.Single(grid.ItemsSource!.Cast<LibraryRow>());
                    SelectionInspectorView inspector = library.GetVisualDescendants().OfType<SelectionInspectorView>().Single();
                    Assert.True(inspector.FindControl<Border>("EmptyState")!.IsVisible);
                    Assert.False(inspector.FindControl<ScrollViewer>("InspectorContent")!.IsVisible);
                }
                else if (destination == ShellDestination.Settings)
                {
                    SettingsView settings = Assert.IsType<SettingsView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    ScrollViewer scroll = settings.FindControl<ScrollViewer>("ConfigurationSettingsScroll")!;
                    StackPanel content = settings.FindControl<StackPanel>("ConfigurationSettingsContent")!;
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, scroll.HorizontalContentAlignment);
                    Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, content.HorizontalAlignment);
                    Assert.True(content.Bounds.Width >= scroll.Bounds.Width - 50,
                        $"Settings content did not fill its viewport. Content={content.Bounds.Width:0}; Scroll={scroll.Bounds.Width:0}");
                }
                else if (destination == ShellDestination.Health)
                {
                    HealthView health = Assert.IsType<HealthView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    Assert.Equal("All findings", Assert.IsType<TextBlock>(
                        Assert.IsType<Grid>(health.FindControl<Button>("FindingRootButton")!.Content)
                            .GetVisualDescendants().OfType<TextBlock>().First()).Text);
                }
                using var frame = window.GetLastRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(1440, frame.PixelSize.Width);
                Assert.Equal(900, frame.PixelSize.Height);
                if (isCapturing)
                    frame.Save(Path.Combine(captureDirectory!, $"{destination}.png"));
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private sealed class FakeSettings : IAppSettings
    {
        private readonly Dictionary<string, string> _preferences = [];
        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() => new(null, null, 0);
        public void LoadConfig(string path) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs() { }
        public string? GetPreference(string key) => _preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }
}
