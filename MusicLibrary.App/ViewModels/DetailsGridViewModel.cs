using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// A tabular details view of every track with user-selectable columns, realtime filtering
/// (substring / glob / regex), and click-to-sort columns. Rows are held in a
/// <see cref="DataGridCollectionView"/> so filtering (via a predicate) and sorting coexist and
/// survive each keystroke. Column values come from the cache via <see cref="DetailsRow"/>.
/// </summary>
public partial class DetailsGridViewModel : ViewModelBase
{
    private const string ColumnLayoutKey = "table.columns";

    private readonly ILibraryService _library;
    private readonly IAppSettings _settings;
    private List<DetailsRow> _allRows = [];
    private PatternMatcher _matcher = PatternMatcher.Create(null, FilterMode.Substring);
    private CancellationTokenSource? _cts;

    // The persisted column layout: visible columns, in display order, with their (absolute) widths.
    // A column absent from this list is hidden. Updated on toggle/reorder/resize and saved immediately.
    private List<ColumnLayout> _layout = [];
    private bool _loadingLayout;

    private sealed record ColumnLayout(string Key, double? Width);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Load to populate the table.";

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private bool _filterValid = true;

    [ObservableProperty]
    private FilterMode _selectedFilterMode = FilterMode.Substring;

    [ObservableProperty]
    private DataGridCollectionView? _view;

    [ObservableProperty]
    private FilterScopeOption? _selectedScope;

    public ObservableCollection<ColumnToggle> Columns { get; } = [];

    /// <summary>Which column the filter applies to; the first entry (Key = null) means all visible.</summary>
    public ObservableCollection<FilterScopeOption> FilterScopes { get; } = [];

    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();

    /// <summary>Raised when the set of visible columns changes so the view can rebuild grid columns.</summary>
    public event Action? VisibleColumnsChanged;

    public IReadOnlyList<(string Key, string Header)> VisibleColumns =>
        Columns.Where(c => c.IsSelected).Select(c => (c.Key, c.Header)).ToList();

    /// <summary>Find the loaded row for a path (regardless of the current filter), or null.</summary>
    public DetailsRow? RowForPath(string path) =>
        _allRows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public DetailsGridViewModel(ILibraryService library, IAppSettings settings)
    {
        _library = library;
        _settings = settings;
        // The grid is the primary browse surface, so it auto-populates whenever a config loads.
        settings.ConfigurationChanged += async (_, _) =>
        {
            LoadCommand.NotifyCanExecuteChanged();
            await ReloadAsync();
        };
        foreach (var col in DetailsColumns.All)
        {
            var toggle = new ColumnToggle(col.Key, col.Header, DetailsColumns.DefaultVisible.Contains(col.Key));
            toggle.Changed += OnColumnsChanged;
            Columns.Add(toggle);
        }
        LoadColumnLayout();
        RebuildScopes();
    }

    /// <summary>The saved (absolute) width for a column, or null to size it automatically.</summary>
    public double? WidthFor(string key) => _layout.FirstOrDefault(l => l.Key == key)?.Width;

    // Restore the persisted column layout: visibility, display order, and widths. Falls back to the
    // default visible set when there's nothing saved (or it can't be parsed).
    private void LoadColumnLayout()
    {
        var saved = ReadSavedLayout();
        if (saved.Count == 0)
        {
            _layout = CurrentVisibleLayout();
            return;
        }

        _loadingLayout = true;
        try
        {
            var savedKeys = saved.Select(l => l.Key).ToList();
            foreach (var toggle in Columns)
                toggle.IsSelected = savedKeys.Contains(toggle.Key);

            // Reorder the toggle list so display order = saved order (visible first), hidden after.
            var order = savedKeys.Concat(Columns.Select(c => c.Key).Where(k => !savedKeys.Contains(k))).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                var idx = IndexOfKey(order[i]);
                if (idx >= 0 && idx != i)
                    Columns.Move(idx, i);
            }
            _layout = saved;
        }
        finally
        {
            _loadingLayout = false;
        }
    }

    private List<ColumnLayout> ReadSavedLayout()
    {
        var json = _settings.GetPreference(ColumnLayoutKey);
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ColumnLayout>>(json) ?? [];
            // Drop any keys that no longer exist in the catalog.
            return parsed.Where(l => Columns.Any(c => c.Key == l.Key)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private int IndexOfKey(string key)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Key == key)
                return i;
        return -1;
    }

    private List<ColumnLayout> CurrentVisibleLayout()
    {
        var widths = _layout.ToDictionary(l => l.Key, l => l.Width);
        return VisibleColumns.Select(c => new ColumnLayout(c.Key, widths.GetValueOrDefault(c.Key))).ToList();
    }

    private void SaveColumnLayout() => _settings.SetPreference(ColumnLayoutKey, JsonSerializer.Serialize(_layout));

    /// <summary>
    /// Record the grid's current column layout (display order + widths for the visible columns) and
    /// persist it. Called from the view when columns are reordered/resized or the window closes.
    /// </summary>
    public void SaveGridLayout(IReadOnlyList<(string Key, double? Width)> orderedVisible)
    {
        _layout = orderedVisible.Select(o => new ColumnLayout(o.Key, o.Width)).ToList();
        SaveColumnLayout();
    }

    // The scope list offers "All visible columns" plus each currently visible column, so the filter
    // can target a single field. Rebuilt whenever the visible set changes.
    private void RebuildScopes()
    {
        var previous = SelectedScope?.Key;
        FilterScopes.Clear();
        FilterScopes.Add(new FilterScopeOption(null, "All visible columns"));
        foreach (var (key, header) in VisibleColumns)
            FilterScopes.Add(new FilterScopeOption(key, header));

        SelectedScope = FilterScopes.FirstOrDefault(s => s.Key == previous) ?? FilterScopes[0];
    }

    private bool CanLoad() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        LoadCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        StatusText = "Loading…";
        try
        {
            var records = await _library.GetAllRecordsAsync(_cts.Token);
            _allRows = records.Select(r => new DetailsRow(r)).ToList();
            RebuildSearchText();

            var view = new DataGridCollectionView(_allRows) { Filter = FilterPredicate };
            View = view;
            ApplyFilter();
            VisibleColumnsChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            LoadCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Reload the table from the cache if the library is ready and not already loading.</summary>
    public async Task ReloadAsync()
    {
        if (_library.IsReady && !IsBusy)
            await LoadAsync();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool FilterPredicate(object o)
    {
        var row = (DetailsRow)o;
        var key = SelectedScope?.Key;
        // Scoped to one column → match that column's text; otherwise the visible-columns search text.
        var text = key is null ? row.SearchText : row[key];
        return _matcher.IsMatch(text);
    }

    partial void OnFilterTextChanged(string? value) => ApplyFilter();
    partial void OnSelectedFilterModeChanged(FilterMode value) => ApplyFilter();
    partial void OnSelectedScopeChanged(FilterScopeOption? value) => ApplyFilter();

    private void OnColumnsChanged()
    {
        if (_loadingLayout)
            return;          // bulk toggle changes while restoring the saved layout

        _layout = CurrentVisibleLayout();   // visibility changed → refresh + persist the layout
        SaveColumnLayout();

        RebuildSearchText();
        RebuildScopes();     // a hidden column shouldn't remain a filter scope
        ApplyFilter();       // re-applies the predicate against the new visible set
        VisibleColumnsChanged?.Invoke();
    }

    private void RebuildSearchText()
    {
        var keys = VisibleColumns.Select(c => c.Key).ToList();
        foreach (var row in _allRows)
            row.RebuildSearchText(keys);
    }

    private void ApplyFilter()
    {
        _matcher = PatternMatcher.Create(FilterText, SelectedFilterMode);
        FilterValid = _matcher.IsValid;
        View?.Refresh();     // re-applies FilterPredicate; keeps any active column sort

        var shown = View?.Count ?? 0;
        StatusText = _allRows.Count == 0
            ? "No rows — Load to populate the table."
            : _matcher.IsEmpty
                ? $"{_allRows.Count:N0} rows"
                : $"{shown:N0} of {_allRows.Count:N0} rows"
                    + (_matcher.IsValid ? "" : "  ·  invalid pattern");
    }
}
