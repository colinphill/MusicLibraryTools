using Microsoft.UI.Xaml.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public sealed partial class OperationsPage : UserControl
{
    public OperationsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<OperationsViewModel>();
    }
}
