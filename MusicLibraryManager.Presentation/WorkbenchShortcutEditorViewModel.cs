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

public enum WorkbenchShortcutPlatform
{
    Windows,
    MacOS,
    Linux,
}

public sealed class WorkbenchShortcutCommandChoice(
    WorkbenchShortcutCommand command,
    string label) : ObservableObject
{
    private string _label = label;

    public WorkbenchShortcutCommand Command { get; } =
        command;

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public override string ToString() => Label;
}

public sealed class WorkbenchShortcutRow(
    WorkbenchShortcutBinding binding,
    ILocalizationService? localization = null) :
    ObservableObject
{
    public WorkbenchShortcutBinding Binding { get; } =
        binding;
    public string Gesture => Binding.Gesture;
    public string Target =>
        Binding.Command is { } command
            ? localization?.Get(
                  $"Workbench.Shortcuts.Command.{command}") ??
              LocalizedText.Get(
                  $"Workbench.Shortcuts.Command.{command}")
            : Binding.TargetLabel ??
              localization?.Get(
                  "Workbench.Shortcuts.Target.Recipe") ??
              LocalizedText.Get(
                  "Workbench.Shortcuts.Target.Recipe");

    public void RefreshLocalization() =>
        OnPropertyChanged(nameof(Target));
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
        out string? error,
        ILocalizationService? localization = null)
    {
        string L(string key) =>
            localization?.Get(key) ??
            LocalizedText.Get(key);
        string LF(
            string key,
            params object?[] arguments) =>
            localization?.Format(
                key,
                arguments) ??
            LocalizedText.Format(
                key,
                arguments);

        gesture = null;
        error = null;
        string[] parts = (value ?? "")
            .Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = L(
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
                    error = LF(
                        "Workbench.Shortcuts.Validation.RepeatedModifier",
                        part);
                    return false;
                }
                modifiers |= modifier;
                continue;
            }
            if (key is not null)
            {
                error = L(
                    "Workbench.Shortcuts.Validation.OneKey");
                return false;
            }
            key = part;
        }
        if (modifiers == WorkbenchShortcutModifiers.None ||
            string.IsNullOrWhiteSpace(key))
        {
            error = L(
                "Workbench.Shortcuts.Validation.ModifierAndKeyRequired");
            return false;
        }
        if (!KeyName.IsMatch(key))
        {
            error = L(
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
    private readonly IWorkbenchShortcutStore? _store;
    private readonly IOperationRecipeStore? _recipes;
    private readonly ILocalizationService? _localization;
    private readonly WorkbenchShortcutPlatform _platform;
    private readonly HashSet<string> _reservedGestures;
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
        ILocalizationService? localization = null,
        WorkbenchShortcutPlatform? platform = null)
    {
        _store = store;
        _recipes = recipes;
        _localization = localization;
        _platform = platform ??
            ResolveCurrentPlatform();
        _reservedGestures = new(
            [
                $"{PrimaryModifier}+K",
                $"{PrimaryModifier}+I",
            ],
            StringComparer.OrdinalIgnoreCase);
        GestureText = DefaultGesture;
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
    public WorkbenchShortcutPlatform Platform =>
        _platform;
    public string PrimaryModifier =>
        _platform == WorkbenchShortcutPlatform.MacOS
            ? "Meta"
            : "Ctrl";
    public string DefaultGesture =>
        $"{PrimaryModifier}+Shift+P";
    public string GestureHelpText =>
        AdaptPrimaryModifier(
            L("Workbench.Shortcuts.GestureHelp"));
    public string InputWarningText =>
        AdaptPrimaryModifier(
            L("Workbench.Shortcuts.InputWarning"));
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
            GestureText = DefaultGesture;
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
        string targetLabel;
        if (TargetKind == WorkbenchShortcutTargetKind.Command &&
            SelectedCommand is not null)
        {
            targetLabel = SelectedCommand.Label;
            binding = new(
                SelectedBinding?.Binding.Id ?? Guid.NewGuid(),
                parsedGesture.Display,
                TargetKind,
                SelectedCommand.Command);
        }
        else if (
            TargetKind == WorkbenchShortcutTargetKind.Recipe &&
            SelectedRecipe is not null)
        {
            targetLabel = SelectedRecipe.Name;
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
            targetLabel);
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
                    out _,
                    _localization) &&
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
            Bindings.Add(new(
                binding,
                _localization));
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
                out string? parserError,
                _localization))
            return AdaptPrimaryModifier(
                parserError ??
                L(
                    "Workbench.Shortcuts.Validation.Invalid"));
        ParsedWorkbenchShortcut parsedGesture = gesture!;
        if (_reservedGestures.Contains(
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
        foreach (WorkbenchShortcutCommand command in
                 Enum.GetValues<WorkbenchShortcutCommand>())
        {
            WorkbenchShortcutCommandChoice? choice =
                Commands.FirstOrDefault(
                    item => item.Command == command);
            string label = L(
                $"Workbench.Shortcuts.Command.{command}");
            if (choice is null)
                Commands.Add(new(command, label));
            else
                choice.Label = label;
        }
        if (selected is { } selectedCommand)
            SelectedCommand = Commands.Single(
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
        foreach (WorkbenchShortcutRow row in Bindings)
            row.RefreshLocalization();
        if (_statusKey is not null)
            Status = LF(
                _statusKey,
                _statusArguments);
        OnPropertyChanged(nameof(GestureHelpText));
        OnPropertyChanged(nameof(InputWarningText));
        GestureValidationMessage =
            GetGestureValidationError(
                out _) ??
            "";
    }

    private string AdaptPrimaryModifier(
        string text) =>
        _platform == WorkbenchShortcutPlatform.MacOS
            ? text.Replace(
                "Ctrl+",
                "Meta+",
                StringComparison.Ordinal)
            : text;

    private static WorkbenchShortcutPlatform
        ResolveCurrentPlatform() =>
        OperatingSystem.IsMacOS()
            ? WorkbenchShortcutPlatform.MacOS
            : OperatingSystem.IsWindows()
                ? WorkbenchShortcutPlatform.Windows
                : WorkbenchShortcutPlatform.Linux;
}
