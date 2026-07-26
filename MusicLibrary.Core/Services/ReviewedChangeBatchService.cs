using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IReviewedChangeBatchService
{
    ReviewedChangeBatchPlan CreatePlan(
        IReadOnlyList<FileMutationPlan> participants);

    Task<ReviewedChangeBatchResult> ApplyAsync(
        ReviewedChangeBatchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ReviewedChangeReconciliationResult> ReconcilePendingAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Coordinates already-transactional per-volume mutation plans into one recoverable reviewed
/// change. Participant staging may happen in parallel, but the live commit order is frozen in the
/// plan and recorded before the first participant starts.
/// </summary>
public sealed class ReviewedChangeBatchService(
    IFileMutationPlanExecutor mutations,
    IOperationJournalService journals,
    IAppSettings? settings = null) : IReviewedChangeBatchService
{
    public const string PendingManifestPreference =
        "manager.reviewed-change.pending-manifests.v2";

    public ReviewedChangeBatchPlan CreatePlan(
        IReadOnlyList<FileMutationPlan> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        FileMutationPlan[] ordered =
        [
            .. participants
                .OrderBy(item => VolumeKey(item.DestinationRoot), PathComparer)
                .ThenBy(item => item.DestinationRoot, PathComparer),
        ];
        if (ordered.Length == 0)
            throw new ArgumentException(
                "A reviewed change must contain at least one participant.",
                nameof(participants));
        Guid id = Guid.NewGuid();
        string manifest = Path.Combine(
            ordered[0].RecoveryRoot,
            $"reviewed-change-v2-{id:N}.tsv");
        return new(
            id,
            [.. ordered],
            manifest,
            DateTimeOffset.UtcNow);
    }

    public async Task<ReviewedChangeBatchResult> ApplyAsync(
        ReviewedChangeBatchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException(
                "The reviewed change contains a blocking issue.");

        ReviewedChangeReconciliationResult reconciliation =
            await ReconcilePendingAsync(ct).ConfigureAwait(false);
        if (!reconciliation.BlockedManifests.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "A previous reviewed change could not be reconciled. " +
                "Resolve its recovery state before applying another reviewed change.");
        }
        ValidateAllParticipants(plan.Participants);
        IProgress<OperationProgress>? safeProgress =
            progress is null
                ? null
                : new SafeProgress<OperationProgress>(
                    progress);
        Directory.CreateDirectory(
            Path.GetDirectoryName(plan.CoordinatorManifestPath)!);
        string[] participantJournals =
        [
            .. plan.Participants.Select(participant =>
                Path.Combine(participant.RecoveryRoot, "journal.tsv")),
        ];
        WriteManifest(
            plan.CoordinatorManifestPath,
            [
                $"BEGIN\t2\t{plan.Id:N}\t" +
                plan.CreatedAtUtc.UtcDateTime.Ticks.ToString(
                    CultureInfo.InvariantCulture),
                .. participantJournals.Select((journal, index) =>
                    $"PARTICIPANT\t{index}\t{journal}"),
            ],
            createNew: true);
        AddPendingManifest(plan.CoordinatorManifestPath);

        var results = new List<FileMutationSummary>();
        try
        {
            for (int index = 0; index < plan.Participants.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                FileMutationPlan participant = plan.Participants[index];
                safeProgress?.Report(new(
                    OperationPhase.Applying,
                    index,
                    plan.Participants.Length,
                    participant.DestinationRoot,
                    $"Committing volume {index + 1:N0} of " +
                    $"{plan.Participants.Length:N0}"));
                FileMutationSummary result = await mutations.ApplyAsync(
                    participant,
                    safeProgress,
                    ct).ConfigureAwait(false);
                if (result.JournalPath is null)
                    throw new InvalidOperationException(
                        "A reviewed-change participant did not retain a recovery journal.");
                results.Add(result);
                WriteManifest(
                    plan.CoordinatorManifestPath,
                    [$"APPLIED\t{index}\t{result.JournalPath}"]);
            }

            // Writing COMMIT is the durable coordinator decision. Any failure
            // before it is eligible for rollback; nothing after it may reverse
            // the decided transaction.
            WriteManifest(
                plan.CoordinatorManifestPath,
                [$"COMMIT\t{plan.Id:N}"]);
        }
        catch (Exception applyError)
        {
            try
            {
                await RollBackParticipantsAsync(
                    plan.CoordinatorManifestPath,
                    CancellationToken.None).ConfigureAwait(false);
                WriteManifest(
                    plan.CoordinatorManifestPath,
                    [$"ROLLED_BACK\t{plan.Id:N}"]);
                RemovePendingManifest(plan.CoordinatorManifestPath);
            }
            catch (Exception rollbackError)
            {
                TryWriteManifest(
                    plan.CoordinatorManifestPath,
                    $"ROLLBACK_FAILED\t{plan.Id:N}\t{rollbackError.Message}");
                throw new AggregateException(
                    "The reviewed change failed and automatic reconciliation was incomplete.",
                    applyError,
                    rollbackError);
            }
            throw;
        }

        // Cancellation and incidental UI/settings failures are intentionally
        // ignored after COMMIT. Startup reconciliation recognizes COMMIT and
        // can clear a retained pending marker safely.
        TryRemovePendingManifest(
            plan.CoordinatorManifestPath);
        try
        {
            safeProgress?.Report(new(
                OperationPhase.Completed,
                plan.Participants.Length,
                plan.Participants.Length,
                Message: "Reviewed change committed"));
        }
        catch
        {
        }
        return new(
            plan.Id,
            [.. results],
            [.. results.Select(item => item.JournalPath!)],
            plan.CoordinatorManifestPath);
    }

    public async Task<ReviewedChangeReconciliationResult> ReconcilePendingAsync(
        CancellationToken ct = default)
    {
        string[] pending =
        [
            .. LoadPendingManifests()
                .Distinct(PathComparer),
        ];
        if (pending.Length == 0)
            return new(0, 0, 0, []);
        int rolledBack = 0;
        int committed = 0;
        var blocked = new HashSet<string>(PathComparer);
        foreach (string manifest in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(manifest))
            {
                // A missing path can mean that the coordinator volume is temporarily offline.
                // Retain the durable pointer and fail closed rather than forgetting a potentially
                // partially applied transaction on the remaining participant volumes.
                blocked.Add(manifest);
                continue;
            }
            string[] lines = File.ReadAllLines(manifest);
            if (lines.Any(line =>
                    line.StartsWith("COMMIT\t", StringComparison.Ordinal)))
            {
                committed++;
                RemovePendingManifest(manifest);
                continue;
            }
            if (lines.Any(line =>
                    line.StartsWith("ROLLED_BACK\t", StringComparison.Ordinal)))
            {
                rolledBack++;
                RemovePendingManifest(manifest);
                continue;
            }
            try
            {
                await RollBackParticipantsAsync(manifest, ct)
                    .ConfigureAwait(false);
                WriteManifest(manifest, ["ROLLED_BACK\tRECONCILED"]);
                rolledBack++;
                RemovePendingManifest(manifest);
            }
            catch
            {
                blocked.Add(manifest);
            }
        }
        return new(
            pending.Length,
            rolledBack,
            committed,
            [.. blocked.OrderBy(item => item, PathComparer)]);
    }

    private async Task RollBackParticipantsAsync(
        string manifestPath,
        CancellationToken ct)
    {
        string[] lines = File.ReadAllLines(manifestPath);
        string[] explicitlyAppliedJournals =
        [
            .. lines
                .Select(line => line.Split('\t'))
                .Where(fields =>
                    fields.Length > 2 &&
                    fields[0] == "APPLIED")
                .Select(fields => fields[2]),
        ];
        string? unavailableAppliedJournal =
            explicitlyAppliedJournals.FirstOrDefault(
                path => !IsCommittedMutationJournal(path));
        if (unavailableAppliedJournal is not null)
        {
            throw new IOException(
                "A committed reviewed-change participant is " +
                "unavailable for reconciliation: " +
                unavailableAppliedJournal);
        }
        string[] participantJournals =
        [
            .. lines
                .Select(line => line.Split('\t'))
                .Where(fields =>
                    fields.Length > 2 &&
                    fields[0] == "PARTICIPANT")
                .OrderBy(fields => int.Parse(
                    fields[1],
                    CultureInfo.InvariantCulture))
                .Select(fields => fields[2])
                .Where(IsCommittedMutationJournal),
        ];
        if (participantJournals.Length == 0)
            return;

        var plans = new List<OperationRestorePlan>();
        foreach (string journalPath in participantJournals.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            string runPath = Path.GetDirectoryName(journalPath)!;
            var summary = new OperationJournalSummary(
                "MusicLibraryManager",
                OperationJournalKind.Other,
                OperationJournalState.Completed,
                runPath,
                journalPath,
                File.GetCreationTimeUtc(journalPath),
                null);
            OperationBrowseResult browse =
                await journals.BrowseAsync(summary, ct).ConfigureAwait(false);
            OperationFileEntry[] candidates =
            [
                .. browse.Entries.Where(entry =>
                    entry.Exists &&
                    entry.CurrentPath is not null &&
                    entry.Kind is
                        OperationEntryKind.Quarantined or
                        OperationEntryKind.Moved or
                        OperationEntryKind.Created),
            ];
            OperationRestorePlan restore =
                await journals.PreviewRestoreAsync(
                    summary,
                    candidates,
                    ct).ConfigureAwait(false);
            if (!restore.CanApply)
            {
                throw new InvalidOperationException(
                    "A committed reviewed-change participant has no usable recovery payload: " +
                    journalPath);
            }
            plans.Add(restore);
        }
        if (plans.Count != participantJournals.Length)
            throw new InvalidOperationException(
                "Every committed reviewed-change participant must have exactly one usable " +
                "recovery plan before rollback can begin.");
        OperationRestoreBatchPlan batch =
            await journals.PreviewRestoreBatchAsync(plans, ct)
                .ConfigureAwait(false);
        await journals.ApplyRestoreBatchAsync(batch, ct: ct)
            .ConfigureAwait(false);
    }

    private static bool IsCommittedMutationJournal(string path)
    {
        if (!File.Exists(path))
            return false;
        return File.ReadLines(path).Any(line =>
            line.StartsWith("COMMIT\t", StringComparison.Ordinal));
    }

    private static void ValidateAllParticipants(
        IReadOnlyList<FileMutationPlan> participants)
    {
        var destinations = new HashSet<string>(PathComparer);
        foreach (FileMutationPlan participant in participants)
        {
            foreach (FileMutationAction action in participant.Actions)
            {
                ValidateSnapshot(action.ExpectedSource);
                ValidateSnapshot(action.ExpectedDestination);
                if (action.Kind is
                        FileMutationKind.Copy or
                        FileMutationKind.Move or
                        FileMutationKind.Replace or
                        FileMutationKind.Write or
                        FileMutationKind.ReplaceGenerated &&
                    !destinations.Add(Path.GetFullPath(action.DestinationPath)))
                {
                    throw new InvalidOperationException(
                        $"A reviewed change targets '{action.DestinationPath}' more than once.");
                }
            }
        }
    }

    private static void ValidateSnapshot(OperationPathSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        string path = snapshot.Path ?? throw new InvalidOperationException(
            "Reviewed-change snapshots must include their normalized path.");
        bool exists = File.Exists(path);
        if (exists != snapshot.Exists)
            throw new InvalidOperationException(
                $"A reviewed path changed before commit: {path}");
        if (!exists)
            return;
        var info = new FileInfo(path);
        if (info.Length != snapshot.Length ||
            info.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
        {
            throw new InvalidOperationException(
                $"A reviewed path changed before commit: {path}");
        }
    }

    private string[] LoadPendingManifests()
    {
        if (settings is null)
            return [];
        try
        {
            string? json = settings.GetPreference(PendingManifestPreference);
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void AddPendingManifest(string path)
    {
        if (settings is null)
            return;
        string[] value =
        [
            .. LoadPendingManifests()
                .Append(Path.GetFullPath(path))
                .Distinct(PathComparer),
        ];
        settings.SetPreference(
            PendingManifestPreference,
            JsonSerializer.Serialize(value));
    }

    private void RemovePendingManifest(string path)
    {
        if (settings is null)
            return;
        string[] value =
        [
            .. LoadPendingManifests()
                .Where(item => !PathComparer.Equals(item, path)),
        ];
        settings.SetPreference(
            PendingManifestPreference,
            value.Length == 0
                ? null
                : JsonSerializer.Serialize(value));
    }

    private void TryRemovePendingManifest(
        string path)
    {
        try
        {
            RemovePendingManifest(path);
        }
        catch
        {
        }
    }

    private static void WriteManifest(
        string path,
        IEnumerable<string> lines,
        bool createNew = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            createNew ? FileMode.CreateNew : FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false));
        foreach (string line in lines)
            writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void TryWriteManifest(string path, string line)
    {
        try
        {
            WriteManifest(path, [line]);
        }
        catch
        {
        }
    }

    private static string VolumeKey(string path) =>
        Path.GetPathRoot(Path.GetFullPath(path)) ??
        Path.GetDirectoryName(Path.GetFullPath(path))!;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class SafeProgress<T>(
        IProgress<T> inner) : IProgress<T>
    {
        public void Report(T value)
        {
            try
            {
                inner.Report(value);
            }
            catch
            {
                // Progress observers cannot participate in transaction
                // success or rollback decisions.
            }
        }
    }
}
