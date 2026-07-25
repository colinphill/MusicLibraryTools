using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.VisualTree;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Page root that derives its presentation from the space allocated to the
/// content host. It deliberately does not inspect the owner window, so shell
/// rails and drawers cannot make responsive modes move backwards as the
/// window grows.
/// </summary>
public sealed class AdaptivePage : Grid
{
    public const double NarrowContentThreshold = 1000;
    public const double CompactHeightThreshold = 700;
    public const double WideGutter = 24;
    public const double NarrowGutter = 16;
    public const double CompactHeightGutter = 12;

    private Control? _contentHost;

    public AdaptivePage()
    {
        SizeChanged += (_, _) => ApplyLayoutMode();
        AttachedToVisualTree += (_, _) =>
        {
            ObserveContentHost();
            ApplyLayoutMode();
        };
        DetachedFromVisualTree += (_, _) =>
            StopObservingContentHost();
    }

    public double ContentWidth => LayoutBounds.Width;

    public bool IsNarrow =>
        LayoutBounds.Width < NarrowContentThreshold;

    public bool IsCompactHeight =>
        LayoutBounds.Height <= CompactHeightThreshold;

    public event EventHandler? LayoutModeChanged;

    private Rect LayoutBounds =>
        _contentHost is { Bounds.Width: > 0, Bounds.Height: > 0 }
            ? _contentHost.Bounds
            : Bounds;

    private void ObserveContentHost()
    {
        Control? host = this.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault();
        if (ReferenceEquals(host, _contentHost))
            return;
        StopObservingContentHost();
        _contentHost = host;
        if (_contentHost is not null)
            _contentHost.SizeChanged += OnContentHostSizeChanged;
    }

    private void StopObservingContentHost()
    {
        if (_contentHost is not null)
            _contentHost.SizeChanged -= OnContentHostSizeChanged;
        _contentHost = null;
    }

    private void OnContentHostSizeChanged(
        object? sender,
        SizeChangedEventArgs e) =>
        ApplyLayoutMode();

    private void ApplyLayoutMode()
    {
        bool narrow = IsNarrow;
        bool compactHeight = IsCompactHeight;
        double gutter = compactHeight
            ? CompactHeightGutter
            : narrow
                ? NarrowGutter
                : WideGutter;
        Thickness next = new(gutter);
        bool changed =
            Margin != next ||
            Classes.Contains("narrow-content") != narrow ||
            Classes.Contains("compact-height") != compactHeight;
        Margin = next;
        Classes.Set("narrow-content", narrow);
        Classes.Set("wide-content", !narrow);
        Classes.Set("compact-height", compactHeight);
        if (changed)
            LayoutModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
