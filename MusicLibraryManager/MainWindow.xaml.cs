using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicLibraryManager.Pages;
using MusicLibraryManager.Presentation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace MusicLibraryManager;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IWindowStateService _windowState;
    private readonly Dictionary<ShellDestination, FrameworkElement> _pages = [];
    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        Root.DataContext = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        _windowState = App.GetService<IWindowStateService>();
        _navigation.NavigationRequested += Navigate;

        nint hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureTitleBar();
        _appWindow.Closing += AppWindow_Closing;
        RestoreWindow();
        Navigate(ShellDestination.Home);
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(45, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(75, 0, 0, 0);
        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
    }

    private void Navigate(ShellDestination destination)
    {
        if (!_pages.TryGetValue(destination, out FrameworkElement? page))
        {
            page = destination switch
            {
                ShellDestination.Home => new HomePage(),
                ShellDestination.Library => new LibraryPage(),
                ShellDestination.Health => new HealthPage(),
                ShellDestination.Ingest => new IngestPage(),
                ShellDestination.Organize => new OrganizePage(),
                ShellDestination.Operations => new OperationsPage(),
                ShellDestination.Settings => new SettingsPage(),
                _ => new MilestonePage(destination),
            };
            _pages[destination] = page;
        }

        PageHost.Content = page;
        ShellNavigation.SelectedItem = destination == ShellDestination.Settings
            ? ShellNavigation.SettingsItem
            : ShellNavigation.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), destination.ToString(), StringComparison.Ordinal));
    }

    private void ShellNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _navigation.Navigate(ShellDestination.Settings);
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag && Enum.TryParse(tag, out ShellDestination destination))
            _navigation.Navigate(destination);
    }

    private void Configuration_Click(object sender, RoutedEventArgs e)
        => _navigation.Navigate(ShellDestination.Settings);

    private void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => App.GetService<LibraryViewModel>().SetGlobalFilter(sender.Text);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && e.Key == VirtualKey.K)
        {
            GlobalSearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.I)
        {
            IndexingViewModel indexing = App.GetService<IndexingViewModel>();
            if (indexing.IndexCommand.CanExecute(null))
                indexing.IndexCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RestoreWindow()
    {
        WindowStateSnapshot? state = _windowState.Load();
        RectInt32 bounds = state is null
            ? new RectInt32(80, 60, 1440, 900)
            : new RectInt32(state.X, state.Y, Math.Max(900, state.Width), Math.Max(600, state.Height));
        _appWindow.MoveAndResize(bounds);
        if (state?.Maximized == true && _appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        RectInt32 bounds = sender.Position.X >= 0
            ? new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height)
            : new RectInt32(80, 60, 1440, 900);
        bool maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _windowState.Save(new WindowStateSnapshot(1, bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized));
    }
}
