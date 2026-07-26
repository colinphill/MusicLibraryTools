using Avalonia;
using Avalonia.Controls;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchBulkOperationSectionView : UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchBulkOperationSectionView()
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

    private void ConfigureColumns()
    {
        PreviewGrid.ConfigureColumns(
        [
            new("File", L("Column.File"), "File", 220, 140),
            new("Field", L("Column.Field"), "Field", 150, 100),
            new("Before", L("Column.Before"), "Before", 320, 180),
            new("After", L("Column.After"), "After", 320, 180),
        ]);
        PendingOperationsGrid.ConfigureColumns(
        [
            new("Number", L("Column.NumberSign"), "Number", 55, 45),
            new("Operation", L("Column.Operation"), "Operation", 220, 130),
            new(
                "Target",
                L("Column.TargetDetails"),
                "Target",
                360,
                180),
            new(
                "AppliesTo",
                L("Column.AppliesTo"),
                "AppliesTo",
                120,
                90),
        ]);
    }

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(
        object? sender,
        EventArgs e)
    {
        PreviewGrid.RefreshLocalizedHeaders();
        PendingOperationsGrid.RefreshLocalizedHeaders();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        RepresentativeSupportingText.IsVisible =
            !compactHeight;
        RecipeLayout.ColumnDefinitions.Clear();
        RecipeLayout.RowDefinitions.Clear();
        if (narrow)
        {
            RecipeLayout.Height = 340;
            RecipeLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            RecipeLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(190)));
            RecipeLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(10)));
            RecipeLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(140)));
            Grid.SetColumn(SavedRecipesPanel, 0);
            Grid.SetRow(SavedRecipesPanel, 2);
        }
        else
        {
            RecipeLayout.Height = 190;
            RecipeLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.2,
                        GridUnitType.Star)));
            RecipeLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(12)));
            RecipeLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.8,
                        GridUnitType.Star)));
            RecipeLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(SavedRecipesPanel, 2);
            Grid.SetRow(SavedRecipesPanel, 0);
        }
    }
}
