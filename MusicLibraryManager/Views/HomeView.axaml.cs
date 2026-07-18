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
        AttachedToVisualTree += (_, _) =>
        {
            if (!_viewModel.IsBusy)
                _ = _viewModel.RefreshAsync();
        };
    }
}
