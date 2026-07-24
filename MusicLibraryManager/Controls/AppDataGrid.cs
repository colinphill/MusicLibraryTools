using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Styling;
using global::Avalonia.Threading;
using System.Collections;
using System.ComponentModel;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Controls;

public sealed record AppGridColumnDefinition(
    string Key,
    string Header,
    string? BindingPath,
    double Width,
    double MinWidth = 60,
    bool Visible = true,
    IDataTemplate? CellTemplate = null,
    bool Sortable = true,
    bool Editable = false,
    IComparer? CustomSortComparer = null,
    string? HeaderResourceKey = null);

public sealed class AppDataGrid : DataGrid
{
    private readonly Dictionary<DataGridColumn, AppGridColumnDefinition> _definitions = [];
    private bool _configuring;
    private string? _programmaticSortKey;
    private bool _programmaticSortDescending;
    private int _sortApplicationVersion;

    // Avalonia styles custom subclasses by their concrete type. Reuse DataGrid's style key so
    // the official Fluent control theme supplies the native header, rows, scrollbars, and cells.
    protected override Type StyleKeyOverride => typeof(DataGrid);

    public AppDataGrid()
    {
        Classes.Add("primary-grid");
        AutoGenerateColumns = false;
        CanUserReorderColumns = true;
        CanUserResizeColumns = true;
        CanUserSortColumns = true;
        IsReadOnly = true;
        SelectionMode = DataGridSelectionMode.Extended;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        GridLinesVisibility = DataGridGridLinesVisibility.All;
        RowHeight = 38;
        ColumnHeaderHeight = 39;
        ColumnDisplayIndexChanged += (_, _) => NotifyLayoutChanged();
        ColumnReordered += (_, _) => NotifyLayoutChanged();
        Sorting += OnSorting;
        AttachedToVisualTree += (_, _) =>
        {
            AvaloniaLocalizationResourceBridge.ResourcesApplied +=
                OnLocalizationResourcesApplied;
            RefreshLocalizedHeaders();
        };
        DetachedFromVisualTree += (_, _) =>
            AvaloniaLocalizationResourceBridge.ResourcesApplied -=
                OnLocalizationResourcesApplied;
    }

    public event EventHandler? LayoutChanged;
    public event EventHandler? SortChanged;
    public string? CurrentSortKey { get; private set; }
    public bool CurrentSortDescending { get; private set; }

    public void ConfigureColumns(IEnumerable<AppGridColumnDefinition> definitions)
    {
        LibrarySortState? sort = CurrentSortKey is null
            ? null
            : new LibrarySortState(CurrentSortKey, CurrentSortDescending);
        _configuring = true;
        try
        {
            foreach (DataGridColumn column in _definitions.Keys)
                column.PropertyChanged -= OnColumnPropertyChanged;
            _definitions.Clear();
            Columns.Clear();
            foreach (AppGridColumnDefinition definition in definitions.Where(item => item.Visible))
            {
                DataGridColumn column = definition.CellTemplate is not null
                    ? new DataGridTemplateColumn { CellTemplate = definition.CellTemplate }
                    : new DataGridTextColumn
                    {
                        Binding = new Binding(definition.BindingPath ?? definition.Key)
                        {
                            Mode = definition.Editable
                                ? BindingMode.TwoWay
                                : BindingMode.OneWay,
                        },
                    };
                column.Header = ResolveHeader(definition);
                column.IsReadOnly = !definition.Editable;
                column.Width = new DataGridLength(definition.Width);
                column.MinWidth = definition.MinWidth;
                column.CanUserSort = definition.Sortable;
                column.CustomSortComparer =
                    definition.CustomSortComparer;
                if (definition.Sortable)
                    column.SortMemberPath = definition.BindingPath ?? definition.Key;
                Columns.Add(column);
                _definitions[column] = definition;
                column.PropertyChanged += OnColumnPropertyChanged;
            }
        }
        finally
        {
            _configuring = false;
        }
        if (sort is not null)
            ApplySort(sort);
    }

    /// <summary>Applies a persisted sort to both the collection view and column indicator.</summary>
    public bool ApplySort(LibrarySortState? sort)
    {
        if (sort is null)
        {
            CurrentSortKey = null;
            CurrentSortDescending = false;
            return true;
        }

        DataGridColumn? target = Columns.FirstOrDefault(column =>
            string.Equals(KeyFor(column), sort.Key, StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.CanUserSort)
            return false;

        string targetKey = KeyFor(target)!;
        int version = ++_sortApplicationVersion;
        _programmaticSortKey = targetKey;
        _programmaticSortDescending =
            sort.Descending;
        CurrentSortKey = targetKey;
        CurrentSortDescending =
            sort.Descending;
        target.Sort(
            sort.Descending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending);
        SortChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_sortApplicationVersion != version)
                    return;
                _programmaticSortKey = null;
                CurrentSortKey = targetKey;
                CurrentSortDescending =
                    sort.Descending;
            },
            DispatcherPriority.Background);
        return true;
    }

    public IReadOnlyList<LibraryColumnState> CaptureColumnLayout() => Columns
        .Select(column =>
        {
            AppGridColumnDefinition definition = _definitions[column];
            double width = column.ActualWidth > 0 ? column.ActualWidth : definition.Width;
            return new LibraryColumnState(definition.Key, width, column.DisplayIndex, true);
        })
        .OrderBy(state => state.DisplayIndex)
        .ToArray();

    public string? KeyFor(DataGridColumn column) =>
        _definitions.TryGetValue(column, out AppGridColumnDefinition? definition) ? definition.Key : null;

    /// <summary>
    /// Refreshes localized display headers without rebuilding columns or disturbing their
    /// persisted order, widths, visibility, or sort state.
    /// </summary>
    public void RefreshLocalizedHeaders()
    {
        foreach ((DataGridColumn column, AppGridColumnDefinition definition) in
                 _definitions)
            column.Header = ResolveHeader(definition);
    }

    private static object ResolveHeader(
        AppGridColumnDefinition definition)
    {
        if (definition.HeaderResourceKey is not { Length: > 0 } key)
            return definition.Header;
        if (Application.Current is not { } application)
            return $"\u27E6{key}\u27E7";
        return application.TryGetResource(
                   AvaloniaLocalizationResourceBridge.ResourcePrefix + key,
                   ThemeVariant.Default,
                   out object? value) &&
               value is string localized
            ? localized
            : $"\u27E6{key}\u27E7";
    }

    private void OnLocalizationResourcesApplied(
        object? sender,
        EventArgs e) =>
        RefreshLocalizedHeaders();

    private void NotifyLayoutChanged()
    {
        if (!_configuring)
            LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataGridColumn.WidthProperty)
            NotifyLayoutChanged();
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        string? key = KeyFor(e.Column);
        if (key is null)
            return;
        if (string.Equals(
                _programmaticSortKey,
                key,
                StringComparison.OrdinalIgnoreCase))
        {
            CurrentSortKey = key;
            CurrentSortDescending =
                _programmaticSortDescending;
            _programmaticSortKey = null;
            return;
        }
        _sortApplicationVersion++;
        CurrentSortDescending = CurrentSortKey == key && !CurrentSortDescending;
        CurrentSortKey = key;
        SortChanged?.Invoke(this, EventArgs.Empty);
    }
}
