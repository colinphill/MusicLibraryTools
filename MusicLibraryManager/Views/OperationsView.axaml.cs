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
            new AppGridColumnDefinition("Created", "When", "Created", 150, 120),
            new AppGridColumnDefinition("Job", "Job", "JobName", 190, 140),
            new AppGridColumnDefinition("State", "State", "State", 140, 100),
            new AppGridColumnDefinition("Elapsed", "Elapsed", "Elapsed", 90, 70),
            new AppGridColumnDefinition("Output", "Output", "Output", 420, 240),
        ]);
    }
}
