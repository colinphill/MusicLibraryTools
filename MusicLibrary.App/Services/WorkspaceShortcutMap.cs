using Avalonia.Input;

namespace MusicLibrary.App.Services;

public enum WorkspaceShortcutKind
{
    None,
    SelectTab,
    MoveTab,
    FocusFilter,
    ReloadLibrary,
    IndexLibrary,
    SaveActiveEditor,
    CancelActiveOperation,
}

public readonly record struct WorkspaceShortcut(WorkspaceShortcutKind Kind, int Argument = 0);

/// <summary>Single source of truth for global keyboard gestures shown by the Shortcuts flyout.</summary>
public static class WorkspaceShortcutMap
{
    public static WorkspaceShortcut Resolve(Key key, KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.Control)
        {
            int? tab = key switch
            {
                Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3,
                Key.D5 => 4, Key.D6 => 5, Key.D7 => 6,
                _ => null,
            };
            if (tab is int index)
                return new WorkspaceShortcut(WorkspaceShortcutKind.SelectTab, index);

            return key switch
            {
                Key.F => new(WorkspaceShortcutKind.FocusFilter),
                Key.R => new(WorkspaceShortcutKind.ReloadLibrary),
                Key.I => new(WorkspaceShortcutKind.IndexLibrary),
                Key.S => new(WorkspaceShortcutKind.SaveActiveEditor),
                Key.PageUp => new(WorkspaceShortcutKind.MoveTab, -1),
                Key.PageDown => new(WorkspaceShortcutKind.MoveTab, 1),
                _ => default,
            };
        }

        return key == Key.Escape && modifiers == KeyModifiers.None
            ? new WorkspaceShortcut(WorkspaceShortcutKind.CancelActiveOperation)
            : default;
    }
}
