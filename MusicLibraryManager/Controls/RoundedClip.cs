using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Avalonia's compositing renderer treats <c>ClipToBounds</c> as a plain rectangular
/// clip and ignores <see cref="Border.CornerRadiusProperty"/> when clipping children
/// (only the legacy immediate renderer honors corner radius there). Without this,
/// edge-to-edge content in a rounded <see cref="Border"/> or templated control - a
/// DataGrid's rows, a ListBox's items - spills into the four corner wedges outside
/// the rounded arc but inside the rectangular bounds. This attaches a real rounded-rect
/// geometry clip that tracks bounds and corner radius so those corners stay clean.
///
/// A <see cref="Border"/> already renders its own rounded background/stroke correctly
/// via a dedicated composition visual - that was never broken. Setting <c>Clip</c>
/// directly on the Border layers a second, independently-computed rounded-rect geometry
/// on top of that same visual's already-curved stroke output, and the two nearly-but-not
/// -exactly-coincident curves produce a visible gap in the stroke at the tangent. So for
/// a Border, the clip is redirected to its Child instead, leaving the Border's own
/// decoration untouched; other controls (e.g. a ListBox, whose rounded stroke comes from
/// a Border nested inside its template, not from the ListBox's own visual) are clipped
/// directly since they don't have that self-conflict.
/// </summary>
public sealed class RoundedClip : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnforceProperty =
        AvaloniaProperty.RegisterAttached<RoundedClip, Control, bool>("Enforce");

    static RoundedClip()
    {
        EnforceProperty.Changed.AddClassHandler<Control>(
            static (control, _) =>
            {
                if (GetEnforce(control))
                    Attach(control);
                else
                    Detach(control);
            });
    }

    public static bool GetEnforce(Control control) =>
        control.GetValue(EnforceProperty);

    public static void SetEnforce(Control control, bool value) =>
        control.SetValue(EnforceProperty, value);

    private RoundedClip()
    {
    }

    private static void Attach(Control control)
    {
        control.PropertyChanged += OnControlPropertyChanged;
        UpdateClip(control);
    }

    private static void Detach(Control control)
    {
        control.PropertyChanged -= OnControlPropertyChanged;
        control.Clip = null;
        if (control is Border { Child: Control child })
            child.Clip = null;
    }

    private static void OnControlPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty ||
            e.Property == Border.CornerRadiusProperty ||
            e.Property == Border.BorderThicknessProperty ||
            e.Property == Decorator.ChildProperty)
        {
            UpdateClip((Control)sender!);
        }
    }

    private static void UpdateClip(Control control)
    {
        if (control is Border border)
        {
            // The Border's own composition visual already draws the correct rounded
            // stroke/background; never layer a redundant clip onto that same visual.
            if (border.Child is Control child)
                ApplyClip(child, child.Bounds.Size, InnerRadius(border));
            return;
        }

        ApplyClip(control, control.Bounds.Size, control.GetValue(Border.CornerRadiusProperty).TopLeft);
    }

    private static double InnerRadius(Border border)
    {
        Thickness thickness = border.BorderThickness;
        double inset = (thickness.Left + thickness.Top + thickness.Right + thickness.Bottom) / 4;
        return Math.Max(0, border.CornerRadius.TopLeft - inset);
    }

    private static void ApplyClip(Control control, Size size, double radius)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            control.Clip = null;
            return;
        }

        control.Clip = new RectangleGeometry(new Rect(size), radius, radius);
    }
}
