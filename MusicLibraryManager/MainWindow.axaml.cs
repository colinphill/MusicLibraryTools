using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using System.ComponentModel;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;
using MusicLibraryManager.Views;

namespace MusicLibraryManager;

public partial class MainWindow : Window
{
    private const double CollapsedRailWidth = 64;
    private const double ExpandedRailWidth = 220;
    private const double MinimumDestinationWidth = 900;

    private readonly ShellViewModel _shell;
    private readonly INavigationService _navigation;
    private readonly IWindowStateService _windowState;
    private readonly DialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly IAppSettings _settings;
    private readonly LibraryViewModel _library;
    private readonly EventHandler
        _appearancePreferencesChangedHandler;
    private readonly Dictionary<ShellDestination, Button> _navigationButtons;
    private readonly Dictionary<ShellDestination, Control> _views = [];
    private readonly ContentControl _contentHost;
    private PixelPoint _normalPosition = new(80, 60);
    private Size _normalSize = new(1440, 900);
    private bool _restoring;
    private bool _closeApproved;
    private bool _checkingClose;
    private bool _shellRailExpanded;
    private bool _navigationOverlayOpen;
    private bool _navigationOverlayDismissed;
    private bool _useOverlayRailPresentation;
    private bool _canDockExpandedRail;
    private bool _syncingNavigationSelection;
    private bool _syncingSearchText;
    private double _dockedRailWidth = CollapsedRailWidth;
    private ShellDestination? _selectionNavigationRequest;

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
        _localization = App.GetService<ILocalizationService>();
        _settings = App.GetService<IAppSettings>();
        _library = App.GetService<LibraryViewModel>();
        _appearancePreferencesChangedHandler =
            OnAppearancePreferencesChanged;
        _shellRailExpanded =
            AppearancePreferences.GetShellRailExpanded(_settings);
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
            [ShellDestination.About] = this.FindControl<Button>("AboutNav")!,
        };
        _navigation.NavigationRequested += Navigate;
        _localization.CultureChanged += OnCultureChanged;
        _shell.PropertyChanged += OnShellPropertyChanged;
        _library.PropertyChanged +=
            OnLibraryPropertyChanged;
        AppearancePreferences.Changed +=
            _appearancePreferencesChangedHandler;
        Opened += OnOpened;
        Closing += OnClosing;
        PositionChanged += (_, _) => CaptureNormalBounds();
        SizeChanged += (_, _) =>
        {
            CaptureNormalBounds();
            ApplyResponsiveLayout();
        };
        KeyDown += OnWindowKeyDown;
        SearchShortcutText.Text = OperatingSystem.IsMacOS()
            ? "⌘K"
            : "Ctrl+K";
        NavigationScrim.SetValue(Visual.ZIndexProperty, 90);
        NavigationRail.SetValue(Visual.ZIndexProperty, 100);
        RootDialogHost.SetValue(Visual.ZIndexProperty, 200);
        ApplyAppearancePreferences();
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
        _localization.CultureChanged -= OnCultureChanged;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _library.PropertyChanged -=
            OnLibraryPropertyChanged;
        AppearancePreferences.Changed -=
            _appearancePreferencesChangedHandler;
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
            _localization.Get("Shell.Quit.Title"),
            _localization.Get("Shell.Quit.Message"),
            _localization.Get("Shell.Quit.Action"));
    }

    private void Navigate(ShellDestination destination)
    {
        CloseNavigationOverlay(restoreFocus: false);
        foreach ((ShellDestination key, Button button) in _navigationButtons)
        {
            bool isActive = key == destination;
            button.Classes.Set("active", isActive);
            global::Avalonia.Automation.AutomationProperties.SetItemStatus(
                button,
                _localization.Get(
                    isActive
                        ? "Shell.Selection.Selected"
                        : "Shell.Selection.NotSelected"));
        }
        SynchronizeNavigationSelection(destination);
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
                ShellDestination.About => new AboutView(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(destination),
                    destination,
                    "Unsupported shell destination."),
            };
            _views[destination] = view;
        }
        _contentHost.Content = view;
        if (destination == ShellDestination.Library)
            SynchronizeSearchToLibrary();
        ApplyResponsiveLayout();
    }

    private void OnNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            Enum.TryParse(
                value,
                out ShellDestination destination) &&
            _selectionNavigationRequest != destination &&
            _navigation.Current != destination)
            _navigation.Navigate(destination);
    }

    private async void OnNavigationSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingNavigationSelection ||
            sender is not ListBox
            {
                SelectedItem: Button
                {
                    Tag: string value,
                },
            } ||
            !Enum.TryParse(
                value,
                out ShellDestination destination) ||
            destination == _navigation.Current)
            return;

        _selectionNavigationRequest = destination;
        try
        {
            if (_navigation is NavigationService navigationService)
                await navigationService.NavigateAsync(destination);
            else
                _navigation.Navigate(destination);
        }
        finally
        {
            _selectionNavigationRequest = null;
            SynchronizeNavigationSelection(_navigation.Current);
        }
    }

    private void SynchronizeNavigationSelection(
        ShellDestination destination)
    {
        if (!_navigationButtons.TryGetValue(
                destination,
                out Button? selected))
            return;

        _syncingNavigationSelection = true;
        try
        {
            bool secondary =
                destination is
                    ShellDestination.Settings or
                    ShellDestination.About;
            PrimaryNavigation.SelectedItem =
                secondary
                    ? null
                    : selected;
            SecondaryNavigation.SelectedItem =
                secondary
                    ? selected
                    : null;
        }
        finally
        {
            _syncingNavigationSelection = false;
        }
    }

    private void OnConfigurationClick(object? sender, RoutedEventArgs e) =>
        _navigation.Navigate(ShellDestination.Settings);

    private void OnCultureChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach ((ShellDestination destination, Button button) in
                     _navigationButtons)
            {
                bool isActive = destination == _navigation.Current;
                global::Avalonia.Automation.AutomationProperties.SetItemStatus(
                    button,
                    _localization.Get(
                        isActive
                            ? "Shell.Selection.Selected"
                            : "Shell.Selection.NotSelected"));
            }
        });

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        SynchronizeSearchToLibrary();
        if (_navigation.Current != ShellDestination.Library)
            _navigation.Navigate(ShellDestination.Library);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            _navigationOverlayOpen)
        {
            CloseNavigationOverlay(restoreFocus: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab &&
            _navigationOverlayOpen)
        {
            CycleNavigationOverlayFocus(
                reverse: e.KeyModifiers.HasFlag(
                    KeyModifiers.Shift));
            e.Handled = true;
            return;
        }

        KeyModifiers shortcutModifier = OperatingSystem.IsMacOS()
            ? KeyModifiers.Meta
            : KeyModifiers.Control;
        if (!e.KeyModifiers.HasFlag(shortcutModifier))
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
        double shellWidth = Math.Max(
            MinWidth,
            BodyGrid.Bounds.Width > 0
                ? BodyGrid.Bounds.Width
                : Bounds.Width > 0
                    ? Bounds.Width
                    : Width);
        _canDockExpandedRail =
            shellWidth >= MinimumDestinationWidth +
            ExpandedRailWidth;
        if (!_shellRailExpanded)
        {
            _navigationOverlayOpen = false;
            _navigationOverlayDismissed = false;
            _useOverlayRailPresentation = false;
        }
        else if (!_canDockExpandedRail &&
                 !_useOverlayRailPresentation)
        {
            // Crossing below the safe docking threshold changes presentation,
            // not the persisted expansion preference. Keep all intermediate
            // widths for the destination and expose the labeled rail above it.
            _useOverlayRailPresentation = true;
            _navigationOverlayOpen =
                !_navigationOverlayDismissed;
        }

        bool dockExpanded =
            _shellRailExpanded &&
            _canDockExpandedRail &&
            !_useOverlayRailPresentation;
        double dockedRailWidth = dockExpanded
            ? ExpandedRailWidth
            : CollapsedRailWidth;
        _dockedRailWidth = dockedRailWidth;
        bool labelsVisible =
            _navigationOverlayOpen ||
            dockExpanded;
        BodyGrid.ColumnDefinitions[0].Width =
            new GridLength(dockedRailWidth);
        NavigationRail.Width = _navigationOverlayOpen
            ? ExpandedRailWidth
            : dockedRailWidth;
        NavigationScrim.IsVisible =
            _navigationOverlayOpen;
        BrandCopy.IsVisible = labelsVisible;
        BrandBlock.HorizontalAlignment = labelsVisible
            ? global::Avalonia.Layout.HorizontalAlignment.Stretch
            : global::Avalonia.Layout.HorizontalAlignment.Center;
        foreach (TextBlock label in this.GetVisualDescendants().OfType<TextBlock>().Where(item => item.Classes.Contains("nav-label")))
            label.IsVisible = labelsVisible;
        double contentWidth = Math.Max(
            0,
            shellWidth - dockedRailWidth);
        bool compactToolbar = contentWidth <= 900;
        ConfigurationChipText.IsVisible = !compactToolbar;
        SearchShortcut.IsVisible = !compactToolbar;
        bool compactHeight = Bounds.Height <= 700;
        double gutter = compactHeight
            ? 12
            : contentWidth < 1000
                ? 16
                : 24;
        TopBarContent.Margin =
            new Thickness(gutter, 8);
        ActivityMessageText.IsVisible = !compactHeight;
        ActivityStateLabel.IsVisible = !compactHeight;
        ActivityBanner.Padding =
            new Thickness(
                12,
                compactHeight ? 8 : 12);
        ActivityBanner.Margin = new Thickness(
            gutter,
            compactHeight ? 4 : 8,
            gutter,
            0);
        if (_contentHost.Content is LibraryView library)
            library.ApplyResponsiveLayout(contentWidth <= 1100);
        else if (_contentHost.Content is WorkbenchView workbench)
            workbench.ApplyResponsiveLayout(contentWidth <= 1100);
    }

    private void OnNavigationRailToggle(
        object? sender,
        RoutedEventArgs e)
    {
        bool dockedExpanded =
            _shellRailExpanded &&
            !_navigationOverlayOpen &&
            _dockedRailWidth == ExpandedRailWidth;
        if (dockedExpanded ||
            _navigationOverlayOpen)
        {
            _shellRailExpanded = false;
            _navigationOverlayOpen = false;
            _navigationOverlayDismissed = false;
            _useOverlayRailPresentation = false;
            AppearancePreferences.SetShellRailExpanded(
                _settings,
                false);
        }
        else if (_canDockExpandedRail)
        {
            _shellRailExpanded = true;
            _navigationOverlayOpen = false;
            _navigationOverlayDismissed = false;
            _useOverlayRailPresentation = false;
            AppearancePreferences.SetShellRailExpanded(
                _settings,
                true);
        }
        else
        {
            _shellRailExpanded = true;
            _navigationOverlayOpen = true;
            _navigationOverlayDismissed = false;
            _useOverlayRailPresentation = true;
            AppearancePreferences.SetShellRailExpanded(
                _settings,
                true);
        }

        ApplyResponsiveLayout();
        e.Handled = true;
    }

    private void OnNavigationScrimPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        CloseNavigationOverlay(restoreFocus: true);
        e.Handled = true;
    }

    private void CloseNavigationOverlay(
        bool restoreFocus)
    {
        bool overlayPresentationRequested =
            _useOverlayRailPresentation ||
            double.IsFinite(Width) &&
            Width <
                MinimumDestinationWidth +
                ExpandedRailWidth;
        if (!_navigationOverlayOpen &&
            !overlayPresentationRequested)
            return;
        _navigationOverlayOpen = false;
        if (overlayPresentationRequested)
        {
            // Navigation can be requested after Width changes but before the
            // corresponding layout pass. Remember the dismissal so that late
            // responsive work does not reopen the rail over the destination.
            _useOverlayRailPresentation = true;
            _navigationOverlayDismissed = true;
        }
        ApplyResponsiveLayout();
        if (restoreFocus)
            NavigationRailToggle.Focus();
    }

    private void CycleNavigationOverlayFocus(
        bool reverse)
    {
        Control[] focusable =
            NavigationRail
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.Focusable &&
                    control.IsEffectivelyEnabled &&
                    control.IsEffectivelyVisible)
                .ToArray();
        if (focusable.Length == 0)
            return;
        object? focused =
            FocusManager?.GetFocusedElement();
        int index = Array.IndexOf(
            focusable,
            focused);
        int next = reverse
            ? index <= 0
                ? focusable.Length - 1
                : index - 1
            : index < 0 ||
              index >= focusable.Length - 1
                ? 0
                : index + 1;
        focusable[next].Focus();
    }

    private void OnNavigationKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key is not (
                Key.Up or
                Key.Down or
                Key.Home or
                Key.End))
            return;

        Button[] buttons =
            Enum.GetValues<ShellDestination>()
                .Select(destination =>
                    _navigationButtons[destination])
                .Where(button => button.IsVisible)
                .ToArray();
        if (buttons.Length == 0)
            return;
        Button? current = e.Source as Button ??
            (e.Source as Visual)?
            .GetVisualAncestors()
            .OfType<Button>()
            .FirstOrDefault();
        int index = current is null
            ? -1
            : Array.IndexOf(buttons, current);
        index = e.Key switch
        {
            Key.Home => 0,
            Key.End => buttons.Length - 1,
            Key.Up => index <= 0
                ? buttons.Length - 1
                : index - 1,
            _ => index < 0 ||
                 index >= buttons.Length - 1
                ? 0
                : index + 1,
        };
        Button target = buttons[index];
        target.Focus();
        ShellDestination destination =
            _navigationButtons.First(
                pair => ReferenceEquals(
                    pair.Value,
                    target)).Key;
        _navigation.Navigate(destination);
        e.Handled = true;
    }

    private void OnShellPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
                nameof(ShellViewModel.GlobalSearchText) &&
            _navigation.Current ==
                ShellDestination.Library)
            SynchronizeSearchToLibrary();
    }

    private void OnLibraryPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName !=
                nameof(LibraryViewModel.FilterText) ||
            _syncingSearchText)
            return;
        _syncingSearchText = true;
        try
        {
            _shell.GlobalSearchText =
                _library.FilterText;
        }
        finally
        {
            _syncingSearchText = false;
        }
    }

    private void SynchronizeSearchToLibrary()
    {
        if (_syncingSearchText ||
            string.Equals(
                _library.FilterText,
                _shell.GlobalSearchText,
                StringComparison.Ordinal))
            return;
        _syncingSearchText = true;
        try
        {
            _library.FilterText =
                _shell.GlobalSearchText;
        }
        finally
        {
            _syncingSearchText = false;
        }
    }

    private void OnAppearancePreferencesChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            bool expanded =
                AppearancePreferences.GetShellRailExpanded(
                    _settings);
            if (expanded != _shellRailExpanded)
            {
                _shellRailExpanded = expanded;
                _navigationOverlayOpen = false;
                _navigationOverlayDismissed = false;
                _useOverlayRailPresentation = false;
            }
            ApplyAppearancePreferences();
            ApplyResponsiveLayout();
        });

    private void ApplyAppearancePreferences()
    {
        bool compact =
            AppearancePreferences.GetDensity(_settings) ==
            UiDensity.Compact;
        Classes.Set("density-compact", compact);
        Classes.Set("density-standard", !compact);
    }

}
