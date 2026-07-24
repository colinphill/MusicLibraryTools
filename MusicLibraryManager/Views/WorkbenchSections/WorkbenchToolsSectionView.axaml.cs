using Avalonia;
using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchToolsSectionView : UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchToolsSectionView()
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
        ExternalToolInvocationGrid.ConfigureColumns(
        [
            new("Number", "#", "Number", 55, 45),
            new("Executable", L("Workbench.Grid.Header.Executable"), "Executable", 190, 120),
            new("Arguments", L("Workbench.Grid.Header.Arguments"), "Arguments", 360, 190),
            new(
                "WorkingDirectory",
                L("Workbench.Grid.Header.WorkingDirectory"),
                "WorkingDirectory",
                220,
                130),
            new("Files", L("Workbench.Grid.Header.Files"), "Files", 65, 52),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        ExternalToolInvocationGrid.RefreshLocalizedHeaders();

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        PlaceholderHelp.IsVisible = !compactHeight;
        SafetyNote.IsVisible = !compactHeight;
        SectionLayout.ColumnDefinitions.Clear();
        SectionLayout.RowDefinitions.Clear();
        if (narrow)
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(12)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(ReviewedPanel, 0);
            Grid.SetRow(ReviewedPanel, 2);
            EditorScroll.MaxHeight =
                compactHeight ? 110 : 280;
            ExternalToolInvocationGrid.MinHeight =
                compactHeight ? 80 : 150;
            ExternalToolInvocationGrid.Height =
                double.NaN;
        }
        else
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.9,
                        GridUnitType.Star)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.1,
                        GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(ReviewedPanel, 2);
            Grid.SetRow(ReviewedPanel, 0);
            EditorScroll.MaxHeight =
                double.PositiveInfinity;
            ExternalToolInvocationGrid.MinHeight = 150;
            ExternalToolInvocationGrid.Height = double.NaN;
        }
    }
}
