using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MusicLibraryManager.Controls;

public enum SplitterResizeSide
{
    Previous,
    Next,
}

/// <summary>A lightweight WinUI column splitter that resizes one adjacent grid column.</summary>
public sealed partial class ColumnSplitter : UserControl
{
    public ColumnSplitter()
    {
        InitializeComponent();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    public SplitterResizeSide ResizeSide { get; set; } = SplitterResizeSide.Previous;

    private void SplitterThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Parent is not Grid grid)
            return;

        int splitterColumn = Grid.GetColumn(this);
        int targetIndex = ResizeSide == SplitterResizeSide.Previous
            ? splitterColumn - 1
            : splitterColumn + 1;
        if (targetIndex < 0 || targetIndex >= grid.ColumnDefinitions.Count)
            return;

        ColumnDefinition target = grid.ColumnDefinitions[targetIndex];
        double change = ResizeSide == SplitterResizeSide.Previous
            ? e.HorizontalChange
            : -e.HorizontalChange;
        double maximum = double.IsInfinity(target.MaxWidth) ? double.MaxValue : target.MaxWidth;
        double width = Math.Clamp(target.ActualWidth + change, target.MinWidth, maximum);
        target.Width = new GridLength(width, GridUnitType.Pixel);
    }
}
