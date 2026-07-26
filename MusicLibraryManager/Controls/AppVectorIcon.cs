using Avalonia.Media;

namespace MusicLibraryManager.Controls;

public enum AppVectorIconKind
{
    Close,
    MoveUp,
    MoveDown,
    More,
    Settings,
    Information,
    Warning,
    Error,
}

/// <summary>
/// Provides the small, theme-aware vector glyphs used by shared icon buttons.
/// Keeping these paths in one control prevents platform font glyph differences
/// and gives every icon the same visual weight.
/// </summary>
public sealed class AppVectorIcon :
    global::Avalonia.Controls.Shapes.Path
{
    private AppVectorIconKind _kind;

    public AppVectorIcon()
    {
        UpdateGeometry();
    }

    public AppVectorIconKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value)
                return;
            _kind = value;
            UpdateGeometry();
        }
    }

    private void UpdateGeometry()
    {
        Data = StreamGeometry.Parse(_kind switch
        {
            AppVectorIconKind.Close =>
                "M4,4 L16,16 M16,4 L4,16",
            AppVectorIconKind.MoveUp =>
                "M4,12 L10,6 L16,12",
            AppVectorIconKind.MoveDown =>
                "M4,8 L10,14 L16,8",
            AppVectorIconKind.More =>
                "M4,9 L4,11 M10,9 L10,11 M16,9 L16,11",
            AppVectorIconKind.Settings =>
                "M3,6 L17,6 M3,14 L17,14 M7,3 L7,9 M13,11 L13,17",
            AppVectorIconKind.Information =>
                "M10,2 A8,8 0 1 1 9.999,2 M10,9 L10,15 M10,5.5 L10,6",
            AppVectorIconKind.Warning =>
                "M10,1 L19,18 L1,18 Z M10,7 L10,12 M10,15 L10.1,15",
            AppVectorIconKind.Error =>
                "M10,2 A8,8 0 1 1 9.999,2 M7,7 L13,13 M13,7 L7,13",
            _ => "M4,4 L16,16",
        });
    }
}
