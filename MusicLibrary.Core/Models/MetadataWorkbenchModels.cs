using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>
/// Identifies either one of the application's canonical fields or a format-native custom field.
/// Exactly one of <see cref="KnownField"/> and <see cref="CustomName"/> is populated.
/// </summary>
public sealed record MetadataFieldKey
{
    [JsonConstructor]
    public MetadataFieldKey(TagFields? knownField, string? customName)
    {
        if ((knownField is null) == string.IsNullOrWhiteSpace(customName))
            throw new ArgumentException(
                "Specify exactly one known field or custom field name.");
        if (knownField == TagFields.NullField)
            throw new ArgumentOutOfRangeException(nameof(knownField));
        KnownField = knownField;
        CustomName = customName?.Trim();
    }

    public TagFields? KnownField { get; }
    public string? CustomName { get; }
    public bool IsKnown => KnownField is not null;
    public string DisplayName => KnownField?.ToString() ?? CustomName ?? "";

    public static MetadataFieldKey Known(TagFields field)
    {
        if (field == TagFields.NullField)
            throw new ArgumentOutOfRangeException(nameof(field));
        return new(field, null);
    }

    public static MetadataFieldKey Custom(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(null, name.Trim());
    }
}

/// <summary>All ordered values associated with one metadata field.</summary>
public sealed record MetadataValueSet(
    MetadataFieldKey Field,
    ImmutableArray<string> Values)
{
    public string DisplayValue => string.Join("; ", Values);
}

/// <summary>One physical tag layer present in a media file.</summary>
public sealed record TagLayerDocument(
    string TagType,
    ImmutableArray<MetadataValueSet> Fields,
    bool SupportsCustomFields,
    bool IsWritable,
    bool SupportsMultipleValues,
    bool SupportsCustomMultipleValues);

/// <summary>A stale-plan guard captured while a media document is read.</summary>
public sealed record MediaFileSnapshot(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc,
    string MetadataHash);

/// <summary>
/// Lossless workbench projection. Values remain grouped by layer and field rather than being
/// collapsed to the first value used by the cache-oriented library grid.
/// </summary>
public sealed record MediaDocument(
    string Path,
    ImmutableArray<TagLayerDocument> TagLayers,
    ImmutableArray<ArtworkModel> Artwork,
    CodecModel? Codec,
    MediaFileSnapshot Snapshot,
    bool IsWritable)
{
    public ImmutableArray<MediaChapter> Chapters { get; init; } = [];
    public ImmutableArray<TagLayerDescriptor> EditableTagLayers { get; init; } = [];
    public ID3v2Version? Id3Version { get; init; }

    public ImmutableArray<string> Values(MetadataFieldKey field) => TagLayers
        .SelectMany(layer => layer.Fields)
        .Where(value => Equals(value.Field, field))
        .SelectMany(value => value.Values)
        .ToImmutableArray();

    public string? FirstValue(TagFields field) =>
        Values(MetadataFieldKey.Known(field)).FirstOrDefault();
}

public enum MetadataConditionOperator
{
    Always,
    Present,
    Missing,
    Equals,
    Contains,
    MatchesRegularExpression,
}

public sealed record MetadataCondition(
    MetadataFieldKey? Field = null,
    MetadataConditionOperator Operator = MetadataConditionOperator.Always,
    string? Value = null,
    bool Negate = false);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssignFieldOperation), "assign")]
[JsonDerivedType(typeof(RemoveFieldOperation), "remove")]
[JsonDerivedType(typeof(CopyFieldOperation), "copy")]
[JsonDerivedType(typeof(ReplaceTextOperation), "replace")]
[JsonDerivedType(typeof(ChangeCaseOperation), "case")]
[JsonDerivedType(typeof(TrimFieldOperation), "trim")]
[JsonDerivedType(typeof(SequenceNumberOperation), "sequence")]
[JsonDerivedType(typeof(CombineFieldsOperation), "combine")]
[JsonDerivedType(typeof(SplitFieldOperation), "split")]
[JsonDerivedType(typeof(JoinFieldValuesOperation), "join")]
[JsonDerivedType(typeof(DeduplicateFieldValuesOperation), "deduplicate")]
[JsonDerivedType(typeof(ReorderFieldValuesOperation), "reorder")]
[JsonDerivedType(typeof(ExtractPathComponentOperation), "extractPath")]
public abstract record MetadataOperation(MetadataCondition? Condition = null);

public sealed record AssignFieldOperation(
    MetadataFieldKey Field,
    string Value,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record RemoveFieldOperation(
    MetadataFieldKey Field,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record CopyFieldOperation(
    MetadataFieldKey Source,
    MetadataFieldKey Destination,
    bool PreserveExisting = false,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record ReplaceTextOperation(
    MetadataFieldKey Field,
    string Search,
    string Replacement,
    bool RegularExpression = false,
    bool IgnoreCase = true,
    MetadataCondition? When = null) : MetadataOperation(When);

public enum MetadataCaseMode { Upper, Lower, Title, Sentence }

[Flags]
public enum MetadataOperationSurface
{
    None = 0,
    Workbench = 1,
    Library = 2,
}

public enum MetadataOperationKind
{
    Assign,
    Remove,
    Copy,
    ReplaceText,
    ChangeCase,
    TrimWhitespace,
    Sequence,
    Combine,
    Split,
    Join,
    Deduplicate,
    Reorder,
    ExtractPathComponent,
}

public sealed record MetadataOperationDescriptor(
    MetadataOperationKind Kind,
    string DisplayName,
    string Description,
    MetadataOperationSurface Surfaces)
{
    public bool Supports(MetadataOperationSurface surface) =>
        (Surfaces & surface) == surface;
}

public sealed record MetadataOperationDraft(
    MetadataOperationKind Kind,
    MetadataFieldKey Field,
    MetadataFieldKey? DestinationField = null,
    MetadataFieldKey? SecondaryField = null,
    string? Value = null,
    string? Search = null,
    string? Replacement = null,
    string? Separator = null,
    bool UseRegularExpression = false,
    MetadataCaseMode CaseMode = MetadataCaseMode.Title,
    MetadataValueOrder ValueOrder = MetadataValueOrder.Ascending,
    MetadataPathComponent PathComponent = MetadataPathComponent.FileNameWithoutExtension,
    int ParentLevel = 1,
    string? ExtractionPattern = null,
    string ExtractionGroup = "value",
    int SequenceStart = 1,
    int SequencePadding = 0,
    MetadataCondition? Condition = null);

public sealed record CombineFieldsOperation(
    MetadataFieldKey First,
    MetadataFieldKey Second,
    MetadataFieldKey Destination,
    string Separator = " ",
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record SplitFieldOperation(
    MetadataFieldKey Field,
    string Separator,
    bool RegularExpression = false,
    bool RemoveEmptyValues = true,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record JoinFieldValuesOperation(
    MetadataFieldKey Field,
    string Separator,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record DeduplicateFieldValuesOperation(
    MetadataFieldKey Field,
    bool IgnoreCase = true,
    MetadataCondition? When = null) : MetadataOperation(When);

public enum MetadataValueOrder { Ascending, Descending, Reverse }

public sealed record ReorderFieldValuesOperation(
    MetadataFieldKey Field,
    MetadataValueOrder Order = MetadataValueOrder.Ascending,
    bool IgnoreCase = true,
    MetadataCondition? When = null) : MetadataOperation(When);

public enum MetadataPathComponent
{
    FileNameWithoutExtension,
    FileName,
    ParentFolder,
    FullPath,
}

public sealed record ExtractPathComponentOperation(
    MetadataFieldKey Field,
    MetadataPathComponent Component = MetadataPathComponent.FileNameWithoutExtension,
    int ParentLevel = 1,
    string? Pattern = null,
    string CaptureGroup = "value",
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record ChangeCaseOperation(
    MetadataFieldKey Field,
    MetadataCaseMode Mode,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record TrimFieldOperation(
    MetadataFieldKey Field,
    bool NormalizeInternalWhitespace = false,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record SequenceNumberOperation(
    MetadataFieldKey Field,
    int Start = 1,
    int Step = 1,
    int PadWidth = 0,
    MetadataFieldKey? TotalField = null,
    MetadataCondition? When = null) : MetadataOperation(When);

public sealed record OperationRecipeStep(
    Guid Id,
    string Name,
    MetadataOperation Operation,
    bool Enabled = true);

public sealed record OperationRecipe(
    Guid Id,
    string Name,
    ImmutableArray<MetadataOperation> Operations,
    bool Enabled = true)
{
    public ImmutableArray<OperationRecipeStep> Steps { get; init; } =
        [.. Operations.Select((operation, index) => new OperationRecipeStep(
            Guid.NewGuid(), $"Step {index + 1}", operation))];

    [JsonIgnore]
    public IEnumerable<MetadataOperation> EnabledOperations =>
        Steps.IsDefaultOrEmpty
            ? Operations
            : Steps.Where(step => step.Enabled).Select(step => step.Operation);

    public static OperationRecipe Create(string name, params MetadataOperation[] operations) =>
        new(Guid.NewGuid(), name, [.. operations]);

    public static OperationRecipe FromSteps(
        Guid id,
        string name,
        IEnumerable<OperationRecipeStep> steps,
        bool enabled = true)
    {
        ImmutableArray<OperationRecipeStep> materialized = [.. steps];
        return new(id, name,
            [.. materialized.Select(step => step.Operation)], enabled)
        {
            Steps = materialized,
        };
    }
}

public sealed record MetadataFieldDifference(
    MetadataFieldKey Field,
    ImmutableArray<string> Before,
    ImmutableArray<string> After);

public sealed record MetadataValueEdit(
    MetadataFieldKey Field,
    ImmutableArray<string> Values);

public enum TagLayerEditMode
{
    Add,
    Remove,
}

public sealed record TagLayerEdit(
    TagLayerKind Kind,
    TagLayerEditMode Mode,
    TagLayerCopyMode CopyMode = TagLayerCopyMode.CopyPrimary);

public sealed record TagLayerDifference(
    TagLayerKind Kind,
    bool WasPresent,
    bool WillBePresent);

public sealed record TagLayerConversionEdit(
    TagLayerKind Source,
    TagLayerKind Target);

public sealed record TagLayerConversionDifference(
    TagLayerKind Source,
    TagLayerKind Target,
    ImmutableArray<string> CompatibilityIssues);

public sealed record Id3VersionEdit(
    ID3v2Version TargetVersion,
    bool DropUnsupportedFrames = false,
    bool CoalesceTextValues = false,
    string MultiValueSeparator = "/",
    ID3TextEncodingPolicy? TextEncodingPolicy = null);

public sealed record Id3VersionDifference(
    ID3v2Version SourceVersion,
    ID3v2Version TargetVersion,
    int ConvertedFrameCount,
    ImmutableArray<ID3VersionConversionIssue> Issues,
    ID3TextEncodingPolicy? TextEncodingPolicy = null);

public enum ArtworkValueEditMode
{
    ReplaceFrontCover,
    ReplaceAll,
    RemoveFrontCover,
    RemoveAll,
}

public sealed record ArtworkValueEdit(
    ArtworkValueEditMode Mode,
    ArtworkInput? Image = null);

public sealed record ArtworkDescriptor(
    ID3v2Util.APICType Type,
    string MimeType,
    string Description,
    int Size,
    string Hash);

public sealed record ArtworkSetEdit(
    ImmutableArray<ArtworkInput> Images);

public sealed record ArtworkSetPreviewRequest(
    ImmutableArray<ArtworkInput> Images,
    int MaxDimension = 0);

public sealed record ArtworkSetDifference(
    ImmutableArray<ArtworkDescriptor> Before,
    ImmutableArray<ArtworkDescriptor> After);

public sealed record MetadataFilePlan(
    string Path,
    MediaFileSnapshot Snapshot,
    ImmutableArray<MetadataFieldDifference> Differences,
    ImmutableArray<MetadataValueEdit> Edits,
    ImmutableArray<OperationIssue> Issues,
    ArtworkSetEdit? ArtworkEdit = null,
    ArtworkSetDifference? ArtworkDifference = null,
    ImmutableArray<TagLayerEdit> TagLayerEdits = default,
    ImmutableArray<TagLayerDifference> TagLayerDifferences = default,
    Id3VersionEdit? Id3VersionEdit = null,
    Id3VersionDifference? Id3VersionDifference = null,
    ImmutableArray<TagLayerConversionEdit> TagLayerConversions = default,
    ImmutableArray<TagLayerConversionDifference> TagLayerConversionDifferences = default)
{
    public bool HasChanges =>
        Differences.Length > 0 ||
        ArtworkDifference is not null ||
        !TagLayerDifferences.IsDefaultOrEmpty ||
        Id3VersionDifference is not null ||
        !TagLayerConversionDifferences.IsDefaultOrEmpty;
    public bool CanApply => HasChanges &&
        Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record MetadataOperationPlan(
    Guid Id,
    string Name,
    ImmutableArray<MetadataFilePlan> Files,
    DateTimeOffset CreatedAtUtc,
    OperationRecipe? Recipe = null)
{
    public int ChangedFileCount => Files.Count(file => file.HasChanges);
    public int ChangeCount => Files.Sum(file =>
        file.Differences.Length +
        (file.ArtworkDifference is null ? 0 : 1) +
        (file.TagLayerDifferences.IsDefault
            ? 0
            : file.TagLayerDifferences.Length) +
        (file.Id3VersionDifference is null ? 0 : 1) +
        (file.TagLayerConversionDifferences.IsDefault
            ? 0
            : file.TagLayerConversionDifferences.Length));

    public bool CanApply => ChangedFileCount > 0 && Files
        .SelectMany(file => file.Issues)
        .All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record MetadataApplyResult(
    int ChangedFiles,
    ImmutableArray<string> JournalPaths,
    ImmutableArray<OperationIssue> Issues,
    RecoveryStorageSummary? RecoveryStorage = null);

public sealed record MetadataStagedFile(
    string LivePath,
    string StagedPath);

public sealed record MetadataOperationStageResult(
    MetadataOperationPlan Plan,
    ImmutableArray<FileMutationPlan> Participants,
    ImmutableArray<MetadataStagedFile> Files)
{
    public int ChangedFiles => Files.Length;
}

public sealed record WorkbenchLoadRequest(
    IReadOnlyList<string> Sources,
    bool Recursive = true);

public sealed record WorkbenchLoadResult(
    ImmutableArray<MediaDocument> Documents,
    ImmutableArray<OperationIssue> Issues);

public sealed record EditHistoryEntry(
    Guid Id,
    string Name,
    DateTimeOffset AppliedAtUtc,
    ImmutableArray<string> JournalPaths,
    ImmutableArray<string> Paths,
    OperationRecipe? Recipe);
