# MusicLibraryManager Memory Usage Audit

Branch: `feature/musiclibrarymanager-memory-audit`

Baseline commit: `5c101f1`

Audit date: 2026-07-27

Last updated: 2026-07-27 after final Release and UI validation

Implementation status: Complete

Catalog paths and metadata values: intentionally omitted

## Outcome

The compact Library implementation meets every agreed memory gate on the
111,302-track production catalog. It does not change the database/index schema,
scan behavior, library configuration, or rescan behavior.

| Measurement | Budget | Measured result | Status |
| --- | ---: | ---: | --- |
| Live managed heap, loaded Library idle | 1.0 GiB | 156.5 MiB | Pass |
| Steady private memory, loaded Library | 1.5 GiB | 707.6–771.7 MiB | Pass |
| Private-memory peak during application load | 2.0 GiB | 938.5 MiB | Pass |
| Managed growth after five filter/scroll/navigation cycles | 100 MiB | 13.1 MiB | Pass |
| Retained row-state overhead, excluding input strings | 1.5 KiB/row | 422.8 bytes/row | Pass |

The headless production-catalog probe independently measured 99.6 MiB live
managed memory, 166.9 MiB private memory, a 176.2 MiB peak, and 55.5 MiB
managed growth across five forced browse cycles. The GUI measurements above are
the acceptance results because they also include Avalonia, Skia, and the actual
visual tree.

## Anonymized catalog baseline

The active catalog was inspected read-only. No paths, tags, fingerprints, or
artwork bytes were copied into this report.

| Catalog property | Baseline |
| --- | ---: |
| Indexed files | 111,302 |
| Metadata values | 2,188,663 |
| Average metadata values per file | 19.66 |
| Metadata text as UTF-8 | approximately 290 MiB |
| AcoustID fingerprint text as UTF-8 | 213.0 MiB |
| SQLite database size | 1,055 MiB |
| Deduplicated artwork data in SQLite | 512.7 MiB |
| Configured index roots | 13 |
| Roots configured for eager artwork reads | 0 |
| Configured Library custom metadata columns | 0 |

The user-observed baseline was more than 4 GiB of process memory with the
Library loaded.

## Diagnostic method

- Built and measured the Release win-x64 application.
- Captured Home after refresh, Library load/idle, filtering/sorting/thumbnail
  scrolling, five Home/Library cycles, and a representative Metadata
  Inconsistencies Health run.
- Used `dotnet-gcdump` for collection-triggering managed captures and
  `dotnet-dump` for heap segment/type inspection.
- Compared managed live/committed memory with process private memory to
  distinguish application object retention from native UI, graphics, runtime,
  and SQLite allocations.
- Installed diagnostic tools temporarily outside the repository. All `.gcdump`
  and `.dmp` captures and the temporary tool directory were permanently deleted
  after the aggregate measurements were recorded.

The locally available `dotnet-counters` version, 9.0.661903, throws an internal
`NullReferenceException` when attaching to the .NET 10 process. Counter data was
therefore corroborated with process samples, GC dumps, and full dumps rather
than being used as an acceptance source.

## Heap findings

The original retention design multiplied the catalog several times:

- `LibraryService.GetAllRecordsAsync` constructed a complete `MetadataCache`,
  including secondary indexes, before creating `TrackRecord` objects.
- Every browse row retained every metadata value, including the 213 MiB
  AcoustID fingerprint field even though no Library column requested it.
- Every row allocated metadata display/original maps and copied value arrays.
- Plain search retained an all-column concatenated search string for every row.
- Home loaded the complete catalog merely to calculate three counts.
- Reload could retain the old and replacement row sets together.

Before the final lazy-map correction, application-owned heap families included
approximately 27.2 MiB of `LibraryRow`, 22.1 MiB of `TrackRecord`, 9.34 MiB of
otherwise-empty metadata maps, 6.85 MiB of per-row event delegates, and 3.4 MiB
of detail-row state. After correction, the empty map family disappeared and
the event-handler family fell to approximately 0.46 MiB in the GUI process.

The final full dump reported 195,219,456 bytes (186.2 MiB) of committed GC
memory. The remaining private-memory gap is primarily runtime/Avalonia/Skia and
SQLite state. It did not grow across repeated filtering and thumbnail
scrolling. Thumbnail eviction, reset, cancellation, and view teardown now all
dispose decoded images deterministically.

## Implemented architecture

### Catalog projections

- `GetLibrarySummaryAsync` computes Home counts without materializing tracks,
  arbitrary metadata, or artwork.
- `GetBrowseRecordsAsync` queries compact scalar rows directly from
  `MetadataSummaryView`. It includes only the structured browse fields and the
  small known fields needed by ordinary columns.
- `GetAllRecordsAsync` remains compatible and now projects records directly
  without constructing a temporary `MetadataCache` with secondary indexes.
- Batched metadata projection reads only requested paths and keys. Queries use
  parameters and never select image data.
- Metadata sorting returns ordered paths from SQLite without returning or
  retaining field values. Text, numeric, and date modes have distinct query
  ordering.
- Low-cardinality browse strings are canonicalized within each snapshot.

### Row and search state

- Compact rows begin with no mutable metadata map. The read-through map is
  allocated only when a projected value or edit requires it.
- Original known-value arrays and mutable edit overlays are allocated lazily.
  Exact originals are retained for pending validation, apply, discard, stale
  source checks, and reload preservation.
- Plain filtering matches fields directly instead of allocating a concatenated
  search string. Qualified, Boolean, and regular-expression behavior remains
  covered by parity tests.
- Visual metadata filters read sparse values in parameterized 200-path batches
  and discard each batch after evaluation. This deliberately uses bounded
  projection rather than retaining a full-library path set per condition.
- Known scalar metadata columns keep their compact in-memory numeric/date
  comparers. Deferred custom metadata columns request database ranks.

### Virtualization, reload, and consumers

- Visible metadata projections are limited to 4,096 cached cells and 32 MiB.
  A value larger than the total cache budget is not retained after row unload.
- Loads are canceled on row recycle, column changes, and Library reload.
  Recycled rows can safely replace a canceled load before its old task exits.
- Editing is blocked until the exact original value is available.
- Reload retains only edited/inspector/committed-overlay rows, releases the old
  untouched browse set, and then installs the replacement snapshot.
- Home uses the aggregate projection. Scalar Health analyses use compact browse
  rows; workflows that explicitly request full cached file details continue to
  do so through their existing APIs.
- Configuration/index notifications mark an inactive Library stale instead of
  eagerly loading 111,302 rows while Home is visible.

## Production measurements

All steady samples were taken after 30 seconds idle and a
collection-triggering heap capture.

| Scenario | Live managed | Private memory / peak |
| --- | ---: | ---: |
| Home after summary refresh | 15.2 MiB | 703.7 MiB after capture |
| Loaded Library idle | 156.5 MiB | 707.7, 707.6, 771.7 MiB |
| Metadata Inconsistencies Health run | 152.3 MiB | 752.4, 816.5, 752.4 MiB |
| After five filter/scroll/Home/Library cycles | 169.7 MiB | 786.5 MiB stable |
| Application startup peak | — | 938.5 MiB |

The five-cycle managed increase relative to the initial loaded-Library capture
was 13.1 MiB. No monotonic native-memory growth was observed during repeated
thumbnail scrolling.

## Implementation ledger

| ID | Work item | Status | Evidence |
| --- | --- | --- | --- |
| MEM-01 | Reproducible baseline and sensitive-capture procedure | Complete | Production aggregates and diagnostic procedure above; captures deleted 2026-07-27 |
| MEM-02 | Aggregate Home summary query | Complete | Summary parity and Home no-materialization tests |
| MEM-03 | Compact Library browse projection | Complete | DB exclusion tests and 111,302-row production probe |
| MEM-04 | Direct full-record compatibility query | Complete | Compatibility projection tests; no temporary `MetadataCache` indexes |
| MEM-05 | Sparse per-row edit state | Complete | Inline edit/original/revert/stale-source tests; heap type audit |
| MEM-06 | Allocation-free retained search projection | Complete | Plain/advanced/regex parity tests; no retained concatenated text |
| MEM-07 | Bounded visible-row metadata projection cache | Complete | Count/byte-limit, oversize, recycle, cancellation, and UI edit tests |
| MEM-08 | Database-backed metadata sorting and bounded visual filtering | Complete | DB text/numeric/date sort tests and sparse visual-filter parity tests |
| MEM-09 | Reload peak-memory reduction | Complete | Draft-preservation tests and 938.5 MiB observed process peak |
| MEM-10 | Compact Home and Health consumers | Complete | Home summary counters, Health parity tests, representative production Health capture |
| MEM-11 | Deterministic thumbnail disposal | Complete | Eviction/reset/cancel/view-teardown tests and stable scroll-cycle private memory |
| MEM-12 | Localization and accessibility states | Complete | Neutral plus nine complete satellites; strict 3,658-key validation |
| MEM-13 | Memory and behavioral regression tests | Complete | 422.8 bytes/row allocation probe and Library/UI parity suites |
| MEM-14 | Real-catalog post-change measurements | Complete | GUI and headless results recorded above |

## Validation

- Complete Release solution build: succeeded with 0 warnings and 0 errors.
- `MusicLibrary.Core.Tests`: 1,130 passed.
- `MusicFileUtilities.Tests`: 506 passed.
- `DumpITL.Tests`: 78 passed.
- `MusicLibraryManager.Tests`: 545 passed.
- `MusicLibraryManager.UI.Tests`: 224 passed in 13 minutes 12 seconds.
- Strict localization validation: 9 satellite catalogs × 3,658 resources,
  no missing keys or pending editorial review.
- Focused UI regression tests cover lazy exact-value editing, pending edits,
  persisted numeric metadata sort, path identity, and row-recycle cancellation.
- Opt-in production test uses `MLM_MEMORY_CONFIG`; it does nothing when the
  private configuration path is absent from the environment.

## Compatibility and deviations

- No database/index schema, cache feature, or serialization version changed.
- No scan, rescan, artwork-hydration, or library-configuration behavior changed.
- Grid order, width, visibility, sorting, pending edits, Inspector selection,
  Library-to-Workbench handoff, transactions, and recovery behavior are
  preserved.
- Bulky technical metadata remains available through lazy columns, filters,
  Inspector/file details, and the full compatibility API.
- Visual metadata filtering uses bounded parameterized field projections rather
  than a monolithic generated SQL expression. It has the same result semantics
  while avoiding both full value retention and a potentially large retained
  path/rank set for every Boolean condition.
- A historical latency comparison could not be reproduced without retaining a
  second sensitive baseline installation. Current compact load/filter/sort
  behavior was exercised on the production catalog and in generated-scale
  tests; custom metadata work remains cancellable as specified.
