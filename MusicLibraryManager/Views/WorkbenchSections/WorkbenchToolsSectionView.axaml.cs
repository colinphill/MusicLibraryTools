using Avalonia;
using Avalonia.Controls;
using System.Collections.Specialized;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchToolsSectionView : UserControl
{
    private readonly ILocalizationService _localization;
    private readonly WorkbenchViewModel _viewModel;
    private bool _compactHeight;
    private bool _narrow;

    public WorkbenchToolsSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        _viewModel = App.GetService<WorkbenchViewModel>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
        {
            _localization.CultureChanged += OnCultureChanged;
            _viewModel.ExternalToolInvocations.CollectionChanged +=
                OnExternalToolInvocationsChanged;
            ApplyPreviewVisibility();
            ApplyResponsiveLayout();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _localization.CultureChanged -= OnCultureChanged;
            _viewModel.ExternalToolInvocations.CollectionChanged -=
                OnExternalToolInvocationsChanged;
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ConfigureColumns() =>
        ExternalToolInvocationGrid.ConfigureColumns(
        [
            new("Number", L("Column.NumberSign"), "Number", 55, 45),
            new("Executable", L("Column.Executable"), "Executable", 190, 120),
            new("Arguments", L("Column.Arguments"), "Arguments", 360, 190),
            new(
                "WorkingDirectory",
                L("Column.WorkingDirectory"),
                "WorkingDirectory",
                220,
                130),
            new("Files", L("Column.Files"), "Files", 65, 52),
        ]);

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private void OnCultureChanged(object? sender, EventArgs e) =>
        ExternalToolInvocationGrid.RefreshLocalizedHeaders();

    private void OnExternalToolInvocationsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        ApplyPreviewVisibility();

    private void ApplyPreviewVisibility()
    {
        bool hasPreview =
            _viewModel.ExternalToolInvocations.Count > 0;
        ExternalToolInvocationGrid.IsVisible =
            hasPreview;
        ExternalToolPreviewEmptyState.IsVisible =
            !hasPreview;
        ApplyCompactRowAllocation(
            hasPreview);
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        _compactHeight = compactHeight;
        _narrow = narrow;
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
            if (compactHeight)
            {
                SectionLayout.RowDefinitions[0].Height =
                    new GridLength(
                        1,
                        GridUnitType.Star);
                SectionLayout.RowDefinitions[2].Height =
                    new GridLength(
                        1,
                        GridUnitType.Star);
            }
            Grid.SetColumn(ReviewedPanel, 0);
            Grid.SetRow(ReviewedPanel, 2);
            EditorScroll.MaxHeight =
                compactHeight
                    ? double.PositiveInfinity
                    : 280;
            ExternalToolInvocationGrid.MinHeight =
                compactHeight ? 40 : 150;
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
        ExternalToolPreviewEmptyDescription.IsVisible =
            !compactHeight;
        ToolsStatusDiagnosticContainer.IsVisible =
            !compactHeight;
        if (compactHeight)
            ToolsStatusBanner.Padding =
                new Thickness(4);
        else
            ToolsStatusBanner.ClearValue(
                Decorator.PaddingProperty);
        ReviewedPanel.RowSpacing =
            compactHeight ? 4 : 8;
        ToolsStatusText.MaxLines =
            compactHeight ? 1 : 0;
        ApplyPreviewVisibility();
    }

    private void ApplyCompactRowAllocation(
        bool hasPreview)
    {
        if (!_compactHeight ||
            !_narrow ||
            SectionLayout
                .RowDefinitions.Count != 3)
        {
            return;
        }

        SectionLayout
            .RowDefinitions[0]
            .Height =
            new GridLength(
                hasPreview ? 0.4 : 0.52,
                GridUnitType.Star);
        SectionLayout
            .RowDefinitions[2]
            .Height =
            new GridLength(
                hasPreview ? 0.6 : 0.48,
                GridUnitType.Star);
    }
}
