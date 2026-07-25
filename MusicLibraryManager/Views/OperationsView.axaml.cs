using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;

namespace MusicLibraryManager.Views;

public partial class OperationsView : UserControl
{
    public OperationsView()
    {
        InitializeComponent();
        DataContext = App.GetService<OperationsViewModel>();
        HistoryGrid.ConfigureColumns([
            new AppGridColumnDefinition("Created", "When", "Created", 150, 120,
                HeaderResourceKey: "Column.When"),
            new AppGridColumnDefinition("Job", "Job", "JobName", 190, 140,
                HeaderResourceKey: "Column.Job"),
            new AppGridColumnDefinition("State", "State", "State", 140, 100,
                HeaderResourceKey: "Column.State"),
            new AppGridColumnDefinition("Elapsed", "Elapsed", "Elapsed", 90, 70,
                HeaderResourceKey: "Column.Elapsed"),
            new AppGridColumnDefinition("Output", "Output", "Output", 420, 240,
                HeaderResourceKey: "Column.Output"),
        ]);
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
        AttachedToVisualTree += (_, _) =>
            ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;
        if (width <= 0)
            return;

        bool narrowJobs = width < 780;
        JobsLayout.ColumnDefinitions.Clear();
        if (narrowJobs)
        {
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(0)));
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(0)));
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
        }
        else
        {
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(300)));
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            JobsLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
        }
        JobListPane.IsVisible = !narrowJobs;
        JobPicker.IsVisible = narrowJobs;

        bool narrowRecovery = width < 860;
        RecoveryLayout.ColumnDefinitions.Clear();
        if (narrowRecovery)
        {
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(0)));
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(0)));
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
        }
        else
        {
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.7,
                        GridUnitType.Star)));
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            RecoveryLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.3,
                        GridUnitType.Star)));
        }
        RecoveryRunPane.IsVisible =
            !narrowRecovery;
        RecoveryRunPicker.IsVisible =
            narrowRecovery;
        RecoveryCompactSearch.IsVisible =
            narrowRecovery;
    }
}
