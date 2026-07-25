using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using System.ComponentModel;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class DevicesView : UserControl
{
    private bool _startedDeviceDiscovery;
    private readonly DevicesViewModel _viewModel;

    public DevicesView()
    {
        InitializeComponent();
        _viewModel =
            App.GetService<DevicesViewModel>();
        DataContext = _viewModel;
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
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
        _viewModel.PropertyChanged +=
            OnViewModelPropertyChanged;
        _viewModel.InitializeCommand.CanExecuteChanged +=
            OnLifecycleCanExecuteChanged;
        _viewModel.PreviewCommand.CanExecuteChanged +=
            OnLifecycleCanExecuteChanged;
        _viewModel.ApplyCommand.CanExecuteChanged +=
            OnLifecycleCanExecuteChanged;
        AttachedToVisualTree += (_, _) =>
        {
            UpdateLifecycleAction();
            ApplyResponsiveLayout();
        };
    }

    private async void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_startedDeviceDiscovery || DataContext is not DevicesViewModel viewModel)
            return;
        _startedDeviceDiscovery = true;
        if (viewModel.RefreshDevicesCommand.CanExecute(null))
            await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(DevicesViewModel.IsBusy) or
            nameof(DevicesViewModel.HasApplicablePreview))
        {
            UpdateLifecycleAction();
        }
    }

    private void OnLifecycleCanExecuteChanged(
        object? sender,
        EventArgs e) =>
        UpdateLifecycleAction();

    private void UpdateLifecycleAction()
    {
        bool canApply =
            _viewModel.ApplyCommand.CanExecute(null);
        bool canPreview =
            _viewModel.PreviewCommand.CanExecute(null);
        bool canInitialize =
            _viewModel.InitializeCommand.CanExecute(null);

        ApplyButton.IsVisible = canApply;
        PreviewButton.IsVisible =
            !canApply && canPreview;
        InitializeButton.IsVisible =
            !canApply &&
            !canPreview &&
            canInitialize;
    }

    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;
        if (width <= 0)
            return;

        bool stacked = width < 920;
        DevicesContentLayout.ColumnDefinitions.Clear();
        DevicesContentLayout.RowDefinitions.Clear();
        if (!stacked)
        {
            DevicesContentLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(360)));
            DevicesContentLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            DevicesContentLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            DevicesContentLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Star));
            Grid.SetColumn(DeviceConfigurationScroll, 0);
            Grid.SetRow(DeviceConfigurationScroll, 0);
            Grid.SetColumn(DeviceResultsPane, 2);
            Grid.SetRow(DeviceResultsPane, 0);
            return;
        }

        DevicesContentLayout.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Star));
        DevicesContentLayout.RowDefinitions.Add(
            new RowDefinition(
                new GridLength(
                    0.48,
                    GridUnitType.Star)));
        DevicesContentLayout.RowDefinitions.Add(
            new RowDefinition(new GridLength(12)));
        DevicesContentLayout.RowDefinitions.Add(
            new RowDefinition(
                new GridLength(
                    0.52,
                    GridUnitType.Star)));
        Grid.SetColumn(DeviceConfigurationScroll, 0);
        Grid.SetRow(DeviceConfigurationScroll, 0);
        Grid.SetColumn(DeviceResultsPane, 0);
        Grid.SetRow(DeviceResultsPane, 2);
    }
}
