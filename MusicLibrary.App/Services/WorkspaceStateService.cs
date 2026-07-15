using System.Text.Json;
using Avalonia;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.Services;

public sealed record WindowWorkspaceState(
    int Version,
    double Width,
    double Height,
    int X,
    int Y,
    bool Maximized);

public sealed record WindowWorkspacePlacement(double Width, double Height, int X, int Y);

public static class WindowWorkspacePlacementCalculator
{
    public static WindowWorkspacePlacement Fit(
        WindowWorkspaceState state,
        PixelRect workingArea,
        double scaling,
        double minimumWidth,
        double minimumHeight)
    {
        if (scaling <= 0) scaling = 1;
        double width = Math.Clamp(state.Width, minimumWidth,
            Math.Max(minimumWidth, workingArea.Width / scaling));
        double height = Math.Clamp(state.Height, minimumHeight,
            Math.Max(minimumHeight, workingArea.Height / scaling));
        int pixelWidth = (int)Math.Round(width * scaling);
        int pixelHeight = (int)Math.Round(height * scaling);
        bool visible = IsMeaningfullyVisible(state, workingArea, scaling);
        int x = visible
            ? Math.Clamp(state.X,
                workingArea.X - pixelWidth + 80,
                workingArea.X + workingArea.Width - 80)
            : workingArea.X + Math.Max(0, (workingArea.Width - pixelWidth) / 2);
        int y = visible
            ? Math.Clamp(state.Y, workingArea.Y, workingArea.Y + workingArea.Height - 80)
            : workingArea.Y + Math.Max(0, (workingArea.Height - pixelHeight) / 2);
        return new WindowWorkspacePlacement(width, height, x, y);
    }

    public static bool IsMeaningfullyVisible(
        WindowWorkspaceState state,
        PixelRect workingArea,
        double scaling)
    {
        if (scaling <= 0) scaling = 1;
        int right = state.X + (int)Math.Round(state.Width * scaling);
        int bottom = state.Y + (int)Math.Round(state.Height * scaling);
        int visibleWidth = Math.Min(right, workingArea.X + workingArea.Width) -
            Math.Max(state.X, workingArea.X);
        int visibleHeight = Math.Min(bottom, workingArea.Y + workingArea.Height) -
            Math.Max(state.Y, workingArea.Y);
        return visibleWidth >= 80 && visibleHeight >= 80;
    }
}

public interface IWorkspaceStateService
{
    WindowWorkspaceState? LoadWindowState();
    void SaveWindowState(WindowWorkspaceState state);
}

/// <summary>Persists validated, versioned view-only state through the shared app settings store.</summary>
public sealed class WorkspaceStateService(IAppSettings settings) : IWorkspaceStateService
{
    private const string WindowStateKey = "workspace.window.v1";
    public const int CurrentVersion = 1;

    public WindowWorkspaceState? LoadWindowState()
    {
        string? json = settings.GetPreference(WindowStateKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<WindowWorkspaceState>(json);
            return state is not null && IsValid(state) ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveWindowState(WindowWorkspaceState state)
    {
        if (IsValid(state))
            settings.SetPreference(WindowStateKey, JsonSerializer.Serialize(state));
    }

    private static bool IsValid(WindowWorkspaceState state) =>
        state.Version == CurrentVersion &&
        double.IsFinite(state.Width) && state.Width is >= 400 and <= 16_384 &&
        double.IsFinite(state.Height) && state.Height is >= 300 and <= 16_384 &&
        state.X is > -1_000_000 and < 1_000_000 &&
        state.Y is > -1_000_000 and < 1_000_000;
}
