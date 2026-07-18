using System.Windows.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public partial class OrganizePage : UserControl
{
    public OrganizePage()
    {
        InitializeComponent();
        DataContext = App.GetService<OrganizeViewModel>();
    }
}
