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
    private readonly IReadOnlyList<IMediaCatalogIntegration> _catalogs;
    private readonly IAppSettings? _settings;
    private readonly IReverseDeltaService _reverseDelta;

    public FileMutationPlanExecutor(
        IFileMutationCoordinator? coordinator = null,
        IItunesMediaMutationService? itunes = null,
        IEnumerable<IMediaCatalogIntegration>? catalogIntegrations = null,
        IAppSettings? settings = null,
        IReverseDeltaService? reverseDelta = null)
    {
        _coordinator = coordinator ?? FileMutationCoordinator.Shared;
        IMediaCatalogIntegration[] configured = catalogIntegrations?.ToArray() ?? [];
        _catalogs = configured.Length > 0
            ? configured
            : itunes is null ? [] : [new ItunesMediaCatalogIntegration(itunes)];
        _settings = settings;
        _reverseDelta = reverseDelta ?? new ReverseDeltaService();
    }

    public Task<FileMutationSummary> ApplyAsync(
        FileMutationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ApplyCoreAsync(plan, progress, ct), ct);
    }

    private async Task<FileMutationSummary> ApplyCoreAsync(
        FileMutationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ValidatePolicy(plan);
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
        await using MediaCatalogMutationSessionGroup? catalogSession =
            await MediaCatalogMutationSessionGroup.BeginAsync(
                _catalogs, paths, backupFiles: false, ct).ConfigureAwait(false);

        // Revalidate after acquiring the in-process mutation lease and still before the first write.
        ValidatePolicy(plan);
        ValidateAll(plan.Actions, ct);
        if (plan.Actions.Count == 0)
        {
            progress?.Report(new(OperationPhase.Completed, 0, 0,
                Message: "No filesystem mutations are required"));
            return new(0, 0, 0, 0, null, plan.Issues);
        }

        if (!plan.RetainRecovery)
            return await ApplyWithoutRecoveryAsync(plan, catalogSession, progress, ct)
                .ConfigureAwait(false);

        Directory.CreateDirectory(plan.RecoveryRoot);
        string journalPath = Path.Combine(plan.RecoveryRoot, "journal.tsv");
        await using var journalStream = new FileStream(journalPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await using var journal = new StreamWriter(journalStream, new UTF8Encoding(false));
        string operationId = Guid.NewGuid().ToString("N");
        await WriteJournalAsync(journal, journalStream, $"BEGIN\t{operationId}", ct);
        foreach (FileMutationAction action in plan.Actions)
            await WriteJournalAsync(
                journal, journalStream, PlanLine(action, plan.RecoveryPayloadPolicy), ct);

        var completed = new List<CompletedMutation>();
        var createdDirectories = new List<string>();
        int copied = 0, moved = 0, replaced = 0, quarantined = 0, deleted = 0;
        RecoveryStorageSummary recoveryStorage = RecoveryStorageSummary.Empty;
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
                if (mutation.RecoveryStorage is not null)
                    recoveryStorage = recoveryStorage.Add(mutation.RecoveryStorage);
                switch (action.Kind)
                {
                    case FileMutationKind.Copy or FileMutationKind.Write: copied++; break;
                    case FileMutationKind.Move: moved++; break;
                    case FileMutationKind.Replace or FileMutationKind.ReplaceGenerated: replaced++; break;
                    case FileMutationKind.Quarantine: quarantined++; break;
                }
            }

            if (catalogSession is not null)
                await catalogSession.CommitAsync(
                    plan.Actions.Select(ToCatalogMutation).ToArray(),
                    CancellationToken.None).ConfigureAwait(false);

            await WriteJournalAsync(journal, journalStream, $"COMMIT\t{operationId}", ct);
            if (catalogSession is not null)
                await catalogSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);

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
            return new(copied, replaced, quarantined, deleted, journalPath, plan.Issues)
            {
                Moved = moved,
                RecoveryStorage = recoveryStorage.FullOriginalCount +
                    recoveryStorage.ReverseDeltaCount > 0
                    ? recoveryStorage
                    : null,
            };
        }
        catch
        {
            progress?.Report(new(OperationPhase.RollingBack, Message: "Rolling back failed operation"));
            List<Exception> rollbackErrors = [];
            foreach (CompletedMutation mutation in completed.AsEnumerable().Reverse())
            {
                try
                {
                    await RollBackAsync(mutation, CancellationToken.None)
                        .ConfigureAwait(false);
                }
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
            string rollbackTerminal = rollbackErrors.Count == 0
                ? "ROLLBACK"
                : "ROLLBACK_FAILED";
            try
            {
                await WriteJournalAsync(
                    journal,
                    journalStream,
                    $"{rollbackTerminal}\t{operationId}",
                    CancellationToken.None);
            }
            catch (Exception error) { rollbackErrors.Add(error); }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("The operation failed and rollback was incomplete.", rollbackErrors);
            throw;
        }
    }

    private void ValidatePolicy(FileMutationPlan plan)
    {
        if (plan.PolicyFingerprint is null || _settings is null)
            return;
        MusicLibraryTools.LibraryConfiguration? configuration =
            _settings.GetSnapshot().Configuration;
        if (configuration is null ||
            plan.LibraryId is Guid libraryId && configuration.LibraryId != libraryId ||
            !StringComparer.Ordinal.Equals(
                plan.PolicyFingerprint, configuration.PolicySnapshot.Fingerprint))
            throw new InvalidOperationException(
                "The library policy changed after preview. Preview the operation again before applying it.");
    }

    private static async Task<FileMutationSummary> ApplyWithoutRecoveryAsync(
        FileMutationPlan plan,
        MediaCatalogMutationSessionGroup? catalogSession,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        int copied = 0, moved = 0, replaced = 0, deleted = 0;
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
                case FileMutationKind.Move:
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    File.Move(action.SourcePath, action.DestinationPath, false);
                    moved++;
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

        if (catalogSession is not null)
        {
            await catalogSession.CommitAsync(plan.Actions.Select(ToCatalogMutation).ToArray(),
                CancellationToken.None).ConfigureAwait(false);
            await catalogSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        progress?.Report(new(OperationPhase.Completed, plan.Actions.Count, plan.Actions.Count,
            Message: "Mutation plan completed"));
        return new(copied, replaced, 0, deleted, null, plan.Issues)
        {
            Moved = moved,
        };
    }

    private async Task<CompletedMutation> ApplyOneAsync(
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

            case FileMutationKind.Move:
                File.Move(action.SourcePath, action.DestinationPath, false);
                try
                {
                    await WriteJournalAsync(journal, journalStream,
                        $"MOVE\tFILE\t{action.SourcePath}\t{action.DestinationPath}", ct);
                }
                catch
                {
                    File.Move(action.DestinationPath, action.SourcePath, false);
                    throw;
                }
                return new(action, action.DestinationPath);

            case FileMutationKind.Replace:
                if (plan.RecoveryPayloadPolicy == RecoveryPayloadPolicy.AdaptiveReverseDelta)
                    return await ApplyAdaptiveReplaceAsync(
                        action, plan, journal, journalStream, ct).ConfigureAwait(false);
                string backup = BackupPath(plan.RecoveryRoot, action.DestinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                long originalLength = new FileInfo(action.DestinationPath).Length;
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
                return new(
                    action,
                    backup,
                    RecoveryPayloadKind.FullOriginal,
                    new(originalLength, originalLength, 1, 0));

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
                long generatedOriginalLength =
                    new FileInfo(action.DestinationPath).Length;
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
                return new(
                    action,
                    generatedBackup,
                    RecoveryPayloadKind.FullOriginal,
                    new(generatedOriginalLength, generatedOriginalLength, 1, 0));

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

    private async Task<CompletedMutation> ApplyAdaptiveReplaceAsync(
        FileMutationAction action,
        FileMutationPlan plan,
        StreamWriter journal,
        FileStream journalStream,
        CancellationToken ct)
    {
        long originalLength = new FileInfo(action.DestinationPath).Length;
        if (!SameVolume(action.SourcePath, action.DestinationPath) ||
            !HasDeltaCreationCapacity(plan.RecoveryRoot, originalLength))
            return await ApplyFullReplacementAsync(
                action, plan, journal, journalStream, originalLength, ct)
                .ConfigureAwait(false);

        string deltaPath = ReverseDeltaPath(plan.RecoveryRoot, action.DestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(deltaPath)!);
        ReverseDeltaDescriptor descriptor = await _reverseDelta.CreateFileAsync(
            action.DestinationPath, action.SourcePath, deltaPath, ct).ConfigureAwait(false);
        bool preserveDelta = false;
        try
        {
            // Validate the durable payload, the post-edit base, every command, and the
            // reconstructed original hash before changing the live path.
            await using (var delta = new FileStream(
                             deltaPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var postEdit = new FileStream(
                             action.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                await _reverseDelta.RestoreAsync(
                    delta, postEdit, Stream.Null, ct).ConfigureAwait(false);
            }

            string descriptorFields = CompactDescriptorFields(
                action.DestinationPath, deltaPath, descriptor);
            string fallbackBackup = BackupPath(
                plan.RecoveryRoot, action.DestinationPath);
            long compactJournalOverhead = Encoding.UTF8.GetByteCount(
                $"DELTA_READY\t{descriptorFields}{Environment.NewLine}" +
                $"COMPACT_REPLACE\t{descriptorFields}{Environment.NewLine}");
            long fullJournalOverhead = Encoding.UTF8.GetByteCount(
                $"QUARANTINE\tREPLACE\t{action.DestinationPath}\t{fallbackBackup}" +
                Environment.NewLine +
                $"INSTALL\tREPLACE\t{action.DestinationPath}{Environment.NewLine}");
            if (!ReverseDeltaService.IsAdaptivePayloadBeneficial(
                    descriptor,
                    originalLength,
                    compactJournalOverhead,
                    fullJournalOverhead))
            {
                File.Delete(deltaPath);
                return await ApplyFullReplacementAsync(
                    action, plan, journal, journalStream, originalLength, ct)
                    .ConfigureAwait(false);
            }

            await WriteJournalAsync(
                journal, journalStream, $"DELTA_READY\t{descriptorFields}", ct)
                .ConfigureAwait(false);

            bool installed = false;
            try
            {
                // Metadata staging occurs beside the live file. Rename-overwrite is therefore a
                // same-volume atomic replacement and avoids retaining a second whole-file copy.
                File.Move(action.SourcePath, action.DestinationPath, overwrite: true);
                installed = true;
                await WriteJournalAsync(
                    journal, journalStream, $"COMPACT_REPLACE\t{descriptorFields}", ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                if (installed && File.Exists(action.DestinationPath))
                {
                    try
                    {
                        await RestoreCompactFileAtomicallyAsync(
                            deltaPath,
                            action.DestinationPath,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The durable delta is now the only retained route back to the original.
                        // Never discard it when the immediate rollback could not be completed.
                        preserveDelta = true;
                        throw;
                    }
                }
                else if (installed)
                {
                    preserveDelta = true;
                }
                throw;
            }

            return new(
                action,
                deltaPath,
                RecoveryPayloadKind.ReverseDelta,
                new(originalLength, descriptor.RetainedBytes, 0, 1),
                descriptor);
        }
        catch
        {
            if (!preserveDelta)
                TryDeleteFile(deltaPath);
            throw;
        }
    }

    private static async Task<CompletedMutation> ApplyFullReplacementAsync(
        FileMutationAction action,
        FileMutationPlan plan,
        StreamWriter journal,
        FileStream journalStream,
        long originalLength,
        CancellationToken ct)
    {
        string backup = BackupPath(plan.RecoveryRoot, action.DestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(action.DestinationPath, backup, false);
        try
        {
            await WriteJournalAsync(
                journal, journalStream,
                $"QUARANTINE\tREPLACE\t{action.DestinationPath}\t{backup}", ct)
                .ConfigureAwait(false);
            if (SameVolume(action.SourcePath, action.DestinationPath))
                File.Move(action.SourcePath, action.DestinationPath, false);
            else
                CopyAtomically(action.SourcePath, action.DestinationPath, replace: false);
            await WriteJournalAsync(
                journal, journalStream,
                $"INSTALL\tREPLACE\t{action.DestinationPath}", ct)
                .ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(action.DestinationPath))
                File.Delete(action.DestinationPath);
            File.Move(backup, action.DestinationPath, false);
            throw;
        }
        return new(
            action,
            backup,
            RecoveryPayloadKind.FullOriginal,
            new(originalLength, originalLength, 1, 0));
    }

    private async Task RestoreCompactFileAtomicallyAsync(
        string deltaPath,
        string postEditPath,
        CancellationToken ct)
    {
        string temporary = Path.Combine(
            Path.GetDirectoryName(postEditPath)!,
            $".{Path.GetFileName(postEditPath)}.{Guid.NewGuid():N}.compact-restore");
        try
        {
            await _reverseDelta.RestoreFileAsync(
                deltaPath, postEditPath, temporary, ct).ConfigureAwait(false);
            File.Move(temporary, postEditPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static string CompactDescriptorFields(
        string destinationPath,
        string deltaPath,
        ReverseDeltaDescriptor descriptor) =>
        string.Join('\t',
            descriptor.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            destinationPath,
            deltaPath,
            descriptor.OriginalLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            descriptor.PostEditLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            descriptor.OriginalSha256,
            descriptor.PostEditSha256,
            descriptor.RetainedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            descriptor.OriginalLastWriteTimeUtc.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)descriptor.OriginalAttributes).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            descriptor.PayloadSha256);

    private static bool HasDeltaCreationCapacity(string recoveryRoot, long originalLength)
    {
        try
        {
            string fullRecoveryRoot = Path.GetFullPath(recoveryRoot);
            DriveInfo? drive = DriveInfo.GetDrives()
                .Where(candidate =>
                {
                    string rooted = Path.GetFullPath(
                        candidate.RootDirectory.FullName);
                    string candidateRoot =
                        Path.TrimEndingDirectorySeparator(rooted);
                    string prefix = Path.EndsInDirectorySeparator(rooted)
                        ? rooted
                        : rooted + Path.DirectorySeparatorChar;
                    return PathComparer.Equals(
                            Path.TrimEndingDirectorySeparator(fullRecoveryRoot),
                            candidateRoot) ||
                        fullRecoveryRoot.StartsWith(prefix, PathComparison);
                })
                .OrderByDescending(candidate =>
                    candidate.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null)
                return true;
            long required = checked(
                ReverseDeltaService.MaximumEncodedLength(originalLength) +
                64 * 1024);
            return drive.AvailableFreeSpace >= required;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch
        {
            // Remote and virtual filesystems often cannot report capacity. Creation remains
            // bounded by the original length and fails before the live rename if storage is full.
            return true;
        }
    }

    private static bool SameVolume(string left, string right)
    {
        string fullLeft = Path.GetFullPath(left);
        string fullRight = Path.GetFullPath(right);
        if (PathComparer.Equals(
                Path.GetDirectoryName(fullLeft),
                Path.GetDirectoryName(fullRight)))
            return true;
        // Drive/UNC roots identify volumes on Windows. Unix path roots are always "/" even
        // across mounted filesystems, so non-sibling paths conservatively use the copy fallback.
        return OperatingSystem.IsWindows() &&
            StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetPathRoot(fullLeft),
                Path.GetPathRoot(fullRight));
    }

    private static string ReverseDeltaPath(string recoveryRoot, string destination)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(destination)));
        return Path.Combine(
            recoveryRoot,
            "deltas",
            Convert.ToHexString(hash),
            Path.GetFileName(destination) + ".mldelta");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
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

    private async Task RollBackAsync(
        CompletedMutation mutation,
        CancellationToken ct)
    {
        FileMutationAction action = mutation.Action;
        switch (action.Kind)
        {
            case FileMutationKind.Copy or FileMutationKind.Write:
                if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                break;
            case FileMutationKind.Replace or FileMutationKind.ReplaceGenerated:
                if (mutation.PayloadKind == RecoveryPayloadKind.ReverseDelta)
                {
                    if (mutation.BackupPath is not null &&
                        File.Exists(mutation.BackupPath) &&
                        File.Exists(action.DestinationPath))
                    {
                        await RestoreCompactFileAtomicallyAsync(
                            mutation.BackupPath, action.DestinationPath, ct)
                            .ConfigureAwait(false);
                        TryDeleteFile(mutation.BackupPath);
                    }
                    break;
                }
                if (File.Exists(action.DestinationPath)) File.Delete(action.DestinationPath);
                if (mutation.BackupPath is not null && File.Exists(mutation.BackupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
                    File.Move(mutation.BackupPath, action.DestinationPath, false);
                }
                break;
            case FileMutationKind.Move or FileMutationKind.Quarantine or FileMutationKind.Delete:
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

    private static string PlanLine(
        FileMutationAction action,
        RecoveryPayloadPolicy recoveryPayloadPolicy) => action.Kind switch
    {
        FileMutationKind.Replace
            when recoveryPayloadPolicy == RecoveryPayloadPolicy.AdaptiveReverseDelta =>
            $"PLAN_COMPACT_REPLACE\t1\t{action.DestinationPath}",
        FileMutationKind.Copy or FileMutationKind.Replace or FileMutationKind.Write or
            FileMutationKind.ReplaceGenerated =>
            $"PLAN_INSTALL\t{action.Kind.ToString().ToUpperInvariant()}\t{action.DestinationPath}",
        FileMutationKind.Move =>
            $"PLAN_MOVE\tFILE\t{action.SourcePath}\t{action.DestinationPath}",
        FileMutationKind.Quarantine =>
            $"PLAN_QUARANTINE\tSTALE\t{action.SourcePath}\t{action.DestinationPath}",
        FileMutationKind.Delete =>
            $"PLAN_DELETE\tSTALE\t{action.SourcePath}",
        _ => throw new ArgumentOutOfRangeException(nameof(action.Kind)),
    };

    private static MediaCatalogMutation ToCatalogMutation(FileMutationAction action) =>
        action.Kind switch
        {
            FileMutationKind.Copy or FileMutationKind.Write =>
                MediaCatalogMutation.Add(action.DestinationPath),
            FileMutationKind.Move =>
                MediaCatalogMutation.Relocate(action.SourcePath, action.DestinationPath),
            FileMutationKind.Replace or FileMutationKind.ReplaceGenerated =>
                MediaCatalogMutation.Refresh(action.DestinationPath),
            FileMutationKind.Quarantine or FileMutationKind.Delete =>
                MediaCatalogMutation.Remove(action.SourcePath),
            _ => throw new ArgumentOutOfRangeException(nameof(action.Kind)),
        };

    private static string BackupPath(string recoveryRoot, string destination)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(destination)));
        return Path.Combine(recoveryRoot, "replaced", Convert.ToHexString(hash),
            Path.GetFileName(destination));
    }

    private sealed record CompletedMutation(
        FileMutationAction Action,
        string? BackupPath,
        RecoveryPayloadKind? PayloadKind = null,
        RecoveryStorageSummary? RecoveryStorage = null,
        ReverseDeltaDescriptor? ReverseDelta = null);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
