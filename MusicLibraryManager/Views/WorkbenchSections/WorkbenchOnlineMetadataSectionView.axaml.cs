using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchOnlineMetadataSectionView :
    UserControl
{
    private readonly ILocalizationService _localization;

    public WorkbenchOnlineMetadataSectionView()
    {
        InitializeComponent();
        _localization = App.GetService<ILocalizationService>();
        ConfigureColumns();
        AttachedToVisualTree += (_, _) =>
            _localization.CultureChanged += OnCultureChanged;
        DetachedFromVisualTree += (_, _) =>
            _localization.CultureChanged -= OnCultureChanged;
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
            new("File", L("Workbench.Grid.Header.File"), "File", 220, 130),
            new("Duration", L("Workbench.Grid.Header.Duration"), "Duration", 90, 70),
            new("Confidence", L("Workbench.Grid.Header.Confidence"), "Confidence", 105, 85),
            new("AcoustID", "AcoustID", "AcoustId", 285, 180),
            new(
                "MusicBrainz",
                L("Workbench.Grid.Header.MusicBrainzRecordingIds"),
                "MusicBrainzRecordingIds",
                390,
                220),
            new(
                "Status",
                L("Workbench.Grid.Header.Status"),
                "Status",
                280,
                160,
                CellTemplate: audioStatusTemplate),
        ]);
        ReleaseDiscoveryGrid.ConfigureColumns(
        [
            new("Title", L("Workbench.Grid.Header.Release"), "Title", 240, 140),
            new("Artist", L("Workbench.Grid.Header.ArtistCredit"), "Artist", 190, 110),
            new("Date", L("Workbench.Grid.Header.Date"), "Date", 100, 75),
            new("Country", L("Workbench.Grid.Header.Country"), "Country", 80, 65),
            new("Status", L("Workbench.Grid.Header.Status"), "Status", 90, 70),
            new("Label", L("Workbench.Grid.Header.Label"), "Label", 170, 100),
            new("Catalog", L("Workbench.Grid.Header.CatalogNumber"), "CatalogNumber", 120, 85),
            new("Formats", L("Workbench.Grid.Header.Formats"), "Formats", 140, 90),
            new(
                "Position",
                L("Workbench.Grid.Header.MatchedPosition"),
                "MatchedTrackPositions",
                140,
                95),
            new("Tracks", L("Workbench.Grid.Header.Tracks"), "TrackCount", 75, 60),
            new(
                "ReleaseID",
                L("Workbench.Grid.Header.MusicBrainzReleaseId"),
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
            Bounds.Height < 430;
        DiscoverySupportingText.IsVisible =
            !compactHeight;
        SearchSupportingText.IsVisible =
            !compactHeight;
        ResultsSupportingText.IsVisible =
            !compactHeight;
        AudioResultSupportingText.IsVisible =
            !compactHeight;
        DiscoveryCard.Padding =
            new Thickness(compactHeight ? 7 : 10);
        SearchCard.Padding =
            new Thickness(compactHeight ? 7 : 10);
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

    private void ConfigureDiscogsGrid() =>
        DiscogsDiscoveryGrid.ConfigureColumns(
        [
            new("Title", L("Workbench.Grid.Header.Release"), "Title", 220, 130),
            new("Artist", L("Workbench.Grid.Header.ArtistCredit"), "Artist", 180, 105),
            new("Year", L("Workbench.Grid.Header.Year"), "Year", 75, 60),
            new("Country", L("Workbench.Grid.Header.Country"), "Country", 75, 62),
            new("Labels", L("Workbench.Grid.Header.Labels"), "Labels", 160, 95),
            new("Catalog", L("Workbench.Grid.Header.CatalogNumber"), "CatalogNumbers", 130, 85),
            new("Formats", L("Workbench.Grid.Header.Formats"), "Formats", 150, 90),
            new("Genres", L("Workbench.Grid.Header.Genres"), "Genres", 130, 85),
            new("Styles", L("Workbench.Grid.Header.Styles"), "Styles", 140, 90),
            new("Tracks", L("Workbench.Grid.Header.Tracks"), "TrackCount", 70, 55),
            new("Source", L("Workbench.Grid.Header.Source"), "Source", 100, 75),
            new(
                "ReleaseID",
                L("Workbench.Grid.Header.DiscogsReleaseId"),
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
                    var check = new CheckBox();
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
                    var combo = new ComboBox
                    {
                        DisplayMemberBinding =
                            new Binding(
                                nameof(
                                    DiscogsTrackChoice
                                        .Display)),
                    };
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
                L("Workbench.Grid.Header.Use"),
                null,
                58,
                48,
                CellTemplate: includeTemplate,
                Sortable: false),
            new("File", L("Workbench.Grid.Header.File"), "File", 180, 110),
            new(
                "Track",
                L("Workbench.Grid.Header.DiscogsTrack"),
                null,
                330,
                190,
                CellTemplate: trackTemplate,
                Sortable: false),
            new("Position", L("Workbench.Grid.Header.Position"), "Position", 80, 62),
            new(
                "Confidence",
                L("Workbench.Grid.Header.Confidence"),
                "Confidence",
                100,
                76),
            new(
                "Status",
                L("Workbench.Grid.Header.Reason"),
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
                    var check = new CheckBox();
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
                    var combo = new ComboBox
                    {
                        DisplayMemberBinding =
                            new Binding(
                                nameof(
                                    MusicBrainzTrackChoice
                                        .Display)),
                    };
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
                L("Workbench.Grid.Header.Use"),
                null,
                60,
                50,
                CellTemplate: includeTemplate,
                Sortable: false),
            new("File", L("Workbench.Grid.Header.File"), "File", 210, 120),
            new(
                "Track",
                L("Workbench.Grid.Header.ReleaseTrack"),
                null,
                390,
                220,
                CellTemplate: trackTemplate,
                Sortable: false),
            new(
                "Confidence",
                L("Workbench.Grid.Header.Confidence"),
                "Confidence",
                110,
                80),
            new(
                "Status",
                L("Workbench.Grid.Header.Reason"),
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
                L("Workbench.Grid.Header.Preview"),
                null,
                90,
                80,
                CellTemplate: thumbnailTemplate,
                Sortable: false),
            new("Roles", L("Workbench.Grid.Header.Types"), "Roles", 170, 100),
            new("Front", L("Workbench.Grid.Header.Front"), "Front", 70, 55),
            new("Back", L("Workbench.Grid.Header.Back"), "Back", 70, 55),
            new("Approved", L("Workbench.Grid.Header.Approved"), "Approved", 85, 65),
            new("Comment", L("Workbench.Grid.Header.Comment"), "Comment", 240, 130),
            new(
                "Status",
                L("Workbench.Grid.Header.Thumbnail"),
                "ThumbnailStatus",
                150,
                90,
                CellTemplate: statusTemplate),
            new("Id", L("Workbench.Grid.Header.ArchiveId"), "Id", 150, 100),
        ]);
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
    }
}
