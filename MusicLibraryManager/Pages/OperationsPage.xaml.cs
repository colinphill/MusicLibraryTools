using System.Windows.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public partial class OperationsPage : UserControl
{
    public OperationsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<OperationsViewModel>();
    }
}
