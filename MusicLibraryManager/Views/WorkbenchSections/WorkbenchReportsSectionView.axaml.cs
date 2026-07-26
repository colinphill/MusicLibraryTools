using Avalonia;
using Avalonia.Controls;
using System.Collections.Specialized;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchReportsSectionView : UserControl
{
    private readonly ILocalizationService _localization;
    private readonly WorkbenchViewModel _viewModel;

    public WorkbenchReportsSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        _viewModel = App.GetService<WorkbenchViewModel>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
        {
            _localization.CultureChanged += OnCultureChanged;
            _viewModel.ReportOutputs.CollectionChanged +=
                OnReportOutputsChanged;
            ApplyPreviewVisibility();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _localization.CultureChanged -= OnCultureChanged;
            _viewModel.ReportOutputs.CollectionChanged -=
                OnReportOutputsChanged;
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ConfigureColumns() =>
        ReportOutputGrid.ConfigureColumns(
        [
            new("Group", L("Column.Group"), "Group", 150, 90),
            new("File", L("Column.Destination"), "File", 420, 220),
            new("Rows", L("Column.Rows"), "Rows", 80, 60),
            new("Bytes", L("Column.Bytes"), "Bytes", 100, 70),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        ReportOutputGrid.RefreshLocalizedHeaders();

    private void OnReportOutputsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        ApplyPreviewVisibility();

    private void ApplyPreviewVisibility()
    {
        bool hasPreview =
            _viewModel.ReportOutputs.Count > 0;
        ReportOutputGrid.IsVisible =
            hasPreview;
        ReportPreviewEmptyState.IsVisible =
            false;
        ReviewedTitle.IsVisible =
            hasPreview;
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool hasPreview =
            _viewModel.ReportOutputs.Count > 0;
        bool stacked =
            narrow || !hasPreview;
        SectionLayout.ColumnDefinitions.Clear();
        SectionLayout.RowDefinitions.Clear();
        ReviewedPanel.RowDefinitions.Clear();
        ReviewedPanel.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        ReviewedPanel.RowDefinitions.Add(
            new RowDefinition(
                hasPreview
                    ? new GridLength(
                        1,
                        GridUnitType.Star)
                    : GridLength.Auto));
        ReviewedPanel.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        ReviewedPanel.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        if (stacked)
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    hasPreview
                        ? GridLength.Auto
                        : new GridLength(
                            1,
                            GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(12)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    hasPreview
                        ? new GridLength(
                            1,
                            GridUnitType.Star)
                        : GridLength.Auto));
            Grid.SetColumn(ReviewedPanel, 0);
            Grid.SetRow(ReviewedPanel, 2);
            EditorScroll.MaxHeight =
                !hasPreview
                    ? double.PositiveInfinity
                    : Bounds.Height < 430
                    ? Math.Clamp(
                        Bounds.Height * .42,
                        150,
                        180)
                    : 280;
            ReportOutputGrid.MinHeight =
                Bounds.Height < 430 ? 80 : 150;
            ReportOutputGrid.Height =
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
                new ColumnDefinition(new GridLength(12)));
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
            ReportOutputGrid.MinHeight = 150;
            ReportOutputGrid.Height = double.NaN;
        }
    }

    private void OnMoveFieldUp(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        ExecuteFieldCommand(
            sender,
            editor =>
                editor.MoveFieldUpCommand.Execute(
                    null));

    private void OnMoveFieldDown(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        ExecuteFieldCommand(
            sender,
            editor =>
                editor.MoveFieldDownCommand.Execute(
                    null));

    private void OnRemoveField(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        ExecuteFieldCommand(
            sender,
            editor =>
                editor.RemoveFieldCommand.Execute(
                    null));

    private static void ExecuteFieldCommand(
        object? sender,
        Action<ReportEditorViewModel> execute)
    {
        if (sender is not MenuItem
            {
                Tag: ReportFieldEditorRow row,
            })
            return;
        WorkbenchViewModel viewModel =
            App.GetService<WorkbenchViewModel>();
        viewModel.ReportEditor.SelectedField =
            row;
        execute(viewModel.ReportEditor);
    }
}
