using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Studio.Avalonia.Controls;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class HealthView : UserControl
{
    private readonly AnalyzerViewModel _viewModel;
    private bool _updatingRootDisposition;

    public HealthView()
    {
        InitializeComponent();
        _viewModel = App.GetService<AnalyzerViewModel>();
        DataContext = _viewModel;
        IReadOnlyList<AnalysisRepairDisposition> rootDispositions = Enum.GetValues<AnalysisRepairDisposition>();
        RepairRootDisposition.ItemsSource = rootDispositions;
        RepresentationRootDisposition.ItemsSource = rootDispositions;
        RepairRootDisposition.SelectionChanged += OnRepairRootDispositionChanged;
        RepresentationRootDisposition.SelectionChanged += OnRepresentationRootDispositionChanged;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AnalyzerViewModel.SelectedRun))
                UpdateRootDispositions();
        };
        UpdateRootDispositions();

        var dispositionTemplate = new FuncDataTemplate<AnalysisRepairItemViewModel>((_, _) =>
        {
            var select = new ComboBox { MinWidth = 115 };
            select.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AnalysisRepairItemViewModel.Dispositions)));
            select.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(AnalysisRepairItemViewModel.Disposition)) { Mode = BindingMode.TwoWay });
            select.Bind(IsEnabledProperty, new Binding(nameof(AnalysisRepairItemViewModel.CanChangeDisposition)));
            return select;
        });
        var representationDispositionTemplate = new FuncDataTemplate<RepresentationRepairActionItemViewModel>((_, _) =>
        {
            var select = new ComboBox { MinWidth = 115 };
            select.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(RepresentationRepairActionItemViewModel.Dispositions)));
            select.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(RepresentationRepairActionItemViewModel.Disposition)) { Mode = BindingMode.TwoWay });
            select.Bind(IsEnabledProperty, new Binding(nameof(RepresentationRepairActionItemViewModel.CanChangeDisposition)));
            return select;
        });
        var conflictTemplate = new FuncDataTemplate<AnalysisConflictGroupViewModel>((_, _) =>
        {
            var select = new ComboBox { MinWidth = 190 };
            select.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AnalysisConflictGroupViewModel.Options)));
            select.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(AnalysisConflictGroupViewModel.SelectedOption)) { Mode = BindingMode.TwoWay });
            select.ItemTemplate = new FuncDataTemplate<AnalysisConflictOptionViewModel>((_, _) =>
            {
                var label = new TextBlock();
                label.Bind(TextBlock.TextProperty, new Binding(nameof(AnalysisConflictOptionViewModel.Value)));
                return label;
            });
            return select;
        });

        FindingGrid.ConfigureColumns([new("Path", "Track", "Path", 380, 220), new("Description", "Reason", "Description", 420, 220)]);
        RepairGrid.ConfigureColumns([
            new("Disposition", "Disposition", null, 145, 125, CellTemplate: dispositionTemplate, Sortable: false), new("Path", "Track", "DisplayPath", 320, 190),
            new("Before", "Before", "Before", 180, 100), new("After", "After", "After", 180, 100), new("Reason", "Reason", "Reason", 320, 180)]);
        RepresentationGrid.ConfigureColumns([
            new("Disposition", "Disposition", null, 145, 125, CellTemplate: representationDispositionTemplate, Sortable: false), new("Source", "Track", "SourcePath", 320, 190),
            new("Action", "Action", "Description", 250, 160), new("Destination", "Destination", "DestinationPath", 340, 200), new("Result", "Result", "ResultText", 180, 110)]);
        ConflictGrid.ConfigureColumns([
            new("Album", "Album", "Album", 190, 120), new("Field", "Field", "Field", 130, 90), new("Files", "Files", "FileCount", 75, 60),
            new("Canonical", "Canonical value", null, 230, 150, CellTemplate: conflictTemplate, Sortable: false), new("Directory", "Directory", "Directory", 360, 200)]);
    }

    private void UpdateRootDispositions()
    {
        _updatingRootDisposition = true;
        RepairRootDisposition.SelectedItem = Aggregate(_viewModel.RepairGroups.Select(group => group.Disposition));
        RepresentationRootDisposition.SelectedItem = Aggregate(_viewModel.RepresentationActionGroups.Select(group => group.Disposition));
        _updatingRootDisposition = false;
    }

    private void OnRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || RepairRootDisposition.SelectedItem is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (AnalysisRepairCategoryGroupViewModel group in _viewModel.RepairGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnRepresentationRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || RepresentationRootDisposition.SelectedItem is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (RepresentationRepairCategoryGroupViewModel group in _viewModel.RepresentationActionGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnSelectFindingRoot(object? sender, RoutedEventArgs e) =>
        _viewModel.SelectedFindingNode = null;

    private static AnalysisRepairDisposition Aggregate(IEnumerable<AnalysisRepairDisposition> values)
    {
        AnalysisRepairDisposition[] distinct = values.Distinct().ToArray();
        return distinct.Length == 0 ? AnalysisRepairDisposition.Ignored
            : distinct.Length == 1 ? distinct[0]
            : AnalysisRepairDisposition.Mixed;
    }
}
