using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record LibraryFilterFieldChoice(
    string Name,
    string Label);

public partial class VisualFilterConditionViewModel :
    ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _group = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private LibraryFilterFieldKind _fieldKind;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private LibraryFilterFieldChoice? _selectedTechnicalField;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private MetadataFieldChoice? _selectedKnownField;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _customFieldName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private LibraryFilterComparison _comparison =
        LibraryFilterComparison.Contains;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _value;
    [ObservableProperty] private bool _negate;

    public string Summary
    {
        get
        {
            string fieldLabel = FieldKind switch
            {
                LibraryFilterFieldKind.Technical =>
                    SelectedTechnicalField?.Label ?? "Technical",
                LibraryFilterFieldKind.KnownMetadata =>
                    SelectedKnownField?.Label ?? "Known",
                _ => CustomFieldName ?? "Custom",
            };
            return $"Group {Group}: {fieldLabel} {Comparison}" +
                (string.IsNullOrWhiteSpace(Value)
                    ? ""
                    : $" {Value}");
        }
    }
}

public partial class VisualFilterEditorViewModel :
    ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveConditionCommand))]
    private VisualFilterConditionViewModel? _selectedCondition;
    [ObservableProperty]
    private LibraryFilterGroupMode _rootMode =
        LibraryFilterGroupMode.All;
    [ObservableProperty] private string _status = "";

    public VisualFilterEditorViewModel()
    {
        FieldKinds = Enum.GetValues<LibraryFilterFieldKind>();
        Comparisons = Enum.GetValues<LibraryFilterComparison>();
        RootModes = Enum.GetValues<LibraryFilterGroupMode>();
        TechnicalFields = DetailsColumns.All
            .Select(column => new LibraryFilterFieldChoice(
                column.Key,
                column.Header))
            .ToArray();
        KnownFields = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new MetadataFieldChoice(
                field,
                field.ToString()))
            .ToArray();
        AddCondition();
    }

    public ObservableCollection<VisualFilterConditionViewModel>
        Conditions { get; } = [];
    public IReadOnlyList<LibraryFilterFieldKind> FieldKinds { get; }
    public IReadOnlyList<LibraryFilterComparison> Comparisons { get; }
    public IReadOnlyList<LibraryFilterGroupMode> RootModes { get; }
    public IReadOnlyList<LibraryFilterFieldChoice>
        TechnicalFields { get; }
    public IReadOnlyList<MetadataFieldChoice> KnownFields { get; }

    [RelayCommand]
    private void AddCondition()
    {
        var condition = new VisualFilterConditionViewModel
        {
            SelectedTechnicalField = TechnicalFields.First(),
            SelectedKnownField = KnownFields.First(),
        };
        Conditions.Add(condition);
        SelectedCondition = condition;
        Status = "Edit the selected condition, then apply the filter.";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCondition))]
    private void RemoveCondition()
    {
        if (SelectedCondition is null)
            return;
        int index = Conditions.IndexOf(SelectedCondition);
        Conditions.Remove(SelectedCondition);
        SelectedCondition = Conditions.Count == 0
            ? null
            : Conditions[Math.Clamp(index, 0, Conditions.Count - 1)];
    }

    private bool CanRemoveCondition() =>
        SelectedCondition is not null;

    public LibraryVisualFilterNode? Build(out string? error)
    {
        error = null;
        if (Conditions.Count == 0)
            return null;
        var built = new List<(
            int Group,
            LibraryFilterCondition Condition)>();
        foreach (VisualFilterConditionViewModel row in Conditions)
        {
            LibraryFilterField? field = Field(row);
            if (field is null)
            {
                error = "Every condition requires a field.";
                return null;
            }
            if (row.Comparison is not
                    LibraryFilterComparison.Present and not
                    LibraryFilterComparison.Missing &&
                string.IsNullOrWhiteSpace(row.Value))
            {
                error =
                    $"{field.Name} requires a comparison value.";
                return null;
            }
            built.Add((
                Math.Max(1, row.Group),
                new(
                    field,
                    row.Comparison,
                    row.Value,
                    row.Negate)));
        }

        LibraryVisualFilterNode[] groups = built
            .GroupBy(item => item.Group)
            .OrderBy(group => group.Key)
            .Select(group =>
                group.Count() == 1
                    ? (LibraryVisualFilterNode)
                        group.First().Condition
                    : new LibraryFilterGroup(
                        LibraryFilterGroupMode.All,
                        [.. group.Select(item =>
                            (LibraryVisualFilterNode)
                                item.Condition)]))
            .ToArray();
        return groups.Length == 1
            ? groups[0]
            : new LibraryFilterGroup(
                RootMode,
                [.. groups]);
    }

    public void Load(LibraryVisualFilterNode? expression)
    {
        Conditions.Clear();
        RootMode = expression is LibraryFilterGroup root
            ? root.Mode
            : LibraryFilterGroupMode.All;
        IEnumerable<(
            int Group,
            LibraryFilterCondition Condition)> flattened =
            Flatten(expression);
        foreach ((int group, LibraryFilterCondition condition) in
                 flattened)
        {
            var row = new VisualFilterConditionViewModel
            {
                Group = group,
                FieldKind = condition.Field.Kind,
                SelectedTechnicalField =
                    TechnicalFields.FirstOrDefault(choice =>
                        choice.Name.Equals(
                            condition.Field.Name,
                            StringComparison.OrdinalIgnoreCase)),
                SelectedKnownField =
                    Enum.TryParse(
                        condition.Field.Name,
                        true,
                        out TagFields known)
                        ? KnownFields.FirstOrDefault(choice =>
                            choice.Field == known)
                        : KnownFields.First(),
                CustomFieldName =
                    condition.Field.Kind ==
                    LibraryFilterFieldKind.CustomMetadata
                        ? condition.Field.Name
                        : null,
                Comparison = condition.Comparison,
                Value = condition.Value,
                Negate = condition.Negate,
            };
            Conditions.Add(row);
        }
        if (Conditions.Count == 0)
            AddCondition();
        else
            SelectedCondition = Conditions[0];
    }

    private LibraryFilterField? Field(
        VisualFilterConditionViewModel row) =>
        row.FieldKind switch
        {
            LibraryFilterFieldKind.Technical =>
                row.SelectedTechnicalField is { } technical
                    ? LibraryFilterField.Technical(
                        technical.Name)
                    : null,
            LibraryFilterFieldKind.KnownMetadata =>
                row.SelectedKnownField is { } known
                    ? LibraryFilterField.Known(known.Field)
                    : null,
            LibraryFilterFieldKind.CustomMetadata =>
                !string.IsNullOrWhiteSpace(row.CustomFieldName)
                    ? LibraryFilterField.Custom(
                        row.CustomFieldName)
                    : null,
            _ => null,
        };

    private static IEnumerable<(
        int Group,
        LibraryFilterCondition Condition)> Flatten(
        LibraryVisualFilterNode? expression)
    {
        if (expression is LibraryFilterCondition condition)
        {
            yield return (1, condition);
            yield break;
        }
        if (expression is not LibraryFilterGroup root)
            yield break;
        int groupNumber = 0;
        foreach (LibraryVisualFilterNode child in root.Children)
        {
            groupNumber++;
            if (child is LibraryFilterCondition direct)
                yield return (groupNumber, direct);
            else if (child is LibraryFilterGroup group)
                foreach (LibraryVisualFilterNode nested in
                         group.Children)
                    if (nested is LibraryFilterCondition item)
                        yield return (groupNumber, item);
        }
    }
}
