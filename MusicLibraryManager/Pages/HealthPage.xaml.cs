using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public sealed partial class HealthPage : UserControl
{
    public HealthPage()
    {
        InitializeComponent();
        DataContext = App.GetService<AnalyzerViewModel>();
    }

    private void FindingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedFindingNode = (sender as ListView)?.SelectedItem;
    }

    private void RepairList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedRepairNode = (sender as ListView)?.SelectedItem;
    }

    private void RepresentationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedRepresentationNode = (sender as ListView)?.SelectedItem;
    }
}
