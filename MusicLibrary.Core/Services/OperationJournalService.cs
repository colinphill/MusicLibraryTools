using System.Globalization;
using System.Security.Cryptography;
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

    Task<OperationRestorePlan> PreviewRestoreAsync(
        OperationJournalSummary run,
        IReadOnlyList<OperationFileEntry> entries,
        CancellationToken ct = default);

    Task<OperationRestoreResult> ApplyRestoreAsync(
        OperationRestorePlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    Task<OperationRestoreBatchPlan> PreviewRestoreBatchAsync(
        IReadOnlyList<OperationRestorePlan> plans,
        CancellationToken ct = default) =>
        Task.FromResult(new OperationRestoreBatchPlan(plans));

    async Task<OperationRestoreBatchResult> ApplyRestoreBatchAsync(
        OperationRestoreBatchPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int restored = 0, collisions = 0;
        var journals = new List<string>();
        foreach (OperationRestorePlan item in plan.Plans)
        {
            OperationRestoreResult result = await ApplyRestoreAsync(item, progress, ct)
                .ConfigureAwait(false);
            restored += result.RestoredCount;
            collisions += result.CollisionBackupCount;
            journals.Add(item.RestoreJournalPath);
        }
        return new(restored, collisions, journals);
    }

    Task<OperationRestoreTransitionState> ReconcileRestoreBatchAsync(
        IReadOnlyList<string> restoreJournalPaths,
        CancellationToken ct = default) =>
        Task.FromResult(OperationRestoreTransitionState.Unapplied);

    Task<OperationPurgePlan> PreviewPurgeAsync(
        IReadOnlyList<OperationJournalSummary> runs,
        int retentionDays,
        DateTimeOffset? nowUtc = null,
        CancellationToken ct = default);

    Task<OperationPurgeResult> ApplyPurgeAsync(
        OperationPurgePlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Discovers existing sibling quarantine/recovery containers with bounded directory enumeration.
/// Discovery reads only immediate directories and small journals; file trees are left for browsing.
/// </summary>
public sealed class OperationJournalService : IOperationJournalService
{
    private static readonly Regex ContainerName = new(
        @"^(?<base>.+)\.(?<tool>IngestMusic|SortDownloads|OrganizeFiles|CrossSyncMusic|AndroidSync|UpdateCarCard|UpdateSmartStorage|MusicLibraryManager)(?<suffix>-quarantine|-recovery)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IFileMutationCoordinator _mutations;
    private readonly IItunesMediaMutationService? _itunes;
    private readonly IReverseDeltaService _reverseDelta;

    public OperationJournalService(
        IFileMutationCoordinator? mutations = null,
        IItunesMediaMutationService? itunes = null,
        IReverseDeltaService? reverseDelta = null)
    {
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _itunes = itunes;
        _reverseDelta = reverseDelta ?? new ReverseDeltaService();
    }

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

    public Task<OperationRestorePlan> PreviewRestoreAsync(
        OperationJournalSummary run,
        IReadOnlyList<OperationFileEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(() => PreviewRestore(run, entries, ct), ct);
    }

    public Task<OperationRestoreResult> ApplyRestoreAsync(
        OperationRestorePlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ApplyRestoreCoreAsync(plan, progress, ct), ct);
    }

    public Task<OperationRestoreBatchPlan> PreviewRestoreBatchAsync(
        IReadOnlyList<OperationRestorePlan> plans,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new OperationRestoreBatchPlan(plans));
    }

    public Task<OperationRestoreBatchResult> ApplyRestoreBatchAsync(
        OperationRestoreBatchPlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(
            () => ApplyRestoreBatchCoreAsync(
                plan, progress, isBatchTransaction: true, ct),
            ct);
    }

    public Task<OperationRestoreTransitionState> ReconcileRestoreBatchAsync(
        IReadOnlyList<string> restoreJournalPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restoreJournalPaths);
        return Task.Run(
            () => ReconcileRestoreBatchCoreAsync(restoreJournalPaths, ct),
            ct);
    }

    private async Task<OperationRestoreResult> ApplyRestoreCoreAsync(
        OperationRestorePlan plan,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        OperationRestoreBatchResult result = await ApplyRestoreBatchCoreAsync(
            new([plan]), progress, isBatchTransaction: false, ct).ConfigureAwait(false);
        return new(result.RestoredCount, result.CollisionBackupCount);
    }

    private async Task<OperationRestoreBatchResult> ApplyRestoreBatchCoreAsync(
        OperationRestoreBatchPlan plan,
        IProgress<int>? progress,
        bool isBatchTransaction,
        CancellationToken ct)
    {
        if (!plan.CanApply)
            return new(0, 0, []);

        OperationRestoreAction[] actions = [.. plan.Actions];
        if (actions.GroupBy(action => action.DestinationPath, PathComparer)
            .Any(group => group.Count() > 1))
            throw new InvalidOperationException(
                "A restore batch cannot target the same destination more than once.");
        ItunesMediaMutation[] itunesMutations = ExpandRestoreMutations(actions).ToArray();
        var paths = actions.SelectMany(action => new[]
        {
            action.SourcePath, action.DestinationPath, action.CollisionBackupPath,
        }).Concat(itunesMutations.SelectMany(mutation =>
                new[] { mutation.OriginalPath, mutation.CurrentPath }))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();
        using var lease = await _mutations.AcquireAsync(paths, ct).ConfigureAwait(false);

        // This is deliberately a batch-wide gate: one stale source or destination prevents all
        // volumes from changing and leaves every recovery payload intact.
        foreach (OperationRestoreAction action in actions)
        {
            ct.ThrowIfCancellationRequested();
            ValidateSnapshot(action.SourcePath, action.SourceSnapshot, "restore source");
            if (action.Disposition == OperationRestoreDisposition.RestoreOriginal)
                ValidateSnapshot(action.DestinationPath, action.DestinationSnapshot,
                    "restore destination");
            else
                await ValidateCreatedOutputAsync(action, ct).ConfigureAwait(false);
            if (action.PayloadKind == RecoveryPayloadKind.ReverseDelta)
                await ValidateCompactBaseAsync(action, ct).ConfigureAwait(false);
            if (Exists(action.CollisionBackupPath))
                throw new InvalidOperationException(
                    $"Restore collision backup already exists: {action.CollisionBackupPath}");
        }

        var preparedCompact = actions
            .Where(item => item.PayloadKind == RecoveryPayloadKind.ReverseDelta)
            .ToDictionary(
                action => action,
                action => SiblingRestorePath(action.DestinationPath));
        try
        {
            foreach (OperationRestorePlan restorePlan in plan.Plans)
            {
                WriteRestoreJournal(
                    restorePlan.RestoreJournalPath,
                    [
                        isBatchTransaction
                            ? "BEGIN\tRESTORE_BATCH"
                            : "BEGIN\tRESTORE",
                        .. restorePlan.Actions.Select(action =>
                            action.Disposition ==
                                OperationRestoreDisposition.RemoveCreatedOutput
                                ? $"PLAN_REMOVE_CREATED\t{action.SourcePath}\t" +
                                  $"{action.CollisionBackupPath}\t" +
                                  $"{action.PostEditLength}\t{action.PostEditSha256}"
                                : action.PayloadKind == RecoveryPayloadKind.ReverseDelta
                                ? $"PLAN_RESTORE_COMPACT\t{action.SourcePath}\t" +
                                  $"{action.DestinationPath}\t" +
                                  $"{action.CollisionBackupPath}\t" +
                                  $"{preparedCompact[action]}"
                                : $"PLAN_RESTORE\t{action.SourcePath}\t" +
                                  $"{action.DestinationPath}\t" +
                                  $"{action.CollisionBackupPath}"),
                    ]);
            }

            // Reconstruct and verify every compact original before the first live rename. A
            // corrupt payload, malicious command, cancellation, or capacity failure therefore
            // cannot leave a multi-volume batch partially restored.
            foreach (OperationRestoreAction action in actions.Where(item =>
                         item.PayloadKind == RecoveryPayloadKind.ReverseDelta))
            {
                ct.ThrowIfCancellationRequested();
                string temporary = preparedCompact[action];
                await _reverseDelta.RestoreFileAsync(
                    action.SourcePath, action.DestinationPath, temporary, ct)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            bool rollbackComplete =
                TryDeleteFilesStrict(preparedCompact.Values);
            foreach (OperationRestorePlan restorePlan in plan.Plans)
                TryWriteRestoreJournal(
                    restorePlan.RestoreJournalPath,
                    rollbackComplete
                        ? isBatchTransaction
                            ? "ROLLBACK\tRESTORE_BATCH"
                            : "ROLLBACK\tRESTORE"
                        : isBatchTransaction
                            ? "ROLLBACK_FAILED\tRESTORE_BATCH"
                            : "ROLLBACK_FAILED\tRESTORE");
            throw;
        }

        IItunesMediaMutationSession? startedSession;
        try
        {
            startedSession = _itunes is null
                ? null
                : await _itunes.BeginAsync(paths, backupFiles: false, ct)
                    .ConfigureAwait(false);
        }
        catch
        {
            bool rollbackComplete =
                TryDeleteFilesStrict(preparedCompact.Values);
            foreach (OperationRestorePlan restorePlan in plan.Plans)
                TryWriteRestoreJournal(
                    restorePlan.RestoreJournalPath,
                    rollbackComplete
                        ? isBatchTransaction
                            ? "ROLLBACK\tRESTORE_BATCH"
                            : "ROLLBACK\tRESTORE"
                        : isBatchTransaction
                            ? "ROLLBACK_FAILED\tRESTORE_BATCH"
                            : "ROLLBACK_FAILED\tRESTORE");
            throw;
        }
        await using IItunesMediaMutationSession? itunesSession = startedSession;

        var completed = new List<(OperationRestorePlan Plan, OperationRestoreAction Action)>();
        bool reachedCommitPoint = false;
        try
        {
            foreach (OperationRestorePlan restorePlan in plan.Plans)
            {
                foreach (OperationRestoreAction action in restorePlan.Actions)
                {
                    ct.ThrowIfCancellationRequested();
                    bool collisionMoved = false;
                    try
                    {
                        if (action.Disposition ==
                            OperationRestoreDisposition.RemoveCreatedOutput)
                        {
                            MovePath(action.SourcePath, action.CollisionBackupPath);
                        }
                        else
                        {
                            if (action.DestinationSnapshot.Exists)
                            {
                                MovePath(action.DestinationPath, action.CollisionBackupPath);
                                collisionMoved = true;
                            }
                            if (action.PayloadKind == RecoveryPayloadKind.ReverseDelta)
                                File.Move(preparedCompact[action], action.DestinationPath, false);
                            else
                                MovePath(action.SourcePath, action.DestinationPath);
                        }
                    }
                    catch
                    {
                        if (collisionMoved && !Exists(action.DestinationPath) &&
                            Exists(action.CollisionBackupPath))
                            MovePath(action.CollisionBackupPath, action.DestinationPath);
                        throw;
                    }
                    completed.Add((restorePlan, action));
                    WriteRestoreJournal(restorePlan.RestoreJournalPath,
                        [action.Disposition ==
                            OperationRestoreDisposition.RemoveCreatedOutput
                            ? $"REMOVE_CREATED\t{action.SourcePath}\t{action.CollisionBackupPath}"
                            : action.PayloadKind == RecoveryPayloadKind.ReverseDelta
                            ? $"RESTORE_COMPACT\t{action.SourcePath}\t{action.DestinationPath}\t{action.CollisionBackupPath}"
                            : $"RESTORE\t{action.SourcePath}\t{action.DestinationPath}\t{action.CollisionBackupPath}"]);
                    progress?.Report(completed.Count);
                }
            }
            if (plan.Plans.Count > 0)
                WriteRestoreJournal(plan.Plans[0].RestoreJournalPath,
                    [isBatchTransaction
                        ? "APPLIED\tRESTORE_BATCH"
                        : "APPLIED\tRESTORE"]);
            if (itunesSession is not null)
                await itunesSession.CommitAsync(itunesMutations, CancellationToken.None)
                    .ConfigureAwait(false);
            foreach (OperationRestorePlan restorePlan in plan.Plans)
                WriteRestoreJournal(
                    restorePlan.RestoreJournalPath,
                    [isBatchTransaction
                        ? "COMMIT\tRESTORE_BATCH"
                        : "COMMIT\tRESTORE"]);
            reachedCommitPoint = true;
            if (itunesSession is not null)
                await itunesSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (OperationRestoreAction action in actions.Where(item =>
                         item.PayloadKind == RecoveryPayloadKind.ReverseDelta))
            {
                // The durable restore commit is the payload-consumption point. Recipe redo does
                // not need either the reverse delta or the transient post-edit collision.
                DeleteFileStrict(action.CollisionBackupPath);
                DeleteFileStrict(action.SourcePath);
            }
            foreach (OperationRestoreAction action in actions.Where(item =>
                         item.Disposition ==
                         OperationRestoreDisposition.RemoveCreatedOutput))
                DeleteFileStrict(action.CollisionBackupPath);
            foreach (OperationRestorePlan restorePlan in plan.Plans)
                WriteRestoreJournal(
                    restorePlan.RestoreJournalPath,
                    [isBatchTransaction
                        ? "CONSUMED\tRESTORE_BATCH"
                        : "CONSUMED\tRESTORE"]);
            return new(
                completed.Count,
                completed.Count(item =>
                    item.Action.Disposition ==
                        OperationRestoreDisposition.RestoreOriginal &&
                    item.Action.DestinationSnapshot.Exists),
                plan.Plans.Select(item => item.RestoreJournalPath).ToArray());
        }
        catch
        {
            if (reachedCommitPoint)
                throw;
            bool rollbackComplete = true;
            foreach ((_, OperationRestoreAction action) in completed.AsEnumerable().Reverse())
            {
                try
                {
                    if (action.Disposition ==
                        OperationRestoreDisposition.RemoveCreatedOutput)
                    {
                        if (Exists(action.SourcePath))
                            throw new IOException(
                                $"A generated output reappeared during rollback: {action.SourcePath}");
                        if (Exists(action.CollisionBackupPath))
                            MovePath(action.CollisionBackupPath, action.SourcePath);
                    }
                    else if (action.PayloadKind == RecoveryPayloadKind.ReverseDelta)
                    {
                        if (File.Exists(action.DestinationPath))
                            DeleteFileStrict(action.DestinationPath);
                    }
                    else if (Exists(action.DestinationPath) && !Exists(action.SourcePath))
                    {
                        MovePath(action.DestinationPath, action.SourcePath);
                    }
                    if (Exists(action.CollisionBackupPath) && !Exists(action.DestinationPath))
                        MovePath(action.CollisionBackupPath, action.DestinationPath);
                }
                catch
                {
                    rollbackComplete = false;
                }
            }
            rollbackComplete &=
                TryDeleteFilesStrict(preparedCompact.Values);
            foreach (OperationRestorePlan restorePlan in plan.Plans)
                TryWriteRestoreJournal(restorePlan.RestoreJournalPath,
                    rollbackComplete
                        ? isBatchTransaction
                            ? "ROLLBACK\tRESTORE_BATCH"
                            : "ROLLBACK\tRESTORE"
                        : isBatchTransaction
                            ? "ROLLBACK_FAILED\tRESTORE_BATCH"
                            : "ROLLBACK_FAILED\tRESTORE");
            throw;
        }
    }

    private async Task<OperationRestoreTransitionState> ReconcileRestoreBatchCoreAsync(
        IReadOnlyList<string> restoreJournalPaths,
        CancellationToken ct)
    {
        RestoreJournalTransaction[] transactions = restoreJournalPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .Where(File.Exists)
            .Select(ReadRestoreTransaction)
            .ToArray();
        if (transactions.Length == 0)
            return OperationRestoreTransitionState.Unapplied;

        if (transactions.Any(transaction =>
                transaction.Terminal == RestoreJournalTerminal.RolledBack))
            return OperationRestoreTransitionState.Unapplied;
        if (transactions.All(transaction =>
                transaction.Terminal == RestoreJournalTerminal.Consumed))
            return OperationRestoreTransitionState.Consumed;

        RestoreJournalAction[] actions = transactions
            .SelectMany(transaction => transaction.Actions)
            .ToArray();
        foreach (RestoreJournalAction action in actions)
            ValidateRestoreJournalAction(action);

        var paths = actions
            .SelectMany(action => new[]
            {
                action.SourcePath,
                action.DestinationPath,
                action.CollisionBackupPath,
                action.PreparedPath,
            })
            .Where(path => path is not null)
            .Select(path => path!)
            .Concat(transactions.Select(transaction => transaction.Path))
            .ToArray();
        using var lease = await _mutations.AcquireAsync(paths, ct).ConfigureAwait(false);

        bool reachedCommitPoint = transactions.Any(transaction =>
            transaction.Terminal is
                RestoreJournalTerminal.Committed or
                RestoreJournalTerminal.Consumed);
        if (!reachedCommitPoint)
        {
            try
            {
                foreach (RestoreJournalAction action in actions.Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    if (action.Disposition ==
                        OperationRestoreDisposition.RemoveCreatedOutput)
                        await RollBackInterruptedCreatedRemovalAsync(action, ct)
                            .ConfigureAwait(false);
                    else if (action.PayloadKind == RecoveryPayloadKind.ReverseDelta)
                        await RollBackInterruptedCompactRestoreAsync(action, ct)
                            .ConfigureAwait(false);
                    else
                        RollBackInterruptedFullRestore(action);
                }
                foreach (RestoreJournalTransaction transaction in transactions)
                    WriteRestoreJournal(
                        transaction.Path,
                        [transaction.IsBatch
                            ? "ROLLBACK\tRESTORE_BATCH"
                            : "ROLLBACK\tRESTORE"]);
                return OperationRestoreTransitionState.Unapplied;
            }
            catch
            {
                foreach (RestoreJournalTransaction transaction in transactions)
                    TryWriteRestoreJournal(
                        transaction.Path,
                        transaction.IsBatch
                            ? "ROLLBACK_FAILED\tRESTORE_BATCH"
                            : "ROLLBACK_FAILED\tRESTORE");
                throw;
            }
        }

        // Once APPLIED is durable, every live file has crossed the batch commit point. Complete
        // the journal transition and idempotently consume only compact transient payloads.
        foreach (RestoreJournalTransaction transaction in transactions.Where(
                     transaction => transaction.Terminal is
                         RestoreJournalTerminal.None or
                         RestoreJournalTerminal.Applied))
        {
            WriteRestoreJournal(
                transaction.Path,
                [transaction.IsBatch
                    ? "COMMIT\tRESTORE_BATCH"
                    : "COMMIT\tRESTORE"]);
        }
        foreach (RestoreJournalAction action in actions.Where(action =>
                     action.PayloadKind == RecoveryPayloadKind.ReverseDelta))
        {
            ct.ThrowIfCancellationRequested();
            await ValidateCommittedCompactRestoreAsync(action, ct)
                .ConfigureAwait(false);
            DeleteFileStrict(action.PreparedPath);
            DeleteFileStrict(action.CollisionBackupPath);
            DeleteFileStrict(action.SourcePath);
        }
        foreach (RestoreJournalAction action in actions.Where(action =>
                     action.Disposition ==
                     OperationRestoreDisposition.RemoveCreatedOutput))
        {
            ct.ThrowIfCancellationRequested();
            await ValidateCommittedCreatedRemovalAsync(action, ct)
                .ConfigureAwait(false);
            DeleteFileStrict(action.CollisionBackupPath);
        }
        foreach (RestoreJournalTransaction transaction in transactions)
        {
            if (transaction.Terminal != RestoreJournalTerminal.Consumed)
                WriteRestoreJournal(
                    transaction.Path,
                    [transaction.IsBatch
                        ? "CONSUMED\tRESTORE_BATCH"
                        : "CONSUMED\tRESTORE"]);
        }
        return OperationRestoreTransitionState.Consumed;
    }

    private static async Task RollBackInterruptedCreatedRemovalAsync(
        RestoreJournalAction action,
        CancellationToken ct)
    {
        bool outputExists = File.Exists(action.SourcePath);
        bool backupExists = File.Exists(action.CollisionBackupPath);
        if (backupExists)
        {
            if (outputExists)
                throw new InvalidOperationException(
                    $"The interrupted generated-output restore has two live copies: " +
                    $"{action.SourcePath}");
            await ValidateCreatedFileAsync(
                action.CollisionBackupPath,
                action.ExpectedLength,
                action.ExpectedSha256,
                ct).ConfigureAwait(false);
            MovePath(action.CollisionBackupPath, action.SourcePath);
            return;
        }
        if (!outputExists)
            throw new InvalidOperationException(
                $"The interrupted generated-output restore lost '{action.SourcePath}'.");
        await ValidateCreatedFileAsync(
            action.SourcePath,
            action.ExpectedLength,
            action.ExpectedSha256,
            ct).ConfigureAwait(false);
    }

    private static async Task ValidateCommittedCreatedRemovalAsync(
        RestoreJournalAction action,
        CancellationToken ct)
    {
        if (File.Exists(action.SourcePath))
            throw new InvalidOperationException(
                $"A committed generated output removal reappeared: {action.SourcePath}");
        if (File.Exists(action.CollisionBackupPath))
            await ValidateCreatedFileAsync(
                action.CollisionBackupPath,
                action.ExpectedLength,
                action.ExpectedSha256,
                ct).ConfigureAwait(false);
    }

    private static async Task ValidateCreatedFileAsync(
        string path,
        long expectedLength,
        string? expectedSha256,
        CancellationToken ct)
    {
        if (!File.Exists(path) ||
            expectedLength < 0 ||
            new FileInfo(path).Length != expectedLength ||
            string.IsNullOrWhiteSpace(expectedSha256) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                await HashFileAsync(path, ct).ConfigureAwait(false),
                expectedSha256))
        {
            throw new InvalidOperationException(
                $"A generated output recovery file changed: {path}");
        }
    }

    private async Task RollBackInterruptedCompactRestoreAsync(
        RestoreJournalAction action,
        CancellationToken ct)
    {
        if (!File.Exists(action.SourcePath))
            throw new InvalidOperationException(
                $"The compact recovery payload is missing: {action.SourcePath}");

        if (File.Exists(action.CollisionBackupPath))
        {
            await _reverseDelta.ValidateBaseFileAsync(
                action.SourcePath,
                action.CollisionBackupPath,
                ct).ConfigureAwait(false);
            if (File.Exists(action.DestinationPath))
            {
                ReverseDeltaDescriptor descriptor = await _reverseDelta.InspectFileAsync(
                    action.SourcePath,
                    ct).ConfigureAwait(false);
                string destinationHash = await HashFileAsync(
                    action.DestinationPath,
                    ct).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        destinationHash,
                        descriptor.OriginalSha256))
                {
                    throw new InvalidOperationException(
                        $"The interrupted compact restore destination changed: " +
                        $"{action.DestinationPath}");
                }
                DeleteFileStrict(action.DestinationPath);
            }
            File.Move(action.CollisionBackupPath, action.DestinationPath, false);
        }
        else if (File.Exists(action.DestinationPath))
        {
            // No collision means the live rename had not begun. The destination must still be
            // the exact post-edit base before the prepared original can be discarded.
            await _reverseDelta.ValidateBaseFileAsync(
                action.SourcePath,
                action.DestinationPath,
                ct).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"The interrupted compact restore lost its live base: " +
                $"{action.DestinationPath}");
        }

        DeleteFileStrict(action.PreparedPath);
    }

    private static void RollBackInterruptedFullRestore(
        RestoreJournalAction action)
    {
        bool sourceExists = Exists(action.SourcePath);
        bool destinationExists = Exists(action.DestinationPath);
        bool collisionExists = Exists(action.CollisionBackupPath);
        if (collisionExists)
        {
            if (!sourceExists)
            {
                if (!destinationExists)
                    throw new InvalidOperationException(
                        $"The interrupted restore lost both copies of " +
                        $"'{action.DestinationPath}'.");
                MovePath(action.DestinationPath, action.SourcePath);
                destinationExists = false;
            }
            else if (destinationExists)
            {
                throw new InvalidOperationException(
                    $"The interrupted restore destination changed: " +
                    $"{action.DestinationPath}");
            }

            MovePath(action.CollisionBackupPath, action.DestinationPath);
            return;
        }

        if (!sourceExists)
        {
            if (!destinationExists)
                throw new InvalidOperationException(
                    $"The interrupted restore lost '{action.SourcePath}'.");
            MovePath(action.DestinationPath, action.SourcePath);
        }
    }

    private async Task ValidateCommittedCompactRestoreAsync(
        RestoreJournalAction action,
        CancellationToken ct)
    {
        if (!File.Exists(action.SourcePath))
            return; // Cleanup already consumed the durable payload before the process stopped.
        ReverseDeltaDescriptor descriptor = await _reverseDelta.InspectFileAsync(
            action.SourcePath,
            ct).ConfigureAwait(false);
        if (!File.Exists(action.DestinationPath))
            throw new InvalidOperationException(
                $"The committed compact restore destination is missing: " +
                $"{action.DestinationPath}");
        string destinationHash = await HashFileAsync(
            action.DestinationPath,
            ct).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                destinationHash,
                descriptor.OriginalSha256))
        {
            throw new InvalidOperationException(
                $"The committed compact restore destination changed: " +
                $"{action.DestinationPath}");
        }
        if (File.Exists(action.CollisionBackupPath))
        {
            await _reverseDelta.ValidateBaseFileAsync(
                action.SourcePath,
                action.CollisionBackupPath,
                ct).ConfigureAwait(false);
        }
    }

    private static RestoreJournalTransaction ReadRestoreTransaction(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string[] lines = File.ReadAllLines(fullPath);
        bool isBatch = lines.Any(line =>
            line.EndsWith("\tRESTORE_BATCH", StringComparison.Ordinal));
        var actions = new List<RestoreJournalAction>();
        RestoreJournalTerminal terminal = RestoreJournalTerminal.None;
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length == 0)
                continue;
            switch (fields[0])
            {
                case "PLAN_RESTORE_COMPACT" when fields.Length > 4:
                    actions.Add(new(
                        RecoveryPayloadKind.ReverseDelta,
                        fields[1],
                        fields[2],
                        fields[3],
                        fields[4],
                        fullPath,
                        OperationRestoreDisposition.RestoreOriginal,
                        0,
                        null));
                    break;
                case "PLAN_RESTORE" when fields.Length > 3:
                    actions.Add(new(
                        RecoveryPayloadKind.FullOriginal,
                        fields[1],
                        fields[2],
                        fields[3],
                        null,
                        fullPath,
                        OperationRestoreDisposition.RestoreOriginal,
                        0,
                        null));
                    break;
                case "PLAN_REMOVE_CREATED" when fields.Length > 4 &&
                    long.TryParse(
                        fields[3],
                        CultureInfo.InvariantCulture,
                        out long expectedLength):
                    actions.Add(new(
                        RecoveryPayloadKind.FullOriginal,
                        fields[1],
                        fields[1],
                        fields[2],
                        null,
                        fullPath,
                        OperationRestoreDisposition.RemoveCreatedOutput,
                        expectedLength,
                        fields[4]));
                    break;
                case "APPLIED":
                    terminal = RestoreJournalTerminal.Applied;
                    break;
                case "COMMIT":
                    terminal = RestoreJournalTerminal.Committed;
                    break;
                case "CONSUMED":
                    terminal = RestoreJournalTerminal.Consumed;
                    break;
                case "ROLLBACK":
                    terminal = RestoreJournalTerminal.RolledBack;
                    break;
                case "ROLLBACK_FAILED":
                    terminal = RestoreJournalTerminal.RollbackFailed;
                    break;
            }
        }
        return new(fullPath, isBatch, actions, terminal);
    }

    private static void ValidateRestoreJournalAction(
        RestoreJournalAction action)
    {
        string restoreRoot = Path.GetDirectoryName(action.JournalPath)!;
        if (!IsDescendant(action.CollisionBackupPath, restoreRoot))
            throw new InvalidDataException(
                "A restore collision path escaped its restore transaction root.");
        if (action.PayloadKind != RecoveryPayloadKind.ReverseDelta)
            return;
        if (action.PreparedPath is null ||
            !PathComparer.Equals(
                Path.GetDirectoryName(Path.GetFullPath(action.PreparedPath)),
                Path.GetDirectoryName(Path.GetFullPath(action.DestinationPath))))
        {
            throw new InvalidDataException(
                "A compact prepared path is not beside its destination.");
        }
        string preparedName = Path.GetFileName(action.PreparedPath);
        string expectedPrefix = "." + Path.GetFileName(action.DestinationPath) + ".";
        if (!preparedName.StartsWith(expectedPrefix, PathComparison) ||
            !preparedName.EndsWith(".undo-restore", PathComparison))
        {
            throw new InvalidDataException(
                "A compact prepared path has an invalid transaction name.");
        }
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static void DeleteFileStrict(string? path)
    {
        if (path is null)
            return;
        if (Directory.Exists(path))
            throw new IOException(
                $"A recovery file path unexpectedly became a directory: {path}");
        if (!File.Exists(path))
            return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static bool TryDeleteFilesStrict(IEnumerable<string> paths)
    {
        bool deleted = true;
        foreach (string path in paths)
        {
            try { DeleteFileStrict(path); }
            catch { deleted = false; }
        }
        return deleted;
    }

    private static IEnumerable<ItunesMediaMutation> ExpandRestoreMutations(
        IReadOnlyList<OperationRestoreAction> actions)
    {
        foreach (OperationRestoreAction action in actions)
        {
            if (action.Disposition ==
                OperationRestoreDisposition.RemoveCreatedOutput)
            {
                yield return ItunesMediaMutation.Remove(action.SourcePath);
                continue;
            }
            if (action.PayloadKind == RecoveryPayloadKind.ReverseDelta)
            {
                yield return ItunesMediaMutation.Refresh(action.DestinationPath);
                continue;
            }
            if (action.SourceSnapshot.IsDirectory)
            {
                foreach (string source in Directory.EnumerateFiles(
                             action.SourcePath, "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(action.DestinationPath,
                        Path.GetRelativePath(action.SourcePath, source));
                    yield return ItunesMediaMutation.Relocate(source, destination);
                }
            }
            else
            {
                yield return ItunesMediaMutation.Relocate(
                    action.SourcePath, action.DestinationPath);
            }
        }
    }

    public Task<OperationPurgePlan> PreviewPurgeAsync(
        IReadOnlyList<OperationJournalSummary> runs,
        int retentionDays,
        DateTimeOffset? nowUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (retentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be at least one day.");
        return Task.Run(() => PreviewPurge(runs, retentionDays, nowUtc ?? DateTimeOffset.UtcNow, ct), ct);
    }

    public Task<OperationPurgeResult> ApplyPurgeAsync(
        OperationPurgePlan plan,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => ApplyPurgeCoreAsync(plan, progress, ct), ct);
    }

    private async Task<OperationPurgeResult> ApplyPurgeCoreAsync(
        OperationPurgePlan plan,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        if (!plan.CanApply)
            return new(0, 0, 0);

        var paths = plan.Runs.SelectMany(run => new[] { run.Run.RunPath, run.StagingPath }).ToList();
        using var lease = await _mutations.AcquireAsync(paths, ct);

        // Validate every run before making the first change. This prevents a partial purge when any
        // reviewed run has received, lost, or changed a file since preview.
        foreach (var run in plan.Runs)
        {
            ct.ThrowIfCancellationRequested();
            ValidatePurgeManifest(run, ct);
            if (Exists(run.StagingPath))
                throw new InvalidOperationException($"Purge staging path already exists: {run.StagingPath}");
        }

        var staged = new List<OperationPurgeRun>();
        try
        {
            foreach (var run in plan.Runs)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(run.StagingPath)!);
                Directory.Move(run.Run.RunPath, run.StagingPath);
                staged.Add(run);
            }
            // Close the validation-to-rename race before reaching the irreversible delete phase.
            foreach (var run in staged)
            {
                ct.ThrowIfCancellationRequested();
                ValidatePurgeManifest(run, run.StagingPath, ct);
            }
        }
        catch
        {
            foreach (var run in staged.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(run.StagingPath) && !Exists(run.Run.RunPath))
                        Directory.Move(run.StagingPath, run.Run.RunPath);
                }
                catch { }
            }
            throw;
        }

        int deleted = 0;
        foreach (var run in staged)
        {
            // Staging is the commit point. Do not honor cancellation between staged deletions or
            // leave reviewed runs hidden in the staging area.
            DeleteStagedRun(run);
            deleted++;
            progress?.Report(deleted);
        }
        foreach (string previewRoot in staged.Select(run => Path.GetDirectoryName(run.StagingPath)!)
                     .Distinct(PathComparer))
            TryDeleteEmptyStagingParents(previewRoot);
        return new(deleted, plan.Runs.Take(deleted).Sum(run => run.FileCount),
            plan.Runs.Take(deleted).Sum(run => run.TotalBytes));
    }

    private static OperationPurgePlan PreviewPurge(
        IReadOnlyList<OperationJournalSummary> runs,
        int retentionDays,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        DateTimeOffset cutoff = nowUtc.ToUniversalTime().AddDays(-retentionDays);
        int interrupted = 0, unsafeRuns = 0, newer = 0;
        var purgeRuns = new List<OperationPurgeRun>();
        string previewId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");

        OperationJournalSummary[] expandedRuns =
        [
            .. runs.SelectMany(
                ExpandReviewedChangeParticipants),
        ];
        foreach (var run in expandedRuns
                     .DistinctBy(
                         run => Path.GetFullPath(
                             run.RunPath),
                         PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            if (run.State == OperationJournalState.Interrupted)
            {
                interrupted++;
                continue;
            }
            if (run.CreatedAtUtc > cutoff)
            {
                newer++;
                continue;
            }
            string fullRun = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.RunPath));
            string container = Path.TrimEndingDirectorySeparator(ContainerForRun(fullRun));
            if (PathComparer.Equals(fullRun, container) || !Directory.Exists(fullRun))
            {
                unsafeRuns++;
                continue;
            }
            string staging = Path.Combine(container, ".MusicLibrary.App-purge-staging", previewId,
                Path.GetFileName(fullRun) + "-" + Guid.NewGuid().ToString("N"));
            purgeRuns.Add(new(run, staging, CapturePurgeManifest(fullRun, ct)));
        }
        return new(retentionDays, cutoff, purgeRuns, interrupted, unsafeRuns, newer);
    }

    private static IEnumerable<OperationJournalSummary>
        ExpandReviewedChangeParticipants(
        OperationJournalSummary run)
    {
        if (run.ReviewedChangeTransaction is not
            { } transaction)
        {
            yield return run;
            yield break;
        }

        foreach (string journal in
                 transaction.ParticipantJournalPaths)
        {
            string runPath =
                Path.GetDirectoryName(journal)!;
            int? affected = null;
            if (File.Exists(journal))
            {
                try
                {
                    affected =
                        ParseMutationJournal(
                            File.ReadAllLines(journal))
                        .Count;
                }
                catch
                {
                }
            }
            yield return new(
                run.ToolName,
                run.Kind,
                run.State,
                runPath,
                journal,
                run.CreatedAtUtc,
                affected);
        }
    }

    private static IReadOnlyList<OperationPurgeManifestEntry> CapturePurgeManifest(
        string root,
        CancellationToken ct)
    {
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Operation run no longer exists: {root}");
        var entries = new List<OperationPurgeManifestEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                bool isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                string relative = Path.GetRelativePath(root, path);
                if (isDirectory)
                {
                    entries.Add(new(relative, true, isReparse, 0, default));
                    if (!isReparse)
                        pending.Push(path);
                }
                else
                {
                    var file = new FileInfo(path);
                    entries.Add(new(relative, false, isReparse, file.Length, file.LastWriteTimeUtc));
                }
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, PathComparer).ToList();
    }

    private static void ValidatePurgeManifest(OperationPurgeRun run, CancellationToken ct) =>
        ValidatePurgeManifest(run, run.Run.RunPath, ct);

    private static void ValidatePurgeManifest(OperationPurgeRun run, string path, CancellationToken ct)
    {
        IReadOnlyList<OperationPurgeManifestEntry> current;
        try { current = CapturePurgeManifest(path, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Operation run changed since purge preview: {run.Run.RunPath}. Preview again before purging.", ex);
        }
        if (current.Count != run.Manifest.Count || !current.Zip(run.Manifest).All(pair =>
                PathComparer.Equals(pair.First.RelativePath, pair.Second.RelativePath) &&
                pair.First.IsDirectory == pair.Second.IsDirectory &&
                pair.First.IsReparsePoint == pair.Second.IsReparsePoint &&
                (pair.First.IsDirectory || pair.First.Length == pair.Second.Length) &&
                (pair.First.IsDirectory || Math.Abs((pair.First.LastWriteTimeUtc -
                    pair.Second.LastWriteTimeUtc).TotalMilliseconds) <= 500)))
            throw new InvalidOperationException(
                $"Operation run changed since purge preview: {run.Run.RunPath}. Preview again before purging.");
    }

    private static void DeleteStagedRun(OperationPurgeRun run)
    {
        // The manifest deliberately does not traverse reparse points. Delete leaves and links first,
        // then ordinary directories from deepest to shallowest, so purge cannot cross a junction.
        foreach (var entry in run.Manifest.Where(entry => !entry.IsDirectory || entry.IsReparsePoint)
                     .OrderByDescending(entry => entry.RelativePath.Length))
        {
            string path = Path.Combine(run.StagingPath, entry.RelativePath);
            if (entry.IsDirectory)
                Directory.Delete(path);
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        foreach (var entry in run.Manifest.Where(entry => entry.IsDirectory && !entry.IsReparsePoint)
                     .OrderByDescending(entry => entry.RelativePath.Length))
            Directory.Delete(Path.Combine(run.StagingPath, entry.RelativePath));
        Directory.Delete(run.StagingPath);
    }

    private static void TryDeleteEmptyStagingParents(string previewRoot)
    {
        try
        {
            if (Directory.Exists(previewRoot) && !Directory.EnumerateFileSystemEntries(previewRoot).Any())
                Directory.Delete(previewRoot);
            string? stagingRoot = Path.GetDirectoryName(previewRoot);
            if (stagingRoot is not null &&
                Path.GetFileName(stagingRoot).Equals(".MusicLibrary.App-purge-staging",
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                Directory.Delete(stagingRoot);
        }
        catch { }
    }

    private static OperationRestorePlan PreviewRestore(
        OperationJournalSummary run,
        IReadOnlyList<OperationFileEntry> entries,
        CancellationToken ct)
    {
        string restoreRoot = Path.Combine(run.RunPath, ".MusicLibrary.App-restore",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        var eligible = entries
            .Where(entry => entry.Kind is OperationEntryKind.Quarantined or
                    OperationEntryKind.Moved or OperationEntryKind.Created or
                    OperationEntryKind.Planned && entry.CurrentPath is not null)
            .Where(entry => entry.CurrentPath is not null && Exists(entry.CurrentPath))
            .Where(entry => entry.Kind != OperationEntryKind.Created ||
                !string.IsNullOrWhiteSpace(entry.PostEditSha256))
            .ToList();
        // A selected directory and one of its selected descendants cannot both be moved. Prefer
        // leaf entries; keep only empty/directly selected directories with no selected descendant.
        var sources = eligible.Select(entry => entry.CurrentPath!).ToHashSet(PathComparer);
        var destinations = eligible.Select(entry => entry.OriginalPath).ToHashSet(PathComparer);
        eligible = eligible.Where(entry => !entry.IsDirectory || !sources.Any(source =>
                !PathComparer.Equals(source, entry.CurrentPath!) && IsDescendant(source, entry.CurrentPath!)) &&
            !destinations.Any(destination => !PathComparer.Equals(destination, entry.OriginalPath) &&
                IsDescendant(destination, entry.OriginalPath)))
            .ToList();

        var actions = new List<OperationRestoreAction>();
        foreach (var entry in eligible
                     .GroupBy(entry => entry.OriginalPath, PathComparer)
                     .Select(group => group.First())
                     .OrderBy(entry => entry.OriginalPath, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            string backup = Path.Combine(restoreRoot, "collisions",
                Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(entry.OriginalPath));
            bool created = entry.Kind == OperationEntryKind.Created;
            actions.Add(new(
                entry.CurrentPath!, entry.OriginalPath, backup,
                Snapshot(entry.CurrentPath!),
                created ? OperationPathSnapshot.Missing(entry.OriginalPath) :
                    Snapshot(entry.OriginalPath),
                entry.Kind,
                entry.PayloadKind,
                entry.OriginalSha256,
                entry.PostEditSha256,
                entry.OriginalBytes,
                entry.PostEditBytes,
                entry.OriginalLastWriteTimeUtc,
                entry.OriginalAttributes,
                entry.PayloadSha256,
                created
                    ? OperationRestoreDisposition.RemoveCreatedOutput
                    : OperationRestoreDisposition.RestoreOriginal));
        }
        return new(run, Path.Combine(restoreRoot, "restore.tsv"), actions,
            entries.Count - actions.Count);
    }

    private static OperationPathSnapshot Snapshot(string path)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new(true, false, file.Length, file.LastWriteTimeUtc);
        }
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new(true, true, 0, directory.LastWriteTimeUtc);
        }
        return new(false, false, 0, default);
    }

    private static void ValidateSnapshot(string path, OperationPathSnapshot expected, string label)
    {
        var current = Snapshot(path);
        bool matches = current.Exists == expected.Exists && current.IsDirectory == expected.IsDirectory &&
            (!current.Exists || current.IsDirectory || current.Length == expected.Length) &&
            (!current.Exists || Math.Abs((current.LastWriteTimeUtc - expected.LastWriteTimeUtc).TotalMilliseconds) <= 500);
        if (!matches)
            throw new InvalidOperationException($"{label} changed since preview: {path}. Preview again before restoring.");
    }

    private async Task ValidateCompactBaseAsync(
        OperationRestoreAction action,
        CancellationToken ct)
    {
        if (!File.Exists(action.DestinationPath) ||
            action.PostEditLength < 0 ||
            new FileInfo(action.DestinationPath).Length != action.PostEditLength ||
            string.IsNullOrWhiteSpace(action.PostEditSha256) ||
            string.IsNullOrWhiteSpace(action.OriginalSha256))
        {
            throw new InvalidOperationException(
                $"Compact recovery base changed after the edit: {action.DestinationPath}. " +
                "Undo was refused and its history was retained.");
        }
        try
        {
            ReverseDeltaDescriptor descriptor = await _reverseDelta.ValidateBaseFileAsync(
                action.SourcePath, action.DestinationPath, ct).ConfigureAwait(false);
            bool journalMatches =
                descriptor.OriginalLength == action.OriginalLength &&
                descriptor.PostEditLength == action.PostEditLength &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    descriptor.OriginalSha256, action.OriginalSha256) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    descriptor.PostEditSha256, action.PostEditSha256) &&
                (action.OriginalLastWriteTimeUtc is null ||
                 descriptor.OriginalLastWriteTimeUtc ==
                 action.OriginalLastWriteTimeUtc.Value) &&
                (action.OriginalAttributes is null ||
                 descriptor.OriginalAttributes == action.OriginalAttributes.Value) &&
                (action.PayloadSha256 is null ||
                 StringComparer.OrdinalIgnoreCase.Equals(
                     descriptor.PayloadSha256, action.PayloadSha256));
            if (!journalMatches)
                throw new InvalidDataException(
                    "The compact journal metadata does not match its reverse-delta payload.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Compact recovery base or payload changed after the edit: " +
                $"{action.DestinationPath}. Undo was refused and its history was retained.",
                error);
        }
    }

    private static async Task ValidateCreatedOutputAsync(
        OperationRestoreAction action,
        CancellationToken ct)
    {
        if (!File.Exists(action.SourcePath) ||
            action.PostEditLength < 0 ||
            new FileInfo(action.SourcePath).Length != action.PostEditLength ||
            string.IsNullOrWhiteSpace(action.PostEditSha256))
        {
            throw new InvalidOperationException(
                $"A generated output changed after the operation: {action.SourcePath}. " +
                "Undo was refused and its history was retained.");
        }
        string currentHash = await HashFileAsync(action.SourcePath, ct).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                currentHash, action.PostEditSha256))
        {
            throw new InvalidOperationException(
                $"A generated output changed after the operation: {action.SourcePath}. " +
                "Undo was refused and its history was retained.");
        }
    }

    private static string SiblingRestorePath(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.undo-restore");

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

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsDescendant(string path, string parent)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, PathComparison);
    }

    private static void MovePath(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    private static void WriteRestoreJournal(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        foreach (string line in lines) writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void TryWriteRestoreJournal(string path, string line)
    {
        try { WriteRestoreJournal(path, [line]); }
        catch { }
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

        string[] journalPaths =
            run.ReviewedChangeTransaction is { } transaction
                ?
                [
                    .. transaction.ParticipantJournalPaths,
                ]
                : run.JournalPath is null
                    ? []
                    : [run.JournalPath];
        foreach (string journalPath in journalPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(journalPath))
            {
                warnings.Add(
                    $"Participant journal is unavailable: " +
                    $"{journalPath}");
                continue;
            }
            try
            {
                string[] lines = File.ReadAllLines(journalPath);
                if (run.ToolName == "UpdateCarCard")
                    ReadDeviceEntries(lines, originalRoot, entries, warnings, ct);
                else
                {
                    string journalRun =
                        Path.GetDirectoryName(journalPath)!;
                    string journalContainer =
                        ContainerForRun(journalRun);
                    Match journalMatch =
                        ContainerName.Match(
                            Path.GetFileName(
                                journalContainer));
                    string journalOriginalRoot =
                        journalMatch.Success
                            ? Path.Combine(
                                Path.GetDirectoryName(
                                    journalContainer) ??
                                "",
                                journalMatch.Groups[
                                    "base"].Value)
                            : originalRoot;
                    ReadMutationEntries(
                        lines,
                        journalOriginalRoot,
                        entries,
                        ct);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"Could not read journal " +
                    $"'{journalPath}': {ex.Message}");
            }
        }

        // Quarantine tools preserve relative paths physically. Walk only after this run is opened;
        // journal-only organize/device operations avoid an unrelated recursive scan.
        bool scansPhysicalQuarantine =
            run.ReviewedChangeTransaction is null &&
            (run.JournalPath is null ||
             run.ToolName is
                 "IngestMusic" or
                 "SortDownloads" or
                 "CrossSyncMusic" or
                 "AndroidSync" or
                 "UpdateSmartStorage");
        if (scansPhysicalQuarantine)
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
        var completedCompactDestinations = lines
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length > 2 &&
                fields[0] == "COMPACT_REPLACE")
            .Select(fields => fields[2])
            .ToHashSet(PathComparer);
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
                    if (entries.TryGetValue(fields[2], out var prior) &&
                        prior.Kind == OperationEntryKind.Quarantined)
                        break; // A replacement's recoverable backup is more useful than its install.
                    Put(entries, originalRoot, fields[2], fields[2], OperationEntryKind.Created);
                    break;
                case "CREATE_REVERSIBLE" when fields.Length > 6:
                    PutCreated(entries, originalRoot, fields);
                    break;
                case "DELTA_READY" when fields.Length > 10:
                    if (!completedCompactDestinations.Contains(fields[2]))
                        PutCompact(entries, originalRoot, fields, installed: false);
                    break;
                case "COMPACT_REPLACE" when fields.Length > 10:
                    PutCompact(entries, originalRoot, fields, installed: true);
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
                if (relative.Equals(".MusicLibrary.App-restore", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".MusicLibrary.App-restore" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
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
        long retainedBytes = current is not null && File.Exists(current)
            ? new FileInfo(current).Length
            : 0;
        entries[original] = new OperationFileEntry(
            original,
            current,
            Relative(originalRoot, original),
            kind,
            exists,
            directory,
            RecoveryPayloadKind.FullOriginal,
            retainedBytes,
            retainedBytes);
    }

    private static void PutCreated(
        Dictionary<string, OperationFileEntry> entries,
        string originalRoot,
        string[] fields)
    {
        long length = -1;
        long lastWriteTicks = 0;
        int attributes = 0;
        bool valid = int.TryParse(fields[1], CultureInfo.InvariantCulture, out int version) &&
            version == 1 &&
            long.TryParse(fields[3], CultureInfo.InvariantCulture, out length) &&
            length >= 0 &&
            long.TryParse(fields[5], CultureInfo.InvariantCulture, out lastWriteTicks) &&
            int.TryParse(fields[6], CultureInfo.InvariantCulture, out attributes) &&
            fields[4].Length == 64;
        if (!valid)
            return;
        string path = fields[2];
        bool exists = File.Exists(path);
        entries[path] = new OperationFileEntry(
            path,
            path,
            Relative(originalRoot, path),
            OperationEntryKind.Created,
            exists,
            false,
            RecoveryPayloadKind.FullOriginal,
            0,
            0,
            length,
            null,
            fields[4],
            null,
            new DateTime(lastWriteTicks, DateTimeKind.Utc),
            (FileAttributes)attributes);
    }

    private static void PutCompact(
        Dictionary<string, OperationFileEntry> entries,
        string originalRoot,
        string[] fields,
        bool installed)
    {
        string original = fields[2];
        string delta = fields[3];
        bool versionParsed = int.TryParse(
            fields[1], CultureInfo.InvariantCulture, out int formatVersion);
        bool originalLengthParsed = long.TryParse(
            fields[4], CultureInfo.InvariantCulture, out long originalBytes);
        bool postLengthParsed = long.TryParse(
            fields[5], CultureInfo.InvariantCulture, out long postEditBytes);
        bool retainedLengthParsed = long.TryParse(
            fields[8], CultureInfo.InvariantCulture, out long retainedBytes);
        bool timestampParsed = long.TryParse(
            fields[9], CultureInfo.InvariantCulture, out long lastWriteTicks);
        bool attributesParsed = int.TryParse(
            fields[10], CultureInfo.InvariantCulture, out int attributes);
        bool parsed = versionParsed &&
            formatVersion == ReverseDeltaService.CurrentFormatVersion &&
            originalLengthParsed && postLengthParsed &&
            retainedLengthParsed && timestampParsed && attributesParsed;
        if (!parsed)
            return;
        bool deltaExists = File.Exists(delta);
        bool baseExists = File.Exists(original);
        if (!installed && deltaExists && baseExists)
        {
            string? currentHash = TryHashFile(original);
            if (StringComparer.OrdinalIgnoreCase.Equals(currentHash, fields[6]))
                return; // Delta became durable, but the live replacement never occurred.
            installed = StringComparer.OrdinalIgnoreCase.Equals(currentHash, fields[7]);
        }
        bool exists = deltaExists && baseExists;
        entries[original] = new OperationFileEntry(
            original,
            delta,
            Relative(originalRoot, original),
            installed ? OperationEntryKind.Quarantined : OperationEntryKind.Planned,
            exists,
            false,
            RecoveryPayloadKind.ReverseDelta,
            retainedBytes,
            originalBytes,
            postEditBytes,
            fields[6],
            fields[7],
            delta,
            new DateTime(lastWriteTicks, DateTimeKind.Utc),
            (FileAttributes)attributes,
            fields.Length > 11 ? fields[11] : null);
    }

    private static string? TryHashFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
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
                if (Path.GetFileName(run).StartsWith(".MusicLibrary.App-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryGetRunTime(run, out var created) && !File.Exists(Path.Combine(run, "journal.tsv")))
                    continue;
                runs.Add(ReadSummary(tool, run, created));
            }
        }

        IReadOnlyList<OperationJournalSummary> grouped =
            GroupReviewedChangeTransactions(
                runs,
                warnings,
                ct);
        return new OperationJournalDiscoveryResult(
            grouped.OrderByDescending(run => run.CreatedAtUtc)
                .ThenBy(run => run.RunPath, PathComparer)
                .ToList(),
            warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<OperationJournalSummary>
        GroupReviewedChangeTransactions(
        IReadOnlyList<OperationJournalSummary> runs,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var manifests = new HashSet<string>(
            PathComparer);
        foreach (string runPath in runs
                     .Where(run =>
                         run.Kind ==
                         OperationJournalKind.ReviewedChange)
                     .Select(run => run.RunPath)
                     .Distinct(PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (string manifest in
                         Directory.EnumerateFiles(
                             runPath,
                             "reviewed-change-v2-*.tsv",
                             SearchOption.TopDirectoryOnly))
                    manifests.Add(
                        Path.GetFullPath(manifest));
            }
            catch (Exception error)
            {
                warnings.Add(
                    $"Could not inspect reviewed-change " +
                    $"coordinators in '{runPath}': " +
                    error.Message);
            }
        }
        if (manifests.Count == 0)
            return runs;

        var grouped =
            new List<OperationJournalSummary>(runs);
        foreach (string manifest in manifests
                     .OrderBy(path => path, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string[] lines =
                    File.ReadAllLines(manifest);
                string[][] fields =
                [
                    .. lines.Select(line =>
                        line.Split('\t')),
                ];
                string[] begin = fields.FirstOrDefault(
                    value =>
                        value.Length > 3 &&
                        value[0] == "BEGIN" &&
                        value[1] == "2") ??
                    throw new InvalidDataException(
                        "The coordinator BEGIN record is missing.");
                if (!Guid.TryParseExact(
                        begin[2],
                        "N",
                        out Guid id) ||
                    !long.TryParse(
                        begin[3],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long createdTicks))
                {
                    throw new InvalidDataException(
                        "The coordinator identity is invalid.");
                }
                string[] participantJournals =
                [
                    .. fields
                        .Where(value =>
                            value.Length > 2 &&
                            value[0] == "PARTICIPANT")
                        .OrderBy(value =>
                            int.Parse(
                                value[1],
                                CultureInfo.InvariantCulture))
                        .Select(value =>
                            Path.GetFullPath(value[2]))
                        .Distinct(PathComparer),
                ];
                if (participantJournals.Length == 0)
                    throw new InvalidDataException(
                        "The coordinator has no participants.");
                int applied = fields
                    .Where(value =>
                        value.Length > 1 &&
                        value[0] == "APPLIED")
                    .Select(value => value[1])
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                OperationJournalState state =
                    fields.Any(value =>
                        value.Length > 0 &&
                        value[0] == "COMMIT")
                        ? OperationJournalState.Completed
                        : fields.Any(value =>
                            value.Length > 0 &&
                            value[0] == "ROLLED_BACK")
                            ? OperationJournalState.RolledBack
                            : OperationJournalState.Interrupted;
                int affected = participantJournals
                    .Where(File.Exists)
                    .Sum(path =>
                        ParseMutationJournal(
                            File.ReadAllLines(path))
                        .Count);
                var transaction =
                    new ReviewedChangeTransactionSummary(
                        id,
                        manifest,
                        participantJournals,
                        applied);
                HashSet<string> participants =
                    participantJournals.ToHashSet(
                        PathComparer);
                grouped.RemoveAll(run =>
                    run.JournalPath is not null &&
                    participants.Contains(
                        Path.GetFullPath(
                            run.JournalPath)));
                grouped.Add(new(
                    "MusicLibraryManager",
                    OperationJournalKind.ReviewedChange,
                    state,
                    Path.GetDirectoryName(manifest)!,
                    manifest,
                    new DateTimeOffset(
                        new DateTime(
                            createdTicks,
                            DateTimeKind.Utc)),
                    affected,
                    transaction));
            }
            catch (Exception error)
            {
                warnings.Add(
                    $"Could not read reviewed-change " +
                    $"coordinator '{manifest}': " +
                    error.Message);
            }
        }
        return grouped;
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
            if (state == OperationJournalState.Interrupted &&
                CompactPreparationIsSafelyUnapplied(lines))
            {
                // A crash after DELTA_READY but before the atomic install left the original
                // untouched. Classifying this run as rolled back keeps it eligible for the
                // existing retention purge instead of protecting an unnecessary delta forever.
                state = OperationJournalState.RolledBack;
            }
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
        OperationJournalState terminal = OperationJournalState.Unknown;
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length == 0)
                continue;
            string operation = fields[0];
            string key = fields.Length > 1 ? fields[1] : "";
            switch (operation)
            {
                case "BEGIN":
                    active.Add(key);
                    break;
                case "COMMIT":
                    active.Remove(key);
                    terminal = OperationJournalState.Completed;
                    break;
                case "ROLLBACK":
                    active.Remove(key);
                    terminal = OperationJournalState.RolledBack;
                    break;
                case "ROLLBACK_FAILED":
                    active.Remove(key);
                    terminal = OperationJournalState.Interrupted;
                    break;
                case "QUARANTINE":
                case "STAGE_DELETE":
                case "MOVE":
                case "INSTALL":
                case "CREATE_REVERSIBLE":
                case "DELTA_READY":
                case "COMPACT_REPLACE":
                    if (fields.Length > 2) affected.Add(fields[2]);
                    break;
            }
        }
        var state = active.Count > 0
            ? OperationJournalState.Interrupted
            : terminal != OperationJournalState.Unknown
                ? terminal
                : lines.Length > 0
                    ? OperationJournalState.Interrupted
                    : OperationJournalState.Unknown;
        return (state, affected.Count);
    }

    private static bool CompactPreparationIsSafelyUnapplied(string[] lines)
    {
        string[][] records = lines
            .Select(line => line.Split('\t'))
            .ToArray();
        string[][] ready = records
            .Where(fields => fields.Length > 10 &&
                fields[0] == "DELTA_READY")
            .ToArray();
        if (ready.Length == 0 ||
            records.Any(fields => fields.Length > 0 &&
                fields[0] is
                    "QUARANTINE" or
                    "STAGE_DELETE" or
                    "MOVE" or
                    "INSTALL" or
                    "COMPACT_REPLACE"))
        {
            return false;
        }
        return ready.All(fields =>
            File.Exists(fields[2]) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                TryHashFile(fields[2]),
                fields[6]));
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
        "MusicLibraryManager" =>
            OperationJournalKind.ReviewedChange,
        _ => OperationJournalKind.Other,
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private enum RestoreJournalTerminal
    {
        None,
        Applied,
        Committed,
        Consumed,
        RolledBack,
        RollbackFailed,
    }

    private sealed record RestoreJournalAction(
        RecoveryPayloadKind PayloadKind,
        string SourcePath,
        string DestinationPath,
        string CollisionBackupPath,
        string? PreparedPath,
        string JournalPath,
        OperationRestoreDisposition Disposition,
        long ExpectedLength,
        string? ExpectedSha256);

    private sealed record RestoreJournalTransaction(
        string Path,
        bool IsBatch,
        IReadOnlyList<RestoreJournalAction> Actions,
        RestoreJournalTerminal Terminal);
}
