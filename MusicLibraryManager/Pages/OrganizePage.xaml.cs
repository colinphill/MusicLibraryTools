using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public sealed partial class OrganizePage : UserControl
{
    public OrganizePage()
    {
        InitializeComponent();
        DataContext = App.GetService<OrganizeViewModel>();
    }
}
