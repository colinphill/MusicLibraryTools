using System.Collections.Immutable;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum DelimitedMetadataEmptyCellMode
{
    Ignore,
    RemoveField,
    PreserveEmptyValue,
}

public enum DelimitedMetadataImportIssueSeverity
{
    Warning,
    Blocker,
}

public sealed record DelimitedMetadataImportOptions(
    char? Delimiter = null,
    string PathColumn = "Path",
    DelimitedMetadataEmptyCellMode EmptyCellMode =
        DelimitedMetadataEmptyCellMode.Ignore);

public sealed record DelimitedMetadataImportIssue(
    DelimitedMetadataImportIssueSeverity Severity,
    string Code,
    string Message,
    int? Row = null,
    string? SourcePath = null);

public sealed record DelimitedMetadataImportResult(
    IReadOnlyDictionary<
        string,
        IReadOnlyList<MetadataValueEdit>> EditsByPath,
    IReadOnlyList<DelimitedMetadataImportIssue> Issues,
    int DataRows,
    int MatchedRows)
{
    public bool CanPreview =>
        EditsByPath.Count > 0 &&
        !Issues.Any(issue =>
            issue.Severity ==
                DelimitedMetadataImportIssueSeverity.Blocker);
}

public interface IDelimitedMetadataImportService
{
    Task<DelimitedMetadataImportResult> ImportAsync(
        string sourcePath,
        IReadOnlyList<string> candidateMediaPaths,
        DelimitedMetadataImportOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class DelimitedMetadataImportService :
    IDelimitedMetadataImportService
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public async Task<DelimitedMetadataImportResult> ImportAsync(
        string sourcePath,
        IReadOnlyList<string> candidateMediaPaths,
        DelimitedMetadataImportOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(candidateMediaPaths);
        options ??= new();
        string text = await File.ReadAllTextAsync(sourcePath, ct);
        ct.ThrowIfCancellationRequested();
        char delimiter = options.Delimiter ??
            DetectDelimiter(sourcePath, text);
        List<string[]> rows;
        try
        {
            rows = Parse(text, delimiter, ct);
        }
        catch (InvalidDataException error)
        {
            return new(
                new Dictionary<
                    string,
                    IReadOnlyList<MetadataValueEdit>>(
                        PathComparer),
                [new(
                    DelimitedMetadataImportIssueSeverity.Blocker,
                    "import.malformed",
                    error.Message)],
                0,
                0);
        }
        if (rows.Count == 0)
            return Blocked("import.empty", "The import file is empty.");
        string[] headers = rows[0];
        int pathIndex = Array.FindIndex(
            headers,
            header => string.Equals(
                header.Trim().TrimStart('\uFEFF'),
                options.PathColumn,
                StringComparison.OrdinalIgnoreCase));
        if (pathIndex < 0)
            return Blocked(
                "import.path-column",
                $"The import file does not contain a " +
                $"'{options.PathColumn}' path column.");

        var issues = new List<DelimitedMetadataImportIssue>();
        var mappings = new List<(int Index, MetadataFieldKey Field)>();
        for (int index = 0; index < headers.Length; index++)
        {
            if (index == pathIndex)
                continue;
            if (TryParseField(headers[index], out MetadataFieldKey field))
                mappings.Add((index, field));
            else if (!string.IsNullOrWhiteSpace(headers[index]))
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Warning,
                    "import.unknown-column",
                    $"Column '{headers[index]}' is not a known field or " +
                    "a Custom:<name> field and will be ignored."));
        }
        if (mappings.Count == 0)
            return Blocked(
                "import.no-fields",
                "The import file has no recognized metadata columns.",
                issues);

        string[] candidates = candidateMediaPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var exact = candidates.ToDictionary(
            path => path, PathComparer);
        Dictionary<string, string[]> byFileName = candidates
            .GroupBy(
                path => Path.GetFileName(path) ?? path,
                PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                PathComparer);
        string sourceDirectory =
            Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ??
            Directory.GetCurrentDirectory();
        var edits = new Dictionary<
            string,
            IReadOnlyList<MetadataValueEdit>>(PathComparer);
        int dataRows = Math.Max(0, rows.Count - 1);
        int matchedRows = 0;
        for (int index = 1; index < rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index - 1,
                dataRows,
                Message:
                    $"Mapping import row {index:N0} of {dataRows:N0}"));
            string[] row = rows[index];
            int rowNumber = index + 1;
            string importedPath =
                pathIndex < row.Length ? row[pathIndex].Trim() : "";
            if (string.IsNullOrWhiteSpace(importedPath))
            {
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Warning,
                    "import.missing-path",
                    "The row has no media path and was skipped.",
                    rowNumber));
                continue;
            }
            string? matched;
            bool ambiguous;
            try
            {
                matched = ResolvePath(
                    importedPath,
                    sourceDirectory,
                    exact,
                    byFileName,
                    out ambiguous);
            }
            catch (Exception error) when (
                error is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Warning,
                    "import.invalid-path",
                    $"'{importedPath}' is not a valid media path.",
                    rowNumber,
                    importedPath));
                continue;
            }
            if (matched is null)
            {
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Warning,
                    ambiguous
                        ? "import.ambiguous-path"
                        : "import.unmatched-path",
                    ambiguous
                        ? $"'{importedPath}' matches more than one file " +
                          "in the selected scope."
                        : $"'{importedPath}' is not present in the " +
                          "selected scope.",
                    rowNumber,
                    importedPath));
                continue;
            }
            if (edits.ContainsKey(matched))
            {
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Blocker,
                    "import.duplicate-path",
                    $"More than one row maps to '{matched}'.",
                    rowNumber,
                    importedPath));
                continue;
            }

            var valuesByField = new Dictionary<
                MetadataFieldKey,
                List<string>>();
            var removeFields = new HashSet<MetadataFieldKey>();
            foreach ((int column, MetadataFieldKey field) in mappings)
            {
                string value =
                    column < row.Length ? row[column] : "";
                if (value.Length == 0)
                {
                    if (options.EmptyCellMode ==
                        DelimitedMetadataEmptyCellMode.Ignore)
                        continue;
                    if (options.EmptyCellMode ==
                        DelimitedMetadataEmptyCellMode.RemoveField)
                    {
                        removeFields.Add(field);
                        continue;
                    }
                }
                if (!valuesByField.TryGetValue(
                        field, out List<string>? values))
                {
                    values = [];
                    valuesByField[field] = values;
                }
                values.Add(value);
            }
            foreach (MetadataFieldKey field in removeFields)
                valuesByField[field] = [];
            MetadataValueEdit[] rowEdits = valuesByField
                .Select(pair => new MetadataValueEdit(
                    pair.Key,
                    pair.Value.ToImmutableArray()))
                .ToArray();
            if (rowEdits.Length == 0)
            {
                issues.Add(new(
                    DelimitedMetadataImportIssueSeverity.Warning,
                    "import.no-values",
                    "The row contains no metadata values to import.",
                    rowNumber,
                    importedPath));
                continue;
            }
            edits[matched] = rowEdits;
            matchedRows++;
        }
        progress?.Report(new(
            OperationPhase.Planning,
            dataRows,
            dataRows,
            Message:
                $"Mapped {matchedRows:N0} of {dataRows:N0} import rows"));
        return new(edits, issues, dataRows, matchedRows);
    }

    private static DelimitedMetadataImportResult Blocked(
        string code,
        string message,
        IEnumerable<DelimitedMetadataImportIssue>? prior = null)
    {
        var issues = prior?.ToList() ?? [];
        issues.Add(new(
            DelimitedMetadataImportIssueSeverity.Blocker,
            code,
            message));
        return new(
            new Dictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>(PathComparer),
            issues,
            0,
            0);
    }

    private static string? ResolvePath(
        string imported,
        string sourceDirectory,
        IReadOnlyDictionary<string, string> exact,
        IReadOnlyDictionary<string, string[]> byFileName,
        out bool ambiguous)
    {
        ambiguous = false;
        string full = Path.GetFullPath(
            Path.IsPathRooted(imported)
                ? imported
                : Path.Combine(sourceDirectory, imported));
        if (exact.TryGetValue(full, out string? match))
            return match;
        string fileName = Path.GetFileName(imported);
        if (!byFileName.TryGetValue(
                fileName, out string[]? matches))
            return null;
        if (matches.Length == 1)
            return matches[0];
        ambiguous = true;
        return null;
    }

    private static bool TryParseField(
        string header,
        out MetadataFieldKey field)
    {
        string value = header.Trim().TrimStart('\uFEFF');
        const string customPrefix = "Custom:";
        if (value.StartsWith(
                customPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            string name = value[customPrefix.Length..].Trim();
            if (name.Length > 0)
            {
                field = MetadataFieldKey.Custom(name);
                return true;
            }
        }
        string normalized = NormalizeHeader(value);
        foreach (TagFields known in Enum.GetValues<TagFields>())
        {
            if (known != TagFields.NullField &&
                NormalizeHeader(known.ToString()) == normalized)
            {
                field = MetadataFieldKey.Known(known);
                return true;
            }
        }
        field = null!;
        return false;
    }

    private static string NormalizeHeader(string value) =>
        new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static char DetectDelimiter(
        string sourcePath,
        string text)
    {
        if (Path.GetExtension(sourcePath).Equals(
                ".tsv",
                StringComparison.OrdinalIgnoreCase))
            return '\t';
        char[] choices = [',', '\t', ';'];
        return choices
            .OrderByDescending(choice =>
                CountHeaderDelimiters(text, choice))
            .First();
    }

    private static int CountHeaderDelimiters(
        string text,
        char delimiter)
    {
        bool quoted = false;
        int count = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (value == '"')
            {
                if (quoted &&
                    index + 1 < text.Length &&
                    text[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                quoted = !quoted;
            }
            else if (!quoted && value == delimiter)
            {
                count++;
            }
            else if (!quoted && value is '\r' or '\n')
            {
                break;
            }
        }
        return count;
    }

    private static List<string[]> Parse(
        string text,
        char delimiter,
        CancellationToken ct)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool fieldStarted = false;
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 0x3fff) == 0)
                ct.ThrowIfCancellationRequested();
            char value = text[index];
            if (quoted)
            {
                if (value == '"')
                {
                    if (index + 1 < text.Length &&
                        text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(value);
                }
                continue;
            }
            if (value == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (value == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (value is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                rows.Add(row.ToArray());
                row.Clear();
                if (value == '\r' &&
                    index + 1 < text.Length &&
                    text[index + 1] == '\n')
                    index++;
            }
            else
            {
                field.Append(value);
                fieldStarted = true;
            }
        }
        if (quoted)
            throw new InvalidDataException(
                "The import file ends inside a quoted field.");
        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
