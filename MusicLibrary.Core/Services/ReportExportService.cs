using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum ReportFormat
{
    Text,
    Csv,
    Html,
    Rtf,
}

public enum ReportEncoding
{
    Utf8,
    Utf8WithBom,
    Utf16LittleEndian,
}

public enum ReportFieldKind
{
    KnownMetadata,
    CustomMetadata,
    FileProperty,
    TechnicalProperty,
}

public enum ReportSortType
{
    Text,
    Natural,
    Numeric,
    DateTime,
}

public sealed record ReportFieldDescriptor(
    string Id,
    string Label,
    ReportFieldKind Kind,
    TagFields? KnownField = null,
    string? Name = null)
{
    public static ReportFieldDescriptor Known(
        TagFields field,
        string? label = null,
        string? id = null) =>
        new(
            id ?? $"metadata.{field}",
            label ?? field.ToString(),
            ReportFieldKind.KnownMetadata,
            field);

    public static ReportFieldDescriptor Custom(
        string name,
        string? label = null,
        string? id = null) =>
        new(
            id ?? $"custom.{name}",
            label ?? name,
            ReportFieldKind.CustomMetadata,
            Name: name);

    public static ReportFieldDescriptor File(
        string name,
        string? label = null,
        string? id = null) =>
        new(
            id ?? $"file.{name}",
            label ?? name,
            ReportFieldKind.FileProperty,
            Name: name);

    public static ReportFieldDescriptor Technical(
        string name,
        string? label = null,
        string? id = null) =>
        new(
            id ?? $"technical.{name}",
            label ?? name,
            ReportFieldKind.TechnicalProperty,
            Name: name);
}

public sealed record ReportSortDescriptor(
    string FieldId,
    ReportSortType Type = ReportSortType.Text,
    bool Descending = false);

public sealed record ReportConfiguration(
    string Name,
    ReportFormat Format,
    string OutputPath,
    ImmutableArray<ReportFieldDescriptor> Fields,
    ImmutableArray<ReportSortDescriptor> Sorting = default,
    string? GroupByFieldId = null,
    bool OneFilePerGroup = false,
    string GroupFileNameTemplate = "{Group}.{Format}",
    ReportEncoding Encoding = ReportEncoding.Utf8);

public sealed record ReportExportRequest(
    IReadOnlyList<string> Paths,
    ReportConfiguration Configuration);

public sealed record ReportFilePlan(
    string Group,
    string DestinationPath,
    int RowCount,
    int ByteCount);

public sealed record ReportExportPlan(
    ReportExportRequest Request,
    IReadOnlyList<ReportFilePlan> Files,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply;
}

public sealed record ReportExportResult(
    int FileCount,
    int RowCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public sealed record ReportRow(
    string SourcePath,
    IReadOnlyDictionary<string, string> Values);

public sealed record ReportRenderRequest(
    IReadOnlyList<ReportFieldDescriptor> Fields,
    IReadOnlyList<ReportRow> Rows);

public interface IReportRenderer
{
    ReportFormat Format { get; }
    string FileExtension { get; }
    string Render(ReportRenderRequest request);
}

public interface IReportExportService
{
    Task<ReportExportPlan> PreviewAsync(
        ReportExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ReportExportResult> ApplyAsync(
        ReportExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class ReportExportService(
    IMetadataDocumentService documents,
    IFileMutationPlanExecutor executor,
    IEnumerable<IReportRenderer> renderers) : IReportExportService
{
    public async Task<ReportExportPlan> PreviewAsync(
        ReportExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        ReportConfiguration configuration = request.Configuration;
        var issues = Validate(request);
        IReportRenderer[] matching = renderers
            .Where(renderer => renderer.Format == configuration.Format)
            .Take(2)
            .ToArray();
        if (matching.Length != 1)
            issues.Add(new(
                matching.Length == 0
                    ? "report-renderer-missing"
                    : "report-renderer-ambiguous",
                OperationIssueSeverity.Blocker,
                matching.Length == 0
                    ? $"No renderer is registered for {configuration.Format}."
                    : $"Multiple renderers are registered for {configuration.Format}."));
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);

        var normalizedPaths = new List<string>(request.Paths.Count);
        foreach (string path in request.Paths.Where(path =>
                     !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                normalizedPaths.Add(Path.GetFullPath(path));
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                issues.Add(new(
                    "report-source-invalid",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    path));
            }
        }
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);
        string[] paths = normalizedPaths
            .Distinct(PathComparer)
            .ToArray();
        var rows = new List<IndexedReportRow>(paths.Length);
        for (int index = 0; index < paths.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[index];
            progress?.Report(new(
                OperationPhase.LoadingLibrary,
                index,
                paths.Length,
                path,
                $"Reading report source {index + 1:N0} of {paths.Length:N0}"));
            try
            {
                MediaDocument document = await documents.LoadAsync(
                    path, includeArtwork: false, ct).ConfigureAwait(false);
                rows.Add(new(
                    index,
                    new ReportRow(
                        path,
                        configuration.Fields.ToDictionary(
                            field => field.Id,
                            field => ResolveValue(document, field),
                            StringComparer.OrdinalIgnoreCase))));
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                issues.Add(new(
                    "report-source-unreadable",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    path));
            }
        }
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);

        Sort(rows, configuration.Sorting);
        IReportRenderer renderer = matching[0];
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string destinationRoot = configuration.OneFilePerGroup
            ? Path.GetFullPath(configuration.OutputPath)
            : Path.GetDirectoryName(
                Path.GetFullPath(configuration.OutputPath))!;
        string recoveryRoot = Path.Combine(
            destinationRoot,
            ".MusicLibraryManager-report-recovery",
            createdAt.UtcDateTime.ToString(
                "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture));
        var actions = new List<FileMutationAction>();
        var outputs = new List<ReportFilePlan>();
        var destinations = new HashSet<string>(PathComparer);
        IReadOnlyList<IGrouping<string, IndexedReportRow>> groups =
            Group(rows, configuration);
        for (int index = 0; index < groups.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            IGrouping<string, IndexedReportRow> group = groups[index];
            string destination = ResolveDestination(
                configuration, renderer, group.Key);
            if (!destinations.Add(destination))
            {
                issues.Add(new(
                    "report-output-collision",
                    OperationIssueSeverity.Blocker,
                    "Two report groups resolve to the same output path.",
                    destination));
                continue;
            }
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                groups.Count,
                destination,
                $"Rendering report {index + 1:N0} of {groups.Count:N0}"));
            string text = renderer.Render(new(
                configuration.Fields,
                group.Select(item => item.Row).ToArray()));
            ImmutableArray<byte> content =
                Encode(text, configuration.Encoding);
            OperationPathSnapshot snapshot =
                CaptureSnapshot(destination);
            if (snapshot.IsDirectory)
            {
                issues.Add(new(
                    "report-output-is-directory",
                    OperationIssueSeverity.Blocker,
                    "The report output path is an existing directory.",
                    destination));
                continue;
            }
            actions.Add(new(
                snapshot.Exists
                    ? FileMutationKind.ReplaceGenerated
                    : FileMutationKind.Write,
                "",
                destination,
                null,
                snapshot,
                content));
            outputs.Add(new(
                group.Key,
                destination,
                group.Count(),
                content.Length));
        }
        var mutationPlan = new FileMutationPlan(
            "ReportExport",
            destinationRoot,
            recoveryRoot,
            actions,
            issues,
            createdAt,
            RetainRecovery: true);
        progress?.Report(new(
            OperationPhase.Completed,
            outputs.Count,
            outputs.Count,
            Message:
                $"Prepared {outputs.Count:N0} report file(s) with {rows.Count:N0} row(s)"));
        return new(request, outputs, mutationPlan, issues);
    }

    public async Task<ReportExportResult> ApplyAsync(
        ReportExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException(
                "The reviewed report plan contains blocking issues.");
        FileMutationSummary mutations = await executor.ApplyAsync(
            plan.MutationPlan, progress, ct).ConfigureAwait(false);
        return new(
            plan.Files.Count,
            plan.Files.Sum(file => file.RowCount),
            mutations,
            [.. plan.Issues, .. mutations.Issues]);
    }

    private static List<OperationIssue> Validate(
        ReportExportRequest request)
    {
        ReportConfiguration configuration = request.Configuration;
        var issues = new List<OperationIssue>();
        if (!request.Paths.Any(path =>
                !string.IsNullOrWhiteSpace(path)))
            issues.Add(new(
                "report-sources-empty",
                OperationIssueSeverity.Blocker,
                "Select at least one report source file."));
        if (string.IsNullOrWhiteSpace(configuration.Name))
            issues.Add(new(
                "report-name-empty",
                OperationIssueSeverity.Blocker,
                "A report name is required."));
        if (string.IsNullOrWhiteSpace(configuration.OutputPath))
            issues.Add(new(
                "report-output-empty",
                OperationIssueSeverity.Blocker,
                "A report output path is required."));
        else
        {
            try
            {
                _ = Path.GetFullPath(configuration.OutputPath);
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                issues.Add(new(
                    "report-output-invalid",
                    OperationIssueSeverity.Blocker,
                    $"The report output path is invalid: {error.Message}"));
            }
        }
        if (configuration.Fields.IsDefaultOrEmpty)
            issues.Add(new(
                "report-fields-empty",
                OperationIssueSeverity.Blocker,
                "Select at least one report field."));
        foreach (ReportFieldDescriptor field in configuration.Fields)
            ValidateField(field, issues);
        string[] duplicateIds = configuration.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Id))
            .GroupBy(field => field.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
            issues.Add(new(
                "report-field-id-duplicate",
                OperationIssueSeverity.Blocker,
                "Report field IDs must be unique: " +
                string.Join(", ", duplicateIds)));
        var fieldIds = configuration.Fields
            .Select(field => field.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configuration.GroupByFieldId) &&
            !fieldIds.Contains(configuration.GroupByFieldId))
            issues.Add(new(
                "report-group-field-missing",
                OperationIssueSeverity.Blocker,
                "The report grouping field is not selected."));
        if (configuration.OneFilePerGroup &&
            string.IsNullOrWhiteSpace(configuration.GroupByFieldId))
            issues.Add(new(
                "report-group-required",
                OperationIssueSeverity.Blocker,
                "One-file-per-group output requires a grouping field."));
        foreach (ReportSortDescriptor sort in
                 configuration.Sorting.IsDefault
                     ? []
                     : configuration.Sorting)
            if (!fieldIds.Contains(sort.FieldId))
                issues.Add(new(
                    "report-sort-field-missing",
                    OperationIssueSeverity.Blocker,
                    $"Sort field '{sort.FieldId}' is not selected."));
        return issues;
    }

    private static void ValidateField(
        ReportFieldDescriptor field,
        ICollection<OperationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(field.Id) ||
            string.IsNullOrWhiteSpace(field.Label))
        {
            issues.Add(new(
                "report-field-invalid",
                OperationIssueSeverity.Blocker,
                "Every report field requires an ID and label."));
            return;
        }
        bool valid = field.Kind switch
        {
            ReportFieldKind.KnownMetadata =>
                field.KnownField is not null and not TagFields.NullField &&
                string.IsNullOrWhiteSpace(field.Name),
            ReportFieldKind.CustomMetadata or
            ReportFieldKind.FileProperty or
            ReportFieldKind.TechnicalProperty =>
                field.KnownField is null &&
                !string.IsNullOrWhiteSpace(field.Name),
            _ => false,
        };
        if (!valid)
            issues.Add(new(
                "report-field-invalid",
                OperationIssueSeverity.Blocker,
                $"Report field '{field.Label}' has an invalid source."));
    }

    private static string ResolveValue(
        MediaDocument document,
        ReportFieldDescriptor field)
    {
        return field.Kind switch
        {
            ReportFieldKind.KnownMetadata => string.Join(
                "; ",
                document.Values(MetadataFieldKey.Known(
                    field.KnownField!.Value))),
            ReportFieldKind.CustomMetadata => string.Join(
                "; ",
                document.Values(MetadataFieldKey.Custom(field.Name!))),
            ReportFieldKind.FileProperty =>
                ResolveFileProperty(document, field.Name!),
            ReportFieldKind.TechnicalProperty =>
                ResolveTechnicalProperty(document, field.Name!),
            _ => "",
        };
    }

    private static string ResolveFileProperty(
        MediaDocument document,
        string name) =>
        name.ToUpperInvariant() switch
        {
            "PATH" => document.Path,
            "FILENAME" => Path.GetFileName(document.Path),
            "DIRECTORY" => Path.GetDirectoryName(document.Path) ?? "",
            "EXTENSION" => Path.GetExtension(document.Path),
            "LENGTH" => document.Snapshot.Length.ToString(
                CultureInfo.InvariantCulture),
            "MODIFIED" => document.Snapshot.LastWriteTimeUtc.ToString(
                "O", CultureInfo.InvariantCulture),
            _ => "",
        };

    private static string ResolveTechnicalProperty(
        MediaDocument document,
        string name)
    {
        CodecModel? codec = document.Codec;
        if (codec is null)
            return "";
        return name.ToUpperInvariant() switch
        {
            "CODEC" => codec.CodecName ?? "",
            "CODECTYPE" => codec.CodecType.ToString(),
            "BITRATE" => codec.AverageBitrate.ToString(
                CultureInfo.InvariantCulture),
            "MAXBITRATE" => codec.MaxBitrate.ToString(
                CultureInfo.InvariantCulture),
            "BITSPERSAMPLE" => codec.BitsPerSample.ToString(
                CultureInfo.InvariantCulture),
            "SAMPLERATE" => codec.Samplerate.ToString(
                CultureInfo.InvariantCulture),
            "CHANNELS" => codec.Channels.ToString(
                CultureInfo.InvariantCulture),
            "DURATION" => codec.DurationInSeconds.ToString(
                "0.###", CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    private static void Sort(
        List<IndexedReportRow> rows,
        ImmutableArray<ReportSortDescriptor> sorting)
    {
        if (sorting.IsDefaultOrEmpty)
            return;
        rows.Sort((left, right) =>
        {
            foreach (ReportSortDescriptor sort in sorting)
            {
                left.Row.Values.TryGetValue(
                    sort.FieldId, out string? leftValue);
                right.Row.Values.TryGetValue(
                    sort.FieldId, out string? rightValue);
                int comparison = Compare(
                    leftValue ?? "",
                    rightValue ?? "",
                    sort.Type);
                if (comparison != 0)
                    return sort.Descending ? -comparison : comparison;
            }
            return left.Index.CompareTo(right.Index);
        });
    }

    private static int Compare(
        string left,
        string right,
        ReportSortType type) =>
        type switch
        {
            ReportSortType.Numeric =>
                CompareNumbers(left, right),
            ReportSortType.DateTime =>
                CompareDates(left, right),
            ReportSortType.Natural =>
                NaturalCompare(left, right),
            _ => StringComparer.OrdinalIgnoreCase.Compare(left, right),
        };

    private static int CompareNumbers(string left, string right)
    {
        bool leftValid = double.TryParse(
            left,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double leftNumber);
        bool rightValid = double.TryParse(
            right,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double rightNumber);
        if (leftValid && rightValid)
            return leftNumber.CompareTo(rightNumber);
        if (leftValid != rightValid)
            return leftValid ? -1 : 1;
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int CompareDates(string left, string right)
    {
        bool leftValid = DateTimeOffset.TryParse(
            left,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset leftDate);
        bool rightValid = DateTimeOffset.TryParse(
            right,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset rightDate);
        if (leftValid && rightValid)
            return leftDate.CompareTo(rightDate);
        if (leftValid != rightValid)
            return leftValid ? -1 : 1;
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int NaturalCompare(string left, string right)
    {
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) &&
                char.IsDigit(right[rightIndex]))
            {
                int leftStart = leftIndex;
                int rightStart = rightIndex;
                while (leftIndex < left.Length &&
                       char.IsDigit(left[leftIndex]))
                    leftIndex++;
                while (rightIndex < right.Length &&
                       char.IsDigit(right[rightIndex]))
                    rightIndex++;
                string leftDigits = left[leftStart..leftIndex]
                    .TrimStart('0');
                string rightDigits = right[rightStart..rightIndex]
                    .TrimStart('0');
                int lengthComparison =
                    leftDigits.Length.CompareTo(rightDigits.Length);
                if (lengthComparison != 0)
                    return lengthComparison;
                int digitComparison = string.CompareOrdinal(
                    leftDigits, rightDigits);
                if (digitComparison != 0)
                    return digitComparison;
                continue;
            }
            int characterComparison = char.ToUpperInvariant(
                    left[leftIndex])
                .CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterComparison != 0)
                return characterComparison;
            leftIndex++;
            rightIndex++;
        }
        return (left.Length - leftIndex)
            .CompareTo(right.Length - rightIndex);
    }

    private static IReadOnlyList<IGrouping<string, IndexedReportRow>> Group(
        IReadOnlyList<IndexedReportRow> rows,
        ReportConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GroupByFieldId))
            return [new ReportGrouping("", rows)];
        return rows
            .GroupBy(
                row => row.Row.Values.GetValueOrDefault(
                    configuration.GroupByFieldId) ?? "",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDestination(
        ReportConfiguration configuration,
        IReportRenderer renderer,
        string group)
    {
        if (!configuration.OneFilePerGroup)
            return Path.GetFullPath(configuration.OutputPath);
        string safeGroup = SafeFileName(
            string.IsNullOrWhiteSpace(group) ? "Missing" : group);
        string fileName = configuration.GroupFileNameTemplate
            .Replace("{Group}", safeGroup, StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{Format}",
                renderer.FileExtension.TrimStart('.'),
                StringComparison.OrdinalIgnoreCase);
        fileName = SafeFileName(fileName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            fileName += renderer.FileExtension;
        return Path.Combine(
            Path.GetFullPath(configuration.OutputPath),
            fileName);
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            result.Append(
                invalid.Contains(character) ||
                character is '/' or '\\'
                    ? '_'
                    : character);
        string safe = result.ToString().Trim().Trim('.');
        return safe.Length == 0 ? "Report" : safe;
    }

    private static ImmutableArray<byte> Encode(
        string text,
        ReportEncoding encoding)
    {
        Encoding selected = encoding switch
        {
            ReportEncoding.Utf8 => new UTF8Encoding(false),
            ReportEncoding.Utf8WithBom => new UTF8Encoding(true),
            ReportEncoding.Utf16LittleEndian =>
                new UnicodeEncoding(false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        byte[] preamble = selected.GetPreamble();
        byte[] content = selected.GetBytes(text);
        if (preamble.Length == 0)
            return [.. content];
        var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return [.. result];
    }

    private static OperationPathSnapshot CaptureSnapshot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var directory = new DirectoryInfo(fullPath);
            return new(true, true, 0, directory.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        }
        var file = new FileInfo(fullPath);
        return file.Exists
            ? new(
                true,
                false,
                file.Length,
                file.LastWriteTimeUtc)
            {
                Path = fullPath,
            }
            : OperationPathSnapshot.Missing(fullPath);
    }

    private static ReportExportPlan EmptyPlan(
        ReportExportRequest request,
        IReadOnlyList<OperationIssue> issues)
    {
        string root = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(
                request.Configuration.OutputPath))
        {
            try
            {
                string output = Path.GetFullPath(
                    request.Configuration.OutputPath);
                root = request.Configuration.OneFilePerGroup
                    ? output
                    : Path.GetDirectoryName(output) ?? root;
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                // Validation already describes the bad path. Keep the empty
                // blocked plan rooted at a harmless existing location.
            }
        }
        var mutation = new FileMutationPlan(
            "ReportExport",
            root,
            Path.Combine(root, ".MusicLibraryManager-report-recovery"),
            [],
            issues,
            DateTimeOffset.UtcNow);
        return new(request, [], mutation, issues);
    }

    private sealed record IndexedReportRow(int Index, ReportRow Row);

    private sealed class ReportGrouping(
        string key,
        IEnumerable<IndexedReportRow> rows) :
        List<IndexedReportRow>(rows),
        IGrouping<string, IndexedReportRow>
    {
        public string Key { get; } = key;
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public sealed class TextReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Text;
    public string FileExtension => ".txt";

    public string Render(ReportRenderRequest request)
    {
        Validate(request);
        var result = new StringBuilder();
        AppendRow(request.Fields.Select(field => field.Label));
        foreach (ReportRow row in request.Rows)
            AppendRow(request.Fields.Select(field =>
                row.Values.GetValueOrDefault(field.Id) ?? ""));
        return result.ToString();

        void AppendRow(IEnumerable<string> values) =>
            result.AppendJoin(
                    '\t',
                    values.Select(value => value
                        .Replace('\t', ' ')
                        .Replace('\r', ' ')
                        .Replace('\n', ' ')))
                .AppendLine();
    }

    private static void Validate(ReportRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Fields);
        ArgumentNullException.ThrowIfNull(request.Rows);
    }
}

public sealed class CsvReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Csv;
    public string FileExtension => ".csv";

    public string Render(ReportRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new StringBuilder();
        AppendRow(request.Fields.Select(field => field.Label));
        foreach (ReportRow row in request.Rows)
            AppendRow(request.Fields.Select(field =>
                row.Values.GetValueOrDefault(field.Id) ?? ""));
        return result.ToString();

        void AppendRow(IEnumerable<string> values) =>
            result.AppendJoin(',', values.Select(Escape))
                .Append("\r\n");
    }

    private static string Escape(string value) =>
        QuoteIfNeeded(NeutralizeFormula(value));

    private static string NeutralizeFormula(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 &&
               trimmed[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;
    }

    private static string QuoteIfNeeded(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

public sealed class HtmlReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Html;
    public string FileExtension => ".html";

    public string Render(ReportRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new StringBuilder(
            "<!doctype html>\n<html><head><meta charset=\"utf-8\">" +
            "<title>Music library report</title></head><body><table>\n<thead><tr>");
        foreach (ReportFieldDescriptor field in request.Fields)
            result.Append("<th>")
                .Append(WebUtility.HtmlEncode(field.Label))
                .Append("</th>");
        result.Append("</tr></thead>\n<tbody>\n");
        foreach (ReportRow row in request.Rows)
        {
            result.Append("<tr>");
            foreach (ReportFieldDescriptor field in request.Fields)
                result.Append("<td>")
                    .Append(WebUtility.HtmlEncode(
                        row.Values.GetValueOrDefault(field.Id) ?? ""))
                    .Append("</td>");
            result.Append("</tr>\n");
        }
        return result.Append(
                "</tbody>\n</table></body></html>\n")
            .ToString();
    }
}

public sealed class RtfReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Rtf;
    public string FileExtension => ".rtf";

    public string Render(ReportRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new StringBuilder(@"{\rtf1\ansi\deff0");
        result.AppendLine();
        AppendRow(request.Fields.Select(field => field.Label), bold: true);
        foreach (ReportRow row in request.Rows)
            AppendRow(request.Fields.Select(field =>
                row.Values.GetValueOrDefault(field.Id) ?? ""), bold: false);
        return result.Append('}').ToString();

        void AppendRow(IEnumerable<string> values, bool bold)
        {
            if (bold)
                result.Append(@"\b ");
            bool first = true;
            foreach (string value in values)
            {
                if (!first)
                    result.Append(@"\tab ");
                AppendEscaped(result, value);
                first = false;
            }
            if (bold)
                result.Append(@"\b0 ");
            result.AppendLine(@"\par");
        }
    }

    private static void AppendEscaped(
        StringBuilder output,
        string value)
    {
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                case '{':
                case '}':
                    output.Append('\\').Append(character);
                    break;
                case '\r':
                    break;
                case '\n':
                    output.Append(@"\line ");
                    break;
                default:
                    if (character <= 0x7f)
                        output.Append(character);
                    else
                        output.Append(@"\u")
                            .Append(unchecked((short)character))
                            .Append('?');
                    break;
            }
        }
    }
}
