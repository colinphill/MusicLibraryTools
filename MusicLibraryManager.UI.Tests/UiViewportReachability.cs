using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace MusicLibraryManager.UI.Tests;

internal readonly record struct UiActionReachabilityResult(
    bool IsReachable,
    bool WasInitiallyVisible,
    bool UsedVerticalScrolling,
    string Detail);

internal static class UiViewportReachability
{
    private const double GeometryTolerance = 1;
    private const double OffsetTolerance = 0.01;

    public static UiActionReachabilityResult
        VerifyAction(
            Control viewportRoot,
            Control action,
            Action render)
    {
        ArgumentNullException.ThrowIfNull(
            viewportRoot);
        ArgumentNullException.ThrowIfNull(
            action);
        ArgumentNullException.ThrowIfNull(
            render);

        if (TryGetFullyVisibleBounds(
                viewportRoot,
                action,
                out _,
                out string initialDetail))
        {
            return new(
                true,
                true,
                false,
                "The action was fully visible.");
        }

        ScrollViewer[] scrollOwners =
        [
            .. AncestorsWithin(
                    action,
                    viewportRoot)
                .OfType<ScrollViewer>(),
        ];
        ScrollViewer[] usableOwners =
        [
            .. scrollOwners.Where(
                owner =>
                    IsUsableVerticalOwner(
                        owner,
                        action)),
        ];
        if (usableOwners.Length == 0)
        {
            return new(
                false,
                false,
                false,
                $"{initialDetail} No usable vertical ScrollViewer owns the action.");
        }

        (ScrollViewer Owner, Vector Offset)[]
            originalOffsets =
        [
            .. scrollOwners.Select(
                owner =>
                    (owner, owner.Offset)),
        ];

        UiActionReachabilityResult result;
        try
        {
            action.BringIntoView();
            render();

            if (!TryGetFullyVisibleBounds(
                    viewportRoot,
                    action,
                    out _,
                    out _))
            {
                ScrollVerticallyIntoView(
                    action,
                    usableOwners,
                    render);
            }

            bool usedVerticalScrolling =
                AnyVerticalOffsetChanged(
                    originalOffsets);
            if (!usedVerticalScrolling)
            {
                result = new(
                    false,
                    false,
                    false,
                    $"{initialDetail} The usable vertical ScrollViewer did not move.");
            }
            else if (TryGetFullyVisibleBounds(
                         viewportRoot,
                         action,
                         out Rect visibleBounds,
                         out string visibleDetail))
            {
                result = new(
                    true,
                    false,
                    true,
                    $"The action was reachable at {visibleBounds}.");
            }
            else
            {
                result = new(
                    false,
                    false,
                    true,
                    $"The action remained clipped after scrolling. {visibleDetail}");
            }
        }
        finally
        {
            foreach (
                (ScrollViewer owner,
                    Vector offset) in
                originalOffsets)
            {
                owner.Offset = offset;
            }

            render();
        }

        foreach (
            (ScrollViewer owner,
                Vector offset) in
            originalOffsets)
        {
            if (!OffsetsEqual(
                    owner.Offset,
                    offset))
            {
                return new(
                    false,
                    false,
                    result.UsedVerticalScrolling,
                    $"The reachability probe did not restore the prior offset for {owner.Name ?? owner.GetType().Name}.");
            }
        }

        return result;
    }

    public static bool
        TryGetFullyVisibleBounds(
            Control viewportRoot,
            Control control,
            out Rect bounds,
            out string detail)
    {
        ArgumentNullException.ThrowIfNull(
            viewportRoot);
        ArgumentNullException.ThrowIfNull(
            control);

        bounds = default;
        if (!control.IsEffectivelyVisible)
        {
            detail =
                "The control was not effectively visible.";
            return false;
        }

        if (control.Bounds.Width <= 0 ||
            control.Bounds.Height <= 0)
        {
            detail =
                $"The control had no rendered size ({control.Bounds.Size}).";
            return false;
        }

        if (!ReferenceEquals(
                control,
                viewportRoot) &&
            !AncestorsWithin(
                    control,
                    viewportRoot)
                .Any(ancestor =>
                    ReferenceEquals(
                        ancestor,
                        viewportRoot)))
        {
            detail =
                "The control was not a descendant of the requested viewport.";
            return false;
        }

        if (!TryGetBoundsRelativeTo(
                control,
                viewportRoot,
                out bounds))
        {
            detail =
                "The control could not be translated into the requested viewport.";
            return false;
        }

        Rect rootViewport =
            new(
                viewportRoot.Bounds.Size);
        if (!Contains(
                rootViewport,
                bounds))
        {
            detail =
                $"The control bounds {bounds} were outside the root viewport {rootViewport}.";
            return false;
        }

        foreach (
            ScrollContentPresenter presenter in
            AncestorsWithin(
                    control,
                    viewportRoot)
                .OfType<
                    ScrollContentPresenter>())
        {
            if (!TryGetBoundsRelativeTo(
                    control,
                    presenter,
                    out Rect presenterBounds))
            {
                detail =
                    $"The control could not be translated into the scroll viewport {presenter.Name ?? presenter.GetType().Name}.";
                return false;
            }

            Rect scrollViewport =
                new(
                    presenter.Bounds.Size);
            if (!Contains(
                    scrollViewport,
                    presenterBounds))
            {
                detail =
                    $"The control bounds {presenterBounds} were outside the scroll viewport {scrollViewport}.";
                return false;
            }
        }

        foreach (
            Control clippingAncestor in
            AncestorsWithin(
                    control,
                    viewportRoot)
                .OfType<Control>()
                .Where(ancestor =>
                    ancestor.ClipToBounds &&
                    ancestor is not
                        ScrollContentPresenter &&
                    !ReferenceEquals(
                        ancestor,
                        viewportRoot)))
        {
            if (!TryGetBoundsRelativeTo(
                    control,
                    clippingAncestor,
                    out Rect clippedBounds))
            {
                detail =
                    $"The control could not be translated into the clipping viewport {clippingAncestor.Name ?? clippingAncestor.GetType().Name}.";
                return false;
            }

            Rect clippingViewport =
                new(
                    clippingAncestor.Bounds.Size);
            if (!Contains(
                    clippingViewport,
                    clippedBounds))
            {
                detail =
                    $"The control bounds {clippedBounds} were outside the clipping viewport {clippingViewport}.";
                return false;
            }
        }

        detail =
            $"The control was fully visible at {bounds}.";
        return true;
    }

    private static IEnumerable<Visual>
        AncestorsWithin(
            Control control,
            Control viewportRoot)
    {
        foreach (
            Visual ancestor in
            control.GetVisualAncestors())
        {
            yield return ancestor;
            if (ReferenceEquals(
                    ancestor,
                    viewportRoot))
                yield break;
        }
    }

    private static bool
        IsUsableVerticalOwner(
            ScrollViewer owner,
            Control action)
    {
        if (!owner.IsEffectivelyVisible ||
            owner.VerticalScrollBarVisibility ==
            ScrollBarVisibility.Disabled ||
            owner.Viewport.Height <=
            GeometryTolerance ||
            owner.Extent.Height <=
            owner.Viewport.Height +
            GeometryTolerance ||
            action.Bounds.Height >
            owner.Viewport.Height +
            GeometryTolerance)
        {
            return false;
        }

        ScrollContentPresenter? presenter =
            FindPresenter(
                owner,
                action);
        if (presenter is null ||
            !TryGetBoundsRelativeTo(
                action,
                presenter,
                out Rect actionBounds))
        {
            return false;
        }

        return actionBounds.Top <
               -GeometryTolerance ||
               actionBounds.Bottom >
               presenter.Bounds.Height +
               GeometryTolerance;
    }

    private static ScrollContentPresenter?
        FindPresenter(
            ScrollViewer owner,
            Control action) =>
        AncestorsWithin(
                action,
                owner)
            .OfType<
                ScrollContentPresenter>()
            .FirstOrDefault(
                presenter =>
                    ReferenceEquals(
                        presenter
                            .GetVisualAncestors()
                            .OfType<ScrollViewer>()
                            .FirstOrDefault(),
                        owner));

    private static void
        ScrollVerticallyIntoView(
            Control action,
            IReadOnlyList<ScrollViewer>
                owners,
            Action render)
    {
        foreach (ScrollViewer owner in
                 owners)
        {
            ScrollContentPresenter? presenter =
                FindPresenter(
                    owner,
                    action);
            if (presenter is null ||
                !TryGetBoundsRelativeTo(
                    action,
                    presenter,
                    out Rect actionBounds))
            {
                continue;
            }

            double delta = 0;
            if (actionBounds.Top < 0)
                delta = actionBounds.Top;
            else if (actionBounds.Bottom >
                     presenter.Bounds.Height)
            {
                delta =
                    actionBounds.Bottom -
                    presenter.Bounds.Height;
            }

            if (Math.Abs(delta) <=
                GeometryTolerance)
                continue;

            double maximum =
                Math.Max(
                    0,
                    owner.Extent.Height -
                    owner.Viewport.Height);
            owner.Offset =
                new(
                    owner.Offset.X,
                    Math.Clamp(
                        owner.Offset.Y +
                        delta,
                        0,
                        maximum));
            render();
        }
    }

    private static bool
        AnyVerticalOffsetChanged(
            IEnumerable<(
                ScrollViewer Owner,
                Vector Offset)> originals) =>
        originals.Any(
            original =>
                Math.Abs(
                    original.Owner.Offset.Y -
                    original.Offset.Y) >
                OffsetTolerance);

    private static bool OffsetsEqual(
        Vector actual,
        Vector expected) =>
        Math.Abs(
            actual.X -
            expected.X) <=
        OffsetTolerance &&
        Math.Abs(
            actual.Y -
            expected.Y) <=
        OffsetTolerance;

    private static bool
        TryGetBoundsRelativeTo(
            Control control,
            Visual ancestor,
            out Rect bounds)
    {
        Point? first =
            control.TranslatePoint(
                default,
                ancestor);
        Point? second =
            control.TranslatePoint(
                new(
                    control.Bounds.Width,
                    control.Bounds.Height),
                ancestor);
        if (first is null ||
            second is null)
        {
            bounds = default;
            return false;
        }

        double left =
            Math.Min(
                first.Value.X,
                second.Value.X);
        double top =
            Math.Min(
                first.Value.Y,
                second.Value.Y);
        bounds =
            new(
                left,
                top,
                Math.Abs(
                    second.Value.X -
                    first.Value.X),
                Math.Abs(
                    second.Value.Y -
                    first.Value.Y));
        return true;
    }

    private static bool Contains(
        Rect viewport,
        Rect bounds) =>
        bounds.Left >=
        viewport.Left -
        GeometryTolerance &&
        bounds.Top >=
        viewport.Top -
        GeometryTolerance &&
        bounds.Right <=
        viewport.Right +
        GeometryTolerance &&
        bounds.Bottom <=
        viewport.Bottom +
        GeometryTolerance;
}
