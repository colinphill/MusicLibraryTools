# Reviewed Workbench Transcode Engine Progress

Branch: `feature/workbench-transcode-engine`  
Base: `afd98209439edc31aa834aa9b332f77116d50d7a`  
Last updated: 2026-07-25

## Capability and encoding foundation

- [Complete] FND-01: Stable nonlocalized format, encoder, rate-mode, tool,
  destination, collision, and preset models.
- [Complete] FND-02: Bounded shell-free process runner with capped diagnostics,
  line progress, cancellation, and process-tree termination.
- [Complete] CAP-01: Runtime capability probing for FFmpeg, WavPack, and
  configured OptimFROG executables. Exact FFmpeg identifier parsing,
  configuration-sensitive caching, force-refresh behavior, and similarly
  named encoder tests are implemented. Opt-in tests probe the locally
  configured real FFmpeg and OptimFROG installations and encode every
  advertised pair. Representative CRLF/LF and comma-identifier table fixtures
  cover the output variants emitted on Windows, macOS, and Linux.
- [In Progress] ENC-01: Typed FFmpeg, WavPack, and OptimFROG execution adapters.
  Rate controls, resampling/depth conversion, deterministic dither selection,
  thread allocation, specialist WAV bridging, OptimFROG source decoding, and
  native verification hooks are implemented. Preview now requires exact
  FFmpeg demuxer identifiers and matching OptimFROG source decoders. The
  real-tool suite exposed and fixed invalid native Opus/Vorbis advertising,
  RF64 muxer selection, OptimFROG Float mode selection, floating-point
  bridging, its actual `.ofr` extension, and `.ofr` decoder ambiguity. The
  adapter now tries both `ofr` and `off` without shell invocation and the real
  Float output round-trips through that fallback. The complete advertised
  codec/source validation matrix remains.
- [Complete] CFG-01: Existing WavPack executable machine-binding round trip now
  treats WavPack as a known configured tool.

## Adaptive parallel transcoding

- [Complete] SCH-01: Adaptive work scheduler supports Automatic and bounded
  manual CPU budgets, independent-file parallelism, encoder thread allocation,
  deterministic result ordering, aggregate progress, cancellation, and
  per-volume I/O gates. Tests cover concurrent single-threaded work, manual
  budgets, aggregate FFmpeg thread limits, per-volume gates, deterministic
  ordering, and queued/active cancellation.
- [In Progress] SCH-02: Scheduler unit tests are present; utilization benchmark
  and per-volume bridge/hash stress coverage remain.
- [Complete] UI-SET-01: The drawer exposes Automatic/manual machine-local
  concurrency and live tool readiness without storing it in presets.

## Drawer, destinations, and presets

- [In Progress] DST-01: Alongside, replacement, chosen-folder, relative layout,
  flattening, stable naming tokens, same-extension suffix, Stop, and numeric
  Suffix planning are implemented in the core preview service. WavPack `.wvc`
  and OptimFROG `.ofc` correction files participate in preview collision
  checks, suffix selection, staging, commit, and Undo.
- [Complete] PRE-01: Versioned named-preset storage with Save, Update, Rename,
  Delete, and hardware-specific setting exclusion. Unavailable format/encoder
  IDs remain visible and stable across capability refresh instead of silently
  falling back.
- [Complete] UI-DRW-01: A responsive shared-host Transcode editor now exposes
  captured selection, presets, validated format/encoder/rate controls, sample
  conversion, destination/naming/collision policy, concurrency, readiness,
  issues, and sticky Preview. Headless tests cover 900x600, 1200x700, and
  1440x900 bounds, initial focus, Escape dismissal, focus restoration, and
  retained semantic selections across repeated opens. Choice collections now
  reconcile in place so two-way bindings and live localization never blank the
  selected format, encoder, or rate mode.
- [In Progress] UI-MENU-01: Session grid context-menu and mirrored Selection
  Actions entries are wired. Right-click preserves an existing multi-selection
  and selects an unselected row; the existing Shift+F10/Menu route opens the
  mirrored context menu. Interaction tests remain.

## Unified pending and media pipeline

- [In Progress] PIPE-01: Immutable per-file preview units, source snapshots and
  hashes, staged parallel encoding, metadata/artwork projection, verification,
  decoded-PCM comparison for unmodified lossless conversions, correction
  sidecars, ready/failed results, and stage cleanup exist in the core service.
  Preview performs conservative per-volume staging estimates and apply repeats
  the capacity check using actual staged output/sidecar sizes before mutation.
  Aggregate staging progress includes active files, elapsed time, and encoded
  audio-time percentage.
- [Complete] PIPE-02: Effective pending metadata, artwork, and tag-layer
  changes are staged without touching live sources. Transcode encoding and
  projection read those reviewed stages, while source hashes continue to
  protect the live audio.
- [Complete] PIPE-03: Transcode previews accumulate by source, overlapping
  intents confirm replacement, Preview opens Review Changes, Revert clears
  pending transcodes, and staging failures offer Apply ready files or Back.
  Ready transcodes and their applicable metadata participants now commit in
  one v2 transaction. Failed units retain their metadata/artwork edits;
  replacement transcodes consume those edits in the generated replacement
  without separately mutating the old source. Direct Workbench interaction
  now covers disjoint accumulation, accepted overlap replacement, and declined
  replacement. If every transcode stage fails, unrelated metadata-only units
  can still apply normally while all failed transcodes and their edits remain
  pending. Workbench tests verify both partial-ready branches: Apply commits
  only ready item IDs and retains failed intent, while Back discards all stages
  and preserves the complete pending set.
- [In Progress] PIPE-04: Preview uses shell-free `ffprobe` JSON inspection to
  count audio programs, audio streams, and non-audio streams. Replacement is
  blocked unless the source contains exactly one audio stream/program and no
  other stream; separate output remains available with a localized
  primary-audio-only warning. Lossless resampling/depth changes now generate
  an independent, metadata-free FFmpeg PCM reference with the reviewed
  resampler/dither settings and compare decoded audio against that reference.
  WavPack `.wvc` and OptimFROG `.ofc` stages are reconstructed with their
  native decoders and the reconstructed audio is decoded-compared against the
  source or transformed reference. The full format verification matrix
  remains.

## Transaction, recovery, catalog, and history

- [In Progress] REC-01: New outputs now receive durable length/SHA-256
  reversible-created journal records. Undo prevalidates the output, moves it
  transactionally into the restore root, removes it from the catalog, and
  refuses the whole restore if it changed externally. Restart reconciliation
  understands created-output removal.
- [Complete] REC-02: A dedicated v2 reviewed-change coordinator now writes a
  durable manifest before participants, freezes deterministic participant
  order, commits per-volume journals, rolls earlier participants back when a
  later participant fails, and reconciles pending manifests at startup. All
  accumulated transcode previews, composed metadata replacements, and
  correction sidecars commit through one coordinator transaction.
  Fault injection covers a participant committed before the coordinator
  decision, a retained pending marker after COMMIT, settings cleanup failure,
  and throwing progress observers. Only pre-COMMIT failure can roll back;
  post-COMMIT reconciliation always preserves the decided batch.
  Reconciliation now keeps an explicitly applied participant blocked and
  pending when its volume or journal is unavailable, instead of falsely
  recording a successful rollback; the retained manifest is retried after the
  volume returns.
- [Complete] HIST-01: A separate `manager.workbench.reviewed-history.v2` index
  records one or more semantic transcode requests per transaction, performs
  batch-prevalidated exact Undo, refuses externally modified outputs without
  consuming history, persists Redo across restart, and regenerates every
  request as a fresh preview. Legacy single-request v2 records normalize on
  load. A write-through durable history spool is created before updating
  roaming settings, so post-COMMIT settings failure remains restart-safe and
  cannot make a successful Apply appear failed.
- [Complete] CAT-01: External and internal catalog updates mirror the tracked
  membership of the source, leave untracked transcodes session-only, exclude
  correction sidecars, and never turn a Refresh into an implicit import.
  Preview warns for output outside configured index roots. Apply and Undo
  perform exact affected-path reindex/remove operations, persist indexed-source
  membership in the compatible v2 history record, and never initiate a scan.
  Post-commit cache failures surface as warnings without misreporting the
  already-committed filesystem transaction.
- [In Progress] OPS-01: MusicLibraryManager journals now appear as localized
  Reviewed changes in Operations. Hash-verified created outputs are visible
  and selectable for recovery removal alongside retained originals and
  compact-payload storage/savings. Coordinator-level grouping and state detail
  remain.

## Localization and accessibility

- [In Progress] LOC-01: Neutral and all nine beta satellite catalogs contain the
  drawer, preset, destination, rate, concurrency, pending, and status strings.
  The deterministic generator validates 3,506 resources per locale, including
  placeholders, protected codec/tool tokens, and CJK residual text. Preview
  issues use localized summaries while retaining raw tool/parser messages as
  diagnostic detail. Planning, preparation, elapsed time, active-file count,
  encoded-time percentage, and reviewed-file progress now use structured
  localization keys with English fallback text for non-UI hosts.
- [Complete] A11Y-01: The Transcode surface uses the shared drawer host,
  300-430 px bounds, Escape/scrim dismissal, initial focus, focus restoration,
  keyboard context-menu access, and accessible close naming. These behaviors
  are exercised by headless interaction tests at all required viewport sizes.

## Validation checkpoint

- `dotnet build MusicLibrary.Core/MusicLibrary.Core.csproj --configuration Release --no-restore`
  passed with 0 warnings and 0 errors on 2026-07-25.
- Targeted `OperationJournalServiceTests` passed: 17 tests, including
  reversible-created output removal and external-change refusal.
- Targeted transcode foundation, reviewed transaction/history, and operation
  journal suites pass: 34 tests. Managed-process coverage additionally checks
  capped diagnostics, shell-free argument passing, and prompt cancellation.
- Localization catalog/source/XAML validation passes: 37 tests.
- Localization generator validation passes for 3,506 keys in all 9 satellites.
- Source layout, aggregate progress, transformed reference, native correction
  reconstruction, platform parser variants, specialist fallback, and
  advertised source-family validation pass the targeted transcode foundation
  suite: 46 tests.
- Targeted Transcode editor/Workbench pending tests pass: 9 tests, including
  Apply-ready and Back behavior after a partially failed stage.
- Targeted transcode/catalog/iTunes integration suites pass: 30 tests.
- Composed staging/transaction tests verify that reviewed metadata is used for
  output projection before live mutation and that metadata plus a generated
  output Undo as one unit.
- Coordinator/history crash-boundary and durable-spool suite passes: 11 tests,
  including unavailable participant-volume retention.
- Opt-in real-tool integration passes: every advertised local FFmpeg pair and
  all three OptimFROG modes encode readable output; Float `.ofr` additionally
  decodes through the specialist fallback into FLAC.
- Headless Workbench matrix validation passes for the required viewports,
  light/dark themes, normal/18 px type, 40%-expanded pseudo-locale, German,
  Japanese, Korean, and Simplified/Traditional Chinese. Three dedicated
  transcode-drawer frames were retained under
  `.artifacts/transcode-ui-20260725`.
- Full portable Release tests pass: Core 1,055; MusicLibraryManager 226;
  MusicLibraryManager UI 82; MusicFileUtilities 506; DumpITL 78.
- The MusicLibraryManager Release project builds with 0 warnings and 0 errors.
- `git diff --check` passes; Git reports only existing line-ending
  normalization notices.
- No database/index schema, scan configuration, or rescan behavior has changed.
- Work is intentionally uncommitted at this checkpoint.

## Resume next

1. Add Operations coordinator-level grouping and state detail for v2 reviewed
   transactions.
2. Expand the transformed-PCM/output verification matrix across writable
   fixture families and specialist correction modes.
3. Add the scheduler utilization benchmark and high-contention per-volume
   bridge/hash stress coverage.
