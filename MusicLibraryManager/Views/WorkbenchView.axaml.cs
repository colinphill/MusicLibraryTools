using global::Avalonia;
using global::Avalonia.Automation;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Presenters;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using System.Collections.Specialized;
using System.ComponentModel;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class WorkbenchView : UserControl
{
    internal const double
        SectionRailWidth = 212;
    internal const double
        SectionDividerAllocation = 10;
    internal const double
        SectionNavigationAllocation =
            SectionRailWidth +
            SectionDividerAllocation;
    internal const double
        PreferredDrawerWidth = 340;
    internal const double
        SplitDividerAllocation = 10;
    internal const double
        MinimumSectionTaskWidth = 720;
    internal const double
        MinimumDockedTaskWidth = 760;
    internal const double
        CompactSectionFramePadding = 8;
    internal const double
        StandardSectionFramePadding = 12;
    internal const double
        SectionFrameBorderThickness = 1;

    private enum WorkbenchDrawerSurface
    {
        None,
        Inspector,
        PendingChanges,
        Columns,
        Transcode,
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

    internal static double
        SectionRailActivationWidth(
            bool compactHeight) =>
        ResponsiveConstraintGutter(
            compactHeight) * 2 +
        SectionNavigationAllocation +
        SectionFrameHorizontalChrome(
            compactHeight) +
        MinimumSectionTaskWidth;

    internal static double
        DockedDrawerActivationWidth(
            bool compactHeight) =>
        ResponsiveConstraintGutter(
            compactHeight) * 2 +
        SectionNavigationAllocation +
        SectionFrameHorizontalChrome(
            compactHeight) +
        SplitDividerAllocation +
        PreferredDrawerWidth +
        MinimumDockedTaskWidth;

    private static double
        ResponsiveConstraintGutter(
            bool compactHeight) =>
        compactHeight
            ? AdaptivePage
                .CompactHeightGutter
            : AdaptivePage.WideGutter;

    private static double
        SectionFrameHorizontalChrome(
            bool compactHeight) =>
        (compactHeight
            ? CompactSectionFramePadding
            : StandardSectionFramePadding) *
        2 +
        SectionFrameBorderThickness * 2;

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
        WorkbenchSessionSection.TranscodeRequested +=
            OnTranscodeRequested;
        WorkbenchColumnsDrawer.Attach(
            WorkbenchSessionSection);
        WorkbenchColumnsDrawer.CloseRequested +=
            OnDrawerCloseRequested;
        WorkbenchPendingChangesDrawer.CloseRequested +=
            OnDrawerCloseRequested;
        WorkbenchInspectorDrawer.CloseRequested +=
            OnInspectorCloseRequested;
        WorkbenchInspectorDrawer.ReviewChangesRequested +=
            OnInspectorReviewChangesRequested;
        WorkbenchTranscodeDrawer.CloseRequested +=
            OnDrawerCloseRequested;
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged +=
                OnViewModelPropertyChanged;
            _viewModel.PendingChanges.CollectionChanged +=
                OnPendingChangesChanged;
            _viewModel.ReviewChangesRequested +=
                OnReviewChangesRequested;
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
            if (_viewModel.TranscodeEditor is not null)
                _viewModel.TranscodeEditor.PreviewCompleted +=
                    OnTranscodePreviewCompleted;
            if (_viewModel.FileOperations is not null)
                _viewModel.FileOperations.PreviewAddedToReview +=
                    OnFileOperationPreviewAddedToReview;
            ApplySectionSelection();
            ApplyActionEmphasis();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _viewModel.PropertyChanged -=
                OnViewModelPropertyChanged;
            _viewModel.PendingChanges.CollectionChanged -=
                OnPendingChangesChanged;
            _viewModel.ReviewChangesRequested -=
                OnReviewChangesRequested;
            _localization.CultureChanged -=
                OnLocalizationCultureChanged;
            if (_viewModel.TranscodeEditor is not null)
                _viewModel.TranscodeEditor.PreviewCompleted -=
                    OnTranscodePreviewCompleted;
            if (_viewModel.FileOperations is not null)
                _viewModel.FileOperations.PreviewAddedToReview -=
                    OnFileOperationPreviewAddedToReview;
        };
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout(
                Bounds.Width <= 1100);

        ApplySectionSelection();
        ApplyResponsiveLayout(compact: false);
        ApplyActionEmphasis();
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

        if (e.PropertyName ==
            nameof(WorkbenchViewModel.HasFiles))
        {
            ApplyActionEmphasis();
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

        _sectionSuppressesInspector =
            section is
                WorkbenchSection.Reports or
                WorkbenchSection.Playlists or
                WorkbenchSection.Tools or
                WorkbenchSection.Shortcuts;
        WorkbenchInspectorToggle.IsVisible =
            !_sectionSuppressesInspector;
        WorkbenchInspectorToggle.IsEnabled =
            !_sectionSuppressesInspector;
        WorkbenchMoreInspectorMenuItem.IsVisible =
            !_sectionSuppressesInspector;
        WorkbenchMoreInspectorMenuItem.IsEnabled =
            !_sectionSuppressesInspector;
        WorkbenchSourceBar.IsVisible =
            section != WorkbenchSection.Shortcuts;

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

    private void OnPendingChangesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        ApplyActionEmphasis();

    private void ApplyActionEmphasis()
    {
        WorkbenchPendingChangesButton.Classes.Set(
            "primary",
            _viewModel.PendingChanges.Count > 0);
        AddWorkbenchSourceButton.Classes.Set(
            "primary",
            !_viewModel.HasFiles);
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
            sender is MenuItem
                ? WorkbenchMoreButton
                : WorkbenchInspectorToggle);
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

    private async void OnTranscodeRequested(
        object? sender,
        EventArgs e) =>
        await OpenTranscodeDrawerAsync();

    internal async Task<bool>
        OpenTranscodeDrawerAsync()
    {
        if (!await _viewModel.OpenTranscodeAsync())
            return false;
        ToggleTransientDrawer(
            WorkbenchDrawerSurface.Transcode,
            WorkbenchSessionSection.SelectionActionsButton);
        return true;
    }

    private void OnTranscodePreviewCompleted(
        object? sender,
        EventArgs e) =>
        ShowDrawer(
            WorkbenchDrawerSurface.PendingChanges,
            WorkbenchPendingChangesButton);

    private void OnFileOperationPreviewAddedToReview(
        object? sender,
        EventArgs e) =>
        ShowDrawer(
            WorkbenchDrawerSurface.PendingChanges,
            WorkbenchPendingChangesButton);

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
                WorkbenchDrawerSurface.Transcode =>
                    WorkbenchTranscodeDrawer.InitialFocus,
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

    private void OnInspectorReviewChangesRequested(
        object? sender,
        EventArgs e) =>
        ShowDrawer(
            WorkbenchDrawerSurface.PendingChanges,
            WorkbenchPendingChangesButton);

    private void OnReviewChangesRequested(
        object? sender,
        EventArgs e) =>
        ShowDrawer(
            WorkbenchDrawerSurface.PendingChanges,
            WorkbenchPendingChangesButton);

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
            WorkbenchDrawerSurface.Columns or
            WorkbenchDrawerSurface.Transcode;

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
        WorkbenchTranscodeDrawer.IsVisible =
            _activeDrawer ==
                WorkbenchDrawerSurface.Transcode;

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
            // Keep the presenter attached while closed so drawer
            // names, focus targets, and live localized resources
            // remain available without constructing a second host.
            presenter.IsVisible = true;
            presenter.IsHitTestVisible =
                drawerVisible;
            presenter.Opacity =
                drawerVisible
                    ? 1
                    : 0;
            WorkbenchDrawerPane.Width =
                _responsiveCompact
                    ? drawerWidth
                    : double.NaN;
            WorkbenchDrawerPane.IsVisible =
                drawerVisible;
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
        // Before the control is measured, honor the shell's requested
        // presentation instead of briefly selecting an unrelated intermediate
        // mode. The measured bounds become authoritative as soon as they exist.
        double width = Bounds.Width > 0
            ? Bounds.Width
            : compact
                ? 900
                : 1380;
        double height = Bounds.Height > 0
            ? Bounds.Height
            : AdaptivePage.CompactHeightThreshold + 1;
        bool wasResponsiveCompact =
            _responsiveCompact;
        _compactHeight =
            height <= 700;

        // Keep the narrow gutter through the conservative rail activation
        // point. Switching 16 -> 24 at 1000 px would otherwise make the
        // central task 15 px narrower when its viewport grows by one pixel.
        double gutter = _compactHeight
            ? AdaptivePage.CompactHeightGutter
            : width <
              SectionRailActivationWidth(
                  compactHeight: false)
                ? AdaptivePage.NarrowGutter
                : AdaptivePage.WideGutter;
        double contentWidth =
            Math.Max(0, width - gutter * 2);
        double sectionFramePadding =
            _compactHeight
                ? CompactSectionFramePadding
                : StandardSectionFramePadding;
        double sectionFrameChrome =
            SectionFrameHorizontalChrome(
                _compactHeight);

        _compactSectionPicker =
            width <
            SectionRailActivationWidth(
                _compactHeight);
        double visibleRailWidth =
            _compactSectionPicker
                ? 0
                : SectionNavigationAllocation;
        _responsiveCompact =
            width <
            DockedDrawerActivationWidth(
                _compactHeight);
        WorkbenchSplit
            .SetResponsiveMinimumLeftWidth(
                visibleRailWidth +
                sectionFrameChrome +
                MinimumDockedTaskWidth);

        WorkbenchSectionPicker.IsVisible =
            _compactSectionPicker;
        WorkbenchSectionRail.IsVisible =
            !_compactSectionPicker;
        WorkbenchSectionDivider.IsVisible =
            !_compactSectionPicker;
        WorkbenchBody.ColumnDefinitions[0].Width =
            _compactSectionPicker
                ? new GridLength(0)
                : new GridLength(
                    SectionRailWidth);
        WorkbenchBody.ColumnDefinitions[1].Width =
            _compactSectionPicker
                ? new GridLength(0)
                : new GridLength(
                    SectionDividerAllocation);
        WorkbenchRoot.Margin =
            new Thickness(gutter);
        WorkbenchRoot.RowSpacing =
            _compactHeight
                ? 8
                : 12;
        WorkbenchSectionPicker.Width =
            contentWidth < 700
                ? 190
                : 240;
        WorkbenchHeader.Subtitle =
            _compactHeight
                ? string.Empty
                : L("Workbench.Subtitle");
        WorkbenchSectionContentCard.Padding =
            new Thickness(
                sectionFramePadding);

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
        Control? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement() as Control;

        if (e.Key == Key.Tab &&
            _activeDrawer != WorkbenchDrawerSurface.None &&
            (WorkbenchInspectorScrim.IsVisible ||
             WorkbenchHeaderScrim.IsVisible) &&
            TryCycleDrawerFocus(
                e.KeyModifiers.HasFlag(
                    KeyModifiers.Shift)))
        {
            e.Handled = true;
            return;
        }

        bool requestsContextMenu =
            e.Key == Key.Apps ||
            e.Key == Key.F10 &&
            e.KeyModifiers.HasFlag(
                KeyModifiers.Shift);
        AppDataGrid sessionGrid =
            WorkbenchSessionSection.SessionGrid;
        bool focusIsInSessionGrid =
            focused is not null &&
            (ReferenceEquals(
                 focused,
                 sessionGrid) ||
             focused.GetVisualAncestors()
                 .Contains(sessionGrid));
        if (requestsContextMenu &&
            _viewModel.SelectedSection ==
                WorkbenchSection.Session &&
            focusIsInSessionGrid &&
            sessionGrid.ContextMenu is
                { } sessionMenu)
        {
            sessionMenu.Open(
                sessionGrid);
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

    private bool TryCycleDrawerFocus(
        bool reverse)
    {
        Control[] focusable =
        [
            .. WorkbenchDrawerPane
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    control.IsEffectivelyEnabled &&
                    control.Focusable),
        ];
        if (focusable.Length == 0)
            return false;

        object? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement();
        int index = Array.IndexOf(
            focusable,
            focused);
        if (index < 0)
        {
            (reverse
                ? focusable[^1]
                : focusable[0]).Focus();
            return true;
        }

        bool atBoundary =
            reverse
                ? index == 0
                : index == focusable.Length - 1;
        if (!atBoundary)
            return false;

        (reverse
            ? focusable[^1]
            : focusable[0]).Focus();
        return true;
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
