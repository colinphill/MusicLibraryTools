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
        LocalizedText.Get(
            "Workbench.Shortcuts.Target.Recipe");
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
            error = LocalizedText.Get(
                "Workbench.Shortcuts.Validation.ModifierAndKeyExample");
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
                    error = LocalizedText.Format(
                        "Workbench.Shortcuts.Validation.RepeatedModifier",
                        part);
                    return false;
                }
                modifiers |= modifier;
                continue;
            }
            if (key is not null)
            {
                error = LocalizedText.Get(
                    "Workbench.Shortcuts.Validation.OneKey");
                return false;
            }
            key = part;
        }
        if (modifiers == WorkbenchShortcutModifiers.None ||
            string.IsNullOrWhiteSpace(key))
        {
            error = LocalizedText.Get(
                "Workbench.Shortcuts.Validation.ModifierAndKeyRequired");
            return false;
        }
        if (!KeyName.IsMatch(key))
        {
            error = LocalizedText.Get(
                "Workbench.Shortcuts.Validation.KeyName");
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
    private readonly ILocalizationService? _localization;
    private bool _loading;
    private string? _statusKey;
    private object?[] _statusArguments = [];

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasGestureValidationError))]
    private string _gestureValidationMessage = "";

    public WorkbenchShortcutEditorViewModel(
        IWorkbenchShortcutStore? store = null,
        IOperationRecipeStore? recipes = null,
        ILocalizationService? localization = null)
    {
        _store = store;
        _recipes = recipes;
        _localization = localization;
        RefreshLocalizedChoices();
        SelectedCommand = Commands[0];
        ReloadRecipes();
        ReloadBindings();
        if (_recipes is not null)
            _recipes.Changed += (_, _) => ReloadRecipes();
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public ObservableCollection<WorkbenchShortcutRow>
        Bindings { get; } = [];
    public ObservableCollection<OperationRecipe>
        Recipes { get; } = [];
    public ObservableCollection<WorkbenchShortcutCommandChoice>
        Commands { get; } = [];
    public IReadOnlyList<WorkbenchShortcutTargetKind>
        TargetKinds { get; } =
            Enum.GetValues<WorkbenchShortcutTargetKind>();
    public ObservableCollection<
        LocalizedChoice<WorkbenchShortcutTargetKind>>
        TargetKindChoices { get; } = [];
    public bool HasGestureValidationError =>
        !string.IsNullOrWhiteSpace(
            GestureValidationMessage);

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
            SetStatus("Workbench.Shortcuts.Status.New");
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
        string? validationError =
            GetGestureValidationError(
                out ParsedWorkbenchShortcut? gesture);
        if (validationError is not null)
        {
            GestureValidationMessage =
                validationError;
            Status = validationError;
            _statusKey = null;
            return;
        }
        ParsedWorkbenchShortcut parsedGesture = gesture!;
        WorkbenchShortcutBinding binding;
        if (TargetKind == WorkbenchShortcutTargetKind.Command &&
            SelectedCommand is not null)
        {
            binding = new(
                SelectedBinding?.Binding.Id ?? Guid.NewGuid(),
                parsedGesture.Display,
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
                parsedGesture.Display,
                TargetKind,
                RecipeId: SelectedRecipe.Id,
                TargetLabel: SelectedRecipe.Name);
        }
        else
        {
            SetStatus(
                "Workbench.Shortcuts.Validation.ChooseTarget");
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
        SetStatus(
            "Workbench.Shortcuts.Status.Saved",
            binding.Gesture,
            binding.TargetLabel);
    }

    private bool CanSaveShortcut() =>
        _store is not null &&
        !string.IsNullOrWhiteSpace(GestureText) &&
        GetGestureValidationError(
            out _) is null &&
        (TargetKind == WorkbenchShortcutTargetKind.Command
            ? SelectedCommand is not null
            : SelectedRecipe is not null);

    partial void OnGestureTextChanged(
        string value)
    {
        GestureValidationMessage =
            GetGestureValidationError(
                out _) ??
            "";
    }

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
        SetStatus(
            "Workbench.Shortcuts.Status.Removed",
            gesture);
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
        GestureValidationMessage =
            GetGestureValidationError(
                out _) ??
            "";
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

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        Status = LF(key, arguments);
    }

    private string? GetGestureValidationError(
        out ParsedWorkbenchShortcut? gesture)
    {
        if (!WorkbenchShortcutGestureParser.TryParse(
                GestureText,
                out gesture,
                out string? parserError))
            return parserError ??
                L(
                    "Workbench.Shortcuts.Validation.Invalid");
        ParsedWorkbenchShortcut parsedGesture = gesture!;
        if (ReservedGestures.Contains(
                parsedGesture.Display))
            return LF(
                "Workbench.Shortcuts.Validation.Reserved",
                parsedGesture.Display);
        WorkbenchShortcutRow? conflict =
            Bindings.FirstOrDefault(row =>
                !ReferenceEquals(
                    row,
                    SelectedBinding) &&
                row.Gesture.Equals(
                    parsedGesture.Display,
                    StringComparison.OrdinalIgnoreCase));
        return conflict is null
            ? null
            : LF(
                "Workbench.Shortcuts.Validation.Conflict",
                parsedGesture.Display,
                conflict.Target);
    }

    private void RefreshLocalizedChoices()
    {
        WorkbenchShortcutCommand? selected =
            SelectedCommand?.Command;
        Commands.Clear();
        foreach (WorkbenchShortcutCommand command in
                 Enum.GetValues<WorkbenchShortcutCommand>())
            Commands.Add(new(
                command,
                L($"Workbench.Shortcuts.Command.{command}")));
        if (selected is { } selectedCommand)
            SelectedCommand = Commands.First(
                choice =>
                    choice.Command == selectedCommand);

        foreach (WorkbenchShortcutTargetKind value in
                 TargetKinds)
        {
            LocalizedChoice<WorkbenchShortcutTargetKind>?
                choice = TargetKindChoices.FirstOrDefault(
                    item => item.Value == value);
            string label = L(
                $"Workbench.Choice.ShortcutTargetKind.{value}");
            if (choice is null)
                TargetKindChoices.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        if (_statusKey is not null)
            Status = LF(
                _statusKey,
                _statusArguments);
        GestureValidationMessage =
            GetGestureValidationError(
                out _) ??
            "";
    }
}
