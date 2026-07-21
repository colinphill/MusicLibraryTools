using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform.Storage;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;

namespace MusicLibraryManager.Views;

public partial class IngestView : UserControl
{
    private readonly IngestViewModel _viewModel;

    public IngestView()
    {
        InitializeComponent();
        _viewModel = App.GetService<IngestViewModel>();
        DataContext = _viewModel;
        PreviewGrid.ConfigureColumns([
            new AppGridColumnDefinition("Type", "Type", "SourceType", 140, 100),
            new AppGridColumnDefinition("Source", "Source", "Source", 360, 220),
            new AppGridColumnDefinition("Plan", "Plan", "Summary", 420, 240),
            new AppGridColumnDefinition("Progress", "Progress", "ProgressText", 160, 110),
        ]);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        bool compactHeight = Bounds.Height <= 560;
        SetupCard.Padding = new global::Avalonia.Thickness(compactHeight ? 12 : 16);
        SetupPanel.Spacing = compactHeight ? 6 : 10;
        PreviewEmptyDescription.IsVisible = !compactHeight;
        HistoryEmptyDescription.IsVisible = !compactHeight;
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
