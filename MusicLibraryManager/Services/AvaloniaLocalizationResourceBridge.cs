using global::Avalonia;
using global::Avalonia.Threading;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Services;

public sealed class AvaloniaLocalizationResourceBridge(
    ILocalizationService localization) : IDisposable
{
    public const string ResourcePrefix = "Loc.";
    public static event EventHandler? ResourcesApplied;
    private bool _started;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        localization.CultureChanged +=
            OnCultureChanged;
        Apply();
    }

    public void Apply()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Apply);
            return;
        }
        if (Application.Current is not { } application)
            return;
        foreach ((string key, string value) in
                 localization.Snapshot())
            application.Resources[
                ResourcePrefix + key] = value;
        ResourcesApplied?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (!_started)
            return;
        localization.CultureChanged -=
            OnCultureChanged;
        _started = false;
    }

    private void OnCultureChanged(
        object? sender,
        EventArgs e) =>
        Apply();
}
