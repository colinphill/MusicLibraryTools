using System.Globalization;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IOperationJournalService
{
    Task<OperationJournalDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> searchRoots,
        CancellationToken ct = default);

    Task<OperationBrowseResult> BrowseAsync(
        OperationJournalSummary run,
        CancellationToken ct = default);
}

/// <summary>
/// Discovers existing sibling quarantine/recovery containers with bounded directory enumeration.
/// Discovery reads only immediate directories and small journals; file trees are left for browsing.
/// </summary>
public sealed class OperationJournalService : IOperationJournalService
{
    private static readonly Regex ContainerName = new(
        @"^(?<base>.+)\.(?<tool>IngestMusic|SortDownloads|OrganizeFiles|CrossSyncMusic|AndroidSync|UpdateCarCard|UpdateSmartStorage)(?<suffix>-quarantine|-recovery)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<OperationJournalDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> searchRoots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        return Task.Run(() => Discover(searchRoots, ct), ct);
    }

    public Task<OperationBrowseResult> BrowseAsync(
        OperationJournalSummary run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Task.Run(() => Browse(run, ct), ct);
    }

    private static OperationBrowseResult Browse(OperationJournalSummary run, CancellationToken ct)
    {
        string container = ContainerForRun(run.RunPath);
        var match = ContainerName.Match(Path.GetFileName(container));
        string originalRoot = match.Success
            ? Path.Combine(Path.GetDirectoryName(container) ?? "", match.Groups["base"].Value)
            : Path.GetDirectoryName(container) ?? container;
        var warnings = new List<string>();
        var entries = new Dictionary<string, OperationFileEntry>(PathComparer);

        if (run.JournalPath is not null && File.Exists(run.JournalPath))
        {
            try
            {
                string[] lines = File.ReadAllLines(run.JournalPath);
                if (run.ToolName == "UpdateCarCard")
                    ReadDeviceEntries(lines, originalRoot, entries, warnings, ct);
                else
                    ReadMutationEntries(lines, originalRoot, entries, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read journal '{run.JournalPath}': {ex.Message}");
            }
        }

        // Quarantine tools preserve relative paths physically. Walk only after this run is opened;
        // journal-only organize/device operations avoid an unrelated recursive scan.
        if (run.JournalPath is null || run.ToolName is "IngestMusic" or "SortDownloads" or
            "CrossSyncMusic" or "AndroidSync" or "UpdateSmartStorage")
            ReadPhysicalEntries(run.RunPath, originalRoot, entries, warnings, ct);

        return new OperationBrowseResult(
            originalRoot,
            entries.Values.OrderBy(entry => entry.RelativePath, PathComparer).ToList(),
            warnings);
    }

    private static void ReadMutationEntries(
        string[] lines,
        string originalRoot,
        Dictionary<string, OperationFileEntry> entries,
        CancellationToken ct)
    {
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            string[] fields = line.Split('\t');
            if (fields.Length == 0)
                continue;
            switch (fields[0])
            {
                case "QUARANTINE" when fields.Length > 3:
                case "STAGE_DELETE" when fields.Length > 3:
                    Put(entries, originalRoot, fields[2], fields[3], OperationEntryKind.Quarantined);
                    break;
                case "DELETE" when fields.Length > 2:
                    Put(entries, originalRoot, fields[2], null, OperationEntryKind.Deleted);
                    break;
                case "MOVE" when fields.Length > 3:
                    Put(entries, originalRoot, fields[2], fields[3], OperationEntryKind.Moved);
                    break;
                case "INSTALL" when fields.Length > 2:
                    Put(entries, originalRoot, fields[2], fields[2], OperationEntryKind.Created);
                    break;
                case "PLAN_QUARANTINE" when fields.Length > 3:
                    PutPlanIfAbsent(entries, originalRoot, fields[2], fields[3], OperationEntryKind.Quarantined);
                    break;
                case "PLAN_DELETE" when fields.Length > 2:
                    PutPlanIfAbsent(entries, originalRoot, fields[2], null, OperationEntryKind.Deleted);
                    break;
                case "PLAN_MOVE" when fields.Length > 3:
                    PutPlanIfAbsent(entries, originalRoot, fields[2], fields[3], OperationEntryKind.Moved);
                    break;
                case "PLAN_INSTALL" when fields.Length > 2:
                    PutPlanIfAbsent(entries, originalRoot, fields[2], fields[2], OperationEntryKind.Created);
                    break;
            }
        }
    }

    private static void ReadDeviceEntries(
        string[] lines,
        string originalRoot,
        Dictionary<string, OperationFileEntry> entries,
        List<string> warnings,
        CancellationToken ct)
    {
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            string[] fields = line.Split('\t');
            if (fields.Length < 2 || fields[0] is not ("MOVE" or "CREATE"))
                continue;
            try
            {
                string first = Decode(fields[1]);
                string? second = fields.Length > 2 && fields[2].Length > 0 ? Decode(fields[2]) : null;
                if (fields[0] == "CREATE" && entries.ContainsKey(first))
                    continue; // Preserve the preceding backup MOVE for a replaced destination.
                Put(entries, originalRoot, first, fields[0] == "MOVE" ? second : first,
                    fields[0] == "MOVE" ? OperationEntryKind.Moved : OperationEntryKind.Created);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not decode a {fields[0]} journal entry: {ex.Message}");
            }
        }
    }

    private static void ReadPhysicalEntries(
        string runPath,
        string originalRoot,
        Dictionary<string, OperationFileEntry> entries,
        List<string> warnings,
        CancellationToken ct)
    {
        try
        {
            foreach (string current in Directory.EnumerateFileSystemEntries(
                         runPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (PathComparer.Equals(current, Path.Combine(runPath, "journal.tsv")))
                    continue;
                string relative = Path.GetRelativePath(runPath, current);
                string original = Path.Combine(originalRoot, relative);
                if (entries.TryGetValue(original, out var recorded) &&
                    recorded.Kind != OperationEntryKind.Planned && recorded.CurrentPath is not null)
                    continue;
                bool directory = Directory.Exists(current);
                entries[original] = new OperationFileEntry(
                    original, current, relative, OperationEntryKind.Quarantined, true, directory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Could not browse '{runPath}': {ex.Message}");
        }
    }

    private static void Put(
        Dictionary<string, OperationFileEntry> entries,
        string originalRoot,
        string original,
        string? current,
        OperationEntryKind kind)
    {
        bool exists = current is not null && (File.Exists(current) || Directory.Exists(current));
        bool directory = current is not null && Directory.Exists(current);
        entries[original] = new OperationFileEntry(
            original, current, Relative(originalRoot, original), kind, exists, directory);
    }

    private static void PutPlanIfAbsent(
        Dictionary<string, OperationFileEntry> entries,
        string originalRoot,
        string original,
        string? current,
        OperationEntryKind completedKind)
    {
        if (entries.ContainsKey(original))
            return;
        bool moved = current is not null && (File.Exists(current) || Directory.Exists(current)) &&
            !File.Exists(original) && !Directory.Exists(original);
        Put(entries, originalRoot, original, current,
            moved ? completedKind : OperationEntryKind.Planned);
    }

    private static string Relative(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }

    private static string ContainerForRun(string runPath)
    {
        string full = Path.GetFullPath(runPath);
        if (ContainerName.IsMatch(Path.GetFileName(full)))
            return full;
        string? parent = Path.GetDirectoryName(full);
        return parent is not null && ContainerName.IsMatch(Path.GetFileName(parent)) ? parent : full;
    }

    private static string Decode(string value) =>
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static OperationJournalDiscoveryResult Discover(
        IReadOnlyList<string> searchRoots,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var containers = new HashSet<string>(PathComparer);
        foreach (string candidate in searchRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            ct.ThrowIfCancellationRequested();
            string root;
            try { root = Path.GetFullPath(candidate); }
            catch (Exception ex)
            {
                warnings.Add($"Invalid search root '{candidate}': {ex.Message}");
                continue;
            }

            if (Directory.Exists(root))
                AddIfContainer(root, containers);

            string anchor = File.Exists(root) ? Path.GetDirectoryName(root)! : root;
            string? parent = Path.GetDirectoryName(anchor);
            string prefix = Path.GetFileName(anchor) + ".";
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                foreach (string directory in EnumerateDirectories(parent, warnings))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        AddIfContainer(directory, containers);
                }
            }
        }

        var runs = new List<OperationJournalSummary>();
        foreach (string container in containers)
        {
            ct.ThrowIfCancellationRequested();
            var match = ContainerName.Match(Path.GetFileName(container));
            if (!match.Success)
                continue;
            string tool = CanonicalToolName(match.Groups["tool"].Value);
            var runDirectories = EnumerateDirectories(container, warnings).ToList();
            if (File.Exists(Path.Combine(container, "journal.tsv")))
                runDirectories.Insert(0, container);
            foreach (string run in runDirectories.Distinct(PathComparer))
            {
                ct.ThrowIfCancellationRequested();
                if (!TryGetRunTime(run, out var created) && !File.Exists(Path.Combine(run, "journal.tsv")))
                    continue;
                runs.Add(ReadSummary(tool, run, created));
            }
        }

        return new OperationJournalDiscoveryResult(
            runs.OrderByDescending(run => run.CreatedAtUtc)
                .ThenBy(run => run.RunPath, PathComparer)
                .ToList(),
            warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static void AddIfContainer(string path, HashSet<string> containers)
    {
        if (ContainerName.IsMatch(Path.GetFileName(path)))
            containers.Add(Path.GetFullPath(path));
    }

    private static IEnumerable<string> EnumerateDirectories(string path, List<string> warnings)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch (Exception ex)
        {
            warnings.Add($"Could not scan '{path}': {ex.Message}");
            return [];
        }
    }

    private static OperationJournalSummary ReadSummary(
        string tool,
        string runPath,
        DateTimeOffset created)
    {
        string journal = Path.Combine(runPath, "journal.tsv");
        if (!File.Exists(journal))
            return new(tool, Kind(tool), OperationJournalState.Unknown, runPath, null, created, null);

        try
        {
            string[] lines = File.ReadAllLines(journal);
            var (state, count) = tool == "UpdateCarCard"
                ? ParseDeviceJournal(lines)
                : ParseMutationJournal(lines);
            return new(tool, Kind(tool), state, runPath, journal, created, count);
        }
        catch
        {
            return new(tool, Kind(tool), OperationJournalState.Unknown, runPath, journal, created, null);
        }
    }

    private static (OperationJournalState State, int Count) ParseMutationJournal(string[] lines)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var affected = new HashSet<string>(PathComparer);
        bool committed = false, rolledBack = false;
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length == 0)
                continue;
            string operation = fields[0];
            string key = fields.Length > 1 ? fields[1] : "";
            switch (operation)
            {
                case "BEGIN": active.Add(key); break;
                case "COMMIT": active.Remove(key); committed = true; break;
                case "ROLLBACK": active.Remove(key); rolledBack = true; break;
                case "QUARANTINE":
                case "STAGE_DELETE":
                case "MOVE":
                    if (fields.Length > 2) affected.Add(fields[2]);
                    break;
            }
        }
        var state = active.Count > 0 ? OperationJournalState.Interrupted
            : committed ? OperationJournalState.Completed
            : rolledBack ? OperationJournalState.RolledBack
            : lines.Length > 0 ? OperationJournalState.Interrupted
            : OperationJournalState.Unknown;
        return (state, affected.Count);
    }

    private static (OperationJournalState State, int Count) ParseDeviceJournal(string[] lines)
    {
        string? terminal = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        var state = terminal switch
        {
            "COMMIT" => OperationJournalState.Completed,
            "ROLLED_BACK" => OperationJournalState.RolledBack,
            _ => OperationJournalState.Interrupted,
        };
        int count = lines.Count(line => line.StartsWith("MOVE\t", StringComparison.Ordinal) ||
            line.StartsWith("CREATE\t", StringComparison.Ordinal));
        return (state, count);
    }

    private static bool TryGetRunTime(string path, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParseExact(Path.GetFileName(path), "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
            return true;
        try
        {
            value = new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static string CanonicalToolName(string value) => value.ToUpperInvariant() switch
    {
        "INGESTMUSIC" => "IngestMusic",
        "SORTDOWNLOADS" => "SortDownloads",
        "ORGANIZEFILES" => "OrganizeFiles",
        "CROSSSYNCMUSIC" => "CrossSyncMusic",
        "ANDROIDSYNC" => "AndroidSync",
        "UPDATECARCARD" => "UpdateCarCard",
        "UPDATESMARTSTORAGE" => "UpdateSmartStorage",
        _ => value,
    };

    private static OperationJournalKind Kind(string tool) => tool switch
    {
        "IngestMusic" => OperationJournalKind.Ingest,
        "SortDownloads" or "OrganizeFiles" => OperationJournalKind.Organize,
        "CrossSyncMusic" or "AndroidSync" => OperationJournalKind.Sync,
        "UpdateCarCard" or "UpdateSmartStorage" => OperationJournalKind.Device,
        _ => OperationJournalKind.Other,
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
