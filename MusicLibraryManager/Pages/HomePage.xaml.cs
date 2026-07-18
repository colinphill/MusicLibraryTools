using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Pages;

public sealed partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = App.GetService<HomeViewModel>();
        Loaded += HomePage_Loaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        => await ((HomeViewModel)DataContext).RefreshAsync();
}
