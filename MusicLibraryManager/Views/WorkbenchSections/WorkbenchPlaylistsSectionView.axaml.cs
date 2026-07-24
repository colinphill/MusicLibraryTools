using Avalonia;
using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchPlaylistsSectionView : UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchPlaylistsSectionView()
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
        PlaylistOutputGrid.ConfigureColumns(
        [
            new("Group", L("Workbench.Grid.Header.Group"), "Group", 150, 90),
            new("File", L("Workbench.Grid.Header.Destination"), "File", 420, 220),
            new("Tracks", L("Workbench.Grid.Header.Tracks"), "Tracks", 80, 60),
            new("Bytes", L("Workbench.Grid.Header.Bytes"), "Bytes", 100, 70),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        PlaylistOutputGrid.RefreshLocalizedHeaders();

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        SupportingText.IsVisible = !compactHeight;
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
                compactHeight ? 110 : 270;
            PlaylistOutputGrid.MinHeight =
                compactHeight ? 80 : 150;
            PlaylistOutputGrid.Height =
                double.NaN;
        }
        else
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.85,
                        GridUnitType.Star)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.15,
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
            PlaylistOutputGrid.MinHeight = 150;
            PlaylistOutputGrid.Height = double.NaN;
        }
    }
}
