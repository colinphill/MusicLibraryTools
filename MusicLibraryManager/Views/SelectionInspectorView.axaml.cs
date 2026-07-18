using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class SelectionInspectorView : UserControl
{
    private readonly SelectionInspectorViewModel _viewModel;

    public SelectionInspectorView()
    {
        InitializeComponent();
        _viewModel = App.GetService<SelectionInspectorViewModel>();
        DataContext = _viewModel;
    }

    private async void OnReplaceArtwork(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArtworkPreviewItem item })
            await _viewModel.ReplaceArtworkItemAsync(item);
    }

    private void OnRemoveArtwork(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArtworkPreviewItem item })
            _viewModel.RemoveArtworkItem(item);
    }
}
