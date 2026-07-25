using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Presenters;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Data;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class SettingsView : UserControl
{
    internal const double CategoryRailWidth = 230;
    internal const double FourColumnPageWidth = 920;
    internal const double TwoColumnPageWidth = 600;
    internal const double SettingsContentHorizontalInset = 32;
    internal const double MinimumPageWidthWithRail = 720;
    internal const double CategoryRailActivationWidth =
        CategoryRailWidth +
        MinimumPageWidthWithRail;
    internal const double FieldMappingSingleColumnWidth = 760;
    private readonly Dictionary<Grid, ResponsiveGridSnapshot> _responsiveGrids = [];
    private readonly HashSet<ComboBox> _localizedChoiceBoxes = [];
    private readonly ILocalizationService _localization;
    private readonly SettingsCategoryOption[] _categories;
    private int _responsiveColumnCount = -1;
    private double _responsivePageWidth;
    private bool _localizationSubscribed;

    private static readonly SettingsCategoryDefinition[] CategoryDefinitions =
    [
        new("Settings.Category.Configuration", "Settings.CategoryGroup.General"),
        new("Settings.Category.LibraryRoots", "Settings.CategoryGroup.Library"),
        new("Settings.Category.Playlists"),
        new("Settings.Category.Tools", "Settings.CategoryGroup.ToolsQuality"),
        new("Settings.Category.Health"),
        new("Settings.Category.RootNamingPolicy", "Settings.CategoryGroup.Policies"),
        new("Settings.Category.IngestPolicy"),
        new("Settings.Category.EffectivePolicy"),
        new("Settings.Category.FieldMappings"),
        new("Settings.Category.Appearance", "Settings.CategoryGroup.Application"),
    ];

    public SettingsView()
    {
        _localization = App.GetService<ILocalizationService>();
        DataContext = App.GetService<SettingsViewModel>();
        InitializeComponent();
        _categories = CategoryDefinitions
            .Select((definition, index) =>
                new SettingsCategoryOption(index, definition))
            .ToArray();
        RefreshCategoryLabels();
        SettingsCategoryList.ItemsSource = _categories;
        SettingsCategoryPicker.ItemsSource = _categories;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        SettingsTabs.SelectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(ApplyResponsiveLayout);
        LayoutUpdated += (_, _) =>
        {
            ApplyResponsiveGrids();
            ApplyLocalizedChoiceSelections();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (!_localizationSubscribed)
            {
                _localization.CultureChanged += OnLocalizationCultureChanged;
                _localizationSubscribed = true;
            }
            RefreshCategoryLabels();
            Dispatcher.UIThread.Post(ApplyResponsiveLayout);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!_localizationSubscribed)
                return;
            _localization.CultureChanged -= OnLocalizationCultureChanged;
            _localizationSubscribed = false;
        };
    }

    private void ApplyResponsiveLayout()
    {
        double navigationWidth =
            SettingsNavigationLayout.Bounds.Width > 0
                ? SettingsNavigationLayout.Bounds.Width
                : Bounds.Width;
        if (navigationWidth <= 0)
            return;

        bool showCategoryRail =
            navigationWidth >=
            CategoryRailActivationWidth;
        bool railVisibilityChanged =
            SettingsCategoryRail.IsVisible !=
            showCategoryRail;
        SettingsCategoryRail.IsVisible = showCategoryRail;
        SettingsCategoryPicker.IsVisible = !showCategoryRail;
        SettingsCategoryPicker.Margin = showCategoryRail
            ? new Thickness(12, 12, 12, 0)
            : new Thickness(12, 8, 12, 0);
        SettingsNavigationLayout.ColumnDefinitions[0].Width =
            showCategoryRail
                 ? new GridLength(
                     CategoryRailWidth)
                 : new GridLength(0);
        HideLegacyTabHeaders();

        double estimatedPageWidth = Math.Max(
            280,
            navigationWidth -
            (showCategoryRail
                ? CategoryRailWidth
                : 0) -
            SettingsContentHorizontalInset);
        double pageWidth =
            ResolveActiveContentWidth() ??
            estimatedPageWidth;
        _responsivePageWidth = pageWidth;
        int columns = pageWidth >= FourColumnPageWidth
            ? 4
            : pageWidth >= TwoColumnPageWidth
                ? 2
                : 1;
        _responsiveColumnCount = columns;

        DisplayLanguagePicker.MaxWidth = 320;
        DisplayLanguagePicker.Width = double.NaN;
        DisplayLanguagePicker.HorizontalAlignment =
            pageWidth < TwoColumnPageWidth
                ? global::Avalonia.Layout.HorizontalAlignment.Stretch
                : global::Avalonia.Layout.HorizontalAlignment.Left;
        UiDensityPicker.MaxWidth = 320;
        UiDensityPicker.Width = double.NaN;
        UiDensityPicker.HorizontalAlignment =
            pageWidth < TwoColumnPageWidth
                ? global::Avalonia.Layout.HorizontalAlignment.Stretch
                : global::Avalonia.Layout.HorizontalAlignment.Left;
        HealthSettingsContent.Margin =
            pageWidth < FourColumnPageWidth
            ? new Thickness(12)
            : new Thickness(16);
        HealthSettingsContent.Spacing =
            pageWidth < FourColumnPageWidth
                ? 6
                : 10;

        foreach (UniformGrid grid in this.GetVisualDescendants()
                     .OfType<UniformGrid>()
                     .Where(grid => grid.Classes.Contains("responsive-theme-grid")))
        {
            grid.Columns = columns;
        }

        ApplyContentWidths();
        ApplyResponsiveGrids();

        if (railVisibilityChanged)
            Dispatcher.UIThread.Post(ApplyResponsiveLayout);
    }

    private void ApplyResponsiveGrids()
    {
        HideLegacyTabHeaders();
        if (_responsiveColumnCount < 0)
            return;

        foreach (Grid grid in this.GetVisualDescendants()
                     .OfType<Grid>()
                     .Where(IsResponsiveForm))
        {
            if (!_responsiveGrids.TryGetValue(grid, out ResponsiveGridSnapshot? snapshot))
            {
                snapshot = ResponsiveGridSnapshot.Capture(grid);
                _responsiveGrids.Add(grid, snapshot);
            }

            int desiredColumns =
                grid.Classes.Contains("field-mapping-fields") &&
                _responsivePageWidth <
                FieldMappingSingleColumnWidth
                    ? 1
                    : _responsiveColumnCount;
            if (snapshot.AppliedColumnCount == desiredColumns)
                continue;

            if (desiredColumns == 4 &&
                snapshot.OriginalColumnCount <= 4)
                snapshot.Restore(grid);
            else
                snapshot.ApplyFlow(grid, desiredColumns);
            snapshot.AppliedColumnCount = desiredColumns;
        }

        ApplyContentWidths();
    }

    private void ApplyLocalizedChoiceSelections()
    {
        foreach (ComboBox comboBox in this.GetVisualDescendants()
                     .OfType<ComboBox>())
        {
            string? group = LocalizedChoiceSelection.GetGroup(comboBox);
            object? value = LocalizedChoiceSelection.GetValue(comboBox);
            if (group is null || value is null)
                continue;

            if (_localizedChoiceBoxes.Add(comboBox))
                comboBox.SelectionChanged += OnLocalizedChoiceSelectionChanged;

            ILocalizedChoice? choice = comboBox.ItemsSource?
                .OfType<ILocalizedChoice>()
                .FirstOrDefault(item =>
                    Equals(item.UntypedValue, value));
            if (choice is not null &&
                !ReferenceEquals(comboBox.SelectedItem, choice))
            {
                comboBox.SelectedItem = choice;
            }
        }
    }

    private static void OnLocalizedChoiceSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.SelectedItem is ILocalizedChoice choice)
        {
            comboBox.SetCurrentValue(
                LocalizedChoiceSelection.ValueProperty,
                choice.UntypedValue);
        }
    }

    private static bool IsResponsiveForm(Grid grid) =>
        grid.Classes.Contains("responsive-form");

    private double? ResolveActiveContentWidth()
    {
        ScrollViewer? viewport = SettingsTabs
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(scroll =>
                scroll.IsEffectivelyVisible &&
                scroll.Classes.Contains("settings-scroll"));
        if (viewport is null ||
            viewport.Bounds.Width <= 0)
        {
            return null;
        }

        StackPanel? content = viewport
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Classes.Contains("settings-content"));
        Thickness margin =
            content?.Margin ??
            new Thickness(16);
        return Math.Max(
            280,
            viewport.Bounds.Width -
            margin.Left -
            margin.Right);
    }

    private void HideLegacyTabHeaders()
    {
        foreach (ItemsPresenter presenter in SettingsTabs
                     .GetVisualDescendants()
                     .OfType<ItemsPresenter>()
                     .Where(presenter => presenter
                         .GetVisualDescendants()
                         .OfType<TabItem>()
                         .Any()))
        {
            presenter.IsVisible = false;
        }
    }

    private void ApplyContentWidths()
    {
        foreach (StackPanel content in this.GetVisualDescendants()
                     .OfType<StackPanel>()
                     .Where(panel => panel.Classes.Contains("settings-content")))
        {
            ScrollViewer? viewport = content.GetVisualAncestors()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            if (viewport is null || viewport.Bounds.Width <= 0)
                continue;

            double availableWidth = Math.Max(
                280,
                viewport.Bounds.Width - content.Margin.Left - content.Margin.Right);
            double maximumWidth = Math.Min(1040, availableWidth);
            if (Math.Abs(content.MaxWidth - maximumWidth) > 0.5)
                content.MaxWidth = maximumWidth;
        }
    }

    private void OnLocalizationCultureChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshCategoryLabels);

    private void RefreshCategoryLabels()
    {
        foreach (SettingsCategoryOption category in _categories)
            category.Refresh(_localization);
    }

    private void OnEditLibraryProfileClicked(
        object? sender,
        RoutedEventArgs e) =>
        RootPolicyEditorExpander.IsExpanded = true;

    private void OnEditIngestProfileClicked(
        object? sender,
        RoutedEventArgs e) =>
        IngestProfileEditorExpander.IsExpanded = true;

    private void OnEditExportProfileClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        Border? card = button
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border =>
                border.Classes.Contains(
                    "export-profile-card"));
        Expander? selectedEditor = card?
            .GetVisualDescendants()
            .OfType<Expander>()
            .FirstOrDefault(expander =>
                expander.Name ==
                "ExportProfileEditorExpander");
        if (selectedEditor is null)
            return;

        foreach (Expander editor in this
                     .GetVisualDescendants()
                     .OfType<Expander>()
                     .Where(expander =>
                         expander.Name ==
                         "ExportProfileEditorExpander"))
        {
            editor.IsExpanded =
                ReferenceEquals(
                    editor,
                    selectedEditor);
        }
    }

    private sealed record SettingsCategoryDefinition(
        string LabelKey,
        string? GroupKey = null);

    private sealed class SettingsCategoryOption(
        int index,
        SettingsCategoryDefinition definition) : INotifyPropertyChanged
    {
        private string _label = "";
        private string? _group;

        public int Index { get; } = index;

        public string Label
        {
            get => _label;
            private set => SetField(ref _label, value);
        }

        public string? Group
        {
            get => _group;
            private set => SetField(ref _group, value);
        }

        public bool HasGroupHeader => definition.GroupKey is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh(ILocalizationService localization)
        {
            Label = localization.Get(definition.LabelKey);
            Group = definition.GroupKey is null
                ? null
                : localization.Get(definition.GroupKey);
        }

        private void SetField<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class ResponsiveGridSnapshot
    {
        private readonly GridTrack[] _columns;
        private readonly GridTrack[] _rows;
        private readonly ChildPlacement[] _children;
        private readonly double _columnSpacing;
        private readonly double _rowSpacing;

        private ResponsiveGridSnapshot(
            GridTrack[] columns,
            GridTrack[] rows,
            ChildPlacement[] children,
            double columnSpacing,
            double rowSpacing)
        {
            _columns = columns;
            _rows = rows;
            _children = children;
            _columnSpacing = columnSpacing;
            _rowSpacing = rowSpacing;
        }

        public int AppliedColumnCount { get; set; } = -1;
        public int OriginalColumnCount => _columns.Length;

        public static ResponsiveGridSnapshot Capture(Grid grid) =>
            new(
                grid.ColumnDefinitions
                    .Select(column => new GridTrack(
                        column.Width,
                        column.MinWidth,
                        column.MaxWidth))
                    .ToArray(),
                grid.RowDefinitions
                    .Select(row => new GridTrack(
                        row.Height,
                        row.MinHeight,
                        row.MaxHeight))
                    .ToArray(),
                grid.Children
                    .Select(child => new ChildPlacement(
                        child,
                        Grid.GetRow(child),
                        Grid.GetColumn(child),
                        Grid.GetRowSpan(child),
                        Grid.GetColumnSpan(child)))
                    .ToArray(),
                grid.ColumnSpacing,
                grid.RowSpacing);

        public void ApplyFlow(Grid grid, int columnCount)
        {
            grid.ColumnDefinitions.Clear();
            for (int index = 0; index < columnCount; index++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            grid.RowDefinitions.Clear();
            int rowCount = Math.Max(
                1,
                (int)Math.Ceiling(_children.Length / (double)columnCount));
            for (int index = 0; index < rowCount; index++)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            grid.ColumnSpacing = Math.Max(8, _columnSpacing);
            grid.RowSpacing = Math.Max(8, _rowSpacing);
            for (int index = 0; index < _children.Length; index++)
            {
                Control child = _children[index].Child;
                Grid.SetRow(child, index / columnCount);
                Grid.SetColumn(child, index % columnCount);
                Grid.SetRowSpan(child, 1);
                Grid.SetColumnSpan(child, 1);
            }
        }

        public void Restore(Grid grid)
        {
            grid.ColumnDefinitions.Clear();
            foreach (GridTrack track in _columns)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(track.Length)
                {
                    MinWidth = track.Minimum,
                    MaxWidth = track.Maximum,
                });
            }

            grid.RowDefinitions.Clear();
            foreach (GridTrack track in _rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(track.Length)
                {
                    MinHeight = track.Minimum,
                    MaxHeight = track.Maximum,
                });
            }

            grid.ColumnSpacing = _columnSpacing;
            grid.RowSpacing = _rowSpacing;
            foreach (ChildPlacement placement in _children)
            {
                Grid.SetRow(placement.Child, placement.Row);
                Grid.SetColumn(placement.Child, placement.Column);
                Grid.SetRowSpan(placement.Child, placement.RowSpan);
                Grid.SetColumnSpan(placement.Child, placement.ColumnSpan);
            }
        }

        private sealed record GridTrack(
            GridLength Length,
            double Minimum,
            double Maximum);

        private sealed record ChildPlacement(
            Control Child,
            int Row,
            int Column,
            int RowSpan,
            int ColumnSpan);
    }
}

public sealed class LocalizedChoiceSelection : AvaloniaObject
{
    public static readonly AttachedProperty<string?> GroupProperty =
        AvaloniaProperty.RegisterAttached<
            LocalizedChoiceSelection,
            ComboBox,
            string?>("Group");

    public static readonly AttachedProperty<object?> ValueProperty =
        AvaloniaProperty.RegisterAttached<
            LocalizedChoiceSelection,
            ComboBox,
            object?>(
            "Value",
            defaultBindingMode: BindingMode.TwoWay);

    public static string? GetGroup(ComboBox comboBox) =>
        comboBox.GetValue(GroupProperty);

    public static void SetGroup(ComboBox comboBox, string? value) =>
        comboBox.SetValue(GroupProperty, value);

    public static object? GetValue(ComboBox comboBox) =>
        comboBox.GetValue(ValueProperty);

    public static void SetValue(ComboBox comboBox, object? value) =>
        comboBox.SetValue(ValueProperty, value);
}
