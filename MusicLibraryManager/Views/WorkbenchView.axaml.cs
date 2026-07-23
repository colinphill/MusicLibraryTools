using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
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
