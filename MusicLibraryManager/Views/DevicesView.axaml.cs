using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class DevicesView : UserControl
{
    private bool _startedDeviceDiscovery;

    public DevicesView()
    {
        InitializeComponent();
        DataContext = App.GetService<DevicesViewModel>();
        Loaded += OnLoaded;
        ActionsGrid.ConfigureColumns([
            new AppGridColumnDefinition("Status", "Status", "Status", 110, 90,
                HeaderResourceKey: "Column.Status"),
            new AppGridColumnDefinition("Kind", "Action", "Kind", 170, 120,
                HeaderResourceKey: "Column.Action"),
            new AppGridColumnDefinition("Path", "Destination path", "RelativePath", 420, 220,
                HeaderResourceKey: "Column.DestinationPath"),
            new AppGridColumnDefinition("Reason", "Reason", "Reason", 430, 220,
                HeaderResourceKey: "Column.Reason"),
            new AppGridColumnDefinition("Length", "Bytes", "Length", 110, 80,
                HeaderResourceKey: "Column.Bytes"),
        ]);
    }

    private async void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_startedDeviceDiscovery || DataContext is not DevicesViewModel viewModel)
            return;
        _startedDeviceDiscovery = true;
        if (viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
    }
}
