using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Globalization;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Pages;

public partial class LibraryPage : UserControl
{
    private const string InspectorWidthPreference = "manager.library.inspectorWidth.v1";
    private readonly IPlatformService _platform = App.GetService<IPlatformService>();
    private readonly IAppSettings _settings = App.GetService<IAppSettings>();
    private bool _overlayInspector;
    private double _inspectorWidth = 390;

    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<LibraryViewModel>();
        DataContext = ViewModel;
        _inspectorWidth = LoadInspectorWidth();
        InspectorColumn.Width = new GridLength(_inspectorWidth);
        Loaded += LibraryPage_Loaded;
        SizeChanged += LibraryPage_SizeChanged;
        ApplyColumnVisibility();
    }

    public LibraryViewModel ViewModel { get; }

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyInspectorLayout(ActualWidth);
        if (ViewModel.Rows.Count == 0)
            await ViewModel.ReloadAsync();
        FilterBox.Focus();
    }

    private async void LibraryTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        IReadOnlyList<LibraryRow> rows = SelectedRows();
        await ViewModel.SelectAsync(rows.Select(row => row.Path).ToArray());
        if (_overlayInspector && rows.Count > 0)
            InspectorPanel.Visibility = Visibility.Visible;
    }

    private async void LibraryTable_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is LibraryRow row)
            await ViewModel.LoadThumbnailAsync(row);
    }

    private void LibraryTable_UnloadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is LibraryRow row)
            ViewModel.ReleaseThumbnail(row);
    }

    private void InspectorButton_Click(object sender, RoutedEventArgs e)
        => InspectorPanel.Visibility = InspectorPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void ColumnVisibility_Changed(object sender, RoutedEventArgs e) => ApplyColumnVisibility();

    private void ApplyColumnVisibility()
    {
        if (LibraryTable is null)
            return;
        var choices = ViewModel.Columns.ToDictionary(column => column.Header, column => column.IsVisible);
        foreach (DataGridColumn column in LibraryTable.Columns)
            if (column.Header is string header && choices.TryGetValue(header, out bool visible))
                column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CopyPaths_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join(Environment.NewLine, SelectedRows().Select(row => row.Path));
        if (text.Length > 0)
            await _platform.CopyTextAsync(text);
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows().FirstOrDefault() is { } row)
            _platform.RevealFile(row.Path);
    }

    private async void ReindexSelection_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ReindexAsync(SelectedRows().Select(row => row.Path).ToArray());

    private IReadOnlyList<LibraryRow> SelectedRows()
        => LibraryTable.SelectedItems.OfType<LibraryRow>().ToArray();

    private void LibraryPage_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyInspectorLayout(e.NewSize.Width);

    private void ApplyInspectorLayout(double width)
    {
        bool overlay = width < 1100;
        if (_overlayInspector == overlay)
            return;
        _overlayInspector = overlay;
        InspectorButton.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        if (overlay)
        {
            if (InspectorColumn.ActualWidth >= 280)
                _inspectorWidth = InspectorColumn.ActualWidth;
            InspectorColumn.MinWidth = 0;
            InspectorColumn.MaxWidth = double.PositiveInfinity;
            InspectorColumn.Width = new GridLength(0);
            InspectorSplitterColumn.Width = new GridLength(0);
            InspectorSplitter.Visibility = Visibility.Collapsed;
            Grid.SetColumn(InspectorPanel, 0);
            InspectorPanel.Width = Math.Min(_inspectorWidth, 390);
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Grid.SetColumn(InspectorPanel, 2);
            InspectorColumn.MinWidth = 280;
            InspectorColumn.MaxWidth = 620;
            InspectorColumn.Width = new GridLength(Math.Clamp(_inspectorWidth, 280, 620));
            InspectorSplitterColumn.Width = new GridLength(10);
            InspectorSplitter.Visibility = Visibility.Visible;
            InspectorPanel.Width = double.NaN;
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorPanel.Visibility = Visibility.Visible;
        }
    }

    private void InspectorSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_overlayInspector)
            return;
        // GridSplitter updates the column's GridLength before the next layout pass. ActualWidth is
        // still the old value during DragCompleted, so reading it here would undo the user's drag.
        _inspectorWidth = Math.Clamp(
            InspectorColumn.Width.IsAbsolute
                ? InspectorColumn.Width.Value
                : InspectorColumn.ActualWidth,
            280, 620);
        _settings.SetPreference(InspectorWidthPreference,
            _inspectorWidth.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private double LoadInspectorWidth()
        => double.TryParse(_settings.GetPreference(InspectorWidthPreference),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            ? Math.Clamp(width, 280, 620)
            : 390;
}
