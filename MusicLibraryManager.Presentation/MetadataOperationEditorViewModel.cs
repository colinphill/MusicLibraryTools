using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record MetadataFieldChoice(TagFields Field, string Label);

public sealed record MetadataPreviewRow(
    string File,
    string Field,
    string Before,
    string After);

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
        MetadataOperationPlan plan)
    {
        destination.Clear();
        if (plan.Recipe is { } recipe)
        {
            IEnumerable<OperationRecipeStep> steps = recipe.Steps.Length > 0
                ? recipe.Steps
                : recipe.Operations.Select((operation, index) =>
                    new OperationRecipeStep(
                        Guid.Empty, $"Step {index + 1}", operation));
            foreach (OperationRecipeStep step in steps.Where(step => step.Enabled))
                destination.Add(new(
                    destination.Count + 1,
                    step.Name,
                    Describe(step.Operation),
                    $"{plan.ChangedFileCount:N0} changed file(s)"));
            return;
        }

        AddValueEdits(destination, plan);

        int artworkFiles = plan.Files.Count(file =>
            file.ArtworkDifference is not null);
        Add(destination, artworkFiles, "Update embedded artwork", "Artwork");

        foreach (var group in plan.Files
                     .SelectMany(file => file.TagLayerDifferences.IsDefaultOrEmpty
                         ? []
                         : file.TagLayerDifferences.Select(
                             difference => (file, difference)))
                     .GroupBy(item => (
                         item.difference.Kind,
                         item.difference.WillBePresent)))
            Add(destination, group.Select(item => item.file.Path).Distinct(
                    PathComparer).Count(),
                group.Key.WillBePresent ? "Add tag layer" : "Remove tag layer",
                DescribeLayer(group.Key.Kind));

        foreach (var group in plan.Files
                     .Where(file => file.Id3VersionDifference is not null)
                     .GroupBy(file => (
                         file.Id3VersionDifference!.TargetVersion,
                         file.Id3VersionDifference.TextEncodingPolicy)))
            Add(destination, group.Count(),
                group.Key.TextEncodingPolicy is null
                    ? "Change ID3 version"
                    : "Change ID3 version or text encoding",
                $"ID3v2.{(int)group.Key.TargetVersion}" +
                (group.Key.TextEncodingPolicy is null
                    ? ""
                    : $" · {group.Key.TextEncodingPolicy}"));

        foreach (var group in plan.Files
                     .SelectMany(file =>
                         file.TagLayerConversionDifferences.IsDefaultOrEmpty
                             ? []
                             : file.TagLayerConversionDifferences.Select(
                                 difference => (file, difference)))
                     .GroupBy(item => (
                         item.difference.Source,
                         item.difference.Target)))
            Add(destination, group.Select(item => item.file.Path).Distinct(
                    PathComparer).Count(),
                "Convert tag layer",
                $"{DescribeLayer(group.Key.Source)} → " +
                DescribeLayer(group.Key.Target));
    }

    private static void AddValueEdits(
        ObservableCollection<PendingMetadataOperationRow> destination,
        MetadataOperationPlan plan)
    {
        foreach (var group in plan.Files
                     .SelectMany(file => file.Edits.Select(edit => (file, edit)))
                     .GroupBy(item => (
                         item.edit.Field,
                         Remove: item.edit.Values.Length == 0)))
            Add(destination, group.Select(item => item.file.Path).Distinct(
                    PathComparer).Count(),
                group.Key.Remove ? "Remove metadata field" : "Set metadata field",
                group.Key.Field.DisplayName);
    }

    private static void Add(
        ObservableCollection<PendingMetadataOperationRow> destination,
        int files,
        string operation,
        string target)
    {
        if (files == 0)
            return;
        destination.Add(new(
            destination.Count + 1,
            operation,
            target,
            files == 1 ? "1 file" : $"{files:N0} files"));
    }

    private static string Describe(MetadataOperation operation) => operation switch
    {
        AssignFieldOperation value =>
            $"Set {value.Field.DisplayName} to “{Short(value.Value)}”",
        RemoveFieldOperation value =>
            $"Remove {value.Field.DisplayName}",
        CopyFieldOperation value =>
            $"Copy {value.Source.DisplayName} to {value.Destination.DisplayName}",
        ReplaceTextOperation value =>
            $"Replace “{Short(value.Search)}” in {value.Field.DisplayName}",
        ChangeCaseOperation value =>
            $"Change {value.Field.DisplayName} to {value.Mode} case",
        TrimFieldOperation value =>
            $"Trim whitespace in {value.Field.DisplayName}",
        SequenceNumberOperation value =>
            $"Number {value.Field.DisplayName} from {value.Start}",
        CombineFieldsOperation value =>
            $"Combine {value.First.DisplayName} and {value.Second.DisplayName} " +
            $"into {value.Destination.DisplayName}",
        SplitFieldOperation value =>
            $"Split {value.Field.DisplayName} on “{Short(value.Separator)}”",
        JoinFieldValuesOperation value =>
            $"Join {value.Field.DisplayName} with “{Short(value.Separator)}”",
        DeduplicateFieldValuesOperation value =>
            $"Remove duplicate {value.Field.DisplayName} values",
        ReorderFieldValuesOperation value =>
            $"Reorder {value.Field.DisplayName} values {value.Order}",
        ExtractPathComponentOperation value =>
            $"Set {value.Field.DisplayName} from {value.Component}",
        _ => operation.GetType().Name,
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
}

public static class MetadataPreviewRowBuilder
{
    public static void Populate(
        ObservableCollection<MetadataPreviewRow> destination,
        MetadataOperationPlan plan)
    {
        destination.Clear();
        foreach (MetadataFilePlan file in plan.Files)
        {
            foreach (MetadataFieldDifference difference in file.Differences)
            {
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    difference.Field.DisplayName,
                    string.Join("; ", difference.Before),
                    string.Join("; ", difference.After)));
            }

            if (file.ArtworkDifference is { } artwork)
            {
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    "Artwork",
                    DescribeArtwork(artwork.Before),
                    DescribeArtwork(artwork.After)));
            }

            if (!file.TagLayerDifferences.IsDefaultOrEmpty)
            {
                foreach (TagLayerDifference difference in
                         file.TagLayerDifferences)
                {
                    destination.Add(new(
                        Path.GetFileName(file.Path),
                        $"{DescribeTagLayer(difference.Kind)} tag layer",
                        difference.WasPresent ? "Present" : "Absent",
                        difference.WillBePresent ? "Present" : "Absent"));
    }
}

            if (file.Id3VersionDifference is { } version)
            {
                string issueSummary = version.Issues.Length == 0
                    ? ""
                    : $" ({version.Issues.Length:N0} compatibility issue(s))";
                string encoding = version.TextEncodingPolicy is null
                    ? ""
                    : $", {version.TextEncodingPolicy}";
                destination.Add(new(
                    Path.GetFileName(file.Path),
                    version.SourceVersion == version.TargetVersion
                        ? "ID3 text encoding"
                        : "ID3 version",
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
                            : $" ({conversion.CompatibilityIssues.Length:N0} " +
                              "compatibility issue(s))";
                    destination.Add(new(
                        Path.GetFileName(file.Path),
                        "Tag-layer conversion",
                        DescribeTagLayer(conversion.Source),
                        $"{DescribeTagLayer(conversion.Target)}{issueSummary}"));
                }
            }
        }
    }

    private static string DescribeArtwork(
        IReadOnlyList<ArtworkDescriptor> images) =>
        images.Count == 0
            ? "(none)"
            : string.Join("; ", images.Select(image =>
                $"{image.Type} {image.MimeType} {image.Size:N0} bytes" +
                (string.IsNullOrWhiteSpace(image.Description)
                    ? ""
                    : $" ({image.Description})")));

    private static string DescribeTagLayer(TagLayerKind kind) => kind switch
    {
        TagLayerKind.Id3v2 => "ID3v2",
        TagLayerKind.ApeV2 => "APEv2",
        _ => kind.ToString(),
    };
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
                "At least one metadata plan is required.",
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
    private Guid? _editingRecipeId;

    public MetadataOperationEditorViewModel(
        IMetadataOperationCatalog catalog,
        MetadataOperationSurface surface,
        IOperationRecipeStore? recipes = null)
    {
        _catalog = catalog;
        _recipes = recipes;
        OperationDescriptors = catalog.Operations
            .Where(operation => operation.Supports(surface))
            .ToArray();
        Fields =
        [
            new(TagFields.Title, "Title"),
            new(TagFields.Artist, "Artist"),
            new(TagFields.AlbumArtist, "Album artist"),
            new(TagFields.Album, "Album"),
            new(TagFields.Genre, "Genre"),
            new(TagFields.Composer, "Composer"),
            new(TagFields.Date, "Date"),
            new(TagFields.TrackNumber, "Track"),
            new(TagFields.TotalTracks, "Track total"),
            new(TagFields.DiscNumber, "Disc"),
            new(TagFields.TotalDiscs, "Disc total"),
            new(TagFields.Comment, "Comment"),
        ];
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
    }

    public ObservableCollection<MetadataRecipeStepViewModel> Steps { get; } = [];
    public ObservableCollection<OperationRecipe> SavedRecipes { get; } = [];
    public event Action? Changed;
    public IReadOnlyList<MetadataOperationDescriptor> OperationDescriptors { get; }
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
    private string _recipeName = "New recipe";

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
                name ?? $"{SelectedOperation?.DisplayName}: {SelectedField?.Label}",
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
        string name = $"{SelectedOperation!.DisplayName}: {SelectedField!.Label}";
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
            Guid.NewGuid(), step.Name + " copy", step.Operation, step.Enabled));
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
        RecipeName = "New recipe";
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
                        Guid.NewGuid(), $"Step {index + 1}", operation))
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
            throw new InvalidOperationException("Choose an operation and field.");
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
}
