using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum ExternalToolInvocationMode
{
    OnceForSelection,
    OncePerFile,
}

public sealed record ExternalToolDefinition(
    Guid Id,
    string Name,
    string Executable,
    ImmutableArray<string> Arguments,
    string? WorkingDirectory = null,
    ExternalToolInvocationMode InvocationMode =
        ExternalToolInvocationMode.OnceForSelection);

public sealed record ExternalToolInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyList<string> SourcePaths)
{
    public string DisplayCommand =>
        Executable + (Arguments.Count == 0
            ? ""
            : " " + string.Join(" ", Arguments.Select(QuoteForDisplay)));

    private static string QuoteForDisplay(string value) =>
        value.Length == 0 ||
        value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
}

public sealed record ExternalToolPlan(
    ExternalToolDefinition Definition,
    IReadOnlyList<ExternalToolInvocation> Invocations,
    IReadOnlyList<OperationIssue> Issues,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyDictionary<string, OperationPathSnapshot>?
        SourceSnapshots = null)
{
    public bool CanRun => Invocations.Count > 0 &&
        Issues.All(issue =>
            issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record ExternalToolProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record ExternalToolInvocationResult(
    ExternalToolInvocation Invocation,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record ExternalToolRunResult(
    IReadOnlyList<ExternalToolInvocationResult> Invocations)
{
    public int SucceededCount =>
        Invocations.Count(result => result.Succeeded);
    public int FailedCount =>
        Invocations.Count - SucceededCount;
}

public interface IExternalToolProcessRunner
{
    Task<ExternalToolProcessResult> RunAsync(
        ExternalToolInvocation invocation,
        CancellationToken ct = default);
}

public interface IExternalToolService
{
    ExternalToolPlan Preview(
        ExternalToolDefinition definition,
        IReadOnlyList<string> paths);

    Task<ExternalToolRunResult> RunAsync(
        ExternalToolPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public interface IExternalToolStore
{
    IReadOnlyList<ExternalToolDefinition> Load();
    void Save(ExternalToolDefinition definition);
    void Delete(Guid id);
}

public sealed class ExternalToolStore(IAppSettings settings) :
    IExternalToolStore
{
    public const string PreferenceKey = "manager.externalTools.v1";
    private readonly object _sync = new();

    public IReadOnlyList<ExternalToolDefinition> Load()
    {
        lock (_sync)
        {
            try
            {
                string? json = settings.GetPreference(PreferenceKey);
                return string.IsNullOrWhiteSpace(json)
                    ? []
                    : JsonSerializer.Deserialize<
                        List<ExternalToolDefinition>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Save(ExternalToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            List<ExternalToolDefinition> tools = Load().ToList();
            int index = tools.FindIndex(tool =>
                tool.Id == definition.Id);
            if (index < 0)
                tools.Add(definition);
            else
                tools[index] = definition;
            Persist(tools);
        }
    }

    public void Delete(Guid id)
    {
        lock (_sync)
        {
            List<ExternalToolDefinition> tools = Load()
                .Where(tool => tool.Id != id)
                .ToList();
            Persist(tools);
        }
    }

    private void Persist(
        IReadOnlyList<ExternalToolDefinition> tools) =>
        settings.SetPreference(
            PreferenceKey,
            JsonSerializer.Serialize(tools));
}

public sealed class ExternalToolService(
    IExternalToolProcessRunner runner) : IExternalToolService
{
    private static readonly Regex Placeholder = new(
        "\\{(?<name>[A-Za-z]+)\\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> FilePlaceholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "File",
            "Directory",
            "FileName",
            "FileNameWithoutExtension",
            "Extension",
            "Index",
        };
    private static readonly HashSet<string> AllPlaceholders =
        new(FilePlaceholders, StringComparer.OrdinalIgnoreCase)
        {
            "Files",
            "Count",
        };

    public ExternalToolPlan Preview(
        ExternalToolDefinition definition,
        IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(paths);
        var issues = ValidateDefinition(definition);
        var normalized = new List<string>(paths.Count);
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index];
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    issues.Add(new(
                        "external-tool-source-missing",
                        OperationIssueSeverity.Blocker,
                        "The selected file no longer exists.",
                        fullPath));
                normalized.Add(fullPath);
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                issues.Add(new(
                    "external-tool-source-invalid",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    path));
            }
        }
        if (normalized.Count == 0)
            issues.Add(new(
                "external-tool-sources-empty",
                OperationIssueSeverity.Blocker,
                "Select at least one file for the external tool."));
        ValidateTemplates(definition, issues);
        ValidateExecutable(definition.Executable, issues);
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return new(
                definition,
                [],
                issues,
                DateTimeOffset.UtcNow,
                CaptureSnapshots(normalized));

        var invocations = new List<ExternalToolInvocation>();
        if (definition.InvocationMode ==
            ExternalToolInvocationMode.OnceForSelection)
        {
            TryAddInvocation(
                definition,
                normalized,
                normalized[0],
                0,
                normalized.Count,
                invocations,
                issues);
        }
        else
        {
            for (int index = 0; index < normalized.Count; index++)
            {
                TryAddInvocation(
                    definition,
                    [normalized[index]],
                    normalized[index],
                    index,
                    normalized.Count,
                    invocations,
                    issues);
            }
        }
        return new(
            definition,
            invocations,
            issues,
            DateTimeOffset.UtcNow,
            CaptureSnapshots(normalized));
    }

    private static void TryAddInvocation(
        ExternalToolDefinition definition,
        IReadOnlyList<string> sourcePaths,
        string currentPath,
        int index,
        int totalCount,
        ICollection<ExternalToolInvocation> invocations,
        ICollection<OperationIssue> issues)
    {
        try
        {
            ExternalToolInvocation invocation = Expand(
                definition,
                sourcePaths,
                currentPath,
                index,
                totalCount);
            ValidateWorkingDirectory(invocation, issues);
            invocations.Add(invocation);
        }
        catch (Exception error) when (
            error is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            issues.Add(new(
                "external-tool-working-directory-invalid",
                OperationIssueSeverity.Blocker,
                error.Message,
                definition.WorkingDirectory));
        }
    }

    public async Task<ExternalToolRunResult> RunAsync(
        ExternalToolPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanRun)
            throw new InvalidOperationException(
                "The reviewed external-tool plan contains blocking issues.");
        RevalidateSources(plan);
        var results =
            new List<ExternalToolInvocationResult>(
                plan.Invocations.Count);
        for (int index = 0; index < plan.Invocations.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            ExternalToolInvocation invocation =
                plan.Invocations[index];
            progress?.Report(new(
                OperationPhase.Applying,
                index,
                plan.Invocations.Count,
                invocation.SourcePaths.FirstOrDefault(),
                $"Running {plan.Definition.Name}: " +
                $"{index + 1:N0} of {plan.Invocations.Count:N0}"));
            ExternalToolProcessResult result =
                await runner.RunAsync(invocation, ct)
                    .ConfigureAwait(false);
            results.Add(new(
                invocation,
                result.ExitCode,
                result.StandardOutput,
                result.StandardError));
        }
        progress?.Report(new(
            OperationPhase.Completed,
            results.Count,
            results.Count,
            Message:
                $"External tool completed: " +
                $"{results.Count(result => result.ExitCode == 0):N0} succeeded, " +
                $"{results.Count(result => result.ExitCode != 0):N0} failed"));
        return new(results);
    }

    private static List<OperationIssue> ValidateDefinition(
        ExternalToolDefinition definition)
    {
        var issues = new List<OperationIssue>();
        if (definition.Id == Guid.Empty)
            issues.Add(new(
                "external-tool-id-empty",
                OperationIssueSeverity.Blocker,
                "The external tool requires an identifier."));
        if (string.IsNullOrWhiteSpace(definition.Name))
            issues.Add(new(
                "external-tool-name-empty",
                OperationIssueSeverity.Blocker,
                "The external tool requires a name."));
        if (string.IsNullOrWhiteSpace(definition.Executable))
            issues.Add(new(
                "external-tool-executable-empty",
                OperationIssueSeverity.Blocker,
                "Select an executable."));
        if (definition.Arguments.IsDefault)
            issues.Add(new(
                "external-tool-arguments-invalid",
                OperationIssueSeverity.Blocker,
                "The argument list is invalid."));
        return issues;
    }

    private static void ValidateTemplates(
        ExternalToolDefinition definition,
        ICollection<OperationIssue> issues)
    {
        ImmutableArray<string> arguments =
            definition.Arguments.IsDefault
                ? []
                : definition.Arguments;
        string[] templates =
        [
            .. arguments,
            definition.WorkingDirectory ?? "",
        ];
        foreach (string template in templates)
        {
            foreach (Match match in Placeholder.Matches(template))
            {
                string name = match.Groups["name"].Value;
                if (!AllPlaceholders.Contains(name))
                    issues.Add(new(
                        "external-tool-placeholder-unknown",
                        OperationIssueSeverity.Blocker,
                        $"Unknown placeholder '{{{name}}}'."));
            }
        }
        bool hasFiles = arguments.Any(argument =>
            argument.Equals(
                "{Files}",
                StringComparison.OrdinalIgnoreCase));
        bool hasPerFile = arguments.Any(argument =>
            Placeholder.Matches(argument)
                .Select(match => match.Groups["name"].Value)
                .Any(FilePlaceholders.Contains));
        if (definition.InvocationMode ==
            ExternalToolInvocationMode.OnceForSelection)
        {
            if (!hasFiles)
                issues.Add(new(
                    "external-tool-files-placeholder-required",
                    OperationIssueSeverity.Blocker,
                    "Once-for-selection tools require a standalone {Files} argument."));
            foreach (string argument in arguments.Where(
                         argument => argument.Contains(
                             "{Files}",
                             StringComparison.OrdinalIgnoreCase) &&
                         !argument.Equals(
                             "{Files}",
                             StringComparison.OrdinalIgnoreCase)))
                issues.Add(new(
                    "external-tool-files-placeholder-standalone",
                    OperationIssueSeverity.Blocker,
                    "{Files} must be a complete argument so each path remains separate."));
            if (hasPerFile)
                issues.Add(new(
                    "external-tool-per-file-placeholder",
                    OperationIssueSeverity.Blocker,
                    "Per-file placeholders cannot be used in once-for-selection mode."));
        }
        else
        {
            if (hasFiles)
                issues.Add(new(
                    "external-tool-files-placeholder-mode",
                    OperationIssueSeverity.Blocker,
                    "{Files} is only available in once-for-selection mode."));
            if (!hasPerFile)
                issues.Add(new(
                    "external-tool-file-placeholder-required",
                    OperationIssueSeverity.Blocker,
                    "Once-per-file tools require a per-file placeholder."));
        }
    }

    private static void ValidateExecutable(
        string executable,
        ICollection<OperationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return;
        if (executable.IndexOfAny(['{', '}']) >= 0)
        {
            issues.Add(new(
                "external-tool-executable-placeholder",
                OperationIssueSeverity.Blocker,
                "Placeholders are not allowed in executable paths."));
            return;
        }
        try
        {
            if ((Path.IsPathFullyQualified(executable) ||
                 executable.IndexOfAny(
                     [Path.DirectorySeparatorChar,
                      Path.AltDirectorySeparatorChar]) >= 0) &&
                !File.Exists(Path.GetFullPath(executable)))
                issues.Add(new(
                    "external-tool-executable-missing",
                    OperationIssueSeverity.Blocker,
                    "The configured executable does not exist.",
                    executable));
        }
        catch (Exception error) when (
            error is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            issues.Add(new(
                "external-tool-executable-invalid",
                OperationIssueSeverity.Blocker,
                error.Message,
                executable));
        }
    }

    private static ExternalToolInvocation Expand(
        ExternalToolDefinition definition,
        IReadOnlyList<string> sourcePaths,
        string currentPath,
        int index,
        int totalCount)
    {
        var arguments = new List<string>();
        foreach (string template in
                 definition.Arguments.IsDefault
                     ? []
                     : definition.Arguments)
        {
            if (template.Equals(
                    "{Files}",
                    StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(sourcePaths);
                continue;
            }
            arguments.Add(ExpandTemplate(
                template,
                currentPath,
                index,
                totalCount));
        }
        string? workingDirectory =
            string.IsNullOrWhiteSpace(definition.WorkingDirectory)
                ? null
                : Path.GetFullPath(ExpandTemplate(
                    definition.WorkingDirectory,
                    currentPath,
                    index,
                    totalCount));
        return new(
            definition.Executable.Trim(),
            arguments,
            workingDirectory,
            sourcePaths.ToArray());
    }

    private static string ExpandTemplate(
        string template,
        string path,
        int index,
        int count) =>
        Placeholder.Replace(template, match =>
            match.Groups["name"].Value.ToUpperInvariant() switch
            {
                "FILE" => path,
                "DIRECTORY" => Path.GetDirectoryName(path) ?? "",
                "FILENAME" => Path.GetFileName(path),
                "FILENAMEWITHOUTEXTENSION" =>
                    Path.GetFileNameWithoutExtension(path),
                "EXTENSION" => Path.GetExtension(path),
                "INDEX" => (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "COUNT" => count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                _ => match.Value,
            });

    private static void ValidateWorkingDirectory(
        ExternalToolInvocation invocation,
        ICollection<OperationIssue> issues)
    {
        if (invocation.WorkingDirectory is not null &&
            !Directory.Exists(invocation.WorkingDirectory))
            issues.Add(new(
                "external-tool-working-directory-missing",
                OperationIssueSeverity.Blocker,
                "The configured working directory does not exist.",
                invocation.WorkingDirectory));
    }

    private static IReadOnlyDictionary<string, OperationPathSnapshot>
        CaptureSnapshots(IEnumerable<string> paths) =>
        paths.Distinct(PathComparer).ToDictionary(
            path => path,
            path =>
            {
                var file = new FileInfo(path);
                return new OperationPathSnapshot(
                    file.Exists,
                    false,
                    file.Exists ? file.Length : 0,
                    file.Exists
                        ? file.LastWriteTimeUtc
                        : DateTime.MinValue)
                {
                    Path = path,
                };
            },
            PathComparer);

    private static void RevalidateSources(ExternalToolPlan plan)
    {
        if (plan.SourceSnapshots is null)
            throw new InvalidOperationException(
                "The external-tool plan has no source snapshots. Preview again.");
        foreach ((string path, OperationPathSnapshot expected) in
                 plan.SourceSnapshots)
        {
            var file = new FileInfo(path);
            bool stale = !file.Exists ||
                !expected.Exists ||
                expected.IsDirectory ||
                file.Length != expected.Length ||
                file.LastWriteTimeUtc != expected.LastWriteTimeUtc;
            if (stale)
                throw new InvalidOperationException(
                    $"Selected file changed after preview: {path}. " +
                    "Preview the external tool again.");
        }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public sealed class ExternalToolProcessRunner :
    IExternalToolProcessRunner
{
    private const int OutputLimit = 16 * 1024;

    public async Task<ExternalToolProcessResult> RunAsync(
        ExternalToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var start = new ProcessStartInfo(invocation.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(
                invocation.WorkingDirectory))
            start.WorkingDirectory = invocation.WorkingDirectory;
        foreach (string argument in invocation.Arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    "Unable to start the external tool.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Unable to start '{invocation.Executable}': " +
                error.Message,
                error);
        }

        Task<string> stdout = ReadBoundedAsync(
            process.StandardOutput, ct);
        Task<string> stderr = ReadBoundedAsync(
            process.StandardError, ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                await Task.WhenAll(stdout, stderr)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            throw;
        }
        return new(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        var result = new StringBuilder(OutputLimit);
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), ct)
                   .ConfigureAwait(false) is var read && read > 0)
        {
            int remaining = OutputLimit - result.Length;
            if (remaining > 0)
                result.Append(
                    buffer,
                    0,
                    Math.Min(remaining, read));
        }
        return result.ToString();
    }
}
