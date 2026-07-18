using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Studio.Avalonia.Views;

namespace MusicLibraryManager.Studio.Avalonia;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly INavigationService _navigation;
    private readonly IWindowStateService _windowState;
    private readonly Dictionary<ShellDestination, Button> _navigationButtons;
    private readonly ContentControl _contentHost;
    private PixelPoint _normalPosition = new(80, 60);
    private Size _normalSize = new(1440, 900);
    private bool _restoring;

    public MainWindow()
    {
        InitializeComponent();
        _contentHost = this.FindControl<ContentControl>("ContentHost")!;
        _shell = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        _windowState = App.GetService<IWindowStateService>();
        DataContext = _shell;
        _navigationButtons = new()
        {
            [ShellDestination.Home] = this.FindControl<Button>("HomeNav")!,
            [ShellDestination.Library] = this.FindControl<Button>("LibraryNav")!,
            [ShellDestination.Health] = this.FindControl<Button>("HealthNav")!,
            [ShellDestination.Ingest] = this.FindControl<Button>("IngestNav")!,
            [ShellDestination.Organize] = this.FindControl<Button>("OrganizeNav")!,
            [ShellDestination.Operations] = this.FindControl<Button>("OperationsNav")!,
            [ShellDestination.Settings] = this.FindControl<Button>("SettingsNav")!,
        };
        _navigation.NavigationRequested += Navigate;
        Opened += OnOpened;
        Closing += OnClosing;
        PositionChanged += (_, _) => CaptureNormalBounds();
        SizeChanged += (_, _) =>
        {
            CaptureNormalBounds();
            ApplyResponsiveLayout();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
                UpdateMaximizeButton();
        };
        KeyDown += OnWindowKeyDown;
        Navigate(ShellDestination.Home);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RestoreWindowState();
        ApplyResponsiveLayout();
    }

    private void RestoreWindowState()
    {
        WindowStateSnapshot? state = _windowState.Load();
        if (state is null)
        {
            Position = _normalPosition;
            return;
        }

        _restoring = true;
        try
        {
            Screen? screen = Screens.All.FirstOrDefault(candidate => IsVisibleOn(state, candidate))
                ?? Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen is null)
                return;
            double scale = Math.Max(.25, screen.Scaling);
            PixelRect area = screen.WorkingArea;
            double width = Math.Clamp(state.Width, MinWidth, area.Width / scale);
            double height = Math.Clamp(state.Height, MinHeight, area.Height / scale);
            int pixelWidth = (int)Math.Round(width * scale);
            int pixelHeight = (int)Math.Round(height * scale);
            int x = Math.Clamp(state.X, area.X - pixelWidth + 80, area.Right - 80);
            int y = Math.Clamp(state.Y, area.Y, area.Bottom - 80);
            Width = width;
            Height = height;
            Position = new PixelPoint(x, y);
            _normalSize = new Size(width, height);
            _normalPosition = Position;
            if (state.Maximized)
                Dispatcher.UIThread.Post(() => WindowState = WindowState.Maximized);
        }
        finally
        {
            _restoring = false;
        }
    }

    private static bool IsVisibleOn(WindowStateSnapshot state, Screen screen)
    {
        double scale = Math.Max(.25, screen.Scaling);
        var rect = new PixelRect(state.X, state.Y,
            (int)Math.Round(state.Width * scale), (int)Math.Round(state.Height * scale));
        PixelRect intersection = rect.Intersect(screen.WorkingArea);
        return intersection.Width >= 80 && intersection.Height >= 80;
    }

    private void CaptureNormalBounds()
    {
        if (_restoring || WindowState != WindowState.Normal || Bounds.Width < MinWidth || Bounds.Height < MinHeight)
            return;
        _normalPosition = Position;
        _normalSize = Bounds.Size;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _navigation.NavigationRequested -= Navigate;
        CaptureNormalBounds();
        _windowState.Save(new WindowStateSnapshot(1, _normalPosition.X, _normalPosition.Y,
            (int)Math.Max(MinWidth, _normalSize.Width),
            (int)Math.Max(MinHeight, _normalSize.Height),
            WindowState == WindowState.Maximized));
    }

    private void Navigate(ShellDestination destination)
    {
        foreach ((ShellDestination key, Button button) in _navigationButtons)
            button.Classes.Set("active", key == destination);
        _contentHost.Content = destination switch
        {
            ShellDestination.Home => new HomeView(),
            ShellDestination.Library => new LibraryView(),
            ShellDestination.Health => new HealthView(),
            ShellDestination.Ingest => new IngestView(),
            ShellDestination.Organize => new OrganizeView(),
            ShellDestination.Operations => new OperationsView(),
            ShellDestination.Settings => new SettingsView(),
            _ => new PlaceholderView(destination.ToString()),
        };
        ApplyResponsiveLayout();
    }

    private void OnNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse(value, out ShellDestination destination))
            _navigation.Navigate(destination);
    }

    private void OnConfigurationClick(object? sender, RoutedEventArgs e) =>
        _navigation.Navigate(ShellDestination.Settings);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        App.GetService<LibraryViewModel>().SetGlobalFilter(_shell.GlobalSearchText);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (e.Key == Key.K)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.I)
        {
            var command = App.GetService<IndexingViewModel>().IndexCommand;
            if (command.CanExecute(null))
                command.Execute(null);
            e.Handled = true;
        }
    }

    private void ApplyResponsiveLayout()
    {
        bool compactNavigation = Bounds.Width < 1050;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(compactNavigation ? 64 : 220);
        BrandCopy.IsVisible = !compactNavigation;
        BrandBlock.HorizontalAlignment = compactNavigation ? global::Avalonia.Layout.HorizontalAlignment.Center : global::Avalonia.Layout.HorizontalAlignment.Stretch;
        foreach (TextBlock label in this.GetVisualDescendants().OfType<TextBlock>().Where(item => item.Classes.Contains("nav-label")))
            label.IsVisible = !compactNavigation;
        bool compactToolbar = Bounds.Width <= 900;
        ConfigurationChipText.IsVisible = !compactToolbar;
        SearchShortcut.IsVisible = !compactToolbar;
        if (_contentHost.Content is LibraryView library)
            library.ApplyResponsiveLayout(Bounds.Width <= 1100);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void UpdateMaximizeButton()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !maximized;
        RestoreGlyph.IsVisible = maximized;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }
}
