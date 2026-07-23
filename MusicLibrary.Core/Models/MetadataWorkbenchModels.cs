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

public sealed record OperationRecipe(
    Guid Id,
    string Name,
    ImmutableArray<MetadataOperation> Operations,
    bool Enabled = true)
{
    public static OperationRecipe Create(string name, params MetadataOperation[] operations) =>
        new(Guid.NewGuid(), name, [.. operations]);
}

public sealed record MetadataFieldDifference(
    MetadataFieldKey Field,
    ImmutableArray<string> Before,
    ImmutableArray<string> After);

public sealed record MetadataValueEdit(
    MetadataFieldKey Field,
    ImmutableArray<string> Values);

public sealed record MetadataFilePlan(
    string Path,
    MediaFileSnapshot Snapshot,
    ImmutableArray<MetadataFieldDifference> Differences,
    ImmutableArray<MetadataValueEdit> Edits,
    ImmutableArray<OperationIssue> Issues)
{
    public bool HasChanges => Differences.Length > 0;
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
    public int ChangeCount => Files.Sum(file => file.Differences.Length);
    public bool CanApply => ChangedFileCount > 0 && Files
        .SelectMany(file => file.Issues)
        .All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record MetadataApplyResult(
    int ChangedFiles,
    ImmutableArray<string> JournalPaths,
    ImmutableArray<OperationIssue> Issues);

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
