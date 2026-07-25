using System.Collections.Immutable;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IReviewedChangeHistoryService
{
    IReadOnlyList<ReviewedChangeHistoryEntry> Entries { get; }
    IReadOnlyList<ReviewedChangeHistoryEntry> RedoEntries { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Record(ReviewedChangeHistoryEntry entry);
    Task<ReviewedChangeUndoResult> UndoLatestAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default);
    Task ReconcilePendingAsync(CancellationToken ct = default);
}

/// <summary>
/// Versioned history for reviewed transcode transactions. It intentionally does not reuse the
/// v1 metadata history identity; older releases therefore ignore these records safely.
/// </summary>
public sealed class ReviewedChangeHistoryService :
    IReviewedChangeHistoryService
{
    public const string Preference =
        "manager.workbench.reviewed-history.v2";

    private readonly IAppSettings _settings;
    private readonly IOperationJournalService _journals;
    private readonly IReindexService? _reindex;
    private readonly string? _durableDirectory;
    private readonly State _state;

    public ReviewedChangeHistoryService(
        IAppSettings settings,
        IOperationJournalService journals,
        IReindexService? reindex = null,
        string? durableDirectory = null)
    {
        _settings = settings;
        _journals = journals;
        _reindex = reindex;
        _durableDirectory = durableDirectory ??
            (settings is AppSettings
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder
                            .ApplicationData),
                    "MusicLibraryTools",
                    "reviewed-history-v2")
                : null);
        _state = Load(settings);
        RecoverDurableEntries();
    }

    public IReadOnlyList<ReviewedChangeHistoryEntry> Entries =>
        _state.Undo;

    public IReadOnlyList<ReviewedChangeHistoryEntry> RedoEntries =>
        _state.Redo;

    public bool CanUndo => _state.Undo.Count > 0;

    public bool CanRedo => _state.Redo.Count > 0;

    public void Record(ReviewedChangeHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry with
        {
            RedoRequests = entry.EffectiveRedoRequests,
            IndexedSourcePaths =
                entry.IndexedSourcePaths.IsDefault
                    ? []
                    : entry.IndexedSourcePaths,
        };
        _state.Undo.Insert(0, entry);
        if (_state.Undo.Count > 100)
            _state.Undo.RemoveRange(100, _state.Undo.Count - 100);
        _state.Redo.Clear();
        string? durablePath =
            TryWriteDurableEntry(entry);
        try
        {
            PersistRequired();
            TryDelete(durablePath);
        }
        catch
        {
            // The durable entry keeps restart-safe Undo available. A
            // committed filesystem transaction is not a failed Apply merely
            // because roaming settings could not be updated.
        }
    }

    public async Task<ReviewedChangeUndoResult> UndoLatestAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await ReconcilePendingAsync(ct).ConfigureAwait(false);
        if (_state.Pending is not null)
            throw new InvalidOperationException(
                "The previous reviewed-change Undo could not be reconciled. " +
                "History and recovery data were retained.");
        if (_state.Undo.Count == 0)
            return new(Guid.Empty, 0, []);

        ReviewedChangeHistoryEntry entry = _state.Undo[0];
        List<OperationRestorePlan> plans =
            await CreateRestorePlansAsync(entry, ct).ConfigureAwait(false);
        if (plans.Count == 0)
            throw new InvalidOperationException(
                "No retained recovery payload is available for the latest reviewed change.");
        OperationRestoreBatchPlan batch =
            await _journals.PreviewRestoreBatchAsync(plans, ct)
                .ConfigureAwait(false);
        _state.Pending = new(
            entry.Id,
            ReviewedHistoryTransitionStage.Prepared,
            [.. plans.Select(plan => plan.RestoreJournalPath)]);
        PersistRequired();

        try
        {
            OperationRestoreBatchResult result =
                await _journals.ApplyRestoreBatchAsync(
                    batch,
                    progress,
                    ct).ConfigureAwait(false);
            _state.Pending = _state.Pending with
            {
                Stage = ReviewedHistoryTransitionStage.Committed,
            };
            PersistRequired();
            await CompletePendingAsync(entry)
                .ConfigureAwait(false);
            return new(
                entry.Id,
                result.RestoredCount,
                result.RestoreJournalPaths.ToImmutableArray());
        }
        catch
        {
            try
            {
                OperationRestoreTransitionState state =
                    await _journals.ReconcileRestoreBatchAsync(
                        _state.Pending.RestoreJournalPaths,
                        CancellationToken.None).ConfigureAwait(false);
                if (state is
                    OperationRestoreTransitionState.Committed or
                    OperationRestoreTransitionState.Consumed)
                {
                    _state.Pending = _state.Pending with
                    {
                        Stage = ReviewedHistoryTransitionStage.Committed,
                    };
                    PersistRequired();
                    await CompletePendingAsync(entry)
                        .ConfigureAwait(false);
                }
                else
                {
                    _state.Pending = null;
                    PersistRequired();
                }
            }
            catch
            {
                TryPersist();
            }
            throw;
        }
    }

    public async Task ReconcilePendingAsync(CancellationToken ct = default)
    {
        PendingTransition? pending = _state.Pending;
        if (pending is null)
            return;
        ReviewedChangeHistoryEntry? entry =
            _state.Undo.FirstOrDefault(item => item.Id == pending.EntryId);
        if (entry is null)
        {
            _state.Pending = null;
            PersistRequired();
            return;
        }
        OperationRestoreTransitionState state =
            pending.Stage == ReviewedHistoryTransitionStage.Committed
                ? OperationRestoreTransitionState.Committed
                : await _journals.ReconcileRestoreBatchAsync(
                    pending.RestoreJournalPaths,
                    ct).ConfigureAwait(false);
        switch (state)
        {
            case OperationRestoreTransitionState.Committed:
            case OperationRestoreTransitionState.Consumed:
                await CompletePendingAsync(entry)
                    .ConfigureAwait(false);
                break;
            case OperationRestoreTransitionState.Unapplied:
                _state.Pending = null;
                PersistRequired();
                break;
        }
    }

    private async Task<List<OperationRestorePlan>> CreateRestorePlansAsync(
        ReviewedChangeHistoryEntry entry,
        CancellationToken ct)
    {
        var plans = new List<OperationRestorePlan>();
        foreach (string journalPath in entry.JournalPaths.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            string runPath = Path.GetDirectoryName(journalPath)!;
            var summary = new OperationJournalSummary(
                "MusicLibraryManager",
                OperationJournalKind.Other,
                OperationJournalState.Completed,
                runPath,
                journalPath,
                entry.AppliedAtUtc,
                entry.SourcePaths.Length);
            OperationBrowseResult browse =
                await _journals.BrowseAsync(summary, ct).ConfigureAwait(false);
            OperationFileEntry[] candidates =
            [
                .. browse.Entries.Where(item =>
                    item.Exists &&
                    item.CurrentPath is not null &&
                    item.Kind is
                        OperationEntryKind.Quarantined or
                        OperationEntryKind.Moved or
                        OperationEntryKind.Created),
            ];
            OperationRestorePlan plan =
                await _journals.PreviewRestoreAsync(
                    summary,
                    candidates,
                    ct).ConfigureAwait(false);
            if (plan.CanApply)
                plans.Add(plan);
        }
        return plans;
    }

    private async Task CompletePendingAsync(
        ReviewedChangeHistoryEntry entry)
    {
        await RefreshInternalCatalogAfterUndoAsync(entry)
            .ConfigureAwait(false);
        _state.Undo.RemoveAll(item => item.Id == entry.Id);
        if (_state.Redo.All(item => item.Id != entry.Id))
            _state.Redo.Insert(0, entry);
        if (_state.Redo.Count > 100)
            _state.Redo.RemoveRange(100, _state.Redo.Count - 100);
        _state.Pending = null;
        PersistRequired();
    }

    private async Task RefreshInternalCatalogAfterUndoAsync(
        ReviewedChangeHistoryEntry entry)
    {
        if (_reindex is null ||
            entry.IndexedSourcePaths.IsDefaultOrEmpty)
            return;
        HashSet<string> sources =
            entry.SourcePaths.ToHashSet(PathComparer);
        foreach (string destination in
                 entry.DestinationPaths
                     .Where(path =>
                         !sources.Contains(path))
                     .Distinct(PathComparer))
        {
            try
            {
                await _reindex.RemoveIndexedFileAsync(
                        destination,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Filesystem recovery is already committed. A later targeted
                // refresh or normal index pass can repair a stale cache row.
            }
        }
        foreach (string source in
                 entry.IndexedSourcePaths
                     .Where(File.Exists)
                     .Distinct(PathComparer))
        {
            try
            {
                await _reindex.ReindexFileAsync(
                        source,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The exact filesystem restore is already committed; a later
                // targeted refresh or normal index pass can repair the cache.
            }
        }
    }

    private static State Load(IAppSettings settings)
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            State? state = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<State>(json);
            if (state is null)
                return new([], []);
            Normalize(state.Undo);
            Normalize(state.Redo);
            return state;
        }
        catch
        {
            return new([], []);
        }
    }

    private static void Normalize(
        List<ReviewedChangeHistoryEntry> entries)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            ReviewedChangeHistoryEntry entry = entries[index];
            if (entry.RedoRequests.IsDefaultOrEmpty)
                entries[index] = entry with
                {
                    RedoRequests = [entry.RedoRequest],
                    IndexedSourcePaths =
                        entry.IndexedSourcePaths.IsDefault
                            ? []
                            : entry.IndexedSourcePaths,
                };
            else if (entry.IndexedSourcePaths.IsDefault)
                entries[index] = entry with
                {
                    IndexedSourcePaths = [],
                };
        }
    }

    private void PersistRequired() =>
        _settings.SetPreference(
            Preference,
            JsonSerializer.Serialize(_state));

    private void TryPersist()
    {
        try
        {
            PersistRequired();
        }
        catch
        {
        }
    }

    private void RecoverDurableEntries()
    {
        if (_durableDirectory is null ||
            !Directory.Exists(_durableDirectory))
            return;
        var recoveredPaths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
                     _durableDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                ReviewedChangeHistoryEntry? entry =
                    JsonSerializer.Deserialize<
                        ReviewedChangeHistoryEntry>(
                        File.ReadAllText(path));
                if (entry is null ||
                    !File.Exists(
                        entry.CoordinatorManifestPath) ||
                    !File.ReadLines(
                            entry.CoordinatorManifestPath)
                        .Any(line =>
                            line.StartsWith(
                                "COMMIT\t",
                                StringComparison.Ordinal)))
                    continue;
                entry = NormalizeEntry(entry);
                if (_state.Undo.All(item =>
                        item.Id != entry.Id) &&
                    _state.Redo.All(item =>
                        item.Id != entry.Id))
                    _state.Undo.Insert(0, entry);
                recoveredPaths.Add(path);
            }
            catch
            {
            }
        }
        if (recoveredPaths.Count == 0)
            return;
        try
        {
            PersistRequired();
            foreach (string path in recoveredPaths)
                TryDelete(path);
        }
        catch
        {
            // Keep spool files until settings persistence is healthy.
        }
    }

    private string? TryWriteDurableEntry(
        ReviewedChangeHistoryEntry entry)
    {
        if (_durableDirectory is null)
            return null;
        try
        {
            Directory.CreateDirectory(
                _durableDirectory);
            string path = Path.Combine(
                _durableDirectory,
                entry.Id.ToString("N") +
                ".json");
            string temporary = path + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(entry));
            File.Move(
                temporary,
                path,
                overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(
        string? path)
    {
        try
        {
            if (path is not null &&
                File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static ReviewedChangeHistoryEntry
        NormalizeEntry(
        ReviewedChangeHistoryEntry entry) =>
        entry with
        {
            RedoRequests = entry.EffectiveRedoRequests,
            IndexedSourcePaths =
                entry.IndexedSourcePaths.IsDefault
                    ? []
                    : entry.IndexedSourcePaths,
        };

    private enum ReviewedHistoryTransitionStage
    {
        Prepared = 0,
        Committed = 1,
    }

    private sealed record PendingTransition(
        Guid EntryId,
        ReviewedHistoryTransitionStage Stage,
        ImmutableArray<string> RestoreJournalPaths);

    private sealed class State(
        List<ReviewedChangeHistoryEntry> undo,
        List<ReviewedChangeHistoryEntry> redo)
    {
        public List<ReviewedChangeHistoryEntry> Undo { get; set; } = undo;
        public List<ReviewedChangeHistoryEntry> Redo { get; set; } = redo;
        public PendingTransition? Pending { get; set; }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
