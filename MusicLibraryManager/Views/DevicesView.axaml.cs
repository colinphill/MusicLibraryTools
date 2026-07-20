using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        DataContext = App.GetService<DevicesViewModel>();
        ActionsGrid.ConfigureColumns([
            new AppGridColumnDefinition("Status", "Status", "Status", 110, 90),
            new AppGridColumnDefinition("Kind", "Action", "Kind", 170, 120),
            new AppGridColumnDefinition("Path", "Destination path", "RelativePath", 420, 220),
            new AppGridColumnDefinition("Reason", "Reason", "Reason", 430, 220),
            new AppGridColumnDefinition("Length", "Bytes", "Length", 110, 80),
        ]);
    }
}
