using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class SelectionInspectorView : UserControl
{
    public static readonly StyledProperty<object?>
        SupplementaryContentProperty =
        AvaloniaProperty.Register<
            SelectionInspectorView,
            object?>(
            nameof(SupplementaryContent));

    private ArtworkPreviewWindow? _artworkPreview;
    private ArtworkPreviewItem? _previewedArtwork;

    public SelectionInspectorView()
    {
        InitializeComponent();
        DataContext = App.GetService<SelectionInspectorViewModel>();
        DetachedFromVisualTree += (_, _) => _artworkPreview?.Close();
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ReviewChangesRequested;

    public object? SupplementaryContent
    {
        get => GetValue(
            SupplementaryContentProperty);
        set => SetValue(
            SupplementaryContentProperty,
            value);
    }

    public Control CloseButton =>
        InspectorCloseButton;

    private SelectionInspectorViewModel ViewModel =>
        DataContext as SelectionInspectorViewModel ??
        App.GetService<SelectionInspectorViewModel>();

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnReviewChanges(
        object? sender,
        RoutedEventArgs e) =>
        ReviewChangesRequested?.Invoke(
            this,
            EventArgs.Empty);

    private async void OnReplaceArtwork(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ArtworkPreviewItem item })
            await ViewModel.ReplaceArtworkItemAsync(item);
    }

    private async void OnSaveArtwork(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ArtworkPreviewItem item })
            await ViewModel.SaveArtworkItemToFileAsync(item);
    }

    private void OnRemoveArtwork(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ArtworkPreviewItem item })
            ViewModel.RemoveArtworkItem(item);
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
            ViewModel.ReportArtworkPreviewUnavailable();
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
