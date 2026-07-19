using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Interactivity;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class LibraryView : UserControl
{
    private readonly LibraryViewModel _viewModel;
    private readonly GridStateService _gridState;
    private readonly IPlatformService _platform;
    private readonly List<AppGridColumnDefinition> _columns = [];
    private IReadOnlyList<LibraryRow> _selected = [];
    private LibrarySortState? _sort;

    public LibraryView()
    {
        InitializeComponent();
        _viewModel = App.GetService<LibraryViewModel>();
        _gridState = App.GetService<GridStateService>();
        _platform = App.GetService<IPlatformService>();
        DataContext = _viewModel;
        BuildColumns();
        ApplySnapshot(_gridState.Load());
        ConfigureGrid();
        BuildColumnOptions();
        LibraryGrid.LayoutChanged += (_, _) => PersistLayout();
        LibraryGrid.SortChanged += (_, _) => Dispatcher.UIThread.Post(CaptureSortAndPersist);
        if (_viewModel.Rows.Count == 0)
            _ = _viewModel.ReloadAsync();
    }

    private void BuildColumns()
    {
        var artworkTemplate = new FuncDataTemplate<LibraryRow>((_, _) =>
        {
            var image = new Image { Width = 28, Height = 28, Stretch = Stretch.UniformToFill };
            image.Bind(Image.SourceProperty, new Binding(nameof(LibraryRow.ThumbnailSource)));
            return image;
        });
        _columns.AddRange([
            new("Artwork", "Artwork", null, 100, 100, CellTemplate: artworkTemplate, Sortable: false),
            new("Title", "Title", "Title", 280, 140),
            new("Artist", "Artist", "Artist", 190, 100),
            new("AlbumArtist", "Album artist", "AlbumArtist", 190, 100, false),
            new("Album", "Album", "Album", 230, 120),
            new("Track", "Track", "Track", 70, 58),
            new("TrackTotal", "Track total", "TrackTotal", 90, 72, false),
            new("Disc", "Disc", "Disc", 65, 58, false),
            new("DiscTotal", "Disc total", "DiscTotal", 85, 70, false),
            new("Codec", "Codec", "Codec", 105, 80),
            new("Duration", "Duration", "Duration", 90, 75),
            new("Modified", "Modified", "Modified", 150, 110, false),
            new("Path", "Path", "Path", 420, 180),
        ]);
    }

    private void ApplySnapshot(GridSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        foreach (LibraryColumnState saved in snapshot.Columns.OrderBy(item => item.DisplayIndex))
        {
            int index = _columns.FindIndex(item => item.Key.Equals(saved.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;
            AppGridColumnDefinition current = _columns[index];
            _columns.RemoveAt(index);
            _columns.Insert(Math.Min(saved.DisplayIndex, _columns.Count), current with
            {
                Width = saved.Width is > 0 ? saved.Width.Value : current.Width,
                Visible = saved.Visible,
            });
        }
        _sort = snapshot.Sort;
    }

    private void ConfigureGrid()
    {
        LibraryGrid.ConfigureColumns(_columns);
        LibraryGrid.FrozenColumnCount = _columns.TakeWhile(item => !item.Visible).Any() ? 0 : 1;
    }

    private void BuildColumnOptions()
    {
        ColumnOptions.Children.Clear();
        foreach (AppGridColumnDefinition definition in _columns)
        {
            var check = new CheckBox { Content = definition.Header, IsChecked = definition.Visible, Tag = definition.Key };
            check.IsCheckedChanged += OnColumnChecked;
            ColumnOptions.Children.Add(check);
        }
    }

    private void OnColumnChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key, IsChecked: bool visible })
            return;
        int index = _columns.FindIndex(item => item.Key == key);
        if (index < 0)
            return;
        _columns[index] = _columns[index] with { Visible = visible };
        ConfigureGrid();
        PersistLayout();
    }

    private IReadOnlyList<LibraryColumnState> CaptureColumns()
    {
        var visible = LibraryGrid.CaptureColumnLayout();
        var visibleByKey = visible.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        int displayIndex = 0;
        var result = new List<LibraryColumnState>(_columns.Count);
        foreach (AppGridColumnDefinition definition in _columns.OrderBy(item =>
                     visibleByKey.TryGetValue(item.Key, out LibraryColumnState? state) ? state.DisplayIndex : int.MaxValue))
        {
            if (visibleByKey.TryGetValue(definition.Key, out LibraryColumnState? state))
                result.Add(state with { DisplayIndex = displayIndex++, Visible = true });
            else
                result.Add(new LibraryColumnState(definition.Key, definition.Width, displayIndex++, false));
        }
        return result;
    }

    private void PersistLayout() => _gridState.Save(new GridSnapshot(CaptureColumns(), _sort));

    private void CaptureSortAndPersist()
    {
        _sort = LibraryGrid.CurrentSortKey is not { } key
            ? null
            : new LibrarySortState(key, LibraryGrid.CurrentSortDescending);
        PersistLayout();
    }

    private async void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = LibraryGrid.SelectedItems.OfType<LibraryRow>().ToArray();
        await _viewModel.SelectAsync(_selected);
        bool hasSelection = _selected.Count > 0;
        SelectedCountLabel.IsVisible = CopyButton.IsVisible = RevealButton.IsVisible = ReindexButton.IsVisible = hasSelection;
        SelectedCountLabel.Text = $"{_selected.Count:N0} selected";
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is LibraryRow row)
            _ = _viewModel.LoadThumbnailAsync(row);
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is LibraryRow row)
            _viewModel.ReleaseThumbnail(row);
    }

    private void OnColumnsClick(object? sender, RoutedEventArgs e) => ColumnPopover.IsOpen = !ColumnPopover.IsOpen;

    private void OnColumnsClose(object? sender, RoutedEventArgs e) => ColumnPopover.IsOpen = false;

    private void OnLibraryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ColumnPopover.IsOpen)
            return;
        ColumnPopover.IsOpen = false;
        e.Handled = true;
    }
    private void OnInspectorToggle(object? sender, RoutedEventArgs e) => WorkspaceSplit.ToggleCompactRight();

    public void ApplyResponsiveLayout(bool compact)
    {
        WorkspaceSplit.SetCompact(compact);
        InspectorToggle.IsVisible = compact;
        ViewNameBox.IsVisible = !compact;
    }

    private void OnSavedViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedView is { } view)
        {
            ApplySnapshot(new GridSnapshot(view.Columns, view.Sort));
            ConfigureGrid();
            BuildColumnOptions();
        }
    }

    private void OnSaveView(object? sender, RoutedEventArgs e)
    {
        string name = _viewModel.NewViewName?.Trim() ?? "";
        if (name.Length > 0)
            _viewModel.SaveNamedView(name, CaptureColumns(), _sort);
    }

    private async void OnCopyPaths(object? sender, RoutedEventArgs e) =>
        await _platform.CopyTextAsync(string.Join(Environment.NewLine, _selected.Select(item => item.Path)));

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (_selected.FirstOrDefault() is { } row)
            _platform.RevealFile(row.Path);
    }

    private async void OnReindex(object? sender, RoutedEventArgs e) =>
        await _viewModel.ReindexAsync(_selected.Select(item => item.Path).ToArray());
}
