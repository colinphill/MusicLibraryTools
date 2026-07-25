using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Stable, nonlocalized values used by the application appearance preferences.
/// </summary>
public enum UiDensity
{
    Standard,
    Compact,
}

/// <summary>
/// Centralizes appearance preference identities and change notification without
/// coupling presentation models to the Avalonia application.
/// </summary>
public static class AppearancePreferences
{
    private static readonly object EventSync = new();
    private static readonly List<
        WeakReference<EventHandler>> ChangedHandlers = [];

    public const string DensityPreference =
        "manager.appearance.density.v1";
    public const string ShellRailExpandedPreference =
        "manager.appearance.shellRailExpanded.v1";

    public static event EventHandler? Changed
    {
        add
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                ChangedHandlers.RemoveAll(
                    reference =>
                        !reference.TryGetTarget(out _));
                ChangedHandlers.Add(
                    new WeakReference<EventHandler>(value));
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                ChangedHandlers.RemoveAll(
                    reference =>
                        !reference.TryGetTarget(
                            out EventHandler? handler) ||
                        handler == value);
            }
        }
    }

    public static UiDensity GetDensity(IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.TryParse(
                   settings.GetPreference(DensityPreference),
                   ignoreCase: true,
                   out UiDensity density)
            ? density
            : UiDensity.Standard;
    }

    public static void SetDensity(
        IAppSettings settings,
        UiDensity density)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(density))
            density = UiDensity.Standard;
        settings.SetPreference(DensityPreference, density.ToString());
        RaiseChanged();
    }

    public static bool GetShellRailExpanded(
        IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !bool.TryParse(
                   settings.GetPreference(
                       ShellRailExpandedPreference),
                   out bool expanded) ||
               expanded;
    }

    public static void SetShellRailExpanded(
        IAppSettings settings,
        bool expanded)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SetPreference(
            ShellRailExpandedPreference,
            expanded.ToString());
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        EventHandler[] handlers;
        lock (EventSync)
        {
            handlers = ChangedHandlers
                .Select(reference =>
                    reference.TryGetTarget(
                        out EventHandler? handler)
                        ? handler
                        : null)
                .OfType<EventHandler>()
                .ToArray();
            ChangedHandlers.RemoveAll(
                reference =>
                    !reference.TryGetTarget(out _));
        }

        foreach (EventHandler handler in handlers)
            handler(null, EventArgs.Empty);
    }
}
