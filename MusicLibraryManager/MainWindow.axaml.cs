using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;
using MusicLibraryManager.Views;

namespace MusicLibraryManager;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly INavigationService _navigation;
    private readonly IWindowStateService _windowState;
    private readonly DialogService _dialogs;
    private readonly Dictionary<ShellDestination, Button> _navigationButtons;
    private readonly Dictionary<ShellDestination, Control> _views = [];
    private readonly ContentControl _contentHost;
    private PixelPoint _normalPosition = new(80, 60);
    private Size _normalSize = new(1440, 900);
    private bool _restoring;
    private bool _closeApproved;
    private bool _checkingClose;

    public MainWindow()
    {
        InitializeComponent();
        _contentHost = this.FindControl<ContentControl>("ContentHost")!;
        _shell = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        if (_navigation is NavigationService navigationService)
            navigationService.Guard = CanNavigateAsync;
        _windowState = App.GetService<IWindowStateService>();
        _dialogs = App.GetService<DialogService>();
        DataContext = _shell;
        _navigationButtons = new()
        {
            [ShellDestination.Home] = this.FindControl<Button>("HomeNav")!,
            [ShellDestination.Library] = this.FindControl<Button>("LibraryNav")!,
            [ShellDestination.Workbench] = this.FindControl<Button>("WorkbenchNav")!,
            [ShellDestination.Health] = this.FindControl<Button>("HealthNav")!,
            [ShellDestination.Ingest] = this.FindControl<Button>("IngestNav")!,
            [ShellDestination.Organize] = this.FindControl<Button>("OrganizeNav")!,
            [ShellDestination.Devices] = this.FindControl<Button>("DevicesNav")!,
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

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeApproved)
        {
            e.Cancel = true;
            if (_checkingClose)
                return;
            _checkingClose = true;
            bool canClose = await CanCloseAsync();
            _checkingClose = false;
            if (canClose)
            {
                _closeApproved = true;
                Close();
            }
            return;
        }

        _navigation.NavigationRequested -= Navigate;
        if (_navigation is NavigationService navigationService)
            navigationService.Guard = null;
        CaptureNormalBounds();
        _windowState.Save(new WindowStateSnapshot(1, _normalPosition.X, _normalPosition.Y,
            (int)Math.Max(MinWidth, _normalSize.Width),
            (int)Math.Max(MinHeight, _normalSize.Height),
            WindowState == WindowState.Maximized));
    }

    private async Task<bool> CanNavigateAsync(ShellDestination destination)
    {
        if (destination == _navigation.Current)
            return true;
        if (_contentHost.Content is Control { DataContext: INavigationGuard guard } &&
            guard.HasUnsavedChanges)
            return await guard.ConfirmNavigationAsync();
        return true;
    }

    private async Task<bool> CanCloseAsync()
    {
        // An active overlay owns the close gesture. Do not queue another prompt behind it, and
        // never let the native caption bypass a dirty/busy editor's dismissal policy.
        if (_dialogs.HandleOwnerWindowClose())
            return false;

        if (_contentHost.Content is Control { DataContext: INavigationGuard guard } &&
            guard.HasUnsavedChanges && !await guard.ConfirmNavigationAsync())
            return false;
        if (!_shell.HasRunningActivity)
            return true;
        return await App.GetService<IDialogCoordinator>().ConfirmAsync(
            "Quit while work is running?",
            "A library operation is still running. Quit only if you are prepared to inspect its recovery state the next time the app starts.",
            "Quit");
    }

    private void Navigate(ShellDestination destination)
    {
        foreach ((ShellDestination key, Button button) in _navigationButtons)
        {
            bool isActive = key == destination;
            button.Classes.Set("active", isActive);
            global::Avalonia.Automation.AutomationProperties.SetItemStatus(
                button, isActive ? "Selected" : "Not selected");
        }
        if (!_views.TryGetValue(destination, out Control? view))
        {
            view = destination switch
            {
                ShellDestination.Home => new HomeView(),
                ShellDestination.Library => new LibraryView(),
                ShellDestination.Workbench => new WorkbenchView(),
                ShellDestination.Health => new HealthView(),
                ShellDestination.Ingest => new IngestView(),
                ShellDestination.Organize => new OrganizeView(),
                ShellDestination.Devices => new DevicesView(),
                ShellDestination.Operations => new OperationsView(),
                ShellDestination.Settings => new SettingsView(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(destination),
                    destination,
                    "Unsupported shell destination."),
            };
            _views[destination] = view;
        }
        _contentHost.Content = view;
        ApplyResponsiveLayout();
        Dispatcher.UIThread.Post(() =>
            view.GetVisualDescendants().OfType<PageHeader>().FirstOrDefault()?.Focus());
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
        bool compactHeight = Bounds.Height <= 650;
        ActivityMessageText.IsVisible = !compactHeight;
        ActivityStateLabel.IsVisible = !compactHeight;
        ActivityBanner.Padding = new Thickness(12, compactHeight ? 6 : 10);
        ActivityBanner.Margin = new Thickness(18, compactHeight ? 4 : 8, 18, 0);
        if (_contentHost.Content is LibraryView library)
            library.ApplyResponsiveLayout(Bounds.Width <= 1100);
    }

}
