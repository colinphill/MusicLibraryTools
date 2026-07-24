using Avalonia;
using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchAllFieldsSectionView : UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchAllFieldsSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
            _localization.CultureChanged += OnCultureChanged;
        DetachedFromVisualTree += (_, _) =>
            _localization.CultureChanged -= OnCultureChanged;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ConfigureColumns() =>
        MetadataFieldsGrid.ConfigureColumns(
        [
            new("Name", L("Workbench.Grid.Header.Field"), "Name", 210, 120),
            new("Kind", L("Workbench.Grid.Header.Kind"), "Kind", 90, 70),
            new("Layers", L("Workbench.Grid.Header.TagLayers"), "Layers", 150, 100),
            new(
                "Coverage",
                L("Workbench.Grid.Header.SelectedFiles"),
                "Coverage",
                105,
                80),
            new(
                "Value",
                L("Workbench.Grid.Header.Values"),
                "DisplayValue",
                340,
                180),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        MetadataFieldsGrid.RefreshLocalizedHeaders();

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        SupportingText.IsVisible = !compactHeight;
        SectionLayout.ColumnDefinitions.Clear();
        SectionLayout.RowDefinitions.Clear();
        if (narrow)
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(210)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(12)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(EditorScroll, 0);
            Grid.SetRow(EditorScroll, 2);
        }
        else
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.15,
                        GridUnitType.Star)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.85,
                        GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(EditorScroll, 2);
            Grid.SetRow(EditorScroll, 0);
        }
    }
}
