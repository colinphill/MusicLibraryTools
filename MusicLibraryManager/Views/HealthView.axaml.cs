using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class HealthView : UserControl
{
    private readonly AnalyzerViewModel _viewModel;
    private readonly IPlatformService _platform;
    private bool _updatingRootDisposition;

    public HealthView()
    {
        InitializeComponent();
        _viewModel = App.GetService<AnalyzerViewModel>();
        _platform = App.GetService<IPlatformService>();
        GridStateService gridState = App.GetService<GridStateService>();
        DataContext = _viewModel;
        FindingRootDisposition.ItemsSource = Enum.GetValues<AnalysisFindingDisposition>();
        IReadOnlyList<AnalysisRepairDisposition> rootDispositions = Enum.GetValues<AnalysisRepairDisposition>();
        ArtistRootDisposition.ItemsSource = rootDispositions;
        RepairRootDisposition.ItemsSource = rootDispositions;
        RepresentationRootDisposition.ItemsSource = rootDispositions;
        ItlRepairRootDisposition.ItemsSource = rootDispositions;
        ArtworkRepairRootDisposition.ItemsSource = rootDispositions;
        FindingRootDisposition.SelectionChanged += OnFindingRootDispositionChanged;
        ArtistRootDisposition.SelectionChanged += OnArtistRootDispositionChanged;
        RepairRootDisposition.SelectionChanged += OnRepairRootDispositionChanged;
        RepresentationRootDisposition.SelectionChanged += OnRepresentationRootDispositionChanged;
        ItlRepairRootDisposition.SelectionChanged += OnItlRepairRootDispositionChanged;
        ArtworkRepairRootDisposition.SelectionChanged += OnArtworkRepairRootDispositionChanged;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AnalyzerViewModel.SelectedRun) or
                nameof(AnalyzerViewModel.FilteredPaths))
                UpdateRootDispositions();
        };
        UpdateRootDispositions();

        var findingDispositionTemplate = new FuncDataTemplate<AnalysisFindingViewModel>((_, _) =>
        {
            var select = new ComboBox { MinWidth = 115 };
            select.Bind(ItemsControl.ItemsSourceProperty,
                new Binding(nameof(AnalysisFindingViewModel.Dispositions)));
            select.Bind(ComboBox.SelectedItemProperty,
                new Binding(nameof(AnalysisFindingViewModel.Disposition))
                {
                    Mode = BindingMode.TwoWay,
                });
            return select;
        });
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
        var itlDispositionTemplate = new FuncDataTemplate<ItlMetadataRepairItemViewModel>((_, _) =>
        {
            var select = new ComboBox { MinWidth = 115 };
            select.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ItlMetadataRepairItemViewModel.Dispositions)));
            select.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ItlMetadataRepairItemViewModel.Disposition)) { Mode = BindingMode.TwoWay });
            select.Bind(IsEnabledProperty, new Binding(nameof(ItlMetadataRepairItemViewModel.CanChangeDisposition)));
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
        IDataTemplate beforeDifferenceTemplate = DifferenceTemplate(
            item => item.BeforeDifference);
        IDataTemplate afterDifferenceTemplate = DifferenceTemplate(
            item => item.AfterDifference);
        IDataTemplate itlBeforeDifferenceTemplate = ItlDifferenceTemplate(
            item => item.BeforeDifference);
        IDataTemplate itlAfterDifferenceTemplate = ItlDifferenceTemplate(
            item => item.AfterDifference);

        PersistedGridLayout.Configure(FindingGrid, gridState, "health.findings",
            [new("Disposition", "Disposition", null, 145, 125,
                    CellTemplate: findingDispositionTemplate, Sortable: false),
                new("Path", "Track", "Path", 380, 220),
                new("Description", "Reason", "Description", 420, 220)]);
        PersistedGridLayout.Configure(RepairGrid, gridState, "health.metadata-repairs", [
            new("Disposition", "Disposition", null, 145, 125, CellTemplate: dispositionTemplate, Sortable: false), new("Path", "Track", "DisplayPath", 320, 190),
            new("Before", "Before", "Before", 180, 100, CellTemplate: beforeDifferenceTemplate),
            new("After", "After", "After", 180, 100, CellTemplate: afterDifferenceTemplate),
            new("Reason", "Reason", "Reason", 320, 180),
            new("Result", "Result", "ResultText", 260, 160)]);
        PersistedGridLayout.Configure(RepresentationGrid, gridState, "health.file-repairs", [
            new("Disposition", "Disposition", null, 145, 125, CellTemplate: representationDispositionTemplate, Sortable: false), new("Source", "Track", "SourcePath", 320, 190),
            new("Action", "Action", "Description", 250, 160), new("Destination", "Destination", "DestinationPath", 340, 200), new("Result", "Result", "ResultText", 180, 110)]);
        PersistedGridLayout.Configure(ItlRepairGrid, gridState, "health.itl-metadata-repairs", [
            new("Disposition", "Disposition", null, 145, 125, CellTemplate: itlDispositionTemplate, Sortable: false),
            new("Path", "Track", "DisplayPath", 330, 200),
            new("Fields", "Fields", "Fields", 210, 130),
            new("Before", "Before", "Before", 280, 160, CellTemplate: itlBeforeDifferenceTemplate),
            new("After", "After", "After", 280, 160, CellTemplate: itlAfterDifferenceTemplate),
            new("Result", "Result", "ResultText", 200, 120)]);
        PersistedGridLayout.Configure(ConflictGrid, gridState, "health.conflicts", [
            new("Album", "Album", "Album", 190, 120), new("Field", "Field", "Field", 130, 90), new("Files", "Files", "FileCount", 75, 60),
            new("Canonical", "Canonical value", null, 230, 150, CellTemplate: conflictTemplate, Sortable: false), new("Directory", "Directory", "Directory", 360, 200)]);
    }

    private void UpdateRootDispositions()
    {
        _updatingRootDisposition = true;
        FindingRootDisposition.SelectedItem = AnalysisProblemGroupViewModel.Aggregate(
            _viewModel.FindingGroups.Select(group => group.Disposition));
        RepairRootDisposition.SelectedItem = Aggregate(_viewModel.RepairGroups.Select(group => group.Disposition));
        ArtistRootDisposition.SelectedItem = Aggregate(_viewModel.ArtistGroups.Select(group => group.Disposition));
        RepresentationRootDisposition.SelectedItem = Aggregate(_viewModel.RepresentationActionGroups.Select(group => group.Disposition));
        ItlRepairRootDisposition.SelectedItem = Aggregate(_viewModel.ItlRepairGroups.Select(group => group.Disposition));
        ArtworkRepairRootDisposition.SelectedItem = Aggregate(
            _viewModel.ArtworkRepairItems.Select(item => item.Disposition));
        _updatingRootDisposition = false;
    }

    private void OnFindingRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            FindingRootDisposition.SelectedItem is not AnalysisFindingDisposition value ||
            value == AnalysisFindingDisposition.Mixed)
            return;
        foreach (AnalysisProblemGroupViewModel group in _viewModel.FindingGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || RepairRootDisposition.SelectedItem is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (AnalysisRepairCategoryGroupViewModel group in _viewModel.RepairGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnArtistRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            ArtistRootDisposition.SelectedItem is not AnalysisRepairDisposition value ||
            value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (ArtistGroupViewModel group in _viewModel.ArtistGroups)
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

    private void OnItlRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || ItlRepairRootDisposition.SelectedItem is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (ItlMetadataRepairCategoryGroupViewModel group in _viewModel.ItlRepairGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnArtworkRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            ArtworkRepairRootDisposition.SelectedItem is not AnalysisRepairDisposition value ||
            value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (ArtworkRepairItemViewModel item in _viewModel.ArtworkRepairItems
                     .Where(item => item.CanChangeDisposition &&
                         (item.CanApply || value is not (AnalysisRepairDisposition.Active or
                             AnalysisRepairDisposition.Completed))))
            item.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnSelectFindingRoot(object? sender, RoutedEventArgs e) =>
        _viewModel.SelectedFindingNode = null;

    // A ComboBox hosted by a TreeViewItem must consume its own press. Otherwise the same press
    // also selects (and scrolls) the tree row after opening the drop-down.
    private void OnEmbeddedInteractivePointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    private void OnArtworkRepairTreeContextRequested(
        object? sender,
        ContextRequestedEventArgs e)
    {
        Control? source = e.Source as Control;
        object? node = source?.DataContext;
        if (node is null || !_viewModel.CanAutomaticallySelectMixedArtwork(node))
            return;

        var first = CreateArtworkSelectionMenuItem(
            "Select first image and mark Active", node, ArtworkCandidateSelectionRule.First);
        var resolution = CreateArtworkSelectionMenuItem(
            "Select highest resolution and mark Active", node,
            ArtworkCandidateSelectionRule.HighestResolution);
        var size = CreateArtworkSelectionMenuItem(
            "Select largest file and mark Active", node,
            ArtworkCandidateSelectionRule.LargestFile);
        var menu = new ContextMenu { ItemsSource = new[] { first, resolution, size } };
        menu.Open(source ?? (Control)sender!);
        e.Handled = true;
    }

    private MenuItem CreateArtworkSelectionMenuItem(
        string header,
        object node,
        ArtworkCandidateSelectionRule rule)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            int activated = _viewModel.AutomaticallySelectMixedArtwork(node, rule);
            _viewModel.StatusText = activated == 0
                ? "No eligible mixed-artwork repairs were changed."
                : $"Selected artwork and marked {activated:N0} repair(s) Active.";
        };
        return item;
    }

    private async void OnArtworkCandidateAttached(
        object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image { Tag: ArtworkRepairCandidateViewModel candidate })
            return;
        try
        {
            await candidate.EnsureThumbnailAsync();
        }
        catch
        {
            // Invalid or unavailable artwork leaves an empty preview without blocking the list.
        }
    }

    private void OnHealthResultContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        ContextMenu? menu = HealthResultContextMenuFactory.CreateForSource(e.Source, _platform);
        if (menu is null)
            return;

        Control target = e.Source as Control ?? (Control)sender!;
        menu.Open(target);
        e.Handled = true;
    }

    private static AnalysisRepairDisposition Aggregate(IEnumerable<AnalysisRepairDisposition> values)
    {
        AnalysisRepairDisposition[] distinct = values.Distinct().ToArray();
        return distinct.Length == 0 ? AnalysisRepairDisposition.Ignored
            : distinct.Length == 1 ? distinct[0]
            : AnalysisRepairDisposition.Mixed;
    }

    private static IDataTemplate DifferenceTemplate(
        Func<AnalysisRepairItemViewModel, IReadOnlyList<TextDifferenceSegment>> selectSegments) =>
        new FuncDataTemplate<AnalysisRepairItemViewModel>((item, _) =>
        {
            var text = new TextBlock
            {
                TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            };
            if (item is null)
                return text;
            foreach (TextDifferenceSegment segment in selectSegments(item))
            {
                var run = new Run(segment.Text);
                if (segment.IsDifferent)
                    run.Classes.Add("text-difference");
                text.Inlines!.Add(run);
            }
            if (item.UnicodeDifferenceDetails is not null)
                ToolTip.SetTip(text, item.UnicodeDifferenceDetails);
            return text;
        });

    private static IDataTemplate ItlDifferenceTemplate(
        Func<ItlMetadataRepairItemViewModel, IReadOnlyList<TextDifferenceSegment>> selectSegments) =>
        new FuncDataTemplate<ItlMetadataRepairItemViewModel>((item, _) =>
        {
            var text = new TextBlock
            {
                TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            };
            if (item is null)
                return text;
            foreach (TextDifferenceSegment segment in selectSegments(item))
            {
                var run = new Run(segment.Text);
                if (segment.IsDifferent)
                    run.Classes.Add("text-difference");
                text.Inlines!.Add(run);
            }
            if (item.UnicodeDifferenceDetails is not null)
                ToolTip.SetTip(text, item.UnicodeDifferenceDetails);
            return text;
        });
}

public static class HealthResultContextMenuFactory
{
    public static ContextMenu? CreateForSource(object? source, IPlatformService platform)
    {
        object? result = (source as global::Avalonia.StyledElement)?.DataContext;
        return HealthResultPathResolver.TryGetPath(result, out string path)
            ? Create(path, platform)
            : null;
    }

    public static ContextMenu Create(string path, IPlatformService platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(platform);

        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) => await platform.CopyTextAsync(path);

        var reveal = new MenuItem { Header = "Reveal in File Explorer" };
        reveal.Click += (_, _) => platform.RevealFile(path);

        return new ContextMenu { ItemsSource = new[] { copy, reveal } };
    }
}
