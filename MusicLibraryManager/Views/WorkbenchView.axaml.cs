using global::Avalonia;
using global::Avalonia.Automation;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Presenters;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;
using System.ComponentModel;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class WorkbenchView : UserControl
{
    private enum WorkbenchDrawerSurface
    {
        None,
        Inspector,
        PendingChanges,
        Columns,
    }

    private readonly WorkbenchViewModel _viewModel;
    private readonly ILocalizationService _localization;
    private WorkbenchDrawerSurface _activeDrawer;
    private bool _responsiveCompact;
    private bool _compactSectionPicker;
    private bool _compactHeight;
    private bool _sectionSuppressesInspector;
    private bool _inspectorPreference;
    private bool _resumeInspectorAfterTransientDrawer;
    private Control? _drawerFocusOwner;

    public WorkbenchView()
    {
        InitializeComponent();
        _viewModel = App.GetService<WorkbenchViewModel>();
        _localization =
            App.GetService<ILocalizationService>();
        DataContext = _viewModel;

        _inspectorPreference = _viewModel.IsInspectorOpen;
        _activeDrawer = _inspectorPreference
            ? WorkbenchDrawerSurface.Inspector
            : WorkbenchDrawerSurface.None;

        WorkbenchSessionSection.ColumnsRequested +=
            OnColumnsRequested;
        WorkbenchColumnsDrawer.Attach(
            WorkbenchSessionSection);
        WorkbenchColumnsDrawer.CloseRequested +=
            OnDrawerCloseRequested;
        WorkbenchPendingChangesDrawer.CloseRequested +=
            OnDrawerCloseRequested;
        WorkbenchInspectorDrawer.CloseRequested +=
            OnInspectorCloseRequested;

        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged +=
                OnViewModelPropertyChanged;
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
            ApplySectionSelection();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged -=
                OnViewModelPropertyChanged;
            _localization.CultureChanged -=
                OnLocalizationCultureChanged;
        };
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout(
                Bounds.Width <= 1100);

        ApplySectionSelection();
        ApplyResponsiveLayout(compact: false);
    }

    private void OnWorkbenchSectionClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } &&
            Enum.TryParse(
                section,
                ignoreCase: false,
                out WorkbenchSection selected))
            _viewModel.SelectedSection = selected;
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
            nameof(WorkbenchViewModel.SelectedSection))
        {
            ApplySectionSelection();
            return;
        }

        if (e.PropertyName !=
            nameof(WorkbenchViewModel.IsInspectorOpen))
            return;

        _inspectorPreference =
            _viewModel.IsInspectorOpen;
        if (!_inspectorPreference &&
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.None;
        }
        else if (_inspectorPreference &&
                 !_responsiveCompact &&
                 !_sectionSuppressesInspector &&
                 _activeDrawer ==
                    WorkbenchDrawerSurface.None)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.Inspector;
        }
        ApplyDrawerState();
    }

    private void ApplySectionSelection()
    {
        WorkbenchSection section =
            _viewModel.SelectedSection;
        WorkbenchTabs.SelectedIndex =
            (int)section;

        foreach ((WorkbenchSection value, Button button) in
                 SectionButtons())
        {
            bool selected = value == section;
            button.Classes.Set(
                "primary",
                selected);
            AutomationProperties.SetItemStatus(
                button,
                selected
                    ? L("Shell.Selection.Selected")
                    : L("Shell.Selection.NotSelected"));
        }

        _sectionSuppressesInspector =
            section is
                WorkbenchSection.Reports or
                WorkbenchSection.Playlists or
                WorkbenchSection.Tools or
                WorkbenchSection.Shortcuts;
        WorkbenchInspectorToggle.IsVisible =
            !_sectionSuppressesInspector;

        if (_sectionSuppressesInspector &&
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.None;
        }
        else if (!_sectionSuppressesInspector &&
                 !_responsiveCompact &&
                 _inspectorPreference &&
                 _activeDrawer ==
                    WorkbenchDrawerSurface.None)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.Inspector;
        }

        if (section != WorkbenchSection.Session &&
            _activeDrawer ==
                WorkbenchDrawerSurface.Columns)
        {
            CloseActiveDrawer(
                restoreFocus: false);
        }

        ApplyDrawerState();
    }

    private IEnumerable<(WorkbenchSection Section, Button Button)>
        SectionButtons()
    {
        yield return (
            WorkbenchSection.Session,
            WorkbenchSectionSession);
        yield return (
            WorkbenchSection.BulkOperation,
            WorkbenchSectionBulkOperation);
        yield return (
            WorkbenchSection.AllFields,
            WorkbenchSectionAllFields);
        yield return (
            WorkbenchSection.Files,
            WorkbenchSectionFiles);
        yield return (
            WorkbenchSection.OnlineMetadata,
            WorkbenchSectionOnlineMetadata);
        yield return (
            WorkbenchSection.Reports,
            WorkbenchSectionReports);
        yield return (
            WorkbenchSection.Playlists,
            WorkbenchSectionPlaylists);
        yield return (
            WorkbenchSection.Tools,
            WorkbenchSectionTools);
        yield return (
            WorkbenchSection.Shortcuts,
            WorkbenchSectionShortcuts);
    }

    private void OnInspectorToggle(
        object? sender,
        RoutedEventArgs e)
    {
        if (_sectionSuppressesInspector)
            return;
        if (_activeDrawer ==
            WorkbenchDrawerSurface.Inspector)
        {
            CloseActiveDrawer(
                restoreFocus: true,
                persistInspectorClose: true);
            return;
        }

        _inspectorPreference = true;
        _viewModel.IsInspectorOpen = true;
        ShowDrawer(
            WorkbenchDrawerSurface.Inspector,
            WorkbenchInspectorToggle);
    }

    private void OnWorkbenchPendingChangesClick(
        object? sender,
        RoutedEventArgs e) =>
        ToggleTransientDrawer(
            WorkbenchDrawerSurface.PendingChanges,
            WorkbenchPendingChangesButton);

    private void OnColumnsRequested(
        object? sender,
        EventArgs e) =>
        ToggleTransientDrawer(
            WorkbenchDrawerSurface.Columns,
            WorkbenchSessionSection.ColumnsButton);

    private void ToggleTransientDrawer(
        WorkbenchDrawerSurface surface,
        Control focusOwner)
    {
        if (_activeDrawer == surface)
        {
            CloseActiveDrawer(
                restoreFocus: true);
            return;
        }
        ShowDrawer(
            surface,
            focusOwner);
    }

    private void ShowDrawer(
        WorkbenchDrawerSurface surface,
        Control focusOwner)
    {
        bool resumeInspector =
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector ||
            IsTransientDrawer(_activeDrawer) &&
            _resumeInspectorAfterTransientDrawer;

        _resumeInspectorAfterTransientDrawer =
            IsTransientDrawer(surface) &&
            resumeInspector;
        _activeDrawer = surface;
        _drawerFocusOwner = focusOwner;
        ApplyDrawerState();

        Control initialFocus =
            surface switch
            {
                WorkbenchDrawerSurface.Inspector =>
                    WorkbenchInspectorDrawer.InitialFocus,
                WorkbenchDrawerSurface.PendingChanges =>
                    WorkbenchPendingChangesDrawer.InitialFocus,
                WorkbenchDrawerSurface.Columns =>
                    WorkbenchColumnsDrawer.InitialFocus,
                _ => focusOwner,
            };
        Dispatcher.UIThread.Post(
            () => initialFocus.Focus(),
            DispatcherPriority.Input);
    }

    private void OnDrawerCloseRequested(
        object? sender,
        EventArgs e) =>
        CloseActiveDrawer(
            restoreFocus: true);

    private void OnInspectorCloseRequested(
        object? sender,
        EventArgs e) =>
        CloseActiveDrawer(
            restoreFocus: true,
            persistInspectorClose: true);

    private void CloseActiveDrawer(
        bool restoreFocus,
        bool persistInspectorClose = false)
    {
        WorkbenchDrawerSurface closing =
            _activeDrawer;
        if (closing == WorkbenchDrawerSurface.None)
            return;

        if (persistInspectorClose &&
            closing ==
                WorkbenchDrawerSurface.Inspector)
        {
            _inspectorPreference = false;
            _viewModel.IsInspectorOpen = false;
        }

        bool resumeInspector =
            IsTransientDrawer(closing) &&
            _resumeInspectorAfterTransientDrawer &&
            _inspectorPreference &&
            !_sectionSuppressesInspector;
        _activeDrawer = resumeInspector
            ? WorkbenchDrawerSurface.Inspector
            : WorkbenchDrawerSurface.None;
        _resumeInspectorAfterTransientDrawer = false;

        Control? focusOwner =
            _drawerFocusOwner;
        _drawerFocusOwner = null;
        ApplyDrawerState();
        if (restoreFocus)
            RestoreDrawerFocus(focusOwner);
    }

    private static bool IsTransientDrawer(
        WorkbenchDrawerSurface surface) =>
        surface is
            WorkbenchDrawerSurface.PendingChanges or
            WorkbenchDrawerSurface.Columns;

    private void ApplyDrawerState()
    {
        if (_activeDrawer ==
                WorkbenchDrawerSurface.Inspector &&
            _sectionSuppressesInspector)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.None;
        }

        bool drawerVisible =
            _activeDrawer !=
                WorkbenchDrawerSurface.None;
        WorkbenchInspectorDrawer.IsVisible =
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector;
        WorkbenchPendingChangesDrawer.IsVisible =
            _activeDrawer ==
                WorkbenchDrawerSurface.PendingChanges;
        WorkbenchColumnsDrawer.IsVisible =
            _activeDrawer ==
                WorkbenchDrawerSurface.Columns;

        WorkbenchSplit.SetCompact(
            _responsiveCompact ||
            !drawerVisible);
        ContentPresenter? presenter =
            WorkbenchSplit.FindControl<ContentPresenter>(
                "RightPresenter");
        if (presenter is not null)
        {
            double drawerWidth =
                Math.Min(
                    430,
                    Math.Max(
                        300,
                        WorkbenchSplit.Bounds.Width -
                        24));
            presenter.Width =
                _responsiveCompact
                    ? drawerWidth
                    : double.NaN;
            presenter.IsVisible =
                drawerVisible;
            WorkbenchDrawerPane.Width =
                _responsiveCompact
                    ? drawerWidth
                    : double.NaN;
        }

        bool scrimVisible =
            drawerVisible &&
            (_responsiveCompact ||
             IsTransientDrawer(_activeDrawer));
        WorkbenchInspectorScrim.IsVisible =
            scrimVisible;
        WorkbenchHeaderScrim.IsVisible =
            scrimVisible;
        WorkbenchInspectorToggle.Content =
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector
                ? L("Workbench.Action.HideInspector")
                : L("Workbench.Action.Inspector");
    }

    private void OnDrawerScrimPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        CloseActiveDrawer(
            restoreFocus: true);
        e.Handled = true;
    }

    private static void RestoreDrawerFocus(
        Control? focusOwner)
    {
        if (focusOwner is null)
            return;
        Dispatcher.UIThread.Post(
            () => focusOwner.Focus(),
            DispatcherPriority.Input);
    }

    public void ApplyResponsiveLayout(
        bool compact)
    {
        double width = Bounds.Width;
        bool wasResponsiveCompact =
            _responsiveCompact;
        _responsiveCompact =
            width > 0
                ? width < 1200
                : compact;
        _compactSectionPicker =
            width > 0
                ? width < 880
                : compact;
        _compactHeight =
            Bounds.Height > 0 &&
            Bounds.Height <= 700;

        WorkbenchSectionPicker.IsVisible =
            _compactSectionPicker;
        WorkbenchSectionRail.IsVisible =
            !_compactSectionPicker;
        WorkbenchSectionDivider.IsVisible =
            !_compactSectionPicker;
        WorkbenchBody.ColumnDefinitions[0].Width =
            _compactSectionPicker
                ? new GridLength(0)
                : new GridLength(156);
        WorkbenchBody.ColumnDefinitions[1].Width =
            _compactSectionPicker
                ? new GridLength(0)
                : new GridLength(10);
        WorkbenchRoot.Margin =
            _compactHeight
                ? new Thickness(
                    10,
                    8,
                    10,
                    10)
                : _compactSectionPicker
                    ? new Thickness(
                        16,
                        14,
                        16,
                        16)
                    : new Thickness(
                        26,
                        22,
                        26,
                        26);
        WorkbenchRoot.RowSpacing =
            _compactHeight
                ? 8
                : 14;
        WorkbenchHeader.Subtitle =
            _compactHeight
                ? string.Empty
                : L("Workbench.Subtitle");
        WorkbenchSectionContentCard.Padding =
            _compactHeight
                ? new Thickness(8)
                : new Thickness(12);

        if (_responsiveCompact &&
            !wasResponsiveCompact &&
            _activeDrawer ==
                WorkbenchDrawerSurface.Inspector)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.None;
        }
        else if (!_responsiveCompact &&
                 wasResponsiveCompact &&
                 _activeDrawer ==
                    WorkbenchDrawerSurface.None &&
                 _inspectorPreference &&
                 !_sectionSuppressesInspector)
        {
            _activeDrawer =
                WorkbenchDrawerSurface.Inspector;
        }

        ApplyDrawerState();
    }

    private void OnDragOver(
        object? sender,
        DragEventArgs e)
    {
        e.DragEffects =
            e.DataTransfer.TryGetFiles()?.Any() == true
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(
        object? sender,
        DragEventArgs e)
    {
        string[] paths =
            e.DataTransfer.TryGetFiles()?
                .Select(item =>
                    item.TryGetLocalPath())
                .Where(path =>
                    path is not null)
                .Cast<string>()
                .ToArray() ??
            [];
        await AddDroppedSourcesAsync(paths);
        e.Handled = true;
    }

    internal Task AddDroppedSourcesAsync(
        IEnumerable<string?> paths)
    {
        string[] usablePaths =
            paths
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
        return usablePaths.Length == 0
            ? Task.CompletedTask
            : _viewModel.AddSourcesAsync(
                usablePaths);
    }

    private async void OnWorkbenchKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        bool requestsContextMenu =
            e.Key == Key.Apps ||
            e.Key == Key.F10 &&
            e.KeyModifiers.HasFlag(
                KeyModifiers.Shift);
        if (requestsContextMenu &&
            _viewModel.SelectedSection ==
                WorkbenchSection.Session &&
            WorkbenchSessionSection.SessionGrid
                .ContextMenu is
                { } sessionMenu)
        {
            sessionMenu.Open(
                WorkbenchSessionSection.SessionGrid);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape &&
            _activeDrawer !=
                WorkbenchDrawerSurface.None)
        {
            CloseActiveDrawer(
                restoreFocus: true);
            e.Handled = true;
            return;
        }

        object? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement();
        if (focused is
            TextBox or
            ComboBox or
            NumericUpDown)
            return;

        WorkbenchShortcutModifiers modifiers =
            WorkbenchShortcutModifiers.None;
        if (e.KeyModifiers.HasFlag(
                KeyModifiers.Control))
            modifiers |=
                WorkbenchShortcutModifiers.Control;
        if (e.KeyModifiers.HasFlag(
                KeyModifiers.Alt))
            modifiers |=
                WorkbenchShortcutModifiers.Alt;
        if (e.KeyModifiers.HasFlag(
                KeyModifiers.Shift))
            modifiers |=
                WorkbenchShortcutModifiers.Shift;
        if (e.KeyModifiers.HasFlag(
                KeyModifiers.Meta))
            modifiers |=
                WorkbenchShortcutModifiers.Meta;
        if (modifiers ==
                WorkbenchShortcutModifiers.None ||
            !_viewModel.ShortcutEditor.TryMatch(
                modifiers,
                e.Key.ToString(),
                out WorkbenchShortcutBinding? binding) ||
            binding is null)
            return;

        e.Handled = true;
        await _viewModel.ExecuteShortcutAsync(
            binding);
    }

    private string L(string key) =>
        _localization.Get(key);

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ApplySectionSelection();
            ApplyDrawerState();
            WorkbenchHeader.Subtitle =
                _compactHeight
                    ? string.Empty
                    : L("Workbench.Subtitle");
        });
}
