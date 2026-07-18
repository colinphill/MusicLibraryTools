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
        SizeChanged += HomePage_SizeChanged;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        => await ((HomeViewModel)DataContext).RefreshAsync();

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
        => HomeContent.Width = Math.Min(1280, Math.Max(0, e.NewSize.Width - 72));
}
