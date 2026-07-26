using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Collections.Specialized;
using System.ComponentModel;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchOnlineMetadataSectionView :
    UserControl
{
    private readonly ILocalizationService _localization;
    private readonly WorkbenchViewModel _viewModel;
    private bool _compactHeight;

    public WorkbenchOnlineMetadataSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        _viewModel =
            App.GetService<WorkbenchViewModel>();
        ConfigureColumns();
        UpdateStepSummaries();
        AttachedToVisualTree += (_, _) =>
        {
            _localization.CultureChanged += OnCultureChanged;
            _viewModel.PropertyChanged +=
                OnViewModelPropertyChanged;
            _viewModel.DiscogsTrackMappings
                .CollectionChanged +=
                OnDiscogsTrackMappingsChanged;
            UpdateStepSummaries();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _localization.CultureChanged -= OnCultureChanged;
            _viewModel.PropertyChanged -=
                OnViewModelPropertyChanged;
            _viewModel.DiscogsTrackMappings
                .CollectionChanged -=
                OnDiscogsTrackMappingsChanged;
        };
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
    }

    private void ConfigureColumns()
    {
        IDataTemplate audioStatusTemplate =
            DiagnosticTextTemplate<AudioDiscoveryRow>(
                nameof(AudioDiscoveryRow.Status),
                nameof(AudioDiscoveryRow.DiagnosticDetail));
        AudioDiscoveryGrid.ConfigureColumns(
        [
            new("File", L("Column.File"), "File", 220, 130),
            new("Duration", L("Column.Duration"), "Duration", 90, 70),
            new("Confidence", L("Column.Confidence"), "Confidence", 105, 85),
            new("AcoustID", L("Column.AcoustId"), "AcoustId", 285, 180),
            new(
                "MusicBrainz",
                L("Column.MusicBrainzRecordingIds"),
                "MusicBrainzRecordingIds",
                390,
                220),
            new(
                "Status",
                L("Column.Status"),
                "Status",
                280,
                160,
                CellTemplate: audioStatusTemplate),
        ]);
        ReleaseDiscoveryGrid.ConfigureColumns(
        [
            new("Title", L("Column.Release"), "Title", 240, 140),
            new("Artist", L("Column.ArtistCredit"), "Artist", 190, 110),
            new("Date", L("Column.Date"), "Date", 100, 75),
            new("Country", L("Column.Country"), "Country", 80, 65),
            new("Status", L("Column.Status"), "Status", 90, 70),
            new("Label", L("Column.Label"), "Label", 170, 100),
            new("Catalog", L("Column.CatalogNumber"), "CatalogNumber", 120, 85),
            new("Formats", L("Column.Formats"), "Formats", 140, 90),
            new(
                "Position",
                L("Column.MatchedPosition"),
                "MatchedTrackPositions",
                140,
                95),
            new("Tracks", L("Column.Tracks"), "TrackCount", 75, 60),
            new(
                "ReleaseID",
                L("Column.MusicBrainzReleaseId"),
                "ReleaseId",
                280,
                180),
        ]);
        ConfigureDiscogsGrid();
        ConfigureDiscogsTrackMappingGrid();
        ConfigureReleaseTrackMappingGrid();
        ConfigureReleaseArtworkGrid();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 620;
        _compactHeight = compactHeight;
        DiscoverySupportingText.IsVisible =
            !compactHeight;
        SearchSupportingText.IsVisible =
            !compactHeight;
        ResultsSupportingText.IsVisible =
            !compactHeight;
        AudioResultSupportingText.IsVisible =
            !compactHeight;
        ArtworkResultSupportingText.IsVisible =
            !compactHeight;
        OnlineMetadataResultsHeader.IsVisible =
            !compactHeight;
        OnlineMetadataStepScroll.MaxHeight =
            compactHeight
                ? Math.Clamp(
                    Bounds.Height * .34,
                    120,
                    190)
                : Math.Clamp(
                    Bounds.Height * .42,
                    180,
                    340);
        DiscoveryCard.Padding =
            new Thickness(compactHeight ? 8 : 12);
        SearchCard.Padding =
            new Thickness(compactHeight ? 8 : 12);
        UpdateStepLayout();
        ArtworkEditorLayout.ColumnDefinitions.Clear();
        ArtworkEditorLayout.RowDefinitions.Clear();
        if (narrow)
        {
            ArtworkEditorLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            ArtworkEditorLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(190)));
            ArtworkEditorLayout.RowDefinitions.Add(
                new RowDefinition(new GridLength(10)));
            ArtworkEditorLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(ArtworkEditorScroll, 0);
            Grid.SetRow(ArtworkEditorScroll, 2);
        }
        else
        {
            ArtworkEditorLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.1,
                        GridUnitType.Star)));
            ArtworkEditorLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(12)));
            ArtworkEditorLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.9,
                        GridUnitType.Star)));
            ArtworkEditorLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(ArtworkEditorScroll, 2);
            Grid.SetRow(ArtworkEditorScroll, 0);
        }
    }

    private void OnDiscoveryStepExpanded(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e)
    {
        if (SearchStep.IsExpanded)
            SearchStep.IsExpanded = false;
        UpdateStepSummaries();
    }

    private void OnSearchStepExpanded(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e)
    {
        if (DiscoveryStep.IsExpanded)
            DiscoveryStep.IsExpanded = false;
        UpdateStepSummaries();
    }

    private void OnStepCollapsed(
        object? sender,
        global::Avalonia.Interactivity
            .RoutedEventArgs e) =>
        UpdateStepSummaries();

    private void UpdateStepSummaries()
    {
        UpdateStepLayout();
        DiscoveryStepSummary.IsVisible =
            _viewModel
                .HasCompletedOnlineDiscovery &&
            !DiscoveryStep.IsExpanded;
        SearchStepSummary.IsVisible =
            _viewModel
                .HasCompletedOnlineSearch &&
            !SearchStep.IsExpanded;
        UpdateCurrentAction();
    }

    private void UpdateStepLayout()
    {
        OnlineMetadataStepPanel.Orientation =
            _compactHeight &&
            !DiscoveryStep.IsExpanded &&
            !SearchStep.IsExpanded
                ? global::Avalonia.Layout
                    .Orientation.Horizontal
                : global::Avalonia.Layout
                    .Orientation.Vertical;
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
                nameof(
                    WorkbenchViewModel
                        .HasCompletedOnlineDiscovery) &&
            _viewModel
                .HasCompletedOnlineDiscovery)
        {
            DiscoveryStep.IsExpanded = false;
            SearchStep.IsExpanded = false;
        }
        else if (e.PropertyName ==
                     nameof(
                         WorkbenchViewModel
                             .HasCompletedOnlineSearch) &&
                 _viewModel
                     .HasCompletedOnlineSearch)
        {
            SearchStep.IsExpanded = false;
            DiscoveryStep.IsExpanded = false;
        }

        if (e.PropertyName is
            nameof(
                WorkbenchViewModel
                    .HasCompletedOnlineDiscovery) or
            nameof(
                WorkbenchViewModel
                    .HasCompletedOnlineSearch) or
            nameof(
                WorkbenchViewModel
                    .SelectedOnlineMetadataResultStep) or
            nameof(
                WorkbenchViewModel
                    .SelectedOnlineMetadataProvider))
        {
            UpdateStepSummaries();
        }
    }

    private void OnDiscogsTrackMappingsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        UpdateCurrentAction();

    private void UpdateCurrentAction()
    {
        Button[] actions =
        [
            OnlineMetadataDiscoverButton,
            OnlineMetadataSearchButton,
            OnlineMetadataPreviewAudioButton,
            OnlineMetadataBuildReleaseMappingButton,
            OnlineMetadataBuildDiscogsMappingButton,
            OnlineMetadataPreviewDiscogsMappingButton,
            OnlineMetadataPreviewReleaseMappingButton,
            PreviewStagedArtworkButton,
        ];
        foreach (Button action in actions)
            action.IsVisible = false;

        Button current =
            DiscoveryStep.IsExpanded
                ? OnlineMetadataDiscoverButton
                : SearchStep.IsExpanded
                    ? OnlineMetadataSearchButton
                    : _viewModel
                        .SelectedOnlineMetadataResultStep
                        switch
                        {
                            WorkbenchOnlineMetadataResultStep
                                    .AudioCandidates =>
                                OnlineMetadataPreviewAudioButton,
                            WorkbenchOnlineMetadataResultStep
                                    .MusicBrainzReleases =>
                                OnlineMetadataBuildReleaseMappingButton,
                            WorkbenchOnlineMetadataResultStep
                                    .DiscogsReleases =>
                                _viewModel
                                    .DiscogsTrackMappings
                                    .Count == 0
                                    ? OnlineMetadataBuildDiscogsMappingButton
                                    : OnlineMetadataPreviewDiscogsMappingButton,
                            WorkbenchOnlineMetadataResultStep
                                    .TrackMapping =>
                                _viewModel
                                    .IsDiscogsOnlineMetadataProvider
                                    ? OnlineMetadataPreviewDiscogsMappingButton
                                    : OnlineMetadataPreviewReleaseMappingButton,
                            WorkbenchOnlineMetadataResultStep
                                    .Artwork =>
                                PreviewStagedArtworkButton,
                            _ =>
                                OnlineMetadataPreviewAudioButton,
                        };
        current.IsVisible = true;
    }

    private void ConfigureDiscogsGrid() =>
        DiscogsDiscoveryGrid.ConfigureColumns(
        [
            new("Title", L("Column.Release"), "Title", 220, 130),
            new("Artist", L("Column.ArtistCredit"), "Artist", 180, 105),
            new("Year", L("Column.Year"), "Year", 75, 60),
            new("Country", L("Column.Country"), "Country", 75, 62),
            new("Labels", L("Column.Labels"), "Labels", 160, 95),
            new("Catalog", L("Column.CatalogNumber"), "CatalogNumbers", 130, 85),
            new("Formats", L("Column.Formats"), "Formats", 150, 90),
            new("Genres", L("Column.Genres"), "Genres", 130, 85),
            new("Styles", L("Column.Styles"), "Styles", 140, 90),
            new("Tracks", L("Column.Tracks"), "TrackCount", 70, 55),
            new("Source", L("Column.Source"), "Source", 100, 75),
            new(
                "ReleaseID",
                L("Column.DiscogsReleaseId"),
                "ReleaseId",
                150,
                100),
        ]);

    private void ConfigureDiscogsTrackMappingGrid()
    {
        IDataTemplate statusTemplate =
            DiagnosticTextTemplate<DiscogsTrackMappingRow>(
                nameof(DiscogsTrackMappingRow.Status),
                nameof(DiscogsTrackMappingRow.DiagnosticDetail));
        var includeTemplate =
            new FuncDataTemplate<DiscogsTrackMappingRow>(
                (_, _) =>
                {
                    CheckBox check =
                        CreateMappingIncludeToggle();
                    check.Bind(
                        CheckBox.IsCheckedProperty,
                        new Binding(
                            nameof(
                                DiscogsTrackMappingRow
                                    .IsIncluded))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return check;
                });
        var trackTemplate =
            new FuncDataTemplate<DiscogsTrackMappingRow>(
                (_, _) =>
                {
                    ComboBox combo =
                        CreateMappingTrackChoice();
                    combo.DisplayMemberBinding =
                        new Binding(
                            nameof(
                                DiscogsTrackChoice
                                    .Display));
                    combo.Bind(
                        ItemsControl.ItemsSourceProperty,
                        new Binding(
                            nameof(
                                DiscogsTrackMappingRow
                                    .TrackChoices)));
                    combo.Bind(
                        ComboBox.SelectedItemProperty,
                        new Binding(
                            nameof(
                                DiscogsTrackMappingRow
                                    .SelectedTrack))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return combo;
                });
        DiscogsTrackMappingGrid.ConfigureColumns(
        [
            new(
                "Include",
                L("Workbench.Online.Mapping.IncludeHeader"),
                null,
                58,
                48,
                CellTemplate: includeTemplate,
                Sortable: false),
            new("File", L("Column.File"), "File", 180, 110),
            new(
                "Track",
                L("Column.DiscogsTrack"),
                null,
                330,
                190,
                CellTemplate: trackTemplate,
                Sortable: false),
            new("Position", L("Column.Position"), "Position", 80, 62),
            new(
                "Confidence",
                L("Column.Confidence"),
                "Confidence",
                100,
                76),
            new(
                "Status",
                L("Column.Reason"),
                "Status",
                260,
                150,
                CellTemplate: statusTemplate),
        ]);
    }

    private void ConfigureReleaseTrackMappingGrid()
    {
        IDataTemplate statusTemplate =
            DiagnosticTextTemplate<MusicBrainzTrackMappingRow>(
                nameof(MusicBrainzTrackMappingRow.Status),
                nameof(MusicBrainzTrackMappingRow.DiagnosticDetail));
        var includeTemplate =
            new FuncDataTemplate<
                MusicBrainzTrackMappingRow>(
                (_, _) =>
                {
                    CheckBox check =
                        CreateMappingIncludeToggle();
                    check.Bind(
                        CheckBox.IsCheckedProperty,
                        new Binding(
                            nameof(
                                MusicBrainzTrackMappingRow
                                    .IsIncluded))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return check;
                });
        var trackTemplate =
            new FuncDataTemplate<
                MusicBrainzTrackMappingRow>(
                (_, _) =>
                {
                    ComboBox combo =
                        CreateMappingTrackChoice();
                    combo.DisplayMemberBinding =
                        new Binding(
                            nameof(
                                MusicBrainzTrackChoice
                                    .Display));
                    combo.Bind(
                        ItemsControl.ItemsSourceProperty,
                        new Binding(
                            nameof(
                                MusicBrainzTrackMappingRow
                                    .TrackChoices)));
                    combo.Bind(
                        ComboBox.SelectedItemProperty,
                        new Binding(
                            nameof(
                                MusicBrainzTrackMappingRow
                                    .SelectedTrack))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return combo;
                });
        ReleaseTrackMappingGrid.ConfigureColumns(
        [
            new(
                "Include",
                L("Workbench.Online.Mapping.IncludeHeader"),
                null,
                60,
                50,
                CellTemplate: includeTemplate,
                Sortable: false),
            new("File", L("Column.File"), "File", 210, 120),
            new(
                "Track",
                L("Column.ReleaseTrack"),
                null,
                390,
                220,
                CellTemplate: trackTemplate,
                Sortable: false),
            new(
                "Confidence",
                L("Column.Confidence"),
                "Confidence",
                110,
                80),
            new(
                "Status",
                L("Column.Reason"),
                "Status",
                310,
                170,
                CellTemplate: statusTemplate),
        ]);
    }

    private void ConfigureReleaseArtworkGrid()
    {
        IDataTemplate statusTemplate =
            DiagnosticTextTemplate<CoverArtCandidateRow>(
                nameof(
                    CoverArtCandidateRow
                        .ThumbnailStatus),
                nameof(
                    CoverArtCandidateRow
                        .ThumbnailDiagnosticDetail));
        var thumbnailTemplate =
            new FuncDataTemplate<CoverArtCandidateRow>(
                (_, _) =>
                {
                    var image = new Image
                    {
                        Width = 72,
                        Height = 72,
                        Stretch = Stretch.Uniform,
                    };
                    image.Bind(
                        Image.SourceProperty,
                        new Binding(
                            nameof(
                                CoverArtCandidateRow
                                    .ThumbnailSource)));
                    return image;
                });
        ReleaseArtworkGrid.RowHeight = 82;
        ReleaseArtworkGrid.ConfigureColumns(
        [
            new(
                "Thumbnail",
                L("Column.Preview"),
                null,
                90,
                80,
                CellTemplate: thumbnailTemplate,
                Sortable: false),
            new("Roles", L("Column.ArtworkRoles"), "Roles", 170, 100),
            new("Front", L("Column.Front"), "Front", 70, 55),
            new("Back", L("Column.Back"), "Back", 70, 55),
            new("Approved", L("Column.Approved"), "Approved", 85, 65),
            new("Comment", L("Column.Comment"), "Comment", 240, 130),
            new(
                "Status",
                L("Column.Thumbnail"),
                "ThumbnailStatus",
                150,
                90,
                CellTemplate: statusTemplate),
            new("Id", L("Column.CoverArtArchiveId"), "Id", 150, 100),
        ]);
    }

    private CheckBox CreateMappingIncludeToggle()
    {
        var toggle = new CheckBox
        {
            Tag = "online-mapping-include",
        };
        toggle.Classes.Add("app");
        AutomationProperties.SetName(
            toggle,
            _localization.Get(
                "Workbench.Online.Mapping.IncludeAutomation"));
        return toggle;
    }

    private ComboBox CreateMappingTrackChoice()
    {
        var choice = new ComboBox
        {
            Tag = "online-mapping-track",
        };
        choice.Classes.Add("app");
        AutomationProperties.SetName(
            choice,
            _localization.Get(
                "Workbench.Online.TrackMapping"));
        return choice;
    }

    private void RefreshGeneratedControlAccessibility()
    {
        string includeName =
            _localization.Get(
                "Workbench.Online.Mapping.IncludeAutomation");
        string trackName =
            _localization.Get(
                "Workbench.Online.TrackMapping");
        foreach (Control control in
                 DiscogsTrackMappingGrid
                     .GetVisualDescendants()
                     .Concat(
                         ReleaseTrackMappingGrid
                             .GetVisualDescendants())
                     .OfType<Control>())
        {
            if (string.Equals(
                    control.Tag as string,
                    "online-mapping-include",
                    StringComparison.Ordinal))
                AutomationProperties.SetName(
                    control,
                    includeName);
            else if (string.Equals(
                         control.Tag as string,
                         "online-mapping-track",
                         StringComparison.Ordinal))
                AutomationProperties.SetName(
                    control,
                    trackName);
        }
    }

    private LocalizedGridHeader L(string key) =>
        new(_localization.Get(key), key);

    private static IDataTemplate DiagnosticTextTemplate<T>(
        string textProperty,
        string diagnosticDetailProperty)
        where T : class =>
        new FuncDataTemplate<T>(
            (_, _) =>
            {
                var text = new TextBlock
                {
                    TextWrapping =
                        TextWrapping.Wrap,
                };
                text.Bind(
                    TextBlock.TextProperty,
                    new Binding(textProperty));
                text.Bind(
                    ToolTip.TipProperty,
                    new Binding(
                        diagnosticDetailProperty));
                return text;
            });

    private void OnCultureChanged(
        object? sender,
        EventArgs e)
    {
        AudioDiscoveryGrid.RefreshLocalizedHeaders();
        ReleaseDiscoveryGrid.RefreshLocalizedHeaders();
        DiscogsDiscoveryGrid.RefreshLocalizedHeaders();
        DiscogsTrackMappingGrid.RefreshLocalizedHeaders();
        ReleaseTrackMappingGrid.RefreshLocalizedHeaders();
        ReleaseArtworkGrid.RefreshLocalizedHeaders();
        RefreshGeneratedControlAccessibility();
    }
}
