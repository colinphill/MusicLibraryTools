using System.Windows.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public partial class HealthPage : UserControl
{
    public HealthPage()
    {
        InitializeComponent();
        DataContext = App.GetService<AnalyzerViewModel>();
    }

    private void FindingTree_SelectedItemChanged(
        object sender,
        System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedFindingNode = e.NewValue;
    }

    private void RepairTree_SelectedItemChanged(
        object sender,
        System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedRepairNode = e.NewValue;
    }

    private void RepresentationTree_SelectedItemChanged(
        object sender,
        System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AnalyzerViewModel viewModel)
            viewModel.SelectedRepresentationNode = e.NewValue;
    }
}
