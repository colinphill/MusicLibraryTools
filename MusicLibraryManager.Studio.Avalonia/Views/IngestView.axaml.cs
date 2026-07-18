using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform.Storage;
using MusicLibrary.App.ViewModels;
using MusicLibraryManager.Studio.Avalonia.Controls;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class IngestView : UserControl
{
    private readonly IngestViewModel _viewModel;

    public IngestView()
    {
        InitializeComponent();
        _viewModel = App.GetService<IngestViewModel>();
        DataContext = _viewModel;
        PreviewGrid.ConfigureColumns([
            new StudioGridColumnDefinition("Type", "Type", "SourceType", 140, 100),
            new StudioGridColumnDefinition("Source", "Source", "Source", 360, 220),
            new StudioGridColumnDefinition("Plan", "Plan", "Summary", 420, 240),
            new StudioGridColumnDefinition("Progress", "Progress", "ProgressText", 160, 110),
        ]);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Any(item => item is IStorageFolder) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageFolder? folder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().FirstOrDefault();
        if (folder is not null)
            _viewModel.SetDroppedSource(folder.Path.LocalPath);
        e.Handled = true;
    }
}
