using Microsoft.UI.Xaml;
using WinUI.TableView;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Corrects WinUI.TableView's visible-column index handling when hidden columns
/// are present in the full columns collection.
/// </summary>
public static class TableViewColumnReorderBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(TableViewColumnReorderBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TableView tableView)
            return;

        tableView.ColumnReordering -= TableView_ColumnReordering;
        if (args.NewValue is true)
            tableView.ColumnReordering += TableView_ColumnReordering;
    }

    private static void TableView_ColumnReordering(
        object? sender,
        TableViewColumnReorderingEventArgs args)
    {
        if (sender is not TableView tableView || args.Cancel)
            return;

        // The control's built-in implementation is correct while these two index
        // spaces are identical. Preserve its normal completion event in that case.
        if (tableView.Columns.VisibleColumns.Count == tableView.Columns.Count)
            return;

        int visibleTargetIndex = args.DropIndex;
        if (visibleTargetIndex < 0 || visibleTargetIndex >= tableView.Columns.VisibleColumns.Count)
            return;

        int sourceIndex = tableView.Columns.IndexOf(args.Column);
        TableViewColumn targetColumn = tableView.Columns.VisibleColumns[visibleTargetIndex];
        int targetIndex = tableView.Columns.IndexOf(targetColumn);
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        // TableView 1.4.1 passes visible-list indexes to the full collection's Move
        // method. Cancel that move and perform it with full-collection indexes.
        args.Cancel = true;
        if (sourceIndex != targetIndex)
            tableView.Columns.Move(sourceIndex, targetIndex);
    }
}
