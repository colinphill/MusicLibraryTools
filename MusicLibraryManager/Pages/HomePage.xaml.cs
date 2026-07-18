using System.Windows;
using System.Windows.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Pages;

public partial class HomePage : UserControl
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
