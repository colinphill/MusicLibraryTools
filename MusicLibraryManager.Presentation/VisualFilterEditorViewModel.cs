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
                    SelectedTechnicalField?.Label ??
                    LocalizedText.Get(
                        "Library.VisualFilter.Field.Technical"),
                LibraryFilterFieldKind.KnownMetadata =>
                    SelectedKnownField?.Label ??
                    LocalizedText.Get(
                        "Library.VisualFilter.Field.Known"),
                _ => CustomFieldName ??
                    LocalizedText.Get(
                        "Library.VisualFilter.Field.Custom"),
            };
            string summary = LocalizedText.Format(
                "Library.VisualFilter.ConditionSummary",
                Group,
                fieldLabel,
                LocalizedText.Get(
                    $"Library.VisualFilter.Choice.Comparison.{Comparison}"));
            return summary +
                (string.IsNullOrWhiteSpace(Value)
                    ? ""
                    : LocalizedText.Format(
                        "Library.VisualFilter.ConditionValue",
                        Value));
        }
    }

    public void RefreshLocalizedText() =>
        OnPropertyChanged(nameof(Summary));
}

public partial class VisualFilterEditorViewModel :
    ObservableObject
{
    private readonly ILocalizationService? _localization;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveConditionCommand))]
    private VisualFilterConditionViewModel? _selectedCondition;
    [ObservableProperty]
    private LibraryFilterGroupMode _rootMode =
        LibraryFilterGroupMode.All;
    [ObservableProperty] private string _status = "";

    public VisualFilterEditorViewModel(
        ILocalizationService? localization = null)
    {
        _localization = localization;
        FieldKinds = Enum.GetValues<LibraryFilterFieldKind>();
        Comparisons = Enum.GetValues<LibraryFilterComparison>();
        RootModes = Enum.GetValues<LibraryFilterGroupMode>();
        RefreshLocalizedChoices();
        AddCondition();
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public ObservableCollection<VisualFilterConditionViewModel>
        Conditions { get; } = [];
    public IReadOnlyList<LibraryFilterFieldKind> FieldKinds { get; }
    public IReadOnlyList<LibraryFilterComparison> Comparisons { get; }
    public IReadOnlyList<LibraryFilterGroupMode> RootModes { get; }
    public ObservableCollection<LibraryFilterFieldChoice>
        TechnicalFields { get; } = [];
    public ObservableCollection<MetadataFieldChoice>
        KnownFields { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryFilterFieldKind>>
        FieldKindChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryFilterComparison>>
        ComparisonChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<LibraryFilterGroupMode>>
        RootModeChoices { get; } = [];

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
        Status = L(
            "Library.VisualFilter.Editor.Status.EditCondition");
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
                error = L(
                    "Library.VisualFilter.Editor.Error.FieldRequired");
                return null;
            }
            if (row.Comparison is not
                    LibraryFilterComparison.Present and not
                    LibraryFilterComparison.Missing &&
                string.IsNullOrWhiteSpace(row.Value))
            {
                error = LF(
                    "Library.VisualFilter.Editor.Error.ValueRequired",
                    field.Name);
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

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private void RefreshLocalizedChoices()
    {
        string? technicalName =
            SelectedCondition?.SelectedTechnicalField?.Name;
        TagFields? knownField =
            SelectedCondition?.SelectedKnownField?.Field;
        TechnicalFields.Clear();
        foreach (DetailsColumn column in DetailsColumns.All)
            TechnicalFields.Add(new(
                column.Key,
                L($"Column.{column.Key}")));
        KnownFields.Clear();
        foreach (TagFields field in Enum.GetValues<TagFields>()
                     .Where(field =>
                         field != TagFields.NullField))
            KnownFields.Add(new(
                field,
                L($"Settings.Choice.TagFields.{field}")));
        RefreshChoices(
            FieldKindChoices,
            FieldKinds,
            "Library.VisualFilter.Choice.FieldKind");
        RefreshChoices(
            ComparisonChoices,
            Comparisons,
            "Library.VisualFilter.Choice.Comparison");
        RefreshChoices(
            RootModeChoices,
            RootModes,
            "Library.VisualFilter.Choice.RootMode");

        foreach (VisualFilterConditionViewModel condition in
                 Conditions)
        {
            string? selectedTechnical =
                condition.SelectedTechnicalField?.Name ??
                technicalName;
            TagFields? selectedKnown =
                condition.SelectedKnownField?.Field ??
                knownField;
            condition.SelectedTechnicalField =
                TechnicalFields.FirstOrDefault(choice =>
                    choice.Name.Equals(
                        selectedTechnical,
                        StringComparison.OrdinalIgnoreCase)) ??
                TechnicalFields.FirstOrDefault();
            condition.SelectedKnownField =
                selectedKnown is { } known
                    ? KnownFields.FirstOrDefault(choice =>
                        choice.Field == known)
                    : KnownFields.FirstOrDefault();
            condition.RefreshLocalizedText();
        }
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        string keyPrefix)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = L($"{keyPrefix}.{value}");
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        Status = L(
            "Library.VisualFilter.Editor.Status.EditCondition");
    }
}
