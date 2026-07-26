using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed partial class MetadataFieldChoice(
    TagFields field,
    string label) : ObservableObject
{
    public TagFields Field { get; } = field;

    [ObservableProperty]
    private string _label = label;
}

public sealed record MetadataPreviewRow(
    string File,
    string Field,
    string Before,
    string After,
    string? DiagnosticDetail = null)
{
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DiagnosticDetail);
}

public sealed record PendingMetadataOperationRow(
    int Number,
    string Operation,
    string Target,
    string AppliesTo);

public static class PendingMetadataOperationRowBuilder
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static void Populate(
        ObservableCollection<PendingMetadataOperationRow> destination,
        MetadataOperationPlan plan,
        ILocalizationService? localization = null)
    {
        destination.Clear();
        if (plan.Recipe is { } recipe)
        {
            IEnumerable<OperationRecipeStep> steps = recipe.Steps.Length > 0
                ? recipe.Steps
                : recipe.Operations.Select((operation, index) =>
                    new OperationRecipeStep(
                        Guid.Empty,
                        Format(
                            localization,
                            "MetadataEditor.Pending.DefaultStepName",
                            index + 1),
                        operation));
            foreach (OperationRecipeStep step in steps.Where(step => step.Enabled))
                destination.Add(new(
                    destination.Count + 1,
                    step.Name,
                    Describe(
                        step.Operation,
                        localization),
                    FormatCount(
                        localization,
                        "MetadataEditor.Pending.ChangedFiles",
                        plan.ChangedFileCount)));
            return;
        }

        AddValueEdits(
            destination,
            plan,
            localization);

        int artworkFiles = plan.Files.Count(file =>
            file.ArtworkDifference is not null);
        Add(
            destination,
            artworkFiles,
            Text(
                localization,
                "MetadataEditor.Pending.Operation.UpdateArtwork"),
            Text(
                localization,
                "MetadataEditor.Pending.Target.Artwork"),
            localization);

        foreach (var group in plan.Files
                     .SelectMany(file => file.TagLayerDifferences.IsDefaultOrEmpty
                         ? []
                         : file.TagLayerDifferences.Select(
                             difference => (file, difference)))
                     .GroupBy(item => (
                         item.difference.Kind,
                         item.difference.WillBePresent)))
            Add(
                destination,
                group.Select(item => item.file.Path)
                    .Distinct(PathComparer)
                    .Count(),
                Text(
                    localization,
                    group.Key.WillBePresent
                        ? "MetadataEditor.Pending.Operation.AddTagLayer"
                        : "MetadataEditor.Pending.Operation.RemoveTagLayer"),
                DescribeLayer(group.Key.Kind),
                localization);

        foreach (var group in plan.Files
                     .Where(file => file.Id3VersionDifference is not null)
                     .GroupBy(file => (
                         file.Id3VersionDifference!.TargetVersion,
                         file.Id3VersionDifference.TextEncodingPolicy)))
            Add(
                destination,
                group.Count(),
                Text(
                    localization,
                    group.Key.TextEncodingPolicy is null
                        ? "MetadataEditor.Pending.Operation.ChangeId3Version"
                        : "MetadataEditor.Pending.Operation.ChangeId3VersionOrEncoding"),
                $"ID3v2.{(int)group.Key.TargetVersion}" +
                (group.Key.TextEncodingPolicy is null
                    ? ""
                    : $" · {Text(
                        localization,
                        TechnicalLabelResourceKeys.For(
                            group.Key.TextEncodingPolicy.Value) ??
                        $"MetadataEditor.Choice.Id3EncodingPolicy.{group.Key.TextEncodingPolicy}")}"),
                localization);

        foreach (var group in plan.Files
                     .SelectMany(file =>
                         file.TagLayerConversionDifferences.IsDefaultOrEmpty
                             ? []
                             : file.TagLayerConversionDifferences.Select(
                                 difference => (file, difference)))
                     .GroupBy(item => (
                         item.difference.Source,
                         item.difference.Target)))
            Add(
                destination,
                group.Select(item => item.file.Path)
                    .Distinct(PathComparer)
                    .Count(),
                Text(
                    localization,
                    "MetadataEditor.Pending.Operation.ConvertTagLayer"),
                $"{DescribeLayer(group.Key.Source)} → " +
                DescribeLayer(group.Key.Target),
                localization);
    }

    private static void AddValueEdits(
        ObservableCollection<PendingMetadataOperationRow> destination,
        MetadataOperationPlan plan,
        ILocalizationService? localization)
    {
        foreach (var group in plan.Files
                     .SelectMany(file => file.Edits.Select(edit => (file, edit)))
                     .GroupBy(item => (
                         item.edit.Field,
                         Remove: item.edit.Values.Length == 0)))
            Add(
                destination,
                group.Select(item => item.file.Path)
                    .Distinct(PathComparer)
                    .Count(),
                Text(
                    localization,
                    group.Key.Remove
                        ? "MetadataEditor.Pending.Operation.RemoveField"
                        : "MetadataEditor.Pending.Operation.SetField"),
                FieldName(
                    group.Key.Field,
                    localization),
                localization);
    }

    private static void Add(
        ObservableCollection<PendingMetadataOperationRow> destination,
        int files,
        string operation,
        string target,
        ILocalizationService? localization)
    {
        if (files == 0)
            return;
        destination.Add(new(
            destination.Count + 1,
            operation,
            target,
            FormatCount(
                localization,
                "MetadataEditor.Pending.Files",
                files)));
    }

    private static string Describe(
        MetadataOperation operation,
        ILocalizationService? localization) =>
        operation switch
    {
        AssignFieldOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Set",
                FieldName(value.Field, localization),
                Short(value.Value)),
        RemoveFieldOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Remove",
                FieldName(value.Field, localization)),
        CopyFieldOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Copy",
                FieldName(value.Source, localization),
                FieldName(value.Destination, localization)),
        ReplaceTextOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Replace",
                Short(value.Search),
                FieldName(value.Field, localization)),
        ChangeCaseOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.ChangeCase",
                FieldName(value.Field, localization),
                Text(
                    localization,
                    $"MetadataEditor.Choice.CaseMode.{value.Mode}")),
        TrimFieldOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Trim",
                FieldName(value.Field, localization)),
        SequenceNumberOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Sequence",
                FieldName(value.Field, localization),
                value.Start),
        CombineFieldsOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Combine",
                FieldName(value.First, localization),
                FieldName(value.Second, localization),
                FieldName(value.Destination, localization)),
        SplitFieldOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Split",
                FieldName(value.Field, localization),
                Short(value.Separator)),
        JoinFieldValuesOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Join",
                FieldName(value.Field, localization),
                Short(value.Separator)),
        DeduplicateFieldValuesOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Deduplicate",
                FieldName(value.Field, localization)),
        ReorderFieldValuesOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Reorder",
                FieldName(value.Field, localization),
                Text(
                    localization,
                    $"MetadataEditor.Choice.ValueOrder.{value.Order}")),
        ExtractPathComponentOperation value =>
            Format(
                localization,
                "MetadataEditor.Pending.Description.Extract",
                FieldName(value.Field, localization),
                Text(
                    localization,
                    $"MetadataEditor.Choice.PathComponent.{value.Component}")),
        _ => Format(
            localization,
            "MetadataEditor.Pending.Description.Unknown",
            operation.GetType().Name),
    };

    private static string Short(string value) =>
        value.Length <= 32 ? value : value[..29] + "…";

    private static string DescribeLayer(TagLayerKind kind) => kind switch
    {
        TagLayerKind.Id3v2 => "ID3v2",
        TagLayerKind.Id3v1 => "ID3v1",
        TagLayerKind.ApeV2 => "APEv2",
        _ => kind.ToString(),
    };

    private static string FieldName(
        MetadataFieldKey field,
        ILocalizationService? localization) =>
        field.KnownField is { } known
            ? Text(
                localization,
                $"Settings.Choice.TagFields.{known}")
            : field.CustomName ?? "";

    private static string Text(
        ILocalizationService? localization,
        string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private static string Format(
        ILocalizationService? localization,
        string key,
        params object?[] arguments) =>
        localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private static string FormatCount(
        ILocalizationService? localization,
        string key,
        long count,
        params object?[] arguments) =>
        localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);
}

public static class MetadataPreviewRowBuilder
{
    public static void Populate(
        ObservableCollection<MetadataPreviewRow> destination,
        MetadataOperationPlan plan,
        ILocalizationService? localization = null)
    {
        destination.Clear();
        foreach (MetadataFilePlan file in plan.Files)
        {
            foreach (MetadataFieldDifference difference in file.Differences)
            {
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    DisplayFieldName(
                        difference.Field,
                        localization),
                    string.Join("; ", difference.Before),
                    string.Join("; ", difference.After)));
            }

            if (file.ArtworkDifference is { } artwork)
            {
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    Text(
                        localization,
                        "MetadataEditor.Pending.Target.Artwork"),
                    DescribeArtwork(
                        artwork.Before,
                        localization),
                    DescribeArtwork(
                        artwork.After,
                        localization)));
            }

            if (!file.TagLayerDifferences.IsDefaultOrEmpty)
            {
                foreach (TagLayerDifference difference in
                         file.TagLayerDifferences)
                {
                    destination.Add(new(
                        Path.GetFileName(file.Path),
                        Format(
                            localization,
                            "MetadataEditor.Preview.TagLayerField",
                            DescribeTagLayer(difference.Kind)),
                        Text(
                            localization,
                            difference.WasPresent
                                ? "MetadataEditor.Preview.Present"
                                : "MetadataEditor.Preview.Absent"),
                        Text(
                            localization,
                            difference.WillBePresent
                                ? "MetadataEditor.Preview.Present"
                                : "MetadataEditor.Preview.Absent")));
    }
}

            if (file.Id3VersionDifference is { } version)
            {
                string issueSummary = version.Issues.Length == 0
                    ? ""
                    : FormatCount(
                        localization,
                        "MetadataEditor.Preview.CompatibilityIssues",
                        version.Issues.Length);
                string encoding = version.TextEncodingPolicy is null
                    ? ""
                    : Format(
                        localization,
                        "MetadataEditor.Preview.EncodingSuffix",
                        Text(
                            localization,
                            TechnicalLabelResourceKeys.For(
                                version.TextEncodingPolicy.Value) ??
                            $"MetadataEditor.Choice.Id3EncodingPolicy.{version.TextEncodingPolicy}"));
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    version.SourceVersion == version.TargetVersion
                        ? Text(
                            localization,
                            "MetadataEditor.Preview.Id3TextEncoding")
                        : Text(
                            localization,
                            "MetadataEditor.Preview.Id3Version"),
                    $"ID3v2.{(int)version.SourceVersion}",
                    $"ID3v2.{(int)version.TargetVersion}{encoding}{issueSummary}"));
            }

            if (!file.TagLayerConversionDifferences.IsDefaultOrEmpty)
            {
                foreach (TagLayerConversionDifference conversion in
                         file.TagLayerConversionDifferences)
                {
                    string issueSummary =
                        conversion.CompatibilityIssues.Length == 0
                            ? ""
                            : FormatCount(
                                localization,
                                "MetadataEditor.Preview.CompatibilityIssues",
                                conversion.CompatibilityIssues.Length);
                    destination.Add(new(
                        Path.GetFileName(file.Path),
                        Text(
                            localization,
                            "MetadataEditor.Preview.TagLayerConversion"),
                        DescribeTagLayer(conversion.Source),
                        $"{DescribeTagLayer(conversion.Target)}{issueSummary}"));
                }
            }
        }
    }

    private static string DescribeArtwork(
        IReadOnlyList<ArtworkDescriptor> images,
        ILocalizationService? localization) =>
        images.Count == 0
            ? Text(
                localization,
                "MetadataEditor.Preview.None")
            : string.Join("; ", images.Select(image =>
                $"{Text(
                    localization,
                    $"Inspector.Artwork.Type.{image.Type}.Label")} " +
                $"{image.MimeType} " +
                FormatCount(
                    localization,
                    "MetadataEditor.Preview.Bytes",
                    image.Size) +
                (string.IsNullOrWhiteSpace(image.Description)
                    ? ""
                    : $" ({image.Description})")));

    private static string DescribeTagLayer(TagLayerKind kind) => kind switch
    {
        TagLayerKind.Id3v2 => "ID3v2",
        TagLayerKind.Id3v1 => "ID3v1",
        TagLayerKind.ApeV2 => "APEv2",
        _ => kind.ToString(),
    };

    public static string DisplayFieldName(
        MetadataFieldKey field,
        ILocalizationService? localization) =>
        field.KnownField is { } known
            ? Text(
                localization,
                $"Settings.Choice.TagFields.{known}")
            : field.CustomName ?? "";

    private static string Text(
        ILocalizationService? localization,
        string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private static string Format(
        ILocalizationService? localization,
        string key,
        params object?[] arguments) =>
        localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private static string FormatCount(
        ILocalizationService? localization,
        string key,
        long count,
        params object?[] arguments) =>
        localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);
}

public static class MetadataOperationPlanComposer
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static MetadataOperationPlan Combine(
        string name,
        params MetadataOperationPlan?[] plans)
    {
        MetadataOperationPlan[] available = plans
            .Where(plan => plan is not null)
            .Cast<MetadataOperationPlan>()
            .ToArray();
        if (available.Length == 0)
            throw new ArgumentException(
                LocalizedText.Get(
                    "MetadataEditor.Validation.PlanRequired"),
                nameof(plans));
        if (available.Length == 1)
            return available[0];

        MetadataFilePlan[] files = available
            .SelectMany(plan => plan.Files)
            .GroupBy(file => file.Path, PathComparer)
            .Select(group =>
            {
                MetadataFilePlan[] parts = group.ToArray();
                MetadataFilePlan first = parts[0];
                return new MetadataFilePlan(
                    first.Path,
                    first.Snapshot,
                    [.. parts.SelectMany(part =>
                        part.Differences)],
                    [.. parts.SelectMany(part =>
                        part.Edits)],
                    [.. parts.SelectMany(part =>
                            part.Issues)
                        .Distinct()],
                    parts.Select(part => part.ArtworkEdit)
                        .LastOrDefault(value => value is not null),
                    parts.Select(part => part.ArtworkDifference)
                        .LastOrDefault(value => value is not null),
                    [.. parts.SelectMany(part =>
                        part.TagLayerEdits.IsDefault
                            ? []
                            : part.TagLayerEdits)],
                    [.. parts.SelectMany(part =>
                        part.TagLayerDifferences.IsDefault
                            ? []
                            : part.TagLayerDifferences)],
                    parts.Select(part => part.Id3VersionEdit)
                        .LastOrDefault(value => value is not null),
                    parts.Select(part => part.Id3VersionDifference)
                        .LastOrDefault(value => value is not null),
                    [.. parts.SelectMany(part =>
                        part.TagLayerConversions.IsDefault
                            ? []
                            : part.TagLayerConversions)],
                    [.. parts.SelectMany(part =>
                        part.TagLayerConversionDifferences.IsDefault
                            ? []
                            : part.TagLayerConversionDifferences)]);
            })
            .ToArray();
        return new(
            Guid.NewGuid(),
            name,
            [.. files],
            DateTimeOffset.UtcNow);
    }
}

public partial class MetadataRecipeStepViewModel(
    Guid id,
    string name,
    MetadataOperation operation,
    bool enabled = true) : ObservableObject
{
    public Guid Id { get; } = id;
    public MetadataOperation Operation { get; } = operation;

    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private bool _enabled = enabled;

    public OperationRecipeStep ToModel() => new(Id, Name, Operation, Enabled);
}

/// <summary>
/// Shared typed-operation editor state. Workbench and Library own separate instances so personal
/// in-progress input does not leak between pages, while both are driven by one Core catalog.
/// </summary>
public partial class MetadataOperationEditorViewModel : ObservableObject
{
    private readonly IMetadataOperationCatalog _catalog;
    private readonly IOperationRecipeStore? _recipes;
    private readonly ILocalizationService? _localization;
    private Guid? _editingRecipeId;
    private string _defaultRecipeName =
        LocalizedText.Get(
            "MetadataEditor.Recipe.NewName");

    public MetadataOperationEditorViewModel(
        IMetadataOperationCatalog catalog,
        MetadataOperationSurface surface,
        IOperationRecipeStore? recipes = null,
        ILocalizationService? localization = null)
    {
        _catalog = catalog;
        _recipes = recipes;
        _localization = localization;
        _defaultRecipeName = Text(
            "MetadataEditor.Recipe.NewName");
        RecipeName = _defaultRecipeName;
        OperationDescriptors = catalog.Operations
            .Where(operation => operation.Supports(surface))
            .ToArray();
        Fields =
        [
            .. EditorFields.Select(field => new MetadataFieldChoice(
                field,
                FieldLabel(field))),
        ];
        RefreshLocalizedChoices();
        SelectedOperation = OperationDescriptors.FirstOrDefault();
        SelectedField = Fields[0];
        DestinationField = Fields[1];
        SecondaryField = Fields[1];
        SelectedConditionField = Fields[0];
        ReloadRecipes();
        if (_recipes is not null)
            _recipes.Changed += (_, _) => ReloadRecipes();
        PropertyChanged += OnEditorPropertyChanged;
        Steps.CollectionChanged += OnStepsChanged;
        _localization?.CultureChanged +=
            OnLocalizationCultureChanged;
    }

    public ObservableCollection<MetadataRecipeStepViewModel> Steps { get; } = [];
    public ObservableCollection<OperationRecipe> SavedRecipes { get; } = [];
    public event Action? Changed;
    public IReadOnlyList<MetadataOperationDescriptor> OperationDescriptors { get; }
    public ObservableCollection<
        LocalizedChoice<MetadataOperationDescriptor>>
        OperationChoices { get; } = [];
    public IReadOnlyList<MetadataFieldChoice> Fields { get; }
    public IReadOnlyList<MetadataCaseMode> CaseModes { get; } =
        Enum.GetValues<MetadataCaseMode>();
    public IReadOnlyList<MetadataValueOrder> ValueOrders { get; } =
        Enum.GetValues<MetadataValueOrder>();
    public IReadOnlyList<MetadataPathComponent> PathComponents { get; } =
        Enum.GetValues<MetadataPathComponent>();
    public IReadOnlyList<MetadataConditionOperator> ConditionOperators { get; } =
        Enum.GetValues<MetadataConditionOperator>()
            .Where(value => value != MetadataConditionOperator.Always)
            .ToArray();
    public ObservableCollection<
        LocalizedChoice<MetadataCaseMode>>
        CaseModeChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<MetadataValueOrder>>
        ValueOrderChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<MetadataPathComponent>>
        PathComponentChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<MetadataConditionOperator>>
        ConditionOperatorChoices { get; } = [];

    [ObservableProperty]
    private MetadataOperationDescriptor? _selectedOperation;

    public bool ShowDestinationField =>
        SelectedOperation?.Kind is
            MetadataOperationKind.Copy or
            MetadataOperationKind.Combine;
    public bool ShowSecondaryField =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.Combine;
    public bool ShowValue =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.Assign;
    public bool ShowTextReplacement =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.ReplaceText;
    public bool ShowCase =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.ChangeCase;
    public bool ShowSeparator =>
        SelectedOperation?.Kind is
            MetadataOperationKind.Combine or
            MetadataOperationKind.Split or
            MetadataOperationKind.Join;
    public bool ShowRegularExpression =>
        SelectedOperation?.Kind is
            MetadataOperationKind.ReplaceText or
            MetadataOperationKind.Split;
    public bool ShowSequence =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.Sequence;
    public bool ShowValueOrder =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.Reorder;
    public bool ShowPathExtraction =>
        SelectedOperation?.Kind ==
        MetadataOperationKind.ExtractPathComponent;

    [ObservableProperty]
    private MetadataFieldChoice? _selectedField;

    [ObservableProperty]
    private MetadataFieldChoice? _destinationField;

    [ObservableProperty]
    private MetadataFieldChoice? _secondaryField;

    [ObservableProperty]
    private string? _operationValue;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _replacementText;

    [ObservableProperty]
    private string _separator = "; ";

    [ObservableProperty]
    private bool _useRegularExpression;

    [ObservableProperty]
    private MetadataCaseMode _selectedCaseMode = MetadataCaseMode.Title;

    [ObservableProperty]
    private MetadataValueOrder _selectedValueOrder = MetadataValueOrder.Ascending;

    [ObservableProperty]
    private MetadataPathComponent _selectedPathComponent =
        MetadataPathComponent.FileNameWithoutExtension;

    [ObservableProperty]
    private int _parentLevel = 1;

    [ObservableProperty]
    private string? _extractionPattern;

    [ObservableProperty]
    private string _extractionGroup = "value";

    [ObservableProperty]
    private int _sequenceStart = 1;

    [ObservableProperty]
    private int _sequencePadding = 2;

    [ObservableProperty]
    private bool _conditionEnabled;

    [ObservableProperty]
    private MetadataFieldChoice? _selectedConditionField;

    [ObservableProperty]
    private MetadataConditionOperator _selectedConditionOperator =
        MetadataConditionOperator.Present;

    [ObservableProperty]
    private string? _conditionValue;

    [ObservableProperty]
    private bool _negateCondition;

    [ObservableProperty]
    private string _recipeName =
        LocalizedText.Get(
            "MetadataEditor.Recipe.NewName");

    [ObservableProperty]
    private OperationRecipe? _selectedSavedRecipe;

    [ObservableProperty]
    private MetadataRecipeStepViewModel? _selectedStep;

    public bool CanCreate => SelectedOperation is not null && SelectedField is not null;

    partial void OnSelectedOperationChanged(
        MetadataOperationDescriptor? value)
    {
        OnPropertyChanged(nameof(ShowDestinationField));
        OnPropertyChanged(nameof(ShowSecondaryField));
        OnPropertyChanged(nameof(ShowValue));
        OnPropertyChanged(nameof(ShowTextReplacement));
        OnPropertyChanged(nameof(ShowCase));
        OnPropertyChanged(nameof(ShowSeparator));
        OnPropertyChanged(nameof(ShowRegularExpression));
        OnPropertyChanged(nameof(ShowSequence));
        OnPropertyChanged(nameof(ShowValueOrder));
        OnPropertyChanged(nameof(ShowPathExtraction));
    }

    public OperationRecipe CreateRecipe(string? name = null)
    {
        if (Steps.Count == 0)
            return OperationRecipe.Create(
                name ?? Format(
                    "MetadataEditor.Recipe.OperationName",
                    SelectedOperation is null
                        ? ""
                        : OperationLabel(
                            SelectedOperation),
                    SelectedField?.Label ?? ""),
                CreateCurrentOperation());
        return OperationRecipe.FromSteps(
            _editingRecipeId ?? Guid.NewGuid(),
            name ?? RecipeName.Trim(),
            Steps.Select(step => step.ToModel()));
    }

    [RelayCommand]
    private void AddCurrentOperation()
    {
        MetadataOperation operation = CreateCurrentOperation();
        string name = Format(
            "MetadataEditor.Recipe.OperationName",
            OperationLabel(
                SelectedOperation!),
            SelectedField!.Label);
        Steps.Add(new(Guid.NewGuid(), name, operation));
    }

    [RelayCommand]
    private void DuplicateStep(MetadataRecipeStepViewModel? step)
    {
        step ??= SelectedStep;
        if (step is null)
            return;
        int index = Steps.IndexOf(step);
        Steps.Insert(index + 1, new(
            Guid.NewGuid(),
            Format(
                "MetadataEditor.Recipe.CopyName",
                step.Name),
            step.Operation,
            step.Enabled));
    }

    [RelayCommand]
    private void RemoveStep(MetadataRecipeStepViewModel? step)
    {
        step ??= SelectedStep;
        if (step is not null)
            Steps.Remove(step);
    }

    [RelayCommand]
    private void MoveStepUp(MetadataRecipeStepViewModel? step)
    {
        step ??= SelectedStep;
        int index = step is null ? -1 : Steps.IndexOf(step);
        if (index > 0)
            Steps.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveStepDown(MetadataRecipeStepViewModel? step)
    {
        step ??= SelectedStep;
        int index = step is null ? -1 : Steps.IndexOf(step);
        if (index >= 0 && index < Steps.Count - 1)
            Steps.Move(index, index + 1);
    }

    [RelayCommand]
    private void NewRecipe()
    {
        _editingRecipeId = null;
        RecipeName = Text(
            "MetadataEditor.Recipe.NewName");
        Steps.Clear();
        SelectedSavedRecipe = null;
    }

    [RelayCommand]
    private void LoadRecipe()
    {
        if (SelectedSavedRecipe is null)
            return;
        _editingRecipeId = SelectedSavedRecipe.Id;
        RecipeName = SelectedSavedRecipe.Name;
        Steps.Clear();
        IEnumerable<OperationRecipeStep> steps =
            SelectedSavedRecipe.Steps.IsDefaultOrEmpty
                ? SelectedSavedRecipe.Operations.Select((operation, index) =>
                    new OperationRecipeStep(
                        Guid.NewGuid(),
                        Format(
                            "MetadataEditor.Pending.DefaultStepName",
                            index + 1),
                        operation))
                : SelectedSavedRecipe.Steps;
        foreach (OperationRecipeStep step in steps)
            Steps.Add(new(step.Id, step.Name, step.Operation, step.Enabled));
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        if (_recipes is null)
            return;
        string name = RecipeName.Trim();
        if (name.Length == 0 || Steps.Count == 0)
            return;
        OperationRecipe recipe = CreateRecipe(name);
        _recipes.Save(recipe);
        _editingRecipeId = recipe.Id;
        SelectedSavedRecipe = SavedRecipes.FirstOrDefault(item => item.Id == recipe.Id);
    }

    [RelayCommand]
    private void DeleteRecipe()
    {
        if (_recipes is null || SelectedSavedRecipe is null)
            return;
        _recipes.Delete(SelectedSavedRecipe.Id);
        NewRecipe();
    }

    private MetadataOperation CreateCurrentOperation()
    {
        if (SelectedOperation is null || SelectedField is null)
            throw new InvalidOperationException(
                Text(
                    "MetadataEditor.Validation.ChooseOperationAndField"));
        MetadataCondition? condition = !ConditionEnabled
            ? null
            : new(
                SelectedConditionField is null
                    ? null
                    : MetadataFieldKey.Known(SelectedConditionField.Field),
                SelectedConditionOperator,
                ConditionValue,
                NegateCondition);
        return _catalog.Create(new MetadataOperationDraft(
            Kind: SelectedOperation.Kind,
            Field: MetadataFieldKey.Known(SelectedField.Field),
            DestinationField: DestinationField is null
                ? null
                : MetadataFieldKey.Known(DestinationField.Field),
            SecondaryField: SecondaryField is null
                ? null
                : MetadataFieldKey.Known(SecondaryField.Field),
            Value: OperationValue,
            Search: SearchText,
            Replacement: ReplacementText,
            Separator: Separator,
            UseRegularExpression: UseRegularExpression,
            CaseMode: SelectedCaseMode,
            ValueOrder: SelectedValueOrder,
            PathComponent: SelectedPathComponent,
            ParentLevel: ParentLevel,
            ExtractionPattern: ExtractionPattern,
            ExtractionGroup: ExtractionGroup,
            SequenceStart: SequenceStart,
            SequencePadding: SequencePadding,
            Condition: condition));
    }

    private void ReloadRecipes()
    {
        Guid? selected = SelectedSavedRecipe?.Id;
        SavedRecipes.Clear();
        if (_recipes is not null)
            foreach (OperationRecipe recipe in _recipes.Recipes.OrderBy(item => item.Name))
                SavedRecipes.Add(recipe);
        SelectedSavedRecipe = selected is null
            ? null
            : SavedRecipes.FirstOrDefault(recipe => recipe.Id == selected);
    }

    private void RefreshLocalizedChoices()
    {
        RefreshChoices(
            OperationChoices,
            OperationDescriptors,
            value => Text(
                $"MetadataEditor.Choice.Operation.{value.Kind}"));
        RefreshChoices(
            CaseModeChoices,
            CaseModes,
            value => Text(
                $"MetadataEditor.Choice.CaseMode.{value}"));
        RefreshChoices(
            ValueOrderChoices,
            ValueOrders,
            value => Text(
                $"MetadataEditor.Choice.ValueOrder.{value}"));
        RefreshChoices(
            PathComponentChoices,
            PathComponents,
            value => Text(
                $"MetadataEditor.Choice.PathComponent.{value}"));
        RefreshChoices(
            ConditionOperatorChoices,
            ConditionOperators,
            value => Text(
                $"MetadataEditor.Choice.ConditionOperator.{value}"));
        foreach (MetadataFieldChoice field in Fields)
            field.Label = FieldLabel(field.Field);
    }

    private static void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        Func<T, string> getLabel)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = getLabel(value);
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private string FieldLabel(TagFields field) =>
        Text(
            $"Settings.Choice.TagFields.{field}");

    private string OperationLabel(
        MetadataOperationDescriptor descriptor) =>
        OperationChoices.FirstOrDefault(choice =>
            choice.Value.Kind == descriptor.Kind)?.Label ??
        Text(
            $"MetadataEditor.Choice.Operation.{descriptor.Kind}");

    private string Text(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string Format(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        bool recipeNameWasDefault =
            string.Equals(
                RecipeName,
                _defaultRecipeName,
                StringComparison.Ordinal);
        _defaultRecipeName = Text(
            "MetadataEditor.Recipe.NewName");
        if (recipeNameWasDefault)
            RecipeName = _defaultRecipeName;
        RefreshLocalizedChoices();
    }

    private void OnEditorPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectedStep))
            Changed?.Invoke();
    }

    private void OnStepsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (MetadataRecipeStepViewModel step in
                     e.OldItems)
                step.PropertyChanged -= OnStepChanged;
        if (e.NewItems is not null)
            foreach (MetadataRecipeStepViewModel step in
                     e.NewItems)
                step.PropertyChanged += OnStepChanged;
        Changed?.Invoke();
    }

    private void OnStepChanged(
        object? sender,
        PropertyChangedEventArgs e) =>
        Changed?.Invoke();

    private static readonly TagFields[] EditorFields =
    [
        TagFields.Title,
        TagFields.Artist,
        TagFields.AlbumArtist,
        TagFields.Album,
        TagFields.Genre,
        TagFields.Composer,
        TagFields.Date,
        TagFields.TrackNumber,
        TagFields.TotalTracks,
        TagFields.DiscNumber,
        TagFields.TotalDiscs,
        TagFields.Comment,
    ];
}
