using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicLibraryManager.Pages;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager;

public partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IWindowStateService _windowState;
    private readonly Dictionary<ShellDestination, FrameworkElement> _pages = [];
    private bool? _compact;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.GetService<ShellViewModel>();
        _navigation = App.GetService<INavigationService>();
        _windowState = App.GetService<IWindowStateService>();
        _navigation.NavigationRequested += Navigate;
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        RestoreWindow();
        Navigate(ShellDestination.Home);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) => ApplyResponsiveLayout(ActualWidth);

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
        SelectNavigation(destination);
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out ShellDestination destination))
            _navigation.Navigate(destination);
    }

    private void Configuration_Click(object sender, RoutedEventArgs e) => _navigation.Navigate(ShellDestination.Settings);

    private void GlobalSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        App.GetService<LibraryViewModel>().SetGlobalFilter(GlobalSearchBox.Text);
        e.Handled = true;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.K)
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.I)
        {
            IndexingViewModel indexing = App.GetService<IndexingViewModel>();
            if (indexing.IndexCommand.CanExecute(null))
                indexing.IndexCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        bool compact = width < 1050;
        if (_compact == compact)
            return;
        _compact = compact;
        NavigationColumn.Width = new GridLength(compact ? 64 : 220);
        foreach (TextBlock label in new[] { HomeLabel, LibraryLabel, HealthLabel, IngestLabel, OrganizeLabel, OperationsLabel, SettingsLabel })
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AppIdentity.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ConfigurationButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ActivityButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SelectNavigation(ShellDestination destination)
    {
        foreach (RadioButton button in FindVisualChildren<RadioButton>(this))
            if (button.Tag is string tag)
                button.IsChecked = tag.Equals(destination.ToString(), StringComparison.Ordinal);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void RestoreWindow()
    {
        WindowStateSnapshot? state = _windowState.Load();
        if (state is null)
            return;
        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        Left = Math.Clamp(state.X, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(state.Y, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (state.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _windowState.Save(new WindowStateSnapshot(1, (int)bounds.Left, (int)bounds.Top,
            (int)bounds.Width, (int)bounds.Height, WindowState == WindowState.Maximized));
    }

}
