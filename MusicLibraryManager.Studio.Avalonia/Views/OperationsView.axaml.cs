using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;
using MusicLibraryManager.Studio.Avalonia.Controls;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class OperationsView : UserControl
{
    public OperationsView()
    {
        InitializeComponent();
        DataContext = App.GetService<OperationsViewModel>();
        HistoryGrid.ConfigureColumns([
            new StudioGridColumnDefinition("Created", "When", "Created", 150, 120),
            new StudioGridColumnDefinition("Job", "Job", "JobName", 190, 140),
            new StudioGridColumnDefinition("State", "State", "State", 140, 100),
            new StudioGridColumnDefinition("Elapsed", "Elapsed", "Elapsed", 90, 70),
            new StudioGridColumnDefinition("Output", "Output", "Output", 420, 240),
        ]);
    }
}
