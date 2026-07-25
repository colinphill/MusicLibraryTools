using global::Avalonia;
using global::Avalonia.Automation;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class HealthView : UserControl
{
    private readonly AnalyzerViewModel _viewModel;
    private readonly IPlatformService _platform;
    private readonly WorkbenchViewModel _workbench;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private readonly IAppSettings _settings;
    private readonly ListBoxItem[]
        _allResultNavigationItems;
    private readonly ComboBoxItem[]
        _allResultPickerItems;
    private bool _updatingRootDisposition;
    private bool _synchronizingResultNavigation;

    public HealthView()
    {
        InitializeComponent();
        _allResultNavigationItems =
        [
            .. HealthResultNavigation.Items
                .OfType<ListBoxItem>(),
        ];
        _allResultPickerItems =
        [
            .. HealthResultPicker.Items
                .OfType<ComboBoxItem>(),
        ];
        HealthResultNavigation.Items.Clear();
        HealthResultPicker.Items.Clear();
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
        HealthResultNavigationLayout.SizeChanged +=
            (_, _) => ApplyResultNavigationLayout();
        foreach (Grid layout in new[]
                 {
                     DuplicateMasterDetailLayout,
                     SimilarArtistMasterDetailLayout,
                     AlbumMatrixMasterDetailLayout,
                     ArtworkRepairMasterDetailLayout,
                 })
        {
            layout.SizeChanged +=
                (_, _) =>
                    ApplyHighCardinalityResultLayouts();
        }
        _viewModel = App.GetService<AnalyzerViewModel>();
        _platform = App.GetService<IPlatformService>();
        _workbench = App.GetService<WorkbenchViewModel>();
        _navigation = App.GetService<INavigationService>();
        _localization = App.GetService<ILocalizationService>();
        _settings = App.GetService<IAppSettings>();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        GridStateService gridState = App.GetService<GridStateService>();
        DataContext = _viewModel;
        FindingRootDisposition.ItemsSource =
            _viewModel.FindingDispositionChoices;
        FindingRootDisposition.SelectedValueBinding =
            new Binding(nameof(LocalizedChoice<AnalysisFindingDisposition>.Value));
        foreach (ComboBox comboBox in new[]
                 {
                     ArtistRootDisposition,
                     RepairRootDisposition,
                     RepresentationRootDisposition,
                     ItlRepairRootDisposition,
                     ArtworkRepairRootDisposition,
                 })
        {
            comboBox.ItemsSource =
                _viewModel.RepairDispositionChoices;
            comboBox.SelectedValueBinding =
                new Binding(nameof(LocalizedChoice<AnalysisRepairDisposition>.Value));
        }
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
            if (args.PropertyName is nameof(AnalyzerViewModel.ActiveResultIndex) or
                nameof(AnalyzerViewModel.SelectedRun) or
                nameof(AnalyzerViewModel.HasDuplicateSection) or
                nameof(AnalyzerViewModel.HasArtistSection) or
                nameof(AnalyzerViewModel.HasRepairSection) or
                nameof(AnalyzerViewModel.HasRepresentationSection) or
                nameof(AnalyzerViewModel.HasConflictSection) or
                nameof(AnalyzerViewModel.HasMatrixSection) or
                nameof(AnalyzerViewModel.HasItlRepairSection) or
                nameof(AnalyzerViewModel.HasArtworkRepairSection))
                SynchronizeResultNavigation();
            if (args.PropertyName is nameof(AnalyzerViewModel.HasRuns))
                UpdateSetupState();
        };
        UpdateRootDispositions();
        SynchronizeResultNavigation();

        var conflictTemplate = new FuncDataTemplate<AnalysisConflictGroupViewModel>((_, _) =>
        {
            ComboBox select =
                CreateGridChoice(
                    190,
                    "Column.Disposition");
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
            [new("Path", "Track", "Path", 380, 220,
                    HeaderResourceKey: "Column.Track"),
                new("Description", "Reason", "Description", 420, 220,
                    HeaderResourceKey: "Column.Reason")]);
        PersistedGridLayout.Configure(RepairGrid, gridState, "health.metadata-repairs", [
            new("Path", "Track", "DisplayPath", 320, 190,
                HeaderResourceKey: "Column.Track"),
            new("Before", "Before", "Before", 180, 100,
                CellTemplate: beforeDifferenceTemplate,
                HeaderResourceKey: "Column.Before"),
            new("After", "After", "After", 180, 100,
                CellTemplate: afterDifferenceTemplate,
                HeaderResourceKey: "Column.After"),
            new("Reason", "Reason", "Reason", 320, 180,
                HeaderResourceKey: "Column.Reason"),
            new("Result", "Result", "ResultText", 260, 160,
                HeaderResourceKey: "Column.Result"),
            new("TechnicalDetails", "Technical details", "ResultDiagnosticDetail", 320, 180,
                HeaderResourceKey: "Column.TechnicalDetails")]);
        PersistedGridLayout.Configure(RepresentationGrid, gridState, "health.file-repairs", [
            new("Source", "Track", "SourcePath", 320, 190,
                HeaderResourceKey: "Column.Track"),
            new("Action", "Action", "Description", 250, 160,
                HeaderResourceKey: "Column.Action"),
            new("Destination", "Destination", "DestinationPath", 340, 200,
                HeaderResourceKey: "Column.Destination"),
            new("Result", "Result", "ResultText", 180, 110,
                HeaderResourceKey: "Column.Result"),
            new("TechnicalDetails", "Technical details", "ResultDiagnosticDetail", 320, 180,
                HeaderResourceKey: "Column.TechnicalDetails")]);
        PersistedGridLayout.Configure(ItlRepairGrid, gridState, "health.itl-metadata-repairs", [
            new("Path", "Track", "DisplayPath", 330, 200,
                HeaderResourceKey: "Column.Track"),
            new("Fields", "Fields", "Fields", 210, 130,
                HeaderResourceKey: "Column.Fields"),
            new("Before", "Before", "Before", 280, 160,
                CellTemplate: itlBeforeDifferenceTemplate,
                HeaderResourceKey: "Column.Before"),
            new("After", "After", "After", 280, 160,
                CellTemplate: itlAfterDifferenceTemplate,
                HeaderResourceKey: "Column.After"),
            new("Result", "Result", "ResultText", 200, 120,
                HeaderResourceKey: "Column.Result"),
            new("TechnicalDetails", "Technical details", "ResultDiagnosticDetail", 320, 180,
                HeaderResourceKey: "Column.TechnicalDetails")]);
        PersistedGridLayout.Configure(ConflictGrid, gridState, "health.conflicts", [
            new("Album", "Album", "Album", 190, 120,
                HeaderResourceKey: "Column.Album"),
            new("Field", "Field", "Field", 130, 90,
                HeaderResourceKey: "Column.Field"),
            new("Files", "Files", "FileCount", 75, 60,
                HeaderResourceKey: "Column.Files"),
            new("Canonical", "Canonical value", null, 230, 150,
                CellTemplate: conflictTemplate, Sortable: false,
                HeaderResourceKey: "Column.CanonicalValue"),
            new("Directory", "Directory", "Directory", 360, 200,
                HeaderResourceKey: "Column.Directory")]);
        PersistedGridLayout.Configure(
            AlbumMatrixRowsGrid,
            gridState,
            "health.album-matrix",
            [
                new("Title", "Track", "Title.Display", 220, 140,
                    HeaderResourceKey: "Health.AlbumMatrix.Track"),
                new("File", "File", "FileName", 210, 130,
                    HeaderResourceKey: "Column.File"),
                new("Disc", "Disc", "DiscNumber.Display", 65, 55,
                    HeaderResourceKey: "Health.AlbumMatrix.Disc"),
                new("TrackNumber", "Track", "TrackNumber.Display", 75, 60,
                    HeaderResourceKey: "Health.AlbumMatrix.TrackNumber"),
                new("TrackTotal", "Track total", "TrackTotal.Display", 85, 65,
                    HeaderResourceKey: "Health.AlbumMatrix.TrackTotal"),
                new("DiscTotal", "Disc total", "DiscTotal.Display", 80, 65,
                    HeaderResourceKey: "Health.AlbumMatrix.DiscTotal"),
                new("Artist", "Artist", "Artist.Display", 180, 120,
                    HeaderResourceKey: "Health.AlbumMatrix.Artist"),
                new("AlbumArtist", "Album artist", "AlbumArtist.Display", 180, 120,
                    HeaderResourceKey: "Health.AlbumMatrix.AlbumArtist"),
                new("Album", "Album", "Album.Display", 210, 140,
                    HeaderResourceKey: "Health.AlbumMatrix.Album"),
                new("Date", "Date", "ReleaseDate.Display", 90, 70,
                    HeaderResourceKey: "Health.AlbumMatrix.Date"),
                new("Reason", "Flag reason", "IssueSummary", 360, 180,
                    HeaderResourceKey: "Health.AlbumMatrix.FlagReason"),
            ]);
        UpdateSetupState();
    }

    private void OnAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        _localization.CultureChanged +=
            OnLocalizationCultureChanged;
        _settings.ConfigurationChanged +=
            OnConfigurationChanged;
        UpdateSetupState();
        // Explicit selector items are materialized after attachment. Reapply
        // availability once that pass completes so a trailing unavailable
        // destination cannot retain its XAML default visibility.
        Dispatcher.UIThread.Post(
            SynchronizeResultNavigation);
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        _localization.CultureChanged -=
            OnLocalizationCultureChanged;
        _settings.ConfigurationChanged -=
            OnConfigurationChanged;
    }

    private void OnConfigurationChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(
            UpdateSetupState);

    private void UpdateSetupState()
    {
        bool canUseHealth =
            _settings.Configuration is not null ||
            _viewModel.HasRuns;
        HealthSetupCard.IsVisible =
            !canUseHealth;
        HealthActionCard.IsVisible =
            canUseHealth;
        HealthResultsHost.IsVisible =
            canUseHealth;
        HealthWorkflowActions.IsVisible =
            canUseHealth;
    }

    private void OnOpenSettings(
        object? sender,
        RoutedEventArgs e) =>
        _navigation.Navigate(
            ShellDestination.Settings);

    private void ApplyResponsiveLayout()
    {
        double width = Bounds.Width;
        if (width <= 0)
            return;

        bool stackActions = width < 720;
        HealthActionLayout.ColumnDefinitions.Clear();
        HealthActionLayout.RowDefinitions.Clear();
        if (!stackActions)
        {
            HealthActionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.25,
                        GridUnitType.Star)));
            HealthActionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            HealthActionLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            for (int index = 0;
                 index < HealthActionLayout.Children.Count;
                 index++)
            {
                Control child =
                    HealthActionLayout.Children[index];
                Grid.SetColumn(child, index);
                Grid.SetRow(child, 0);
            }
        }
        else
        {
            HealthActionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            HealthActionLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            HealthActionLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(12)));
            HealthActionLayout.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            for (int index = 0;
                 index < HealthActionLayout.Children.Count;
                 index++)
            {
                Control child =
                    HealthActionLayout.Children[index];
                Grid.SetColumn(child, 0);
                Grid.SetRow(
                    child,
                    index == 0 ? 0 : 2);
            }
        }

        ApplyResultNavigationLayout();
        ApplyHighCardinalityResultLayouts();
    }

    private void ApplyHighCardinalityResultLayouts()
    {
        ConfigureMasterDetailLayout(
            DuplicateMasterDetailLayout,
            280);
        ConfigureMasterDetailLayout(
            SimilarArtistMasterDetailLayout,
            280);
        ConfigureMasterDetailLayout(
            AlbumMatrixMasterDetailLayout,
            280);
        ConfigureMasterDetailLayout(
            ArtworkRepairMasterDetailLayout,
            260);
    }

    private static void ConfigureMasterDetailLayout(
        Grid layout,
        double masterWidth)
    {
        if (layout.Children.Count < 2)
            return;

        double availableWidth =
            layout.Bounds.Width;
        if (availableWidth <= 0)
            return;

        bool stack =
            availableWidth < 680;
        bool isStacked =
            layout.ColumnDefinitions.Count == 1 &&
            layout.RowDefinitions.Count == 2;
        bool isSideBySide =
            layout.ColumnDefinitions.Count == 2 &&
            layout.RowDefinitions.Count == 1;
        if ((stack && isStacked) ||
            (!stack && isSideBySide))
            return;

        layout.ColumnDefinitions.Clear();
        layout.RowDefinitions.Clear();
        if (stack)
        {
            layout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    GridLength.Star));
            layout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(120)));
            layout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Star));
            layout.ColumnSpacing = 0;
            layout.RowSpacing = 12;
            Grid.SetColumn(
                layout.Children[0],
                0);
            Grid.SetRow(
                layout.Children[0],
                0);
            Grid.SetColumn(
                layout.Children[1],
                0);
            Grid.SetRow(
                layout.Children[1],
                1);
        }
        else
        {
            layout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        masterWidth)));
            layout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    GridLength.Star));
            layout.RowDefinitions.Add(
                new RowDefinition(
                    GridLength.Star));
            layout.ColumnSpacing = 12;
            layout.RowSpacing = 0;
            Grid.SetColumn(
                layout.Children[0],
                0);
            Grid.SetRow(
                layout.Children[0],
                0);
            Grid.SetColumn(
                layout.Children[1],
                1);
            Grid.SetRow(
                layout.Children[1],
                0);
        }
    }

    private void ApplyResultNavigationLayout()
    {
        double contentWidth =
            HealthResultNavigationLayout.Bounds.Width;
        if (contentWidth <= 0)
            return;

        bool useCompactPicker =
            contentWidth < 760;
        HealthResultPickerHost.IsVisible =
            useCompactPicker;
        HealthResultNavigationRail.IsVisible =
            !useCompactPicker;
        SynchronizeResultNavigation();
    }

    private void OnHealthResultNavigationChanged(
        object? sender,
        SelectionChangedEventArgs e) =>
        SelectResultFromNavigation(
            HealthResultNavigation.SelectedItem);

    private void OnHealthResultPickerChanged(
        object? sender,
        SelectionChangedEventArgs e) =>
        SelectResultFromNavigation(
            HealthResultPicker.SelectedItem);

    private void SelectResultFromNavigation(
        object? item)
    {
        if (_synchronizingResultNavigation ||
            DataContext is not AnalyzerViewModel viewModel ||
            item is not Control control ||
            !TryGetResultIndex(
                control,
                out int resultIndex))
            return;

        if (viewModel.ActiveResultIndex != resultIndex)
            viewModel.ActiveResultIndex =
                resultIndex;
        SynchronizeResultNavigation();
    }

    private void SynchronizeResultNavigation()
    {
        if (DataContext is not AnalyzerViewModel viewModel)
            return;

        _synchronizingResultNavigation = true;
        try
        {
            UpdateResultNavigationAvailability(
                viewModel);
            HealthResultNavigation.SelectedItem =
                HealthResultNavigation.Items
                    .OfType<ListBoxItem>()
                    .FirstOrDefault(
                        item => HasResultIndex(
                            item,
                            viewModel.ActiveResultIndex));
            HealthResultPicker.SelectedItem =
                HealthResultPicker.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(
                        item => HasResultIndex(
                            item,
                            viewModel.ActiveResultIndex));
            // Selection controls may temporarily reveal their former selected
            // container while moving selection. Reassert availability after
            // both selectors have synchronized.
            UpdateResultNavigationAvailability(
                viewModel);
        }
        finally
        {
            _synchronizingResultNavigation = false;
        }
    }

    private void UpdateResultNavigationAvailability(
        AnalyzerViewModel viewModel)
    {
        UpdateResultNavigationLabels();
        bool hasRepairDestination =
            viewModel.HasRepairSection ||
            viewModel.HasRepresentationSection ||
            viewModel.HasItlRepairSection ||
            viewModel.HasArtworkRepairSection;
        int groupIndex = 0;
        ListBoxItem[] navigationItems =
        [
            .. _allResultNavigationItems.Where(item =>
            {
                if (TryGetResultIndex(
                        item,
                        out int resultIndex))
                    return IsResultAvailable(
                        viewModel,
                        resultIndex);

                bool includeGroup =
                    groupIndex == 0 ||
                    hasRepairDestination;
                groupIndex++;
                return includeGroup;
            }),
        ];
        ComboBoxItem[] pickerItems =
        [
            .. _allResultPickerItems.Where(item =>
                TryGetResultIndex(
                    item,
                    out int resultIndex) &&
                IsResultAvailable(
                    viewModel,
                    resultIndex)),
        ];

        if (!HealthResultNavigation.Items
                .OfType<ListBoxItem>()
                .SequenceEqual(
                    navigationItems))
            HealthResultNavigation.ItemsSource =
                navigationItems;
        if (!HealthResultPicker.Items
                .OfType<ComboBoxItem>()
                .SequenceEqual(
                    pickerItems))
            HealthResultPicker.ItemsSource =
                pickerItems;
    }

    private void UpdateResultNavigationLabels()
    {
        int groupIndex = 0;
        foreach (ListBoxItem item in
                 _allResultNavigationItems)
        {
            string key;
            if (TryGetResultIndex(
                    item,
                    out int resultIndex))
            {
                key = ResultLabelKey(
                    resultIndex);
                AutomationProperties.SetName(
                    item,
                    _localization.Get(key));
            }
            else
            {
                key = groupIndex++ == 0
                    ? "Health.Audit.Title"
                    : "Health.Repair.Title";
            }

            if (item.Content is TextBlock text)
                text.Text =
                    _localization.Get(key);
        }

        foreach (ComboBoxItem item in
                 _allResultPickerItems)
        {
            if (!TryGetResultIndex(
                    item,
                    out int resultIndex))
                continue;
            string label = _localization.Get(
                ResultLabelKey(
                    resultIndex));
            item.Content = label;
            AutomationProperties.SetName(
                item,
                label);
        }
    }

    private static string ResultLabelKey(
        int resultIndex) =>
        resultIndex switch
        {
            0 => "Health.Tab.Findings",
            1 => "Health.Tab.Duplicates",
            2 => "Health.Tab.SimilarArtists",
            3 => "Health.Tab.MetadataRepairs",
            4 => "Health.Tab.FileRepairs",
            5 => "Health.Tab.Conflicts",
            6 => "Health.Tab.AlbumMatrix",
            7 => "Health.Tab.ItunesRepairs",
            8 => "Health.Tab.ArtworkRepairs",
            _ => throw new ArgumentOutOfRangeException(
                nameof(resultIndex)),
        };

    private static bool IsResultAvailable(
        AnalyzerViewModel viewModel,
        int resultIndex) =>
        resultIndex switch
        {
            0 => true,
            1 => viewModel.HasDuplicateSection,
            2 => viewModel.HasArtistSection,
            3 => viewModel.HasRepairSection,
            4 => viewModel.HasRepresentationSection,
            5 => viewModel.HasConflictSection,
            6 => viewModel.HasMatrixSection,
            7 => viewModel.HasItlRepairSection,
            8 => viewModel.HasArtworkRepairSection,
            _ => false,
        };

    private static bool HasResultIndex(
        Control control,
        int expectedIndex) =>
        TryGetResultIndex(
            control,
            out int actualIndex) &&
        actualIndex == expectedIndex;

    private static bool TryGetResultIndex(
        Control control,
        out int resultIndex) =>
        int.TryParse(
            control.Tag?.ToString(),
            out resultIndex);

    private void UpdateRootDispositions()
    {
        _updatingRootDisposition = true;
        FindingRootDisposition.SelectedValue = AnalysisProblemGroupViewModel.Aggregate(
            _viewModel.FindingGroups.Select(group => group.Disposition));
        RepairRootDisposition.SelectedValue = Aggregate(_viewModel.RepairGroups.Select(group => group.Disposition));
        ArtistRootDisposition.SelectedValue = Aggregate(_viewModel.ArtistGroups.Select(group => group.Disposition));
        RepresentationRootDisposition.SelectedValue = Aggregate(_viewModel.RepresentationActionGroups.Select(group => group.Disposition));
        ItlRepairRootDisposition.SelectedValue = Aggregate(_viewModel.ItlRepairGroups.Select(group => group.Disposition));
        ArtworkRepairRootDisposition.SelectedValue = Aggregate(
            _viewModel.ArtworkRepairItems.Select(item => item.Disposition));
        _updatingRootDisposition = false;
    }

    private void OnFindingRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            FindingRootDisposition.SelectedValue is not AnalysisFindingDisposition value ||
            value == AnalysisFindingDisposition.Mixed)
            return;
        foreach (AnalysisProblemGroupViewModel group in _viewModel.FindingGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || RepairRootDisposition.SelectedValue is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (AnalysisRepairCategoryGroupViewModel group in _viewModel.RepairGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnArtistRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            ArtistRootDisposition.SelectedValue is not AnalysisRepairDisposition value ||
            value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (ArtistGroupViewModel group in _viewModel.ArtistGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnRepresentationRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || RepresentationRootDisposition.SelectedValue is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (RepresentationRepairCategoryGroupViewModel group in _viewModel.RepresentationActionGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnItlRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition || ItlRepairRootDisposition.SelectedValue is not AnalysisRepairDisposition value || value == AnalysisRepairDisposition.Mixed)
            return;
        foreach (ItlMetadataRepairCategoryGroupViewModel group in _viewModel.ItlRepairGroups)
            group.Disposition = value;
        UpdateRootDispositions();
    }

    private void OnArtworkRepairRootDispositionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingRootDisposition ||
            ArtworkRepairRootDisposition.SelectedValue is not AnalysisRepairDisposition value ||
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
            _localization.Get("Health.Artwork.Action.SelectFirst"),
            node,
            ArtworkCandidateSelectionRule.First);
        var resolution = CreateArtworkSelectionMenuItem(
            _localization.Get("Health.Artwork.Action.SelectHighestResolution"),
            node,
            ArtworkCandidateSelectionRule.HighestResolution);
        var size = CreateArtworkSelectionMenuItem(
            _localization.Get("Health.Artwork.Action.SelectLargestFile"),
            node,
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
            _viewModel.ReportAutomaticArtworkSelection(activated);
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
        ContextMenu? menu = HealthResultContextMenuFactory.CreateForSource(
            e.Source,
            _platform,
            OpenInWorkbenchAsync,
            _localization);
        if (menu is null)
            return;

        Control target = e.Source as Control ?? (Control)sender!;
        menu.Open(target);
        e.Handled = true;
    }

    private async Task OpenInWorkbenchAsync(string path)
    {
        await _workbench.AddSourcesAsync([path]);
        _navigation.Navigate(ShellDestination.Workbench);
    }

    private ComboBox CreateGridChoice(
        double minimumWidth,
        string accessibleNameKey)
    {
        var choice = new ComboBox
        {
            MinWidth = minimumWidth,
            Tag = accessibleNameKey,
        };
        choice.Classes.Add("app");
        AutomationProperties.SetName(
            choice,
            _localization.Get(
                accessibleNameKey));
        return choice;
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        foreach (ComboBox choice in
                 this.GetVisualDescendants()
                     .OfType<ComboBox>())
        {
            if (choice.Tag is not string key)
                continue;
            AutomationProperties.SetName(
                choice,
                _localization.Get(key));
        }
        SynchronizeResultNavigation();
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
    public static ContextMenu? CreateForSource(
        object? source,
        IPlatformService platform,
        Func<string, Task>? openInWorkbench = null,
        ILocalizationService? localization = null)
    {
        object? result = (source as global::Avalonia.StyledElement)?.DataContext;
        return HealthResultPathResolver.TryGetPath(result, out string path)
            ? Create(
                path,
                platform,
                openInWorkbench,
                localization)
            : null;
    }

    public static ContextMenu Create(
        string path,
        IPlatformService platform,
        Func<string, Task>? openInWorkbench = null,
        ILocalizationService? localization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(platform);

        var copy = new MenuItem
        {
            Header = Localize(
                "Health.Context.CopyPath",
                localization),
        };
        copy.Click += async (_, _) => await platform.CopyTextAsync(path);

        var items = new List<MenuItem> { copy };
        if (openInWorkbench is not null)
        {
            var workbench = new MenuItem
            {
                Header = Localize(
                    "Health.Context.OpenWorkbench",
                    localization),
            };
            workbench.Click += async (_, _) => await openInWorkbench(path);
            items.Add(workbench);
        }

        var reveal = new MenuItem
        {
            Header = Localize(
                "Health.Context.RevealExplorer",
                localization),
        };
        reveal.Click += (_, _) => platform.RevealFile(path);
        items.Add(reveal);

        return new ContextMenu { ItemsSource = items };
    }

    private static string Localize(
        string key,
        ILocalizationService? localization) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);
}
