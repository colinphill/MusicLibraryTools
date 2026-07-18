using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Studio.Components;
using MusicLibraryManager.Studio.Services;

namespace MusicLibraryManager.Studio;

public partial class MainWindow : Window
{
    private readonly IWindowStateService _windowState;
    private readonly IThemeService _theme;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        _windowState = App.GetService<IWindowStateService>();
        _theme = App.GetService<IThemeService>();
        if (_theme is StudioThemeService studioTheme)
            studioTheme.Changed += OnThemeChanged;
        StudioWebView.Services = App.Services;
        StudioWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(StudioApp),
        });
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized;
        ApplyTitleBarTheme();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowStateSnapshot? state = _windowState.Load();
        if (state is null)
        {
            Left = 80;
            Top = 60;
            Width = 1440;
            Height = 900;
            return;
        }

        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        Left = state.X;
        Top = state.Y;
        if (state.Maximized)
            WindowState = WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_theme is StudioThemeService studioTheme)
            studioTheme.Changed -= OnThemeChanged;
        StateChanged -= OnWindowStateChanged;
        _windowSource?.RemoveHook(WindowMessageHook);
        Rect bounds = RestoreBounds;
        _windowState.Save(new WindowStateSnapshot(
            1,
            (int)bounds.Left,
            (int)bounds.Top,
            (int)Math.Max(MinWidth, bounds.Width),
            (int)Math.Max(MinHeight, bounds.Height),
            WindowState == WindowState.Maximized));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        ApplyTitleBarTheme();
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyTitleBarTheme);

    private void ApplyTitleBarTheme()
    {
        bool highContrast = SystemParameters.HighContrast;
        bool light = _theme.Current == "Light" || (_theme.Current == "System" && SystemUsesLightTheme());
        Color canvas = highContrast ? SystemColors.WindowColor : ParseColor(light ? "#EEF4F3" : "#0D1417");
        Color panel = highContrast ? SystemColors.WindowColor : ParseColor(light ? "#F8FBFA" : "#121D21");
        Color raised = highContrast ? SystemColors.ControlColor : ParseColor(light ? "#FFFFFF" : "#18262B");
        Color border = highContrast ? SystemColors.WindowTextColor : ParseColor(light ? "#CFDDDA" : "#2A3B40");
        Color text = highContrast ? SystemColors.WindowTextColor : ParseColor(light ? "#142321" : "#EAF3F2");
        Color accent = highContrast ? SystemColors.HighlightColor : ParseColor(light ? "#087F8C" : "#2CC7BC");
        Color captionHover = highContrast ? SystemColors.HighlightColor : ParseColor(light ? "#E7EFED" : "#1B2C31");
        Color captionPressed = highContrast ? SystemColors.HighlightColor : ParseColor(light ? "#D8E6E3" : "#244047");
        Color closeHover = highContrast ? SystemColors.HighlightColor : ParseColor(light ? "#BD414B" : "#D85D65");
        Color closeHoverText = highContrast ? SystemColors.HighlightTextColor : Colors.White;

        Background = Brush(canvas);
        TitleBar.Background = Brush(panel);
        TitleBar.BorderBrush = Brush(border);
        TitleText.Foreground = Brush(text);
        StudioBadge.Background = Brush(raised);
        StudioBadge.BorderBrush = Brush(border);
        StudioBadgeText.Foreground = Brush(accent);
        Resources["CaptionForegroundBrush"] = Brush(text);
        Resources["CaptionHoverBrush"] = Brush(captionHover);
        Resources["CaptionPressedBrush"] = Brush(captionPressed);
        Resources["CloseHoverBrush"] = Brush(closeHover);
        Resources["CloseHoverTextBrush"] = Brush(closeHoverText);
        ApplyNativeChromeColors(!light && !highContrast, panel, border, text);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSettingChange = 0x001A;
        const int WmThemeChanged = 0x031A;
        const int WmDwmColorizationColorChanged = 0x0320;
        if (message is WmSettingChange or WmThemeChanged or WmDwmColorizationColorChanged)
            Dispatcher.BeginInvoke(ApplyTitleBarTheme);
        return IntPtr.Zero;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        string label = maximized ? "Restore" : "Maximize";
        MaximizeButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeButton, label);
    }

    private void ApplyNativeChromeColors(bool dark, Color caption, Color border, Color text)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        int cornerPreference = 2;
        DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        int borderColor = ToColorRef(border);
        int captionColor = ToColorRef(caption);
        int textColor = ToColorRef(text);
        DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? personalization = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalization?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static Color ParseColor(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private void OnBlazorWebViewInitialized(object sender, BlazorWebViewInitializedEventArgs e)
    {
        e.WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        string? captureDestination = Environment.GetEnvironmentVariable("STUDIO_CAPTURE_DESTINATION");
        if (Enum.TryParse(captureDestination, ignoreCase: true, out ShellDestination destination))
            _ = NavigateForCaptureAsync(destination);
    }

    private async Task NavigateForCaptureAsync(ShellDestination destination)
    {
        // The environment variable is used only by Capture-OpticalReferences.ps1. Give the
        // Blazor root component time to subscribe to navigation before selecting the page.
        await Task.Delay(1000);
        await Dispatcher.InvokeAsync(() => App.GetService<INavigationService>().Navigate(destination));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.TryGetWebMessageAsString(), "StudioFilesDropped", StringComparison.Ordinal))
            return;

        string? path = e.AdditionalObjects.OfType<CoreWebView2FileSystemHandle>()
            .FirstOrDefault(handle => handle.Kind == CoreWebView2FileSystemHandleKind.Directory)?.Path
            ?? e.AdditionalObjects.OfType<CoreWebView2File>().Select(file => file.Path)
                .FirstOrDefault(Directory.Exists);
        if (!string.IsNullOrWhiteSpace(path))
            App.GetService<StudioDropService>().SetDroppedSource(path);
    }
}
