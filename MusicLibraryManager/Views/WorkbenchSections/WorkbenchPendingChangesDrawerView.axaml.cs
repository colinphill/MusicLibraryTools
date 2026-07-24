using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchPendingChangesDrawerView :
    UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchPendingChangesDrawerView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
            _localization.CultureChanged += OnCultureChanged;
        DetachedFromVisualTree += (_, _) =>
            _localization.CultureChanged -= OnCultureChanged;
        SizeChanged += (_, _) =>
        {
            bool compactHeight =
                Bounds.Height > 0 &&
                Bounds.Height < 430;
            SupportingText.IsVisible =
                !compactHeight;
            StatusText.IsVisible =
                !compactHeight;
        };
    }

    private void ConfigureColumns() =>
        WorkbenchPendingChangesGrid.ConfigureColumns(
        [
            new("File", L("Workbench.Grid.Header.File"), "File", 180, 120),
            new("Field", L("Workbench.Grid.Header.Field"), "Field", 130, 90),
            new("Before", L("Workbench.Grid.Header.Before"), "Before", 240, 140),
            new("After", L("Workbench.Grid.Header.After"), "After", 240, 140),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        WorkbenchPendingChangesGrid.RefreshLocalizedHeaders();

    public event EventHandler? CloseRequested;

    public Control InitialFocus =>
        WorkbenchPendingChangesCloseButton;

    private void OnClose(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        CloseRequested?.Invoke(
            this,
            EventArgs.Empty);
}
