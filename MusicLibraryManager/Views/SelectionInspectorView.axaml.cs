using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class SelectionInspectorView : UserControl
{
    private readonly SelectionInspectorViewModel _viewModel;
    private ArtworkPreviewWindow? _artworkPreview;
    private ArtworkPreviewItem? _previewedArtwork;

    public SelectionInspectorView()
    {
        InitializeComponent();
        _viewModel = App.GetService<SelectionInspectorViewModel>();
        DataContext = _viewModel;
        DetachedFromVisualTree += (_, _) => _artworkPreview?.Close();
    }

    public event EventHandler? CloseRequested;

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

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

    private void OnArtworkDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { Tag: ArtworkPreviewItem item })
            return;

        OpenArtworkPreview(item);
        e.Handled = true;
    }

    private void OnArtworkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            sender is not Control { Tag: ArtworkPreviewItem item })
            return;

        OpenArtworkPreview(item);
        e.Handled = true;
    }

    private void OpenArtworkPreview(ArtworkPreviewItem item)
    {
        if (_artworkPreview?.IsVisible == true && ReferenceEquals(_previewedArtwork, item))
        {
            _artworkPreview.Activate();
            return;
        }

        _artworkPreview?.Close();
        _artworkPreview = null;
        _previewedArtwork = null;

        Window? owner = TopLevel.GetTopLevel(this) as Window;

        if (!ArtworkPreviewWindow.TryCreate(item, owner, out ArtworkPreviewWindow? preview) || preview is null)
        {
            _viewModel.ReportArtworkPreviewUnavailable();
            return;
        }

        _artworkPreview = preview;
        _previewedArtwork = item;
        preview.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_artworkPreview, preview))
                return;
            _artworkPreview = null;
            _previewedArtwork = null;
        };

        if (owner is not null)
            preview.Show(owner);
        else
            preview.Show();
    }
}
