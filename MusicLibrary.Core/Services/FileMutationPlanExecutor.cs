using System.Text;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IFileMutationPlanExecutor
{
    Task<FileMutationSummary> ApplyAsync(
        FileMutationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Revalidates an entire reviewed plan before its first write, then executes atomic installs and
/// recoverable moves under a durable journal. A failure rolls completed actions back in reverse
/// order; an interrupted process leaves the journal and recovery tree for the Operations UI.
/// </summary>
public sealed class FileMutationPlanExecutor : IFileMutationPlanExecutor
{
    private readonly IFileMutationCoordinator _coordinator;
    private readonly IItunesMediaMutationService? _itunes;

    public FileMutationPlanExecutor(
        IFileMutationCoordinator? coordinator = null,
        IItunesMediaMutationService? itunes = null)
    {
        _coordinator = coordinator ?? FileMutationCoordinator.Shared;
        _itunes = itunes;
    }

    public async Task<FileMutationSummary> ApplyAsync(
        FileMutationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The mutation plan contains blocking issues.");

        progress?.Report(new(OperationPhase.Validating, Message: "Validating reviewed snapshots"));
        ValidateAll(plan.Actions, ct);

        string[] paths = plan.Actions.SelectMany(action =>
                new[] { action.SourcePath, action.DestinationPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
        using var lease = await _coordinator.AcquireAsync(paths, ct).ConfigureAwait(false);
        await using IItunesMediaMutationSession? itunesSession = _itunes is null
            ? null
            : await _itunes.BeginAsync(paths, backupFiles: false, ct).ConfigureAwait(false);

        // Revalidate after acquiring the in-process mutation lease and still before the first write.
        ValidateAll(plan.Actions, ct);
        if (plan.Actions.Count == 0)
        {
            progress?.Report(new(OperationPhase.Completed, 0, 0,
                Message: "No filesystem mutations are required"));
            return new(0, 0, 0, 0, null, plan.Issues);
        }

        if (!plan.RetainRecovery)
            return await ApplyWithoutRecoveryAsync(plan, itunesSession, progress, ct)
                .ConfigureAwait(false);

        Directory.CreateDirectory(plan.RecoveryRoot);
        string journalPath = Path.Combine(plan.RecoveryRoot, "journal.tsv");
        await using var journalStream = new FileStream(journalPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await using var journal = new StreamWriter(journalStream, new UTF8Encoding(false));
        string operationId = Guid.NewGuid().ToString("N");
        await WriteJournalAsync(journal, journalStream, $"BEGIN\t{operationId}", ct);
        foreach (FileMutationAction action in plan.Actions)
            await WriteJournalAsync(journal, journalStream, PlanLine(action), ct);

        var completed = new List<CompletedMutation>();
        var createdDirectories = new List<string>();
        int copied = 0, replaced = 0, quarantined = 0, deleted = 0;
        try
        {
            for (int index = 0; index < plan.Actions.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                FileMutationAction action = plan.Actions[index];
                progress?.Report(new(OperationPhase.Applying, index, plan.Actions.Count,
                    action.DestinationPath, $"{action.Kind}: {action.DestinationPath}"));
                if (action.Kind != FileMutationKind.Quarantine)
                {
                    foreach (string directory in MissingDirectories(
                                 Path.GetDirectoryName(action.DestinationPath)!))
                    {
                        Directory.CreateDirectory(directory);
                        createdDirectories.Add(directory);
                        await WriteJournalAsync(journal, journalStream,
                            $"INSTALL\tDIRECTORY\t{directory}", ct);
                    }
                }
                CompletedMutation mutation = await ApplyOneAsync(action, plan, journal,
                    journalStream, ct).ConfigureAwait(false);
                completed.Add(mutation);
                switch (action.Kind)
                {
                    case FileMutationKind.Copy or FileMutationKind.Write: copied++; break;
                    case FileMutationKind.Replace or FileMutationKind.ReplaceGenerated: replaced++; break;
                    case FileMutationKind.Quarantine: quarantined++; break;
                }
            }

            if (itunesSession is not null)
                await itunesSession.CommitAsync(
                    plan.Actions.Select(ToItunesMutation).ToArray(),
                    CancellationToken.None).ConfigureAwait(false);

            await WriteJournalAsync(journal, journalStream, $"COMMIT\t{operationId}", ct);
            if (itunesSession is not null)
                await itunesSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);

            // Delete actions are recoverable moves until the complete plan and any iTunes update
            // have committed. Purge only afterward; an individual purge failure safely leaves that
            // item in the recovery tree and reports it as quarantined.
            foreach (CompletedMutation mutation in completed.Where(item =>
                         item.Action.Kind == FileMutationKind.Delete))
            {
                string staged = mutation.BackupPath!;
                try
                {
                    File.SetAttributes(staged, FileAttributes.Normal);
                    File.Delete(staged);
                    deleted++;
                }
                catch (Exception error)
                {
                    quarantined++;
                    try
                    {
                        await WriteJournalAsync(journal, journalStream,
                            $"DELETE_FAILED\tSTALE\t{mutation.Action.SourcePath}\t{staged}\t{error.Message}",
                            CancellationToken.None);
                    }
                    catch { }
                    continue;
                }
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"DELETE\tSTALE\t{mutation.Action.SourcePath}", CancellationToken.None);
                }
                catch { }
            }
            progress?.Report(new(OperationPhase.Completed, plan.Actions.Count, plan.Actions.Count,
                Message: "Mutation plan completed"));
            return new(copied, replaced, quarantined, deleted, journalPath, plan.Issues);
        }
        catch
        {
            progress?.Report(new(OperationPhase.RollingBack, Message: "Rolling back failed operation"));
            List<Exception> rollbackErrors = [];
            foreach (CompletedMutation mutation in completed.AsEnumerable().Reverse())
            {
                try { RollBack(mutation); }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(directory) &&
                        !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            try
            {
                await WriteJournalAsync(journal, journalStream, $"ROLLBACK\t{operationId}",
                    CancellationToken.None);
            }
            catch (Exception error) { rollbackErrors.Add(error); }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("The operation failed and rollback was incomplete.", rollbackErrors);
            throw;
        }
    }

    private static async Task<FileMutationSummary> ApplyWithoutRecoveryAsync(
        FileMutationPlan plan,
        IItunesMediaMutationSession? itunesSession,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        int copied = 0, replaced = 0, deleted = 0;
        for (int index = 0; index < plan.Actions.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            FileMutationAction action = plan.Actions[index];
            string currentPath = action.Kind == FileMutationKind.Delete
                ? action.SourcePath
                : action.DestinationPath;
            progress?.Report(new(OperationPhase.Applying, index, plan.Actions.Count,
                currentPath, $"{action.Kind}: {currentPath}"));

            switch (action.Kind)
            {
                case FileMutationKind.Copy:
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    CopyAtomically(action.SourcePath, action.DestinationPath, replace: false);
                    copied++;
                    break;
                case FileMutationKind.Replace:
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    CopyAtomically(action.SourcePath, action.DestinationPath, replace: true);
                    replaced++;
                    break;
                case FileMutationKind.Write:
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    WriteAtomically(action.Content, action.DestinationPath);
                    copied++;
                    break;
                case FileMutationKind.ReplaceGenerated:
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    WriteAtomically(action.Content, action.DestinationPath, replace: true);
                    replaced++;
                    break;
                case FileMutationKind.Delete:
                    DeleteWithinRoot(action.SourcePath, action.DestinationPath);
                    deleted++;
                    break;
                case FileMutationKind.Quarantine:
                    throw new InvalidOperationException(
                        "A no-recovery mutation plan cannot contain quarantine actions.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(action.Kind));
            }
        }

        if (itunesSession is not null)
        {
            await itunesSession.CommitAsync(plan.Actions.Select(ToItunesMutation).ToArray(),
                CancellationToken.None).ConfigureAwait(false);
            await itunesSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        progress?.Report(new(OperationPhase.Completed, plan.Actions.Count, plan.Actions.Count,
            Message: "Mutation plan completed"));
        return new(copied, replaced, 0, deleted, null, plan.Issues);
    }

    private static async Task<CompletedMutation> ApplyOneAsync(
        FileMutationAction action,
        FileMutationPlan plan,
        StreamWriter journal,
        FileStream journalStream,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
        switch (action.Kind)
        {
            case FileMutationKind.Copy:
                CopyAtomically(action.SourcePath, action.DestinationPath, replace: false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"INSTALL\tCOPY\t{action.DestinationPath}", ct);
                }
                catch
                {
                    if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                    throw;
                }
                return new(action, null);

            case FileMutationKind.Replace:
                string backup = BackupPath(plan.RecoveryRoot, action.DestinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(action.DestinationPath, backup, false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"QUARANTINE\tREPLACE\t{action.DestinationPath}\t{backup}", ct);
                    CopyAtomically(action.SourcePath, action.DestinationPath, replace: false);
                    await WriteJournalAsync(journal, journalStream,
                        $"INSTALL\tREPLACE\t{action.DestinationPath}", ct);
                }
                catch
                {
                    if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                    File.Move(backup, action.DestinationPath, false);
                    throw;
                }
                return new(action, backup);

            case FileMutationKind.Write:
                WriteAtomically(action.Content, action.DestinationPath);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"INSTALL\tWRITE\t{action.DestinationPath}", ct);
                }
                catch
                {
                    if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                    throw;
                }
                return new(action, null);

            case FileMutationKind.ReplaceGenerated:
                string generatedBackup = BackupPath(plan.RecoveryRoot, action.DestinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(generatedBackup)!);
                File.Move(action.DestinationPath, generatedBackup, false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"QUARANTINE\tREPLACE\t{action.DestinationPath}\t{generatedBackup}", ct);
                    WriteAtomically(action.Content, action.DestinationPath);
                    await WriteJournalAsync(journal, journalStream,
                        $"INSTALL\tWRITE\t{action.DestinationPath}", ct);
                }
                catch
                {
                    if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                    File.Move(generatedBackup, action.DestinationPath, false);
                    throw;
                }
                return new(action, generatedBackup);

            case FileMutationKind.Quarantine:
                File.Move(action.SourcePath, action.DestinationPath, false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"QUARANTINE\tSTALE\t{action.SourcePath}\t{action.DestinationPath}", ct);
                }
                catch
                {
                    File.Move(action.DestinationPath, action.SourcePath, false);
                    throw;
                }
                return new(action, action.DestinationPath);

            case FileMutationKind.Delete:
                File.Move(action.SourcePath, action.DestinationPath, false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"STAGE_DELETE\tSTALE\t{action.SourcePath}\t{action.DestinationPath}", ct);
                }
                catch
                {
                    File.Move(action.DestinationPath, action.SourcePath, false);
                    throw;
                }
                return new(action, action.DestinationPath);

            default:
                throw new ArgumentOutOfRangeException(nameof(action.Kind));
        }
    }

    private static IReadOnlyList<string> MissingDirectories(string path)
    {
        var missing = new List<string>();
        string? current = Path.GetFullPath(path);
        while (current is not null && !Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }
        missing.Reverse();
        return missing;
    }

    private static void RollBack(CompletedMutation mutation)
    {
        FileMutationAction action = mutation.Action;
        switch (action.Kind)
        {
            case FileMutationKind.Copy or FileMutationKind.Write:
                if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                break;
            case FileMutationKind.Replace or FileMutationKind.ReplaceGenerated:
                if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                if (mutation.BackupPath is not null && File.Exists(mutation.BackupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    File.Move(mutation.BackupPath, action.DestinationPath, false);
                }
                break;
            case FileMutationKind.Quarantine or FileMutationKind.Delete:
                if (File.Exists(action.DestinationPath) && !File.Exists(action.SourcePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.SourcePath)!);
                    File.Move(action.DestinationPath, action.SourcePath, false);
                }
                break;
        }
    }

    private static void ValidateAll(IReadOnlyList<FileMutationAction> actions, CancellationToken ct)
    {
        foreach (FileMutationAction action in actions)
        {
            ct.ThrowIfCancellationRequested();
            if (action.ExpectedSource is not null)
                ValidateSnapshot(action.ExpectedSource);
            if (action.ExpectedDestination is not null)
                ValidateSnapshot(action.ExpectedDestination);
        }
    }

    private static void ValidateSnapshot(OperationPathSnapshot expected)
    {
        string path = expected.Path ?? throw new InvalidOperationException(
            "Mutation snapshots must include their normalized path.");
        bool exists = File.Exists(path);
        if (exists != expected.Exists)
            throw new InvalidOperationException($"Stale plan: existence changed for '{path}'.");
        if (!exists)
            return;
        var info = new FileInfo(path);
        if (info.Length != expected.Length || info.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new InvalidOperationException($"Stale plan: file changed since preview: '{path}'.");
    }

    private static void CopyAtomically(string source, string destination, bool replace)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, false);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                stream.Flush(true);
            File.Move(temporary, destination, replace);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void WriteAtomically(System.Collections.Immutable.ImmutableArray<byte> content,
        string destination, bool replace = false)
    {
        if (content.IsDefault)
            throw new InvalidOperationException("A generated-file mutation has no reviewed content.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(content.AsSpan());
                stream.Flush(true);
            }
            File.Move(temporary, destination, replace);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void DeleteWithinRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison))
            throw new InvalidOperationException(
                $"Delete target '{fullPath}' is outside reviewed root '{fullRoot}'.");

        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
        string? directory = Path.GetDirectoryName(fullPath);
        while (directory is not null && !PathComparer.Equals(directory, fullRoot) &&
               directory.StartsWith(rootPrefix, PathComparison))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static async Task WriteJournalAsync(StreamWriter writer, FileStream stream,
        string line, CancellationToken ct)
    {
        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(true);
    }

    private static string PlanLine(FileMutationAction action) => action.Kind switch
    {
        FileMutationKind.Copy or FileMutationKind.Replace or FileMutationKind.Write or
            FileMutationKind.ReplaceGenerated =>
            $"PLAN_INSTALL\t{action.Kind.ToString().ToUpperInvariant()}\t{action.DestinationPath}",
        FileMutationKind.Quarantine =>
            $"PLAN_QUARANTINE\tSTALE\t{action.SourcePath}\t{action.DestinationPath}",
        FileMutationKind.Delete =>
            $"PLAN_DELETE\tSTALE\t{action.SourcePath}",
        _ => throw new ArgumentOutOfRangeException(nameof(action.Kind)),
    };

    private static ItunesMediaMutation ToItunesMutation(FileMutationAction action) =>
        action.Kind switch
        {
            FileMutationKind.Copy or FileMutationKind.Write =>
                ItunesMediaMutation.Add(action.DestinationPath),
            FileMutationKind.Replace or FileMutationKind.ReplaceGenerated =>
                ItunesMediaMutation.Refresh(action.DestinationPath),
            FileMutationKind.Quarantine or FileMutationKind.Delete =>
                ItunesMediaMutation.Remove(action.SourcePath),
            _ => throw new ArgumentOutOfRangeException(nameof(action.Kind)),
        };

    private static string BackupPath(string recoveryRoot, string destination)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(destination)));
        return Path.Combine(recoveryRoot, "replaced", Convert.ToHexString(hash),
            Path.GetFileName(destination));
    }

    private sealed record CompletedMutation(FileMutationAction Action, string? BackupPath);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
