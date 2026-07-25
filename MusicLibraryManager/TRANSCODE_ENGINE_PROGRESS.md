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
- [Complete] ENC-01: Typed FFmpeg, WavPack, and OptimFROG execution adapters.
  Rate controls, resampling/depth conversion, deterministic dither selection,
  thread allocation, specialist WAV bridging, OptimFROG source decoding, and
  native verification hooks are implemented. Preview now requires exact
  FFmpeg demuxer identifiers and matching OptimFROG source decoders. The
  real-tool suite exposed and fixed invalid native Opus/Vorbis advertising,
  RF64 muxer selection, OptimFROG Float mode selection, floating-point
  bridging, its actual `.ofr` extension, and `.ofr` decoder ambiguity. The
  adapter now tries both `ofr` and `off` without shell invocation and the real
  Float output round-trips through that fallback. Floating or
  unspecified-precision decoded sources now receive deterministic,
  format-appropriate integer projection and dither when the lossless output
  requires integer PCM; explicit float output remains untouched. The installed
  WavPack 5.9.0 tools now validate lossless output, hybrid/correction output,
  native correction reconstruction, and DSF-preserving output with decoded
  equality and native DSD inspection. The advertised codec/source validation
  matrix is complete.
- [Complete] CFG-01: Existing WavPack executable machine-binding round trip now
  treats WavPack as a known configured tool.

## Adaptive parallel transcoding

- [Complete] SCH-01: Adaptive work scheduler supports Automatic and bounded
  manual CPU budgets, independent-file parallelism, encoder thread allocation,
  deterministic result ordering, aggregate progress, cancellation, and
  per-volume I/O gates. Tests cover concurrent single-threaded work, manual
  budgets, aggregate FFmpeg thread limits, per-volume gates, deterministic
  ordering, and queued/active cancellation.
- [Complete] SCH-02: Scheduler coverage includes a conservative multi-file
  throughput benchmark showing Automatic mode materially outperforms a forced
  single worker, plus a 32-item high-contention test proving per-volume
  bridge/hash gates stay bounded while independent volumes continue in
  parallel.
- [Complete] UI-SET-01: The drawer exposes Automatic/manual machine-local
  concurrency and live tool readiness without storing it in presets.

## Drawer, destinations, and presets

- [Complete] DST-01: Alongside, replacement, chosen-folder, relative layout,
  flattening, stable naming tokens, same-extension suffix, Stop, and numeric
  Suffix planning are implemented in the core preview service. WavPack `.wvc`
  and OptimFROG `.ofc` correction files participate in preview collision
  checks, suffix selection, staging, commit, and Undo. Preserved chosen-folder
  layout now prefers the most-specific configured library root and falls back
  to the common source ancestor. Sources without a shared volume fall back
  safely to flattened planning. Non-replacement output can never resolve back
  over its source. Tests cover configured-root layout, same-path protection,
  deterministic flattened Stop/Suffix behavior, and companion collisions.
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
- [Complete] UI-MENU-01: Session grid context-menu and mirrored Selection
  Actions entries are wired. Right-click preserves an existing multi-selection
  and selects an unselected row; the existing Shift+F10/Menu route opens the
  mirrored context menu. Headless interaction tests invoke Transcode from both
  menus, verify the captured selection and drawer, preserve a selected
  multi-row right-click, and select an unselected right-clicked row.

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
- [Complete] PIPE-04: Preview uses shell-free `ffprobe` JSON inspection to
  count audio programs, audio streams, and non-audio streams. Replacement is
  blocked unless the source contains exactly one audio stream/program and no
  other stream; separate output remains available with a localized
  primary-audio-only warning. Lossless resampling/depth changes now generate
  an independent, metadata-free FFmpeg PCM reference with the reviewed
  resampler/dither settings and compare decoded audio against that reference.
  WavPack `.wvc` and OptimFROG `.ofc` stages are reconstructed with their
  native decoders and the reconstructed audio is decoded-compared against the
  source or transformed reference. Real FFmpeg coverage additionally decodes
  15 representative AAC, AIFF, Monkey's Audio, FLAC, Matroska, MP3, Musepack,
  Ogg, TTA, WAV, WebM, ASF/WMA, WavPack, AAC/M4A, and ALAC/M4A sources into
  verified lossless output.
  Integer-lossless inputs compare directly; lossy/floating decoder output is
  compared with an independently generated deterministic integer reference.
  A 96 kHz/24-bit to 48 kHz/16-bit case verifies matching resampling and
  bit-depth reduction end to end. A corrected full-block DSD64 fixture verifies
  the explicit-rate/depth preview guard and DSD-to-88.2 kHz/24-bit PCM against
  the transformed reference. The real OptimFROG DualStream test creates its
  `.ofc`, reconstructs with the native decoder, and decoded-compares it with
  the source. The real WavPack tests likewise decoded-compare ordinary
  lossless output and hybrid-plus-correction reconstruction with the source;
  DSF output is additionally inspected as 1-bit DSD at 2.8224 MHz and
  decoded-compared with the DSF source.

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
- [Complete] OPS-01: MusicLibraryManager journals now appear as localized
  Reviewed changes in Operations. Hash-verified created outputs are visible
  and selectable for recovery removal alongside retained originals and
  compact-payload storage/savings. V2 coordinator manifests now group
  participant journals into one transaction row with localized participant,
  applied, state, and aggregate affected-item detail. Opening the row browses
  every participant journal, restore preview remains one atomic action set,
  unavailable journals surface warnings, and retention safely expands the
  transaction back into all participant run directories.

## Localization and accessibility

- [In Progress] LOC-01: Neutral and all nine beta satellite catalogs contain the
  drawer, preset, destination, rate, concurrency, pending, and status strings.
  The deterministic generator validates 3,508 resources per locale, including
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
- Localization generator validation passes for 3,508 keys in all 9 satellites.
- Source layout, aggregate progress, transformed reference, native correction
  reconstruction, platform parser variants, specialist fallback, and
  advertised source-family validation pass the targeted transcode foundation
  suite: 54 tests. Scheduler validation includes a conservative multi-file
  performance comparison and high-contention per-volume I/O gating across two
  independently progressing volumes.
- Targeted Workbench interaction validation passes: 6 tests, including both
  Transcode menu entry points and right-click selection behavior.
- Targeted Transcode editor/Workbench pending tests pass: 9 tests, including
  Apply-ready and Back behavior after a partially failed stage.
- Targeted transcode/catalog/iTunes integration suites pass: 30 tests.
- Composed staging/transaction tests verify that reviewed metadata is used for
  output projection before live mutation and that metadata plus a generated
  output Undo as one unit.
- Coordinator/history crash-boundary and durable-spool suite passes: 11 tests,
  including unavailable participant-volume retention.
- Operations coordinator discovery, grouped browsing/restore preview,
  retention expansion, localized projection, and coordinator terminal-state
  coverage pass: 33 focused Core cases plus 40 localization/source-validation
  cases.
- Opt-in real-tool integration passes: every advertised local FFmpeg pair and
  all three OptimFROG modes encode readable output; Float `.ofr` additionally
  decodes through the specialist fallback into FLAC. Installed WavPack 5.9.0
  tools cover lossless output, hybrid correction output, native correction
  reconstruction, decoded equality, and DSF-preserving output. Eight real-tool
  cases now include the 15-family FFmpeg source matrix, deterministic
  transformed-reference equality, DSD-to-PCM, native WavPack and OptimFROG
  correction reconstruction, WavPack DSD preservation, and a complete reviewed
  MP3-to-FLAC staging pass through scheduling, output validation, and decoded
  verification.
- The shared fixture generator now emits an 8,284-byte, FFmpeg-decodable DSD64
  stream instead of the legacy undersized structural file. Monkey's Audio now
  uses a deterministic 0.3-second tone generated with the official 13.20
  3-clause-BSD SDK; only the verified encoded fixture and provenance/hash
  record are committed, not the SDK or encoder.
- Headless Workbench matrix validation passes for the required viewports,
  light/dark themes, normal/18 px type, 40%-expanded pseudo-locale, German,
  Japanese, Korean, and Simplified/Traditional Chinese. Three dedicated
  transcode-drawer frames were retained under
  `.artifacts/transcode-ui-20260725`.
- Self-contained `win-x64`, `linux-x64`, and `osx-x64` publishes pass from the
  Windows validation host. Each output contains its application host, matching
  native Skia runtime, all nine satellite assemblies, pinned AvaloniaUI and
  SkiaSharp licenses/notices, and the embedded four-ABI Syncer server payload;
  no ImageSharp artifact is present. Native installer/package creation still
  requires validation on each target operating system.
- Full portable Release tests pass: Core 1,074; MusicLibraryManager 226;
  MusicLibraryManager UI 84; MusicFileUtilities 506; DumpITL 78.
- The MusicLibraryManager Release project builds with 0 warnings and 0 errors.
- `git diff --check` passes; Git reports only existing line-ending
  normalization notices.
- No database/index schema, scan configuration, or rescan behavior has changed.
- The primary engine checkpoint is commit `c069eec`; Operations, scheduler,
  and cross-runtime validation are committed in `7a760e8`; destination,
  Workbench interaction, and expanded verification are committed in
  `4497846`; valid APE/DSF fixtures and specialist correction coverage are
  committed in `588d6b0`.

## Resume next

1. Run target-native installer/package verification. Cross-runtime publish
   layouts are already verified; native installer creation still requires each
   target operating system.
