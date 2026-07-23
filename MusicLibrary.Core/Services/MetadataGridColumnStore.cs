using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum MetadataGridSurface
{
    Workbench,
    Library,
}

public enum MetadataGridColumnSortType
{
    Text,
    Numeric,
    Date,
}

public sealed record UserMetadataColumnDescriptor(
    Guid Id,
    string Label,
    MetadataFieldKey Field,
    bool Visible,
    int Order,
    double Width,
    MetadataGridColumnSortType SortType =
        MetadataGridColumnSortType.Text,
    MetadataFieldKey? EditTarget = null)
{
    [JsonIgnore]
    public string ColumnKey => $"Metadata.{Id:N}";

    [JsonIgnore]
    public string ValueKey => MetadataGridValueKey.For(Field);
}

public static class MetadataGridValueKey
{
    public static string For(MetadataFieldKey field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.KnownField is { } known
            ? $"K_{known}"
            : "C_" + Convert.ToHexString(
                Encoding.UTF8.GetBytes(field.CustomName!));
    }
}

public interface IMetadataGridColumnStore
{
    IReadOnlyList<UserMetadataColumnDescriptor> Load(
        MetadataGridSurface surface);

    void Save(
        MetadataGridSurface surface,
        IReadOnlyList<UserMetadataColumnDescriptor> columns);
}

public sealed class MetadataGridColumnStore(IAppSettings settings) :
    IMetadataGridColumnStore
{
    private const int MaximumColumns = 100;
    private readonly object _sync = new();

    public IReadOnlyList<UserMetadataColumnDescriptor> Load(
        MetadataGridSurface surface)
    {
        lock (_sync)
        {
            try
            {
                string? json = settings.GetPreference(
                    Preference(surface));
                if (string.IsNullOrWhiteSpace(json))
                    return [];
                StoredColumns? stored =
                    JsonSerializer.Deserialize<StoredColumns>(json);
                return stored?.Version == 1
                    ? stored.Columns
                        .Where(IsValid)
                        .OrderBy(column => column.Order)
                        .Take(MaximumColumns)
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
        MetadataGridSurface surface,
        IReadOnlyList<UserMetadataColumnDescriptor> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count > MaximumColumns)
            throw new ArgumentOutOfRangeException(
                nameof(columns),
                $"At most {MaximumColumns} metadata columns can be saved.");
        if (columns.Any(column => !IsValid(column)) ||
            columns.Select(column => column.Id).Distinct().Count() !=
            columns.Count)
            throw new ArgumentException(
                "Every metadata column requires a unique ID, label, " +
                "valid field, width, and order.",
                nameof(columns));
        lock (_sync)
            settings.SetPreference(
                Preference(surface),
                JsonSerializer.Serialize(new StoredColumns(
                    1,
                    [.. columns])));
    }

    private static bool IsValid(
        UserMetadataColumnDescriptor column)
    {
        if (column.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(column.Label) ||
            column.Label.Length > 100 ||
            column.Field is null ||
            column.Width is < 50 or > 2000 ||
            column.Order < 0 ||
            !Enum.IsDefined(column.SortType))
            return false;
        if (!IsValidField(column.Field) ||
            column.EditTarget is not null &&
            !IsValidField(column.EditTarget))
            return false;
        return true;
    }

    private static bool IsValidField(MetadataFieldKey field) =>
        field.KnownField is { } known
            ? known != TagFields.NullField &&
              Enum.IsDefined(known)
            : !string.IsNullOrWhiteSpace(field.CustomName) &&
              field.CustomName.Length <= 256;

    private static string Preference(
        MetadataGridSurface surface) =>
        $"manager.metadata-columns.{surface.ToString().ToLowerInvariant()}.v1";

    private sealed record StoredColumns(
        int Version,
        ImmutableArray<UserMetadataColumnDescriptor> Columns);
}
