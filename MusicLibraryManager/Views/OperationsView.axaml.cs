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
    }
}
