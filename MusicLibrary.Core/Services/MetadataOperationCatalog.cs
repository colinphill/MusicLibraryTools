using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IMetadataOperationCatalog
{
    IReadOnlyList<MetadataOperationDescriptor> Operations { get; }
    MetadataOperation Create(MetadataOperationDraft draft);
}

/// <summary>
/// The authoritative catalog shared by Workbench, Library, recipes, and future CLI hosts.
/// Surface flags describe genuine applicability rather than duplicating operation definitions
/// in each user interface.
/// </summary>
public sealed class MetadataOperationCatalog : IMetadataOperationCatalog
{
    public IReadOnlyList<MetadataOperationDescriptor> Operations { get; } =
    [
        Both(MetadataOperationKind.Assign, "Assign",
            "Replace a field with a specified value."),
        Both(MetadataOperationKind.Remove, "Remove",
            "Remove a field and all of its values."),
        Both(MetadataOperationKind.Copy, "Copy",
            "Copy ordered values from one field to another."),
        Both(MetadataOperationKind.ReplaceText, "Replace text",
            "Apply a literal or regular-expression replacement."),
        Both(MetadataOperationKind.ChangeCase, "Change case",
            "Apply upper, lower, title, or sentence case."),
        Both(MetadataOperationKind.TrimWhitespace, "Normalize whitespace",
            "Trim surrounding whitespace and normalize internal runs."),
        Both(MetadataOperationKind.Sequence, "Sequence",
            "Assign ordered, optionally padded numbers."),
        Both(MetadataOperationKind.Combine, "Combine fields",
            "Combine two fields into an ordered destination value."),
        Both(MetadataOperationKind.Split, "Split values",
            "Split each field value using literal text or a regular expression."),
        Both(MetadataOperationKind.Join, "Join values",
            "Join ordered field values into one value."),
        Both(MetadataOperationKind.Deduplicate, "Deduplicate values",
            "Remove duplicate values while preserving their first occurrence."),
        Both(MetadataOperationKind.Reorder, "Reorder values",
            "Sort or reverse the ordered values in a field."),
    ];

    public MetadataOperation Create(MetadataOperationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Kind switch
        {
            MetadataOperationKind.Assign =>
                new AssignFieldOperation(
                    draft.Field, draft.Value ?? "", draft.Condition),
            MetadataOperationKind.Remove =>
                new RemoveFieldOperation(draft.Field, draft.Condition),
            MetadataOperationKind.Copy =>
                new CopyFieldOperation(
                    draft.Field,
                    draft.DestinationField ?? throw new InvalidOperationException(
                        "A destination field is required for Copy."),
                    When: draft.Condition),
            MetadataOperationKind.ReplaceText =>
                new ReplaceTextOperation(
                    draft.Field,
                    draft.Search ?? "",
                    draft.Replacement ?? "",
                    draft.UseRegularExpression,
                    When: draft.Condition),
            MetadataOperationKind.ChangeCase =>
                new ChangeCaseOperation(
                    draft.Field, draft.CaseMode, draft.Condition),
            MetadataOperationKind.TrimWhitespace =>
                new TrimFieldOperation(
                    draft.Field,
                    NormalizeInternalWhitespace: true,
                    When: draft.Condition),
            MetadataOperationKind.Sequence =>
                new SequenceNumberOperation(
                    draft.Field,
                    Math.Max(0, draft.SequenceStart),
                    PadWidth: Math.Max(0, draft.SequencePadding),
                    When: draft.Condition),
            MetadataOperationKind.Combine =>
                new CombineFieldsOperation(
                    draft.Field,
                    draft.SecondaryField ?? throw new InvalidOperationException(
                        "A second source field is required for Combine."),
                    draft.DestinationField ?? throw new InvalidOperationException(
                        "A destination field is required for Combine."),
                    draft.Separator ?? " ",
                    draft.Condition),
            MetadataOperationKind.Split =>
                new SplitFieldOperation(
                    draft.Field,
                    RequireSeparator(draft.Separator, "Split"),
                    draft.UseRegularExpression,
                    When: draft.Condition),
            MetadataOperationKind.Join =>
                new JoinFieldValuesOperation(
                    draft.Field,
                    draft.Separator ?? "; ",
                    draft.Condition),
            MetadataOperationKind.Deduplicate =>
                new DeduplicateFieldValuesOperation(
                    draft.Field,
                    When: draft.Condition),
            MetadataOperationKind.Reorder =>
                new ReorderFieldValuesOperation(
                    draft.Field,
                    draft.ValueOrder,
                    When: draft.Condition),
            _ => throw new NotSupportedException(
                $"Unsupported metadata operation '{draft.Kind}'."),
        };
    }

    private static string RequireSeparator(string? separator, string operationName)
    {
        if (string.IsNullOrEmpty(separator))
            throw new InvalidOperationException(
                $"A separator is required for {operationName}.");
        return separator;
    }

    private static MetadataOperationDescriptor Both(
        MetadataOperationKind kind,
        string displayName,
        string description) =>
        new(kind, displayName, description,
            MetadataOperationSurface.Workbench | MetadataOperationSurface.Library);
}
