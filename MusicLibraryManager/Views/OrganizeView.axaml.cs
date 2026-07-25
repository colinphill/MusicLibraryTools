using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;

namespace MusicLibraryManager.Views;

public partial class OrganizeView : UserControl
{
    private readonly IAppSettings _settings;
    private readonly INavigationService _navigation;

    public OrganizeView()
    {
        InitializeComponent();
        _settings = App.GetService<IAppSettings>();
        _navigation =
            App.GetService<INavigationService>();
        DataContext = App.GetService<OrganizeViewModel>();
        MovesGrid.ConfigureColumns([
            new AppGridColumnDefinition("Source", "Current location", "Source", 520, 260,
                HeaderResourceKey: "Column.CurrentLocation"),
            new AppGridColumnDefinition("Destination", "New location", "Destination", 620, 300,
                HeaderResourceKey: "Column.NewLocation"),
        ]);
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        UpdateSetupState();
    }

    private void OnAttached(
        object? sender,
        global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _settings.ConfigurationChanged +=
            OnConfigurationChanged;
        UpdateSetupState();
    }

    private void OnDetached(
        object? sender,
        global::Avalonia.VisualTreeAttachmentEventArgs e) =>
        _settings.ConfigurationChanged -=
            OnConfigurationChanged;

    private void OnConfigurationChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateSetupState);

    private void UpdateSetupState()
    {
        bool hasConfiguration =
            _settings.Configuration is not null;
        OrganizeSetupCard.IsVisible =
            !hasConfiguration;
        OrganizeSummaryCard.IsVisible =
            hasConfiguration;
        OrganizeResultsCard.IsVisible =
            hasConfiguration;
        OrganizeWorkflowActions.IsVisible =
            hasConfiguration;
    }

    private void OnOpenSettings(
        object? sender,
        RoutedEventArgs e) =>
        _navigation.Navigate(
            ShellDestination.Settings);
}
