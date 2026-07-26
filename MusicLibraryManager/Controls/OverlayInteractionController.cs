using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Shared interaction policy for temporary modal navigation, menu, and drawer
/// surfaces. The owning view remains responsible for deciding whether a
/// surface may close; this controller supplies consistent focus containment,
/// Escape/scrim routing, focus restoration, and viewport bounds.
/// </summary>
internal sealed class OverlayInteractionController
{
    private WeakReference<IInputElement>? _focusReturn;

    public void CaptureFocus(Control owner)
    {
        RememberFocus(
            TopLevel.GetTopLevel(owner)?
                .FocusManager?
                .GetFocusedElement());
    }

    public void RememberFocus(IInputElement? element)
    {
        _focusReturn = element is null
            ? null
            : new WeakReference<IInputElement>(
                element);
    }

    public bool HandleKeyDown(
        KeyEventArgs e,
        Control focusSurface,
        bool canDismiss,
        Action dismiss,
        Action? blockedDismissal = null,
        bool moveEveryTab = false)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(
            focusSurface);
        ArgumentNullException.ThrowIfNull(
            dismiss);

        if (e.Key == Key.Escape)
        {
            if (canDismiss)
                dismiss();
            else
                blockedDismissal?.Invoke();
            e.Handled = true;
            return true;
        }

        if (e.Key != Key.Tab)
            return false;

        bool handled = TryCycleFocus(
            focusSurface,
            e.KeyModifiers.HasFlag(
                KeyModifiers.Shift),
            moveEveryTab);
        if (handled)
            e.Handled = true;
        return handled;
    }

    public bool HandleScrimPressed(
        PointerPressedEventArgs e,
        Visual scrim,
        bool canDismiss,
        Action dismiss,
        Action? blockedDismissal = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(scrim);
        ArgumentNullException.ThrowIfNull(
            dismiss);

        bool isPrimaryPointer =
            e.Pointer.Type != PointerType.Mouse ||
            e.GetCurrentPoint(scrim)
                .Properties
                .IsLeftButtonPressed;
        if (isPrimaryPointer)
        {
            if (canDismiss)
                dismiss();
            else
                blockedDismissal?.Invoke();
        }

        e.Handled = true;
        return isPrimaryPointer && canDismiss;
    }

    public static bool TryCycleFocus(
        Control focusSurface,
        bool reverse,
        bool moveEveryTab = false)
    {
        ArgumentNullException.ThrowIfNull(
            focusSurface);
        Control[] focusable =
        [
            .. focusSurface
                .GetVisualDescendants()
                .Prepend(focusSurface)
                .OfType<Control>()
                .Where(IsFocusable),
        ];
        if (focusable.Length == 0)
            return false;

        object? focused =
            TopLevel.GetTopLevel(focusSurface)?
                .FocusManager?
                .GetFocusedElement();
        int index = Array.IndexOf(
            focusable,
            focused);
        if (index < 0)
        {
            (reverse
                ? focusable[^1]
                : focusable[0]).Focus(
                NavigationMethod.Tab);
            return true;
        }

        bool atBoundary = reverse
            ? index == 0
            : index == focusable.Length - 1;
        if (!moveEveryTab && !atBoundary)
            return false;

        int next = reverse
            ? index == 0
                ? focusable.Length - 1
                : index - 1
            : index == focusable.Length - 1
                ? 0
                : index + 1;
        focusable[next].Focus(
            NavigationMethod.Tab);
        return true;
    }

    public static void FocusFirst(
        Control focusSurface,
        bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(
            focusSurface);
        Dispatcher.UIThread.Post(
            () =>
            {
                Control[] focusable =
                [
                    .. focusSurface
                        .GetVisualDescendants()
                        .Prepend(focusSurface)
                        .OfType<Control>()
                        .Where(IsFocusable),
                ];
                if (focusable.Length > 0)
                {
                    (reverse
                        ? focusable[^1]
                        : focusable[0]).Focus(
                        NavigationMethod.Tab);
                }
            },
            DispatcherPriority.Input);
    }

    public void RestoreFocus(
        Control? fallback = null)
    {
        IInputElement? remembered = null;
        _focusReturn?.TryGetTarget(
            out remembered);
        _focusReturn = null;
        Dispatcher.UIThread.Post(
            () =>
            {
                IInputElement? target =
                    remembered is Control control &&
                    IsFocusable(control)
                        ? control
                        : fallback is not null &&
                          IsFocusable(fallback)
                            ? fallback
                            : null;
                target?.Focus(
                    NavigationMethod.Unspecified);
            },
            DispatcherPriority.Input);
    }

    public void ClearFocusReturn() =>
        _focusReturn = null;

    public static double ConstrainLength(
        double availableLength,
        double minimumLength,
        double maximumLength,
        double viewportInset = 0)
    {
        if (!double.IsFinite(
                availableLength) ||
            availableLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableLength));
        }

        if (!double.IsFinite(
                minimumLength) ||
            minimumLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumLength));
        }

        if (!double.IsFinite(
                maximumLength) ||
            maximumLength <
            minimumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength));
        }

        if (!double.IsFinite(viewportInset) ||
            viewportInset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportInset));
        }

        double usable = Math.Max(
            0,
            availableLength -
            viewportInset);
        return Math.Min(
            maximumLength,
            Math.Max(
                minimumLength,
                usable));
    }

    private static bool IsFocusable(
        Control control) =>
        control.Focusable &&
        control.IsEffectivelyEnabled &&
        control.IsEffectivelyVisible;
}
