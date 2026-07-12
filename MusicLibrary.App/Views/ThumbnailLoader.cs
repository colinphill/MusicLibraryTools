using Avalonia;
using Avalonia.Controls;
using MusicLibrary.App.Services;

namespace MusicLibrary.App.Views;

/// <summary>
/// Attached property that lazily fills an <see cref="Image"/> with a file's artwork thumbnail. Set
/// <c>ThumbnailLoader.Path</c> on an Image (bound to a row's path) and the corresponding thumbnail
/// is fetched from the <see cref="IThumbnailProvider"/> and assigned to the Image's Source.
///
/// Because the details grid virtualizes and recycles cell controls, the requested path is captured
/// and re-checked after the async load; a stale result (the Image was recycled to another row) is
/// discarded.
/// </summary>
public static class ThumbnailLoader
{
    private static IThumbnailProvider? _provider;

    /// <summary>Wire the provider once at startup (from the composition root).</summary>
    public static void Init(IThumbnailProvider provider) => _provider = provider;

    public static readonly AttachedProperty<string?> PathProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Path", typeof(ThumbnailLoader));

    public static void SetPath(Image element, string? value) => element.SetValue(PathProperty, value);
    public static string? GetPath(Image element) => element.GetValue(PathProperty);

    static ThumbnailLoader()
    {
        PathProperty.Changed.AddClassHandler<Image>(OnPathChanged);
    }

    private static async void OnPathChanged(Image image, AvaloniaPropertyChangedEventArgs e)
    {
        if (image.Source is IDisposable oldSource)
            oldSource.Dispose();
        image.Source = null;

        var path = e.GetNewValue<string?>();
        if (string.IsNullOrEmpty(path) || _provider is null)
            return;

        var bmp = await _provider.GetAsync(path);

        // The cell may have been recycled to a different row while we were loading.
        if (GetPath(image) == path)
            image.Source = bmp;
        else
            bmp?.Dispose();
    }
}
