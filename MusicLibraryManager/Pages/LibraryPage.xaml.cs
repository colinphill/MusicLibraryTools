using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLibraryManager.Presentation;
using WinUI.TableView;

namespace MusicLibraryManager.Pages;

public sealed partial class LibraryPage : UserControl
{
    private readonly IPlatformService _platform = App.GetService<IPlatformService>();
    private bool _overlayInspector;

    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<LibraryViewModel>();
        DataContext = ViewModel;
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
        FilterBox.Focus(FocusState.Programmatic);
    }

    private async void LibraryTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        IReadOnlyList<LibraryRow> rows = SelectedRows();
        await ViewModel.SelectAsync(rows);
        if (_overlayInspector && rows.Count > 0)
            InspectorPanel.Visibility = Visibility.Visible;
    }

    private async void LibraryTable_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not LibraryRow row)
            return;
        if (args.InRecycleQueue)
            ViewModel.ReleaseThumbnail(row);
        else
            await ViewModel.LoadThumbnailAsync(row);
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
        foreach (TableViewColumn column in LibraryTable.Columns)
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
            InspectorSplitter.Visibility = Visibility.Collapsed;
            InspectorSplitterColumn.Width = new GridLength(0);
            InspectorColumn.MinWidth = 0;
            InspectorColumn.Width = new GridLength(0);
            Grid.SetColumn(InspectorPanel, 0);
            InspectorPanel.Width = Math.Min(390, Math.Max(300, width - 48));
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            InspectorSplitter.Visibility = Visibility.Visible;
            InspectorSplitterColumn.Width = new GridLength(12);
            Grid.SetColumn(InspectorPanel, 2);
            InspectorColumn.MinWidth = 280;
            InspectorColumn.Width = new GridLength(390);
            InspectorPanel.Width = double.NaN;
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorPanel.Visibility = Visibility.Visible;
        }
    }
}
