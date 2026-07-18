using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.GetService<SettingsViewModel>();
    }
}
