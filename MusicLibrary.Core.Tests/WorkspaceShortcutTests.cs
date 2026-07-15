using Avalonia.Input;
using MusicLibrary.App.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class WorkspaceShortcutTests
{
    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D4, 3)]
    [InlineData(Key.D7, 6)]
    [InlineData(Key.D8, 7)]
    public void ControlNumberSelectsWorkspace(Key key, int tab)
    {
        var shortcut = WorkspaceShortcutMap.Resolve(key, KeyModifiers.Control);

        Assert.Equal(WorkspaceShortcutKind.SelectTab, shortcut.Kind);
        Assert.Equal(tab, shortcut.Argument);
    }

    [Theory]
    [InlineData(Key.F, WorkspaceShortcutKind.FocusFilter, 0)]
    [InlineData(Key.R, WorkspaceShortcutKind.ReloadLibrary, 0)]
    [InlineData(Key.I, WorkspaceShortcutKind.IndexLibrary, 0)]
    [InlineData(Key.S, WorkspaceShortcutKind.SaveActiveEditor, 0)]
    [InlineData(Key.PageUp, WorkspaceShortcutKind.MoveTab, -1)]
    [InlineData(Key.PageDown, WorkspaceShortcutKind.MoveTab, 1)]
    public void ControlGesturesMapToDocumentedActions(
        Key key, WorkspaceShortcutKind kind, int argument)
    {
        var shortcut = WorkspaceShortcutMap.Resolve(key, KeyModifiers.Control);

        Assert.Equal(kind, shortcut.Kind);
        Assert.Equal(argument, shortcut.Argument);
    }

    [Fact]
    public void EscapeCancelsOnlyWithoutModifiers()
    {
        Assert.Equal(WorkspaceShortcutKind.CancelActiveOperation,
            WorkspaceShortcutMap.Resolve(Key.Escape, KeyModifiers.None).Kind);
        Assert.Equal(WorkspaceShortcutKind.None,
            WorkspaceShortcutMap.Resolve(Key.Escape, KeyModifiers.Control).Kind);
    }

    [Fact]
    public void ModifiedOrUnknownGesturesAreNotClaimed()
    {
        Assert.Equal(WorkspaceShortcutKind.None,
            WorkspaceShortcutMap.Resolve(Key.F, KeyModifiers.Control | KeyModifiers.Shift).Kind);
        Assert.Equal(WorkspaceShortcutKind.None,
            WorkspaceShortcutMap.Resolve(Key.A, KeyModifiers.Control).Kind);
        Assert.Equal(WorkspaceShortcutKind.None,
            WorkspaceShortcutMap.Resolve(Key.F, KeyModifiers.None).Kind);
    }
}
