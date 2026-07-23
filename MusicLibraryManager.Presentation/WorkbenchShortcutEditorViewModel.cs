using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

[Flags]
public enum WorkbenchShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

public sealed record ParsedWorkbenchShortcut(
    WorkbenchShortcutModifiers Modifiers,
    string Key,
    string Display);

public sealed record WorkbenchShortcutCommandChoice(
    WorkbenchShortcutCommand Command,
    string Label);

public sealed record WorkbenchShortcutRow(
    WorkbenchShortcutBinding Binding)
{
    public string Gesture => Binding.Gesture;
    public string Target => Binding.TargetLabel ??
        Binding.Command?.ToString() ??
        "Recipe";
}

public static class WorkbenchShortcutGestureParser
{
    private static readonly Regex KeyName = new(
        "^[A-Za-z][A-Za-z0-9]{0,31}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool TryParse(
        string? value,
        out ParsedWorkbenchShortcut? gesture,
        out string? error)
    {
        gesture = null;
        error = null;
        string[] parts = (value ?? "")
            .Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error =
                "Use at least one modifier plus a key, such as Ctrl+Shift+P.";
            return false;
        }
        WorkbenchShortcutModifiers modifiers =
            WorkbenchShortcutModifiers.None;
        string? key = null;
        foreach (string part in parts)
        {
            WorkbenchShortcutModifiers modifier =
                part.ToUpperInvariant() switch
                {
                    "CTRL" or "CONTROL" =>
                        WorkbenchShortcutModifiers.Control,
                    "ALT" =>
                        WorkbenchShortcutModifiers.Alt,
                    "SHIFT" =>
                        WorkbenchShortcutModifiers.Shift,
                    "META" or "CMD" or "COMMAND" or "WIN" =>
                        WorkbenchShortcutModifiers.Meta,
                    _ => WorkbenchShortcutModifiers.None,
                };
            if (modifier != WorkbenchShortcutModifiers.None)
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"Modifier '{part}' is repeated.";
                    return false;
                }
                modifiers |= modifier;
                continue;
            }
            if (key is not null)
            {
                error = "A shortcut can contain only one non-modifier key.";
                return false;
            }
            key = part;
        }
        if (modifiers == WorkbenchShortcutModifiers.None ||
            string.IsNullOrWhiteSpace(key))
        {
            error = "A modifier and a key are required.";
            return false;
        }
        if (!KeyName.IsMatch(key))
        {
            error =
                "Use an Avalonia key name such as P, Enter, Delete, or F8.";
            return false;
        }
        string display = BuildDisplay(modifiers, key);
        gesture = new(modifiers, key, display);
        return true;
    }

    private static string BuildDisplay(
        WorkbenchShortcutModifiers modifiers,
        string key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(
                WorkbenchShortcutModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(WorkbenchShortcutModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(WorkbenchShortcutModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(WorkbenchShortcutModifiers.Meta))
            parts.Add("Meta");
        parts.Add(key.Length == 1
            ? key.ToUpperInvariant()
            : char.ToUpperInvariant(key[0]) + key[1..]);
        return string.Join("+", parts);
    }
}

public partial class WorkbenchShortcutEditorViewModel :
    ObservableObject
{
    private static readonly HashSet<string> ReservedGestures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Ctrl+K",
            "Ctrl+I",
        };

    private readonly IWorkbenchShortcutStore? _store;
    private readonly IOperationRecipeStore? _recipes;
    private bool _loading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveShortcutCommand))]
    private string _gestureText = "Ctrl+Shift+P";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveShortcutCommand))]
    private WorkbenchShortcutTargetKind _targetKind =
        WorkbenchShortcutTargetKind.Command;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveShortcutCommand))]
    private WorkbenchShortcutCommandChoice? _selectedCommand;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveShortcutCommand))]
    private OperationRecipe? _selectedRecipe;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveShortcutCommand))]
    private WorkbenchShortcutRow? _selectedBinding;
    [ObservableProperty] private string _status = "";

    public WorkbenchShortcutEditorViewModel(
        IWorkbenchShortcutStore? store = null,
        IOperationRecipeStore? recipes = null)
    {
        _store = store;
        _recipes = recipes;
        Commands =
        [
            new(WorkbenchShortcutCommand.AddFiles, "Add files"),
            new(WorkbenchShortcutCommand.AddFolder, "Add folder"),
            new(
                WorkbenchShortcutCommand.PreviewInlineEdits,
                "Preview inline edits"),
            new(
                WorkbenchShortcutCommand.PreviewCurrentRecipe,
                "Preview current recipe"),
            new(
                WorkbenchShortcutCommand.ApplyReviewedChanges,
                "Apply reviewed changes"),
            new(
                WorkbenchShortcutCommand.UndoLastApply,
                "Undo last apply"),
            new(WorkbenchShortcutCommand.Redo, "Redo"),
            new(
                WorkbenchShortcutCommand.RepeatLastRecipe,
                "Repeat last recipe"),
            new(
                WorkbenchShortcutCommand.CancelCurrentOperation,
                "Cancel current operation"),
        ];
        SelectedCommand = Commands[0];
        ReloadRecipes();
        ReloadBindings();
        if (_recipes is not null)
            _recipes.Changed += (_, _) => ReloadRecipes();
    }

    public ObservableCollection<WorkbenchShortcutRow>
        Bindings { get; } = [];
    public ObservableCollection<OperationRecipe>
        Recipes { get; } = [];
    public IReadOnlyList<WorkbenchShortcutCommandChoice>
        Commands { get; }
    public IReadOnlyList<WorkbenchShortcutTargetKind>
        TargetKinds { get; } =
            Enum.GetValues<WorkbenchShortcutTargetKind>();

    [RelayCommand]
    private void NewShortcut()
    {
        _loading = true;
        try
        {
            SelectedBinding = null;
            GestureText = "Ctrl+Shift+P";
            TargetKind =
                WorkbenchShortcutTargetKind.Command;
            SelectedCommand = Commands[0];
            SelectedRecipe = Recipes.FirstOrDefault();
            Status = "New unsaved shortcut.";
        }
        finally
        {
            _loading = false;
        }
        SaveShortcutCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveShortcut))]
    private void SaveShortcut()
    {
        if (_store is null)
            return;
        if (!WorkbenchShortcutGestureParser.TryParse(
                GestureText,
                out ParsedWorkbenchShortcut? gesture,
                out string? error))
        {
            Status = error ?? "The shortcut is invalid.";
            return;
        }
        if (ReservedGestures.Contains(gesture!.Display))
        {
            Status =
                $"{gesture.Display} is reserved by the application shell.";
            return;
        }
        WorkbenchShortcutRow? conflict = Bindings.FirstOrDefault(row =>
            !ReferenceEquals(row, SelectedBinding) &&
            row.Gesture.Equals(
                gesture.Display,
                StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            Status =
                $"{gesture.Display} is already assigned to {conflict.Target}.";
            return;
        }
        WorkbenchShortcutBinding binding;
        if (TargetKind == WorkbenchShortcutTargetKind.Command &&
            SelectedCommand is not null)
        {
            binding = new(
                SelectedBinding?.Binding.Id ?? Guid.NewGuid(),
                gesture.Display,
                TargetKind,
                SelectedCommand.Command,
                TargetLabel: SelectedCommand.Label);
        }
        else if (
            TargetKind == WorkbenchShortcutTargetKind.Recipe &&
            SelectedRecipe is not null)
        {
            binding = new(
                SelectedBinding?.Binding.Id ?? Guid.NewGuid(),
                gesture.Display,
                TargetKind,
                RecipeId: SelectedRecipe.Id,
                TargetLabel: SelectedRecipe.Name);
        }
        else
        {
            Status = "Choose a shortcut target.";
            return;
        }
        List<WorkbenchShortcutBinding> bindings =
            Bindings.Select(row => row.Binding).ToList();
        int index = bindings.FindIndex(candidate =>
            candidate.Id == binding.Id);
        if (index < 0)
            bindings.Add(binding);
        else
            bindings[index] = binding;
        _store.Save(bindings);
        ReloadBindings(binding.Id);
        Status = $"Saved {binding.Gesture} for {binding.TargetLabel}.";
    }

    private bool CanSaveShortcut() =>
        _store is not null &&
        !string.IsNullOrWhiteSpace(GestureText) &&
        (TargetKind == WorkbenchShortcutTargetKind.Command
            ? SelectedCommand is not null
            : SelectedRecipe is not null);

    [RelayCommand(CanExecute = nameof(CanRemoveShortcut))]
    private void RemoveShortcut()
    {
        if (_store is null || SelectedBinding is null)
            return;
        string gesture = SelectedBinding.Gesture;
        _store.Save(Bindings
            .Where(row => !ReferenceEquals(row, SelectedBinding))
            .Select(row => row.Binding)
            .ToArray());
        ReloadBindings();
        NewShortcut();
        Status = $"Removed {gesture}.";
    }

    private bool CanRemoveShortcut() =>
        _store is not null && SelectedBinding is not null;

    partial void OnSelectedBindingChanged(
        WorkbenchShortcutRow? value)
    {
        RemoveShortcutCommand.NotifyCanExecuteChanged();
        if (_loading || value is null)
            return;
        _loading = true;
        try
        {
            GestureText = value.Binding.Gesture;
            TargetKind = value.Binding.TargetKind;
            SelectedCommand = value.Binding.Command is { } command
                ? Commands.FirstOrDefault(choice =>
                    choice.Command == command)
                : SelectedCommand;
            SelectedRecipe = value.Binding.RecipeId is { } recipeId
                ? Recipes.FirstOrDefault(recipe =>
                    recipe.Id == recipeId)
                : SelectedRecipe;
        }
        finally
        {
            _loading = false;
        }
        SaveShortcutCommand.NotifyCanExecuteChanged();
    }

    public bool TryMatch(
        WorkbenchShortcutModifiers modifiers,
        string key,
        out WorkbenchShortcutBinding? binding)
    {
        binding = null;
        foreach (WorkbenchShortcutRow row in Bindings)
        {
            if (WorkbenchShortcutGestureParser.TryParse(
                    row.Gesture,
                    out ParsedWorkbenchShortcut? gesture,
                    out _) &&
                gesture!.Modifiers == modifiers &&
                gesture.Key.Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                binding = row.Binding;
                return true;
            }
        }
        return false;
    }

    private void ReloadBindings(Guid? selectedId = null)
    {
        Bindings.Clear();
        foreach (WorkbenchShortcutBinding binding in
                 _store?.Load() ?? [])
            Bindings.Add(new(binding));
        SelectedBinding = selectedId is null
            ? null
            : Bindings.FirstOrDefault(row =>
                row.Binding.Id == selectedId);
    }

    private void ReloadRecipes()
    {
        Guid? selected = SelectedRecipe?.Id;
        Recipes.Clear();
        if (_recipes is not null)
            foreach (OperationRecipe recipe in
                     _recipes.Recipes.OrderBy(
                         recipe => recipe.Name,
                         StringComparer.OrdinalIgnoreCase))
                Recipes.Add(recipe);
        SelectedRecipe = selected is null
            ? Recipes.FirstOrDefault()
            : Recipes.FirstOrDefault(recipe =>
                recipe.Id == selected);
        SaveShortcutCommand.NotifyCanExecuteChanged();
    }
}
