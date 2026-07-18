using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;

namespace MusicLibraryManager.Views;

public partial class OrganizeView : UserControl
{
    public OrganizeView()
    {
        InitializeComponent();
        DataContext = App.GetService<OrganizeViewModel>();
        MovesGrid.ConfigureColumns([
            new AppGridColumnDefinition("Source", "Current location", "Source", 520, 260),
            new AppGridColumnDefinition("Destination", "New location", "Destination", 620, 300),
        ]);
    }
}
