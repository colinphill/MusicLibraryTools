using System.Collections.Immutable;
using System.Text.Json;

namespace MusicLibrary.Core.Services;

public enum WorkbenchShortcutTargetKind
{
    Command,
    Recipe,
}

public enum WorkbenchShortcutCommand
{
    AddFiles,
    AddFolder,
    PreviewInlineEdits,
    PreviewCurrentRecipe,
    ApplyReviewedChanges,
    UndoLastApply,
    Redo,
    RepeatLastRecipe,
    CancelCurrentOperation,
}

public sealed record WorkbenchShortcutBinding(
    Guid Id,
    string Gesture,
    WorkbenchShortcutTargetKind TargetKind,
    WorkbenchShortcutCommand? Command = null,
    Guid? RecipeId = null,
    string? TargetLabel = null);

public interface IWorkbenchShortcutStore
{
    IReadOnlyList<WorkbenchShortcutBinding> Load();
    void Save(IReadOnlyList<WorkbenchShortcutBinding> bindings);
}

public sealed class WorkbenchShortcutStore(IAppSettings settings) :
    IWorkbenchShortcutStore
{
    public const string PreferenceKey =
        "manager.workbench.shortcuts.v1";
    private const int MaximumShortcuts = 100;
    private readonly object _sync = new();

    public IReadOnlyList<WorkbenchShortcutBinding> Load()
    {
        lock (_sync)
        {
            try
            {
                string? json =
                    settings.GetPreference(PreferenceKey);
                if (string.IsNullOrWhiteSpace(json))
                    return [];
                StoredShortcuts? stored =
                    JsonSerializer.Deserialize<StoredShortcuts>(json);
                return stored?.Version == 1
                    ? stored.Bindings
                        .Where(IsStructurallyValid)
                        .Take(MaximumShortcuts)
                        .ToArray()
                    : [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Save(
        IReadOnlyList<WorkbenchShortcutBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaximumShortcuts)
            throw new ArgumentOutOfRangeException(
                nameof(bindings),
                $"At most {MaximumShortcuts} shortcuts can be saved.");
        if (bindings.Any(binding =>
                !IsStructurallyValid(binding)))
            throw new ArgumentException(
                "Every shortcut requires a gesture and exactly one target.",
                nameof(bindings));
        lock (_sync)
            settings.SetPreference(
                PreferenceKey,
                JsonSerializer.Serialize(new StoredShortcuts(
                    1,
                    [.. bindings])));
    }

    private static bool IsStructurallyValid(
        WorkbenchShortcutBinding binding)
    {
        if (binding.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(binding.Gesture))
            return false;
        return binding.TargetKind switch
        {
            WorkbenchShortcutTargetKind.Command =>
                binding.Command is not null &&
                binding.RecipeId is null,
            WorkbenchShortcutTargetKind.Recipe =>
                binding.Command is null &&
                binding.RecipeId.HasValue &&
                binding.RecipeId.Value != Guid.Empty,
            _ => false,
        };
    }

    private sealed record StoredShortcuts(
        int Version,
        ImmutableArray<WorkbenchShortcutBinding> Bindings);
}
