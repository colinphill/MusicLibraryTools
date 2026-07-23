using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Media;
using global::Avalonia.Platform.Storage;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class WorkbenchView : UserControl
{
    private readonly WorkbenchViewModel _viewModel;

    public WorkbenchView()
    {
        InitializeComponent();
        _viewModel = App.GetService<WorkbenchViewModel>();
        DataContext = _viewModel;
        WorkbenchGrid.IsReadOnly = false;
        WorkbenchGrid.ConfigureColumns(
        [
            new("File", "File", "FileName", 220, 140),
            new("Title", "Title", "Title", 220, 120, Editable: true),
            new("Artist", "Artist", "Artist", 190, 110, Editable: true),
            new("AlbumArtist", "Album artist", "AlbumArtist", 190, 110, Editable: true),
            new("Album", "Album", "Album", 210, 120, Editable: true),
            new("Genre", "Genre", "Genre", 130, 90, Editable: true),
            new("Composer", "Composer", "Composer", 170, 100, Editable: true),
            new("Date", "Date", "Date", 90, 70, Editable: true),
            new("Track", "Track", "Track", 75, 60, Editable: true),
            new("Disc", "Disc", "Disc", 70, 60, Editable: true),
            new("Format", "Format", "Format", 80, 65),
            new("Duration", "Duration", "Duration", 90, 75),
            new("Bitrate", "Bitrate", "Bitrate", 100, 75),
        ]);
        PreviewGrid.ConfigureColumns(
        [
            new("File", "File", "File", 220, 140),
            new("Field", "Field", "Field", 150, 100),
            new("Before", "Before", "Before", 320, 180),
            new("After", "After", "After", 320, 180),
        ]);
        MetadataFieldsGrid.ConfigureColumns(
        [
            new("Name", "Field", "Name", 210, 120),
            new("Kind", "Kind", "Kind", 90, 70),
            new("Layers", "Tag layers", "Layers", 150, 100),
            new("Value", "Values", "DisplayValue", 340, 180),
        ]);
        AudioDiscoveryGrid.ConfigureColumns(
        [
            new("File", "File", "File", 220, 130),
            new("Duration", "Duration", "Duration", 90, 70),
            new("Confidence", "Confidence", "Confidence", 105, 85),
            new("AcoustID", "AcoustID", "AcoustId", 285, 180),
            new("MusicBrainz", "MusicBrainz recording IDs",
                "MusicBrainzRecordingIds", 390, 220),
            new("Status", "Status", "Status", 280, 160),
        ]);
        ReleaseDiscoveryGrid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 240, 140),
            new("Artist", "Artist credit", "Artist", 190, 110),
            new("Date", "Date", "Date", 100, 75),
            new("Country", "Country", "Country", 80, 65),
            new("Status", "Status", "Status", 90, 70),
            new("Label", "Label", "Label", 170, 100),
            new("Catalog", "Catalog no.", "CatalogNumber", 120, 85),
            new("Formats", "Formats", "Formats", 140, 90),
            new("Position", "Matched position", "MatchedTrackPositions", 140, 95),
            new("Tracks", "Tracks", "TrackCount", 75, 60),
            new("ReleaseID", "MusicBrainz release ID", "ReleaseId", 280, 180),
        ]);
        ConfigureDiscogsGrid(DiscogsDiscoveryGrid);
        ConfigureReleaseTrackMappingGrid(ReleaseTrackMappingGrid);
        ConfigureReleaseArtworkGrid(ReleaseArtworkGrid);
    }

    private static void ConfigureDiscogsGrid(AppDataGrid grid) =>
        grid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 220, 130),
            new("Artist", "Artist credit", "Artist", 180, 105),
            new("Year", "Year", "Year", 75, 60),
            new("Country", "Country", "Country", 75, 62),
            new("Labels", "Labels", "Labels", 160, 95),
            new("Catalog", "Catalog no.", "CatalogNumbers", 130, 85),
            new("Formats", "Formats", "Formats", 150, 90),
            new("Genres", "Genres", "Genres", 130, 85),
            new("Styles", "Styles", "Styles", 140, 90),
            new("Tracks", "Tracks", "TrackCount", 70, 55),
            new("Source", "Source", "Source", 100, 75),
            new("ReleaseID", "Discogs release ID", "ReleaseId", 150, 100),
        ]);

    private static void ConfigureReleaseArtworkGrid(AppDataGrid grid)
    {
        var thumbnailTemplate = new FuncDataTemplate<CoverArtCandidateRow>(
            (_, _) =>
            {
                var image = new Image
                {
                    Width = 72,
                    Height = 72,
                    Stretch = Stretch.Uniform,
                };
                image.Bind(Image.SourceProperty,
                    new Binding(nameof(CoverArtCandidateRow.ThumbnailSource)));
                return image;
            });
        grid.RowHeight = 82;
        grid.ConfigureColumns(
        [
            new("Thumbnail", "Preview", null, 90, 80,
                CellTemplate: thumbnailTemplate, Sortable: false),
            new("Roles", "Types", "Roles", 170, 100),
            new("Front", "Front", "Front", 70, 55),
            new("Back", "Back", "Back", 70, 55),
            new("Approved", "Approved", "Approved", 85, 65),
            new("Comment", "Comment", "Comment", 240, 130),
            new("Status", "Thumbnail", "ThumbnailStatus", 150, 90),
            new("Id", "Archive ID", "Id", 150, 100),
        ]);
    }

    private static void ConfigureReleaseTrackMappingGrid(AppDataGrid grid)
    {
        var includeTemplate = new FuncDataTemplate<MusicBrainzTrackMappingRow>(
            (_, _) =>
            {
                var check = new CheckBox();
                check.Bind(CheckBox.IsCheckedProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.IsIncluded))
                    {
                        Mode = BindingMode.TwoWay,
                    });
                return check;
            });
        var trackTemplate = new FuncDataTemplate<MusicBrainzTrackMappingRow>(
            (_, _) =>
            {
                var combo = new ComboBox
                {
                    DisplayMemberBinding =
                        new Binding(nameof(MusicBrainzTrackChoice.Display)),
                };
                combo.Bind(ItemsControl.ItemsSourceProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.TrackChoices)));
                combo.Bind(ComboBox.SelectedItemProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.SelectedTrack))
                    {
                        Mode = BindingMode.TwoWay,
                    });
                return combo;
            });
        grid.ConfigureColumns(
        [
            new("Include", "Use", null, 60, 50,
                CellTemplate: includeTemplate, Sortable: false),
            new("File", "File", "File", 210, 120),
            new("Track", "Release track", null, 390, 220,
                CellTemplate: trackTemplate, Sortable: false),
            new("Confidence", "Confidence", "Confidence", 110, 80),
            new("Status", "Reason", "Status", 310, 170),
        ]);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Any() == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        string[] paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray() ?? [];
        if (paths.Length > 0)
            await _viewModel.AddSourcesAsync(paths);
        e.Handled = true;
    }
}
