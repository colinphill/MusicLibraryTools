using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;

    public HomeView()
    {
        InitializeComponent();
        _viewModel = App.GetService<HomeViewModel>();
        DataContext = _viewModel;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        PageScaffold.LayoutModeChanged +=
            (_, _) => ApplyResponsiveLayout();
        AttachedToVisualTree += (_, _) =>
        {
            if (!_viewModel.IsBusy)
                _ = _viewModel.RefreshAsync();
            ApplyResponsiveLayout();
        };
    }

    private void ApplyResponsiveLayout()
    {
        double width = PageScaffold.ContentWidth;
        if (width <= 0)
            return;

        MetricGrid.Columns = width >= 1120
            ? 4
            : width >= 620
                ? 2
                : 1;

        ApplyTwoPaneLayout(
            SetupLayout,
            width >= 760,
            firstWeight: 1.2,
            secondWeight: 0.8,
            gap: 28);
        ApplyTwoPaneLayout(
            HealthMetricLayout,
            width >= 700,
            firstWeight: 1,
            secondWeight: 1,
            gap: 14);
        ApplyTwoPaneLayout(
            LibraryActionLayout,
            width >= 820,
            firstWeight: 1.45,
            secondWeight: 1,
            gap: 14);
    }

    private static void ApplyTwoPaneLayout(
        Grid grid,
        bool sideBySide,
        double firstWeight,
        double secondWeight,
        double gap)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (sideBySide)
        {
            grid.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        firstWeight,
                        GridUnitType.Star)));
            grid.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(gap)));
            grid.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        secondWeight,
                        GridUnitType.Star)));
            grid.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            foreach (Control child in grid.Children)
                Grid.SetRow(child, 0);
            if (grid.Children.Count > 0)
                Grid.SetColumn(grid.Children[0], 0);
            if (grid.Children.Count > 1)
                Grid.SetColumn(
                    grid.Children[grid.Children.Count - 1],
                    2);
            return;
        }

        grid.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Star));
        grid.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(
            new RowDefinition(new GridLength(12)));
        grid.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        if (grid.Children.Count > 0)
        {
            Grid.SetColumn(grid.Children[0], 0);
            Grid.SetRow(grid.Children[0], 0);
        }
        if (grid.Children.Count > 1)
        {
            Control second =
                grid.Children[grid.Children.Count - 1];
            Grid.SetColumn(second, 0);
            Grid.SetRow(second, 2);
        }
    }
}
