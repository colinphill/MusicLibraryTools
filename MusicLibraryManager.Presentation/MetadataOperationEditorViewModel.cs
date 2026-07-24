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
