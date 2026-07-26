using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchAllFieldsSectionView : UserControl
{
    private readonly ILocalizationService _localization;
    private readonly WorkbenchViewModel _viewModel;
    private bool _narrow;
    private bool _drillInRequested;

    public WorkbenchAllFieldsSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        _viewModel = App.GetService<WorkbenchViewModel>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
        {
            _localization.CultureChanged += OnCultureChanged;
            _viewModel.PropertyChanged +=
                OnViewModelPropertyChanged;
            ApplyPanelVisibility();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _localization.CultureChanged -= OnCultureChanged;
            _viewModel.PropertyChanged -=
                OnViewModelPropertyChanged;
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ConfigureColumns() =>
        MetadataFieldsGrid.ConfigureColumns(
        [
            new("Name", L("Column.Field"), "Name", 210, 120),
            new("Kind", L("Column.Kind"), "Kind", 90, 70),
            new("Layers", L("Column.TagLayers"), "Layers", 150, 100),
            new(
                "Coverage",
                L("Column.SelectedFiles"),
                "Coverage",
                105,
                80),
            new(
                "Value",
                L("Column.Values"),
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
        _narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        SupportingText.IsVisible = !compactHeight;
        SectionLayout.ColumnDefinitions.Clear();
        SectionLayout.RowDefinitions.Clear();
        if (_narrow)
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(EditorScroll, 0);
            Grid.SetRow(EditorScroll, 0);
            Grid.SetColumn(FieldListPanel, 0);
            Grid.SetRow(FieldListPanel, 0);
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
            Grid.SetColumn(FieldListPanel, 0);
            Grid.SetRow(FieldListPanel, 0);
        }
        ApplyPanelVisibility();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
            nameof(
                WorkbenchViewModel
                    .SelectedMetadataField))
            ApplyPanelVisibility();
    }

    private void ApplyPanelVisibility()
    {
        bool hasSelection =
            _viewModel.SelectedMetadataField is not null;
        bool showEditor =
            hasSelection ||
            _drillInRequested;
        FieldListPanel.IsVisible =
            !_narrow || !showEditor;
        EditorScroll.IsVisible =
            showEditor;
        AllFieldsBackButton.IsVisible =
            _narrow && showEditor;
        Grid.SetColumnSpan(
            FieldListPanel,
            !_narrow && !showEditor
                ? 3
                : 1);
    }

    private void OnBackToFields(
        object? sender,
        RoutedEventArgs e) =>
        BackToFields();

    private void BackToFields()
    {
        _drillInRequested = false;
        _viewModel.SelectedMetadataField =
            null;
        ApplyPanelVisibility();
    }

    private void OnBeginNewKnownField(
        object? sender,
        RoutedEventArgs e)
    {
        _viewModel.BeginNewKnownFieldCommand.Execute(
            null);
        _drillInRequested = true;
        ApplyPanelVisibility();
    }

    private void OnBeginNewCustomField(
        object? sender,
        RoutedEventArgs e)
    {
        _viewModel.BeginNewCustomFieldCommand.Execute(
            null);
        _drillInRequested = true;
        ApplyPanelVisibility();
    }
}
