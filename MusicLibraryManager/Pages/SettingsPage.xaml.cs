using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Pages;

public sealed partial class SettingsPage : UserControl
{
    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IndexTargetEditorRow row } && ViewModel.BrowseIndexTargetCommand.CanExecute(row))
            ViewModel.BrowseIndexTargetCommand.Execute(row);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IndexTargetEditorRow row } && ViewModel.RemoveIndexTargetCommand.CanExecute(row))
            ViewModel.RemoveIndexTargetCommand.Execute(row);
    }
}
