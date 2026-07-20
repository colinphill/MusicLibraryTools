# Core Service Migration Plan

This document records the staged replacement of the legacy command-line job integration.

## Migration status

Checkpoint completed 2026-07-15:

- Removed all linked executable `Program.cs` files and the `MUSIC_LIBRARY_CORE` build mode from
  `MusicLibrary.Core`.
- Added typed operation progress/issues, one-pass buffered file inventory, immutable filesystem
  snapshots, and a stale-validating journaled mutation-plan executor with rollback.
- Migrated `CrossSyncMusic` to `ICrossLibrarySyncService`. Its CLI and the Operations tab use the
  same preview plan for apply.
- Migrated `CrossSyncPlaylists` to `IPlaylistExportService`, including repeatable set-filtered
  targets. Rendered M3U/WPL bytes are stored in the reviewed plan rather than regenerated at apply.
- Migrated redundancy analysis and ITL validation to `IRedundancyAnalysisService` and
  `IItunesValidationService`.
- Migrated `FixArtwork` to `IArtworkNormalizationService`. Preview retains the exact encoded JPEG
  bytes and media/artwork identities; apply revalidates the complete plan, journals durable media
  and ITL backups, verifies each written tag, and rolls back the whole operation on failure. The
  CLI and Operations tab both consume the typed service.
- Replaced the managed ADB sync implementation with a typed `IDeviceSyncService` adapter over the
  packaged native `syncer` runtime. The service persists the structured dry-run artifact and applies
  that reviewed action list without repeating either full inventory, enforces removal limits,
  reports and persists the device-side recovery run, exposes one-click native restore, and requests
  cooperative cancellation over redirected standard input before using a timed kill fallback.
- Migrated `UpdateSmartStorage` to `ISmartStorageService`. Preview preserves stable bucket/name
  assignments, lazily reuses the existing artwork catalog, records exact M3U/XML/binary output
  bytes, and plans initialization, media changes, stale-track/playlist quarantine, and the database
  pair as one stale-validating journaled transaction. Its CLI and the Operations tab use that plan.
- Migrated `UpdateCarCard` to `ICarCardService`. Core now owns the compatible balanced-path and
  `syncdb.xml` domain model, desired media projection, exact M3U generation, collision/removal
  safeguards, and journaled copy/quarantine execution. Rebalancing is planned as recoverable
  copy-plus-quarantine path changes, missing target files are naturally repaired by desired-state
  comparison, and the CLI and Operations tab consume the same reviewed plan.
- Migrated `AnalyzeMetadata` to the existing Core indexing, cross-set comparison, artist
  reconciliation, and typed analyzer services. The executable now owns only check selection,
  interactive canonical-artist selection, rendering, and exit codes.
- Migrated `OrganizeFiles` to `ILibraryOrganizer`. The CLI now indexes, previews, and applies
  through `LibraryService`; canonical path planning, exclusion policy, recovery journaling,
  full-plan stale validation, rollback, filesystem mutation, cleanup, and cache synchronization
  remain in Core.
- Reduced `UnifiedJobService` to a read-only catalog containing only operations backed by typed
  services. Core no longer redirects or writes process-wide console streams.
- Replaced the Operations tab's raw command-line argument editor with operation-specific paths,
  browse controls, switches, and removal-limit fields. The ViewModel translates those controls
  directly into typed service requests; argument parsing remains a CLI-adapter concern.

Migration completed 2026-07-15. Follow-up work should be tracked as normal feature work rather
than extending the legacy migration:

1. Run device acceptance tests against representative removable storage and ADB targets.
2. Add format-fixture regression tests when anonymized production `syncdb.xml` samples are
   available.

## Objective

Rewrite the library-operation algorithms as typed, independently testable services in
`MusicLibrary.Core`. `MusicLibraryManager` and the existing command-line executables will call those
same services. The command-line projects will contain only option parsing, progress rendering,
result rendering, and exit-code mapping.

The current native-job implementation is an interim approach and is not the target architecture.
In particular, the final implementation must not:

- compile linked copies of executable `Program.cs` files into `MusicLibrary.Core`;
- use `MUSIC_LIBRARY_CORE` conditional compilation;
- call command-line `Program.Run` methods from Core;
- redirect global `Console.Out` or `Console.Error` to obtain operation results;
- pass raw command-line argument arrays through Core APIs; or
- preview by running an algorithm once without `--apply` and then recomputing its decisions during
  apply.

## Target services

| Existing executable | Core service |
| --- | --- |
| CrossSyncPlaylists | `IPlaylistExportService` |
| CrossSyncMusic | `ICrossLibrarySyncService` |
| AndroidSync | `IDeviceSyncService` |
| UpdateCarCard | `ICarCardService` |
| UpdateSmartStorage | `ISmartStorageService` |
| FixArtwork | `IArtworkNormalizationService` |
| CheckRedundancies | `IRedundancyAnalysisService` |
| ITL validation | `IItunesValidationService` |
| AnalyzeMetadata | `ILibraryService`, `IArtistReconciler`, typed `LibraryAnalyzer` reports |
| OrganizeFiles | `ILibraryOrganizer` |

Mutable services will expose typed preview/apply methods. For example:

```csharp
public interface ICrossLibrarySyncService
{
    Task<CrossLibrarySyncPlan> PreviewAsync(
        CrossLibrarySyncRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<CrossLibrarySyncResult> ApplyAsync(
        CrossLibrarySyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}
```

Requests, plans, actions, issues, and results will be strongly typed. Exit codes and console text
are presentation concerns owned by the CLI adapters.

## Shared infrastructure

The services should reuse focused infrastructure instead of a generic legacy-job runner:

- `ILibraryOperationContextFactory`: load configuration, metadata cache, and ITL data once and
  expose normalized roots and indexed track lookups.
- `IFileInventoryService`: capture a destination tree and file snapshots in one buffered traversal,
  with behavior suitable for high-latency network shares.
- `IFileMutationPlanExecutor`: validate snapshots and execute atomic copy, replace, move,
  quarantine, directory, and generated-file actions under a recovery journal.
- `ISyncerProcessRunner`: invoke the packaged native Android sync runtime without exposing process
  details to application ViewModels.
- `OperationProgress`: report phase, completed count, total count, current path, and message without
  using global console state.
- Shared typed models such as `OperationIssue`, `OperationPathSnapshot`, `FileMutationAction`, and
  `FileMutationSummary`.

Planning should be deterministic and free of destination mutations after the input inventories have
been loaded:

```text
typed request
    -> load source context and destination inventory once
    -> project the desired state
    -> compute an immutable delta plan
    -> review
    -> stale validation and journaled execution
    -> typed result
```

Apply consumes the reviewed plan. It may deterministically regenerate planned output, but it must
not rescan and choose a different set of actions from the original request.

## Service boundaries

### Playlist export

Separate playlist selection/mapping, M3U/WPL rendering, sanitized-name collision detection, and
filesystem execution. The plan records rendered outputs, missing or ineligible tracks, writes, and
obsolete managed playlists to quarantine when cleanup is requested.

### Cross-library synchronization

Separate destination inventory, playlist/source mapping, desired-path calculation, collision and
safety validation, delta planning, and execution. The plan records copies, replacements, unchanged
files, quarantines, missing inputs, overlap errors, snapshots, and removal-limit blockers.

### Device synchronization

Keep Android transport, hashing, delta planning, and device mutation in the native `syncer`
runtime. The Core adapter requests a deterministic structured plan, preserves its digest for apply,
and maps actions, issues, progress, and recovery metadata into typed application models.

### Car-card update

Keep the car-card format as a distinct domain service. Split it into catalog building, balanced-path
planning, database planning, playlist rendering, and journaled execution rather than forcing it into
a generic directory-sync algorithm.

### Smart storage

Keep smart-storage projection and database formats domain-specific. Separate library projection,
artwork catalog generation, playlist rendering, database serialization, and plan execution.

### Artwork normalization

Build on the existing artwork and media-writing infrastructure. Preview records media snapshots,
current artwork characteristics, proposed encoding characteristics, issues, and expected ITL cache
updates. Apply revalidates the file and artwork identity, writes and verifies the normalized image,
then saves a validated ITL update.

### Redundancy analysis

Return typed redundancy groups suitable for both the Analyze UI and CLI rendering. Do not write
directly to a log or console.

### ITL validation

Keep the low-level validator in ITLTools, where that domain logic belongs. A Core service loads the
document and returns typed issues and summary counts. The same rule applies to media parsers and the
metadata database: Core owns workflow and policy without duplicating lower-level parsing code.

## Application and CLI integration

The Operations tab uses operation-specific typed inputs instead of a raw arguments text box. It
displays structured summaries, blockers, warnings, and planned actions; its text output is
supplemental rather than an execution interface.

`UnifiedJobService` should either be removed or reduced to a read-only operation catalog. It must
not execute argument strings.

Each executable should become a thin adapter:

```text
parse options -> construct typed request -> call Core service -> render progress/result -> exit code
```

No synchronization algorithm, media rewrite, database generation, static logging, or conditional
Core compilation should remain in an executable project.

## Migration order

1. Remove the linked-source/preprocessor architecture when the first replacement is ready.
2. Add shared operation models, inventory, planning, and execution infrastructure.
3. Implement `ICrossLibrarySyncService` as the reference vertical slice.
4. Implement playlist export.
5. Implement redundancy analysis, ITL validation, and artwork normalization.
6. Implement device sync with local and ADB endpoints.
7. Implement smart storage.
8. Implement car-card last because it has the largest specialized planning surface.
9. Add typed Operations-tab editors as services become available.
10. Reduce each CLI to its final adapter only after characterization and parity tests pass.

The legacy executables may remain temporarily as reference implementations during migration, but
Core and the App must not invoke them.

## Completion verification

Verified 2026-07-15:

- the portable solution builds in Release with zero warnings and zero errors;
- all 476 tests pass (248 Core/App, 179 media utilities, and 49 ITL tests);
- Core contains no CLI `Program`, `Console`, or `LogConsole` integration;
- the Operations ViewModel contains no command-line parsing or raw argument field;
- every catalog entry resolves to a typed Core service; and
- `git diff --check` reports no whitespace errors.

## Completion criteria

- `MusicLibrary.Core` builds when all command-line source directories are unavailable.
- Core has no references to CLI `Program` classes, `Console`, or `LogConsole`.
- No Core service accepts command-line argument strings.
- Preview returns an exact, immutable, inspectable plan.
- Apply validates the reviewed plan for staleness before its first mutation.
- Mutations are atomic or journaled and recoverable.
- Algorithms provide cancellation and structured progress.
- The App and corresponding CLI call the same typed service.
- Tests preserve overlap prevention, destination collision detection, removal limits,
  missing-source aborts, quarantine, atomic writes, and rollback behavior.
- Network inventories use one traversal, dictionary-based matching, and bounded I/O concurrency.
