using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;
using MusicLibraryManager.Studio.Avalonia.Controls;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class OrganizeView : UserControl
{
    public OrganizeView()
    {
        InitializeComponent();
        DataContext = App.GetService<OrganizeViewModel>();
        MovesGrid.ConfigureColumns([
            new StudioGridColumnDefinition("Source", "Current location", "Source", 520, 260),
            new StudioGridColumnDefinition("Destination", "New location", "Destination", 620, 300),
        ]);
    }
}
