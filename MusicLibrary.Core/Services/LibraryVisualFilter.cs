using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum LibraryFilterFieldKind
{
    Technical,
    KnownMetadata,
    CustomMetadata,
}

public sealed record LibraryFilterField(
    LibraryFilterFieldKind Kind,
    string Name)
{
    public static LibraryFilterField Technical(string name) =>
        new(LibraryFilterFieldKind.Technical, name);

    public static LibraryFilterField Known(TagFields field) =>
        new(LibraryFilterFieldKind.KnownMetadata, field.ToString());

    public static LibraryFilterField Custom(string name) =>
        new(LibraryFilterFieldKind.CustomMetadata, name.Trim());
}

public enum LibraryFilterComparison
{
    Present,
    Missing,
    Contains,
    Equals,
    MatchesRegularExpression,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}

public enum LibraryFilterGroupMode
{
    All,
    Any,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LibraryFilterCondition), "condition")]
[JsonDerivedType(typeof(LibraryFilterGroup), "group")]
public abstract record LibraryVisualFilterNode(bool Negate = false);

public sealed record LibraryFilterCondition(
    LibraryFilterField Field,
    LibraryFilterComparison Comparison,
    string? Value = null,
    bool IsNegated = false) : LibraryVisualFilterNode(IsNegated);

public sealed record LibraryFilterGroup(
    LibraryFilterGroupMode Mode,
    ImmutableArray<LibraryVisualFilterNode> Children,
    bool IsNegated = false) : LibraryVisualFilterNode(IsNegated);

public sealed class LibraryVisualFilter
{
    private readonly LibraryVisualFilterNode? _root;
    private readonly Dictionary<LibraryFilterCondition, PatternMatcher>
        _regularExpressions = [];

    public LibraryVisualFilter(LibraryVisualFilterNode? root)
    {
        _root = root;
        foreach (LibraryFilterCondition condition in Conditions(root))
        {
            if (condition.Comparison !=
                LibraryFilterComparison.MatchesRegularExpression)
                continue;
            PatternMatcher matcher = PatternMatcher.Create(
                condition.Value,
                FilterMode.Regex);
            _regularExpressions[condition] = matcher;
            if (!matcher.IsValid)
            {
                IsValid = false;
                Error =
                    $"Invalid regular expression for {condition.Field.Name}.";
                return;
            }
        }
        IsValid = true;
    }

    public bool IsValid { get; }
    public string? Error { get; }
    public bool IsEmpty => _root is null;

    public bool IsMatch(TrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IsValid &&
            (_root is null || Evaluate(_root, record));
    }

    private bool Evaluate(
        LibraryVisualFilterNode node,
        TrackRecord record)
    {
        bool result = node switch
        {
            LibraryFilterCondition condition =>
                EvaluateCondition(condition, record),
            LibraryFilterGroup group =>
                group.Mode == LibraryFilterGroupMode.All
                    ? group.Children.All(child =>
                        Evaluate(child, record))
                    : group.Children.Any(child =>
                        Evaluate(child, record)),
            _ => false,
        };
        return node.Negate ? !result : result;
    }

    private bool EvaluateCondition(
        LibraryFilterCondition condition,
        TrackRecord record)
    {
        string[] values = Values(condition.Field, record);
        string expected = condition.Value ?? "";
        return condition.Comparison switch
        {
            LibraryFilterComparison.Present =>
                values.Any(value =>
                    !string.IsNullOrWhiteSpace(value)),
            LibraryFilterComparison.Missing =>
                values.All(string.IsNullOrWhiteSpace),
            LibraryFilterComparison.Contains =>
                values.Any(value => value.Contains(
                    expected,
                    StringComparison.CurrentCultureIgnoreCase)),
            LibraryFilterComparison.Equals =>
                values.Any(value => value.Equals(
                    expected,
                    StringComparison.CurrentCultureIgnoreCase)),
            LibraryFilterComparison.MatchesRegularExpression =>
                values.Any(value =>
                    _regularExpressions[condition].IsMatch(value)),
            LibraryFilterComparison.GreaterThan =>
                CompareAny(values, expected, comparison =>
                    comparison > 0),
            LibraryFilterComparison.GreaterThanOrEqual =>
                CompareAny(values, expected, comparison =>
                    comparison >= 0),
            LibraryFilterComparison.LessThan =>
                CompareAny(values, expected, comparison =>
                    comparison < 0),
            LibraryFilterComparison.LessThanOrEqual =>
                CompareAny(values, expected, comparison =>
                    comparison <= 0),
            _ => false,
        };
    }

    private static string[] Values(
        LibraryFilterField field,
        TrackRecord record)
    {
        if (field.Kind == LibraryFilterFieldKind.Technical)
        {
            DetailsColumn? column =
                DetailsColumns.All.FirstOrDefault(column =>
                    column.Key.Equals(
                        field.Name,
                        StringComparison.OrdinalIgnoreCase));
            if (column is null)
                return [];
            string display = column.Get(record);
            string? sortable = Convert.ToString(
                column.SortKey?.Invoke(record),
                CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(sortable) ||
                   sortable.Equals(
                       display,
                       StringComparison.Ordinal)
                ? [display]
                : [display, sortable];
        }
        string key = field.Kind ==
            LibraryFilterFieldKind.CustomMetadata
            ? CachedMetadataKeys.Custom(field.Name)
            : field.Name;
        if (record.Metadata.TryGetValue(
                key,
                out string[]? values))
            return values;
        if (field.Kind == LibraryFilterFieldKind.KnownMetadata)
        {
            DetailsColumn? fallback =
                DetailsColumns.All.FirstOrDefault(column =>
                    column.Key.Equals(
                        KnownDetailsKey(field.Name),
                        StringComparison.OrdinalIgnoreCase));
            if (fallback is not null)
                return [fallback.Get(record)];
        }
        return [];
    }

    private static string KnownDetailsKey(string name) => name switch
    {
        nameof(TagFields.Date) => "Date",
        nameof(TagFields.TrackNumber) => "Track",
        nameof(TagFields.TotalTracks) => "TrackTotal",
        nameof(TagFields.DiscNumber) => "Disc",
        nameof(TagFields.TotalDiscs) => "DiscTotal",
        _ => name,
    };

    private static bool CompareAny(
        IEnumerable<string> values,
        string expected,
        Func<int, bool> predicate)
    {
        if (!TryNumber(expected, out decimal target))
            return false;
        return values.Any(value =>
            TryNumber(value, out decimal actual) &&
            predicate(actual.CompareTo(target)));
    }

    private static bool TryNumber(
        string value,
        out decimal number) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.CurrentCulture,
            out number) ||
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out number);

    private static IEnumerable<LibraryFilterCondition> Conditions(
        LibraryVisualFilterNode? node)
    {
        if (node is LibraryFilterCondition condition)
            yield return condition;
        else if (node is LibraryFilterGroup group)
            foreach (LibraryVisualFilterNode child in group.Children)
                foreach (LibraryFilterCondition nested in
                         Conditions(child))
                    yield return nested;
    }
}
