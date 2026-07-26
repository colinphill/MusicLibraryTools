using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform.Storage;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;

namespace MusicLibraryManager.Views;

public partial class IngestView : UserControl
{
    private readonly IngestViewModel _viewModel;

    public IngestView()
    {
        InitializeComponent();
        _viewModel = App.GetService<IngestViewModel>();
        DataContext = _viewModel;
        PreviewGrid.ConfigureColumns([
            new AppGridColumnDefinition("Type", "Type", "SourceType", 140, 100,
                HeaderResourceKey: "Column.Type"),
            new AppGridColumnDefinition("Source", "Source", "Source", 360, 220,
                HeaderResourceKey: "Column.Source"),
            new AppGridColumnDefinition("Plan", "Plan", "Summary", 420, 240,
                HeaderResourceKey: "Column.Plan"),
            new AppGridColumnDefinition("Progress", "Progress", "ProgressText", 160, 110,
                HeaderResourceKey: "Column.Progress"),
        ]);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;
        if (width <= 0)
            return;

        bool compactHeight = Bounds.Height <= 560;
        SetupCard.Padding = new global::Avalonia.Thickness(compactHeight ? 12 : 16);
        SetupPanel.Spacing = compactHeight ? 6 : 10;
        PreflightChecksScroll.MaxHeight =
            compactHeight ? 128 : 180;
        PreviewFilterField.Orientation =
            compactHeight
                ? global::Avalonia.Layout
                    .Orientation.Horizontal
                : global::Avalonia.Layout
                    .Orientation.Vertical;
        PreviewEmptyDescription.IsVisible = !compactHeight;
        HistoryEmptyDescription.IsVisible = !compactHeight;

        bool narrow = width < 760;
        SourcePickerLayout.ColumnDefinitions.Clear();
        SourcePickerLayout.RowDefinitions.Clear();
        if (narrow)
        {
            SourcePickerLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            SourcePickerLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Auto));
            SourcePickerLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            SourcePickerLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            Grid.SetColumn(RecentFoldersField, 0);
            Grid.SetColumnSpan(RecentFoldersField, 2);
            Grid.SetRow(RecentFoldersField, 1);
        }
        else
        {
            SourcePickerLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            SourcePickerLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Auto));
            SourcePickerLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(220)));
            SourcePickerLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            Grid.SetColumn(RecentFoldersField, 2);
            Grid.SetColumnSpan(RecentFoldersField, 1);
            Grid.SetRow(RecentFoldersField, 0);
        }

        ApplyPreviewSummaryLayout(width < 700);
    }

    private void ApplyPreviewSummaryLayout(bool narrow)
    {
        PreviewSummaryLayout.ColumnDefinitions.Clear();
        PreviewSummaryLayout.RowDefinitions.Clear();
        if (!narrow)
        {
            for (int index = 0; index < 4; index++)
                PreviewSummaryLayout.ColumnDefinitions.Add(
                    new ColumnDefinition(GridLength.Auto));
            PreviewSummaryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            PreviewSummaryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(220)));
            PreviewSummaryLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            for (int index = 0;
                 index < PreviewSummaryLayout.Children.Count;
                 index++)
            {
                Control child =
                    PreviewSummaryLayout.Children[index];
                Grid.SetColumn(
                    child,
                    index < 4 ? index : 5);
                Grid.SetRow(child, 0);
                Grid.SetColumnSpan(child, 1);
            }
            return;
        }

        PreviewSummaryLayout.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Star));
        PreviewSummaryLayout.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Star));
        PreviewSummaryLayout.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        PreviewSummaryLayout.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        PreviewSummaryLayout.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        for (int index = 0;
             index < PreviewSummaryLayout.Children.Count;
             index++)
        {
            Control child =
                PreviewSummaryLayout.Children[index];
            if (index < 4)
            {
                Grid.SetColumn(child, index % 2);
                Grid.SetRow(child, index / 2);
                Grid.SetColumnSpan(child, 1);
            }
            else
            {
                Grid.SetColumn(child, 0);
                Grid.SetRow(child, 2);
                Grid.SetColumnSpan(child, 2);
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Any(item => item is IStorageFolder) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageFolder? folder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().FirstOrDefault();
        if (folder is not null)
            _viewModel.SetDroppedSource(folder.Path.LocalPath);
        e.Handled = true;
    }
}
