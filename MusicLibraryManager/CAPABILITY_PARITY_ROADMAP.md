# Capability-Parity Roadmap

Last updated: 2026-07-23

## Purpose

Mp3tag is used only as a capability checklist. MusicLibraryManager will not copy
its interface, terminology, rule syntax, configuration files, presets, or
interaction model.

Equivalent outcomes will be implemented in the project's existing style:

- Policy-aware and library-oriented.
- Preview before apply.
- Typed operations rather than a scripting language.
- Recoverable mutations and centralized Operations history.
- Shared Core services used by both GUI and command-line workflows.
- Progress reporting and user cancellation for every long-running operation.
- One shared operation catalog for Workbench and Library selections.
- Library policy in configuration XML; personal UI state in `IAppSettings`.
- Native metadata parsers with explicit capability flags.

A new **Metadata Workbench** page is the home for ad-hoc files and focused
editing. The existing Library page remains optimized for indexed browsing.
Workbench is not an exclusive home for operations: every operation that is
meaningful for indexed files must also be invokable from the Library view with
the same editor, preview, policy checks, recovery behavior, and history.

## Long-running operation invariant

- [~] Every operation that scans, reads, fingerprints, queries, previews,
  applies, restores, exports, or indexes multiple files reports progress and
  accepts cancellation through its Core contract.
- [x] Workbench source loading, preview, apply, reload, and undo expose live
  progress and one cancellation command.
- [x] Library cache loading and shared metadata preview/apply expose progress
  indicators and cancellation.
- [x] Chromaprint generation and AcoustID lookup accept cancellation and report
  progress.
- [ ] Audit pre-existing ingest, health, artwork, playlist, export, and external
  tool workflows for the same invariant before parity hardening is complete.

## Cross-surface operation parity invariant

- [x] Define operation applicability in the shared operation catalog rather
  than in either view.
- [~] Expose every applicable metadata, artwork, file, online-enrichment,
  report, and playlist operation from both Workbench and Library. The initial
  typed metadata catalog, audio-fingerprint discovery, structured reports, and
  playlist output are exposed in both surfaces.
- [~] Share recipe editing, typed conditions, preview rendering, apply,
  cancellation, history, undo, redo, and repeat between the two surfaces.
- [x] Let Library operations target an explicit scope: selected tracks,
  selected albums, visible filtered results, or the complete active view.
- [x] Resolve Library scopes to stable path snapshots before preview and show
  files that disappeared, moved, or became unavailable as plan issues.
- [x] Read authoritative metadata directly from disk while building a Library
  operation preview; the cache is for browsing and candidate selection only.
- [x] Reindex successfully changed Library files immediately after apply.
- [x] Display a concrete reason when an operation is unavailable for a
  selection because of format capability, offline state, or library policy.
- [x] Keep session-only Workbench actions—adding/removing sources and manual
  session ordering—out of Library because they do not operate on library files.

## Progress legend

- [ ] Not started
- [~] In progress
- [x] Complete and verified

## Current implementation status

- [~] Phase 1: Metadata Workbench and safe editing foundation
  - [x] Initial lossless metadata, operation, plan, and history model types added.
  - [x] Core document, workbench, operation, and edit-history services added and
    registered.
  - [x] Core services compiled and covered by tests.
  - [x] Initial Workbench page and navigation implemented.
  - [x] End-to-end preview, apply, restart-safe undo, and redo verified.
- [~] Phase 2: Typed bulk operations and recipes
- [~] Phase 3: Online metadata and artwork
- [~] Phase 4: Flexible views, reporting, playlists, and tools
- [ ] Phase 5: Native format and tag-layer expansion
- [ ] Phase 6: Integration and parity hardening

The detailed checklists below are updated as implementation proceeds. An item is
marked complete only after its implementation has been validated by an
appropriate build or test.

## Core model and interfaces

- [~] Introduce a lossless metadata model:
  - [x] `MetadataFieldKey` represents a known field or native custom field.
  - [x] `MetadataValueSet` preserves ordered multiple values.
  - [x] `TagLayerDocument` describes individual tag layers.
  - [x] `MediaDocument` combines tag layers, artwork, codec properties, and the
    file snapshot.
- [ ] Add scalable native-format contracts:
  - [ ] `IMediaFormatHandler` handles detection, reading, staged writing,
    artwork, and tag-layer operations.
  - [~] `IMultiValueMetadataWriter` replaces the current first-value-only write
    path; native Vorbis/FLAC values are implemented and tested first.
  - [ ] `ITagLayerEditor` adds, removes, and converts individual tag types.
  - [ ] Existing format classes receive adapters before new formats are added.
- [~] Add typed metadata-operation contracts:
  - [x] `MetadataOperation` is a closed hierarchy of supported operations.
  - [x] `MetadataCondition` models field, file, and codec predicates.
  - [x] `OperationRecipe` is an ordered collection of typed operations.
  - [x] `MetadataOperationPlan` holds before/after changes and validation data.
  - [~] `IMetadataOperationService` owns preview and apply.
- [~] Add workflow services:
  - [~] `IWorkbenchService` manages ad-hoc source loading and ordering.
  - [~] `IEditHistoryService` provides persistent operation history and undo.
  - [x] Persistent redo and repeat.
  - [~] Code-backed metadata provider extension points; the first
    `IMusicBrainzMetadataProvider` is implemented, while the broader provider
    abstraction and additional sources remain.
  - [~] `IAudioFingerprintService` decodes audio and generates a Chromaprint
    fingerprint plus whole-file duration.
  - [x] `IAcoustIdLookupService` resolves fingerprints to scored AcoustID and
    MusicBrainz recording candidates.
  - [x] `IReportExportService`.
  - [x] `IPlaylistWorkspaceService`.
  - [x] `IExternalToolService`.
  - [x] `IWorkbenchShortcutStore`.
  - [x] `IMetadataGridColumnStore`.
- [x] Preserve the existing `{Field}` convention where composition is useful;
  do not implement mp3tag's format-string language.

## Phase 1: Metadata Workbench and safe editing foundation

### Workbench sources and session

- [x] Add Workbench navigation that works without a library configuration.
- [x] Open/add individual files and folders.
- [x] Optional recursive folder scanning.
- [x] Drag-and-drop.
- [x] Recent locations.
- [~] Library and Health "Open in Workbench" commands; Library is implemented.
- [x] M3U/M3U8 and cuesheet loading.
- [x] Explicit file ordering and removal from the session.

### Grid and inspector

- [x] Configurable Workbench grid with persisted visibility, order, widths,
  sorting, and technical-property columns.
- [x] Inline editing of the first set of actual metadata fields.
- [ ] Multi-selection and mixed-value handling.
- [~] Multiple values with keep, replace, append, remove-value, and remove-field
  semantics. These are available for a focused Workbench file; multi-selection
  mixed-value editing remains.
- [x] Inspect and edit arbitrary known and native custom fields.
- [~] Multiple artwork items, types, descriptions, and previews. The shared
  staged plan now preserves non-front artwork and previews a selected front
  cover; general-purpose artwork editing remains.
- [x] Read-only technical properties.
- [ ] Refactor Library inspector and fields dialog onto shared document/write
  services.

### Safe application and history

- [x] Capture length, modification time, metadata hash, policy fingerprint,
  target path, and collision state.
- [x] Generate complete staged output files.
- [x] Revalidate the whole plan immediately before applying.
- [x] Install through the mutation coordinator and recovery machinery.
- [x] Retain originals under `.MusicLibraryManager-recovery`.
- [x] Expose Workbench recovery containers in Operations.
- [x] Reuse the existing recovery retention preference.
- [x] Require sufficient recovery space before apply.
- [x] Persist committed operations so undo survives application restarts.
- [x] Redo regenerates and reviews a new plan against current files.

**Acceptance:** Arbitrary files can be opened, batch-edited, previewed, applied,
and restored without creating or indexing a library.

## Phase 2: Typed bulk operations and recipes

### Operations

- [x] Assign metadata fields.
- [x] Remove metadata fields.
- [x] Copy metadata fields.
- [x] Combine metadata fields.
- [x] Literal and regular-expression replacement.
- [x] Upper, lower, title, sentence, and configurable case conversion.
- [x] Trim and normalize whitespace.
- [x] Split a field into multiple values.
- [x] Join, deduplicate, or reorder multiple values.
- [x] Extract metadata from file and folder components.
- [ ] Generate filenames and directories using existing templates.
- [ ] Rearrange filename components.
- [ ] Import metadata from delimited text or CSV.
- [~] Sequential track and disc numbering.
- [~] Artwork add, replace, export, resize, classify, and remove. Local and
  Cover Art Archive front-cover replacement plus front/all removal are
  implemented; policy normalization is applied during preview. Export,
  explicit resize, and role classification remain.
- [ ] Copy, move, rename, and quarantine files.
- [ ] Generate reports or playlists as a recipe final step.

### Recipes and editing commands

- [x] Ordered visual operation list.
- [x] Typed conditions per operation.
- [x] Enable/disable, duplicate, rename, and reorder operations.
- [ ] Representative-file preview while editing.
- [x] Apply every applicable operation to Workbench or explicit Library scope
  through the same operation catalog and editor.
- [x] Persist personal recipes through `IAppSettings`.
- [x] Reserve configuration XML for shared library policies.
- [x] Do not import or emulate mp3tag action groups or presets.
- [ ] Tag-aware copy/paste.
- [x] Undo/redo commands; redo regenerates a new preview from current files.
- [x] Repeat-current-recipe command.
- [x] Keep preview enabled by default and reject stale streamlined applies.

**Acceptance:** Every converter and common batch-cleanup outcome in the
capability comparison is possible through typed operations without scripting.

## Phase 3: Online metadata and artwork

- [x] Implement `IMetadataSourceProvider` as a code-backed extension point
  with typed capability interfaces and a shared provider catalog.
- [~] Add audio fingerprinting and AcoustID-assisted discovery before the
  MusicBrainz release-selection workflow:
  - [~] Generate Chromaprint fingerprints locally from decoded audio for any
    readable codec, independently of existing tags. Native payload identities
    now cover every currently registered audio family.
  - [~] Use bundled or explicitly configured `fpcalc`/Chromaprint through a
    cross-platform `IAudioFingerprintService`; report tool availability and
    unsupported codecs without failing unrelated files.
  - [x] Cache the fingerprint with an audio-payload identity guard so metadata-
    only changes do not require recalculation, while audio changes invalidate
    it.
  - [x] Query AcoustID using the fingerprint and whole-file duration and request
    MusicBrainz recording IDs.
  - [x] Preserve all returned candidates, AcoustID confidence scores, and
    recording IDs; never silently accept the first match.
  - [~] Combine fingerprint confidence, duration, existing artist/title,
    track/disc position, and album context when ranking MusicBrainz candidates.
    Release-track mapping now ranks recording ID, duration, artist/title, and
    track/disc position; AcoustID confidence and broader album context remain.
  - [x] Use matched MusicBrainz recording IDs to locate releases and editions
    through the normal MusicBrainz provider.
  - [x] Require confirmation of recording matches, release, file-to-track
    mapping, imported fields, and artwork before mutation.
  - [~] Offer `ACOUSTID_FINGERPRINT`, `ACOUSTID_ID`, and MusicBrainz recording
    ID tag updates as ordinary optional previewed metadata changes.
  - [x] Support local fingerprint generation offline; make lookup status and
    cached/offline results explicit.
  - [x] Enforce AcoustID's provider-specific rate limit and application API-key
    requirements, plus cancellation, retry/backoff, and bounded concurrency.
  - [x] Do not submit fingerprints to AcoustID as part of lookup. Any future
    submission feature is separately opt-in, uses `ISecretStore` for the user's
    key, previews submitted data, and clearly identifies the external write.
- [~] Ship MusicBrainz and Cover Art Archive providers:
  - [x] Search MusicBrainz by artist/album, barcode, catalog number, or release
    ID, then load complete details only for the selected edition.
  - [x] Compare recording-linked editions, dates, countries, status, labels,
    catalog numbers, media formats, track positions, and tracklists.
  - [x] Suggest mappings using recording ID, disc/track number, normalized
    artist/title, and duration while leaving ties unselected.
  - [x] Confirm release, mapping, imported fields, and artwork.
  - [x] Route selective metadata and embedded-artwork changes through the
    normal staged preview, stale-plan validation, recovery, and history path.
- [x] Cache recording-linked editions, typed release searches, and complete
  release details in a bounded application-local SQLite database. Fresh
  results avoid network calls and expired results remain an explicit fallback
  when MusicBrainz is unavailable.
- [x] Cache downloaded artwork and thumbnails in a bounded application-data
  cache with least-recently-used pruning.
- [x] Add provider rate limiting, identifiable user agent, cancellation,
  retry/backoff, and offline behavior. A personal global offline mode now
  restricts AcoustID, MusicBrainz, and Cover Art Archive to persistent cached
  data and reports cache misses explicitly.
- [x] Introduce `ISecretStore`.
- [x] Implement Windows Credential Manager, macOS Keychain, and Linux Secret
  Service support, with session-only fallback.
- [x] Add Discogs after secret storage is available:
  - [x] Store the personal access token only through `ISecretStore`.
  - [x] Add typed artist/album, barcode, catalog-number, and release-ID
    search plus complete edition and track-detail lookup.
  - [x] Use the shared provider cache, offline policy, retry/backoff,
    rate limiting, progress, and cancellation behavior.
  - [x] Expose release comparison and detail loading in both Workbench and
    Library.
  - [x] Convert a confirmed Discogs edition and user-reviewed file-to-track
    mapping into a selective metadata preview.
  - [x] Download a confirmed Discogs image into the normal artwork preview.
- [x] Do not create an mp3tag-compatible web-source language.

**Acceptance:** An album can be searched, matched, selectively enriched,
previewed, applied, and undone. Files with missing or unreliable tags can first
be fingerprinted, matched through AcoustID to MusicBrainz recording candidates,
and then resolved to a user-confirmed release.

## Phase 4: Flexible views, reporting, playlists, and tools

### Views and filters

- [x] Persist column descriptors containing label, field, visibility, order,
  width, sort type, and optional edit target.
- [x] Allow technical, known metadata, and native custom metadata fields
  without code changes.
- [x] Keep saved views in the existing app-settings mechanism.
- [x] Filter arbitrary known, custom, and technical fields.
- [x] Add present/missing checks, equality, numeric comparisons, Boolean
  grouping, and regular expressions.
- [x] Add a visual filter builder alongside the existing text filter.
- [x] Add format-specific canonical-to-native field mapping settings.
  - [x] Persist validated per-format overrides in personal application settings.
  - [x] Promote mapped native values into canonical fields during authoritative
    Workbench and Library preview reads.
  - [x] Route both staged Workbench/Library operations and the legacy Library
    field editor through the configured native key.

### Reports

- [x] Structured report configuration for fields, column order, grouping,
  sorting, output path, encoding, and grouping behavior.
- [x] Safe TXT renderer.
- [x] Safe CSV renderer.
- [x] Safe HTML renderer.
- [x] Safe RTF renderer.

### Playlists and external tools

- [x] Interactive M3U/M3U8 and cuesheet loading.
- [x] Add, remove, and reorder tracks.
- [x] Save M3U/M3U8/WPL through existing playlist writers.
- [x] Generate grouped playlists with naming templates.
- [x] External tool executable, argument list, working directory, and
  invocation-mode configuration.
- [x] Expand only documented placeholders.
- [x] Use `ProcessStartInfo.ArgumentList`; never invoke a shell implicitly.
- [x] Add configurable shortcuts for Workbench commands and recipes.

**Acceptance:** Custom views, advanced filtering, reports, manual playlists,
and external integrations cover the remaining workflow gaps using native
application concepts.

## Phase 5: Native format and tag-layer expansion

### Reuse existing implementations

- [x] M4B and M4V through the MP4 handler.
  - [x] Advertise explicit metadata, artwork, remux, and transcode-source
    capabilities while keeping both extensions out of automatic indexing.
  - [x] Verify metadata and artwork round trips preserve the `mdat` payload
    byte-for-byte for both extension aliases.
- [x] Opus and Speex through generalized Ogg handling.
  - [x] Parse codec-specific identification headers while sharing ordered
    Vorbis-comment metadata and artwork handling.
  - [x] Preserve every non-comment packet across metadata and artwork writes.
  - [x] Release `.opus` and `.spx` for indexing after native round-trip and
    compressed-audio identity coverage.
- [x] WAV/RF64 and AIFF/AIFC through chunk handlers using existing ID3.
  - [x] Parse RIFF/RF64 and FORM chunks with their native byte order,
    alignment, technical properties, and RF64 `ds64` size overrides.
  - [x] Read, create, and rewrite container-owned ID3v2 metadata and artwork
    while preserving audio and unknown chunks.
  - [x] Keep PCM payload identities stable across ID3-only changes and release
    `.wav`, `.rf64`, `.aif`, `.aiff`, and `.aifc` for indexing.
- [x] Raw AAC with ID3 and APE tag-layer support.
  - [x] Parse ADTS technical properties, CRC header variants, frame lengths,
    sample counts, channel configuration, and bitrate.
  - [x] Inspect and preserve distinct leading ID3v2 and trailing APEv2 layers;
    create ID3v2 for previously untagged streams.
  - [x] Keep ADTS payload identities stable across either tag-layer edit and
    release `.aac` for indexing.

### Reuse APEv2

- [x] Monkey's Audio.
  - [x] Parse legacy 3.80-3.97 and descriptor-based 3.98+ stream headers,
    including technical properties, sample counts, duration, and bitrate.
  - [x] Reuse a writable APEv2 metadata/artwork layer with atomic in-place and
    staged saves that preserve the complete codec payload byte-for-byte.
  - [x] Keep payload identities stable across APEv2-only edits and release
    `.ape` for indexing and transcode-source workflows.
- [x] Musepack.
  - [x] Parse both SV7 frame headers and SV8 chunked stream headers, including
    exact sample counts, sample rate, channels, duration, and bitrate.
  - [x] Preserve the complete Musepack payload across writable APEv2 metadata,
    custom-field, artwork, repeated-save, and staged-output workflows.
- [x] TTA.
  - [x] Parse TTA1/TTA2 headers and seek tables with bounded frame-size
    validation and technical-property projection.
  - [x] Pass real FFmpeg encoder/demuxer, APEv2 persistence, payload identity,
    indexing, remux, and transcode source/destination gates.
- [x] TAK.
  - [x] Parse the native metadata-block chain and little-endian packed
    stream-info fields for sample count, rate, bit depth, and channels.
  - [x] Pass FFmpeg demuxer, APEv2 persistence, payload identity, indexing,
    and transcode-source gates.
- [x] OptimFROG/OFS.
  - [x] Parse modern `OFR `/`OFRX` headers for OptimFROG Lossless, Float, and
    DualStream technical properties and codec classification.
  - [x] Release `.ofr`, `.off`, and `.ofs` for metadata/artwork indexing while
    withholding transform capabilities unavailable in the bundled toolchain.

### Complex native handlers

- [x] ASF/WMA metadata and artwork.
  - [x] Parse ASF Header, File Properties, audio Stream Properties, Content
    Description, Extended Content Description, Metadata, and Metadata Library
    objects with WMA codec, duration, bitrate, sample-rate, channel, and
    bit-depth projection.
  - [x] Read and write standard/custom UTF-16 metadata plus small and large
    `WM/Picture` artwork while preserving unknown header objects and the
    complete Data Object/index tail byte-for-byte across atomic and staged
    saves.
  - [x] Keep ASF Data Object identities stable across metadata/artwork edits,
    release `.wma` for indexing, and expose `.asf`/`.wmv` for direct editing.
- [x] Matroska/WebM tags, chapters, and image attachments.
  - [x] Parse bounded EBML headers, Segment Info, audio Track Entries,
    global/audio-targeted SimpleTags, simple/nested chapters, and Matroska
    image attachments with codec and technical-property projection.
  - [x] Write ordered standard/custom tags, Segment titles, simple chapters,
    and typed Matroska cover attachments while preserving unedited tag
    structures, non-image attachments, and unknown top-level elements.
  - [x] Follow the Matroska edit layout by voiding replaced metadata,
    appending replacements, and rebuilding a pre-Cluster SeekHead without
    moving or rewriting any Cluster or Cue bytes.
  - [x] Release `.mka` and `.weba` for indexing and keep video-oriented
    `.mkv` and `.webm` available for direct editing; WebM advertises tag and
    chapter support but not Attachments, which its container subset forbids.

### Tag-layer controls and release gates

- [~] Keep new formats out of automatic indexing until preservation tests pass.
  M4B and M4V remain Workbench/direct-edit formats after passing MP4
  preservation tests; each later family must pass its own release gate.
- [~] Inspect all layers present in a file.
- [x] Add or remove specific tag types.
  - Raw AAC exposes honest ID3v2/APEv2 layer descriptors and can add either
    layer empty or by copying the primary layer's known fields, custom text,
    and artwork.
  - Workbench previews and applies individual layer additions/removals through
    the ordinary stale-plan, recovery-journal, and undo pipeline; removing the
    final layer deliberately produces tagless AAC without touching ADTS bytes.
- [ ] Convert ID3v2.2/2.3/2.4.
- [ ] Configure ID3 encoding and ID3v1 compatibility.
- [ ] Preview truncation, unsupported fields, and lossy conversions.
- [x] Preserve native-parser ownership; do not add a general tagging library.

**Acceptance:** Target format families have native coverage with accurate
per-operation capability flags.

## Phase 6: Integration and parity hardening

- [ ] Maintain a MusicLibraryManager-language capability checklist.
- [ ] Classify capabilities as covered natively, covered by a broader workflow,
  intentionally different, or not implemented.
- [ ] Add migration and rollback for new app settings and cache data.
- [ ] Preserve existing XML configurations, saved views, databases, and CLI
  workflows.
- [ ] Audit large selections and network shares.
- [ ] Audit offline roots.
- [ ] Audit Unicode paths and metadata.
- [ ] Audit keyboard and accessibility behavior.
- [ ] Audit high-DPI and malformed artwork.
- [ ] Audit Windows, macOS, and Linux behavior.
- [ ] Remove legacy paths only after replacements ship and migrate.

## Test plan

- [ ] Multi-value round trips across every supported tag format.
- [ ] Preserve unknown fields, additional tag layers, artwork, and audio
  payloads.
- [ ] Typed-operation tests for every operation and condition combination.
- [ ] Preview/apply equivalence.
- [ ] Stale-plan rejection.
- [ ] Collision, cancellation, insufficient-space, rollback, and recovery tests.
- [ ] Undo, redo, and concurrent-edit tests.
- [ ] Headless UI tests for Workbench loading, drag-and-drop, editing,
  selection, recipe construction, and navigation guards.
- [ ] Recorded fixtures for online providers; CI must not use live services.
- [ ] Golden Chromaprint/fpcalc fixtures for supported codecs, Unicode paths,
  cancellation, malformed output, missing tools, and deterministic
  fingerprints.
- [~] Recorded AcoustID responses for no match, one match, ambiguous matches,
  low confidence, multiple MusicBrainz recording IDs, throttling, retry, and
  offline cache behavior.
- [ ] Audio-payload identity tests proving tag-only writes retain cached
  fingerprints and audio changes invalidate them.
- [x] Operation-catalog contract tests proving each operation marked applicable
  to indexed files is exposed in both Workbench and Library.
- [ ] Library-scope tests for selection, album, filtered results, full view,
  offline files, stale cache rows, policy denial, preview/apply equivalence,
  reindex, and undo.
- [ ] Golden report and playlist output tests across encodings and path styles.
- [ ] Parser corpus tests for valid, unusual, truncated, and corrupt files.
- [ ] Full existing solution tests at every milestone.

## Architectural defaults

- Mp3tag is a research reference and acceptance checklist, not an architectural
  template.
- Functional outcomes matter; equivalent syntax, menus, files, presets, and
  settings do not.
- Metadata Workbench is a new page; Library remains cache-first.
- Personal recipes and layouts use current application settings; shared library
  behavior uses configuration policy.
- Typed operations and visual conditions are preferred over a general-purpose
  expression language.
- MusicBrainz and Cover Art Archive precede Discogs.
- Chromaprint and AcoustID-assisted MusicBrainz discovery are part of the first
  MusicBrainz milestone, not a later provider.
- An AcoustID is assigned by the AcoustID service; the app generates the local
  Chromaprint fingerprint used to look it up.
- Workbench and Library are two selection surfaces over one operation system;
  applicable capabilities must not diverge between them.
- Preview, policy validation, staged writes, and recovery are mandatory.
- Native parsers and cross-platform behavior are preserved.

## Progress log

### 2026-07-23

- Created this tracked roadmap.
- Added the first lossless metadata document and typed-operation model types.
- Drafted the Core services for ad-hoc source loading, preview/apply, staging,
  recovery integration, and persistent edit history.
- Corrected contract mismatches, registered the services, added Workbench
  recovery-container discovery to Operations, and compiled `MusicLibrary.Core`
  successfully with no warnings.
- Added focused tests for document loading, playlist ordering, typed-operation
  preview, invalid-expression blocking, recoverable apply, restart-safe undo,
  and ordered FLAC/Vorbis multi-value round trips.
- Added the first Metadata Workbench page with navigation, file/folder picking,
  recursive loading, drag-and-drop, recent locations, editable metadata columns,
  ordering/removal, technical properties, typed-operation preview, safe apply,
  and undo. Added "Open in Workbench" to Library.
- Added explicit native multi-value writer contracts and implemented ordered
  known/custom values for Vorbis comments without semicolon joining.
- Verified the desktop project builds with no warnings and the focused
  Workbench tests pass.
- Next validation point: expand the Workbench field inspector to edit arbitrary
  known/custom multi-values and artwork, then add recipe persistence and redo.
- Requirements update: added local Chromaprint generation, AcoustID lookup, and
  AcoustID-assisted MusicBrainz recording/release discovery to the first online
  metadata milestone.
- Requirements update: established cross-surface operation parity. Every
  Workbench operation meaningful for indexed files must also be available from
  Library through the same catalog, editor, preview, apply, recovery, and
  history services.
- Created the shared typed-operation catalog and shared editor state. The first
  seven typed operations are now exposed in both Workbench and Library.
- Added Library operation scopes for selected tracks, selected albums, visible
  filtered results, and the complete library. Library preview reads directly
  from disk, reports unavailable cached candidates as blockers, uses the normal
  staged/recoverable apply path, and refreshes the cache afterward.
- Added the Workbench All Fields editor. It displays every known and native
  custom field with its tag layers and ordered values, and supports replace,
  append, remove-value, and remove-field previews without flattening values into
  a delimiter-separated string.
- Added ordered, named recipe steps with enable/disable, rename, duplicate,
  remove, and reorder controls. Personal recipes are versioned in
  `IAppSettings`, shared by Workbench and Library, and retain typed conditions.
- Added persistent redo candidates and repeat-recipe commands. Both regenerate
  a fresh plan against current files and require review before apply.
- Added typed combine, split, join, deduplicate, and reorder operations to the
  shared catalog and editor. Their ordered multi-value behavior is covered by
  Core preview tests, and each operation is available in both Workbench and
  Library.
- Added typed extraction from file names, parent folders at a selected depth,
  and full paths. Optional named regular-expression captures support focused
  parsing without introducing a general action language; invalid capture groups
  are reported as preview blockers.
- Added the cross-platform `IAudioFingerprintService` foundation. It invokes a
  personally configured `fpcalc` executable without a shell, fingerprints the
  full decoded stream, parses compressed Chromaprint JSON plus whole-file
  duration, supports cancellation, and keeps the local fingerprint distinct
  from a server-assigned AcoustID.
- Made progress and cancellation an architectural invariant. Workbench loading,
  preview, apply, reload, and undo now share a live progress surface and Cancel
  command; Library cache loading and metadata preview/apply have equivalent
  controls. The Core contracts carry progress and cancellation end to end.
- Added lookup-only `IAcoustIdLookupService` support with a personal application
  client-key setting, identifiable HTTP user agent, three-request-per-second
  throttling, retry/backoff, cancellation, and recorded response tests. All
  scored AcoustID and MusicBrainz recording candidates are preserved for later
  user-confirmed matching.
- Added a shared batch audio-discovery service that fingerprints and looks up
  each distinct file, continues after ordinary per-file failures, and reports
  two cancellable progress steps per file. Workbench now has an Online Metadata
  tab for selected or all session files; Library exposes the same candidate
  table for its selected-track, selected-album, filtered, and complete scopes.
  Candidates are displayed without silently selecting or applying any match.
- Added explicit candidate confirmation before tag changes. A selected candidate
  can generate a normal metadata preview for `ACOUSTID_FINGERPRINT`,
  `ACOUSTID_ID`, and an unambiguous MusicBrainz recording ID in either
  Workbench or Library; the existing staged apply and recovery path remains the
  only way to commit those fields.
- Added the first code-backed MusicBrainz metadata provider. It resolves a
  confirmed recording ID to every paged release edition while preserving
  release-group, country, date, status, label, catalog-number, medium, track,
  artist-credit, and duration details. Requests use provider-specific
  throttling, retry/backoff, an identifiable user agent, progress, and
  cancellation.
- Added release-edition comparison tables to both Workbench and Library. The
  user explicitly starts resolution from one unambiguous recording candidate;
  the shared workflow displays editions and matching disc/track positions
  without selecting a release or mutating metadata.
- Added recorded MusicBrainz response tests plus shared presentation coverage,
  so normal test runs do not depend on the live service.
- Added cancellable MusicBrainz release-track mapping. Exact recording IDs
  outrank disc/track, normalized artist/title, and duration hints; tied or
  duplicate suggestions remain unselected for manual review.
- Added editable mapping tables to Workbench and Library. Users choose the
  release, replace or exclude each file-to-track suggestion, and select title,
  artist, release identity, numbering, release-detail, and MusicBrainz-ID field
  groups.
- Mapped metadata enters the same authoritative per-file preview, stale-plan
  validation, staged apply, recovery, and history path as every other metadata
  operation. Mapping and preview both report progress and support cancellation.
- Added direct, paged MusicBrainz release search by artist/album, barcode,
  catalog number, or release ID in both Workbench and Library. Search results
  remain lightweight; building a mapping loads the chosen edition's complete
  media and track details through the same throttled, retryable, cancellable
  provider.
- Added the code-backed Cover Art Archive provider with recorded JSON tests,
  retry/backoff for transient failures, bounded concurrency, an identifiable
  user agent, cancellation, and explicit no-artwork handling for HTTP 404.
- Added a 128 MiB application-local artwork cache keyed by immutable source
  URLs with least-recently-used pruning. Workbench and Library now browse every
  image attached to the selected release, including roles, approval state,
  comments, front/back classification, and cached thumbnails.
- Added artwork-aware operation plans with before/after descriptors, native
  format capability checks, effective library-policy validation, full staged
  output generation, stale-plan rejection, recovery-space checks, and
  restart-safe undo.
- Workbench and Library can download a user-selected Cover Art Archive image
  with progress and cancellation, then preview it as the front cover only for
  explicitly included release mappings. Applying uses the same recoverable
  mutation path as metadata edits and preserves non-front embedded images.
- Sidecar and combined embedded/sidecar policies remain blocked until the
  operation plan can recover multiple output artifacts atomically.
- Added a bounded application-local SQLite cache for MusicBrainz
  recording-linked editions, exact typed searches, and complete release
  details. Cache hits report progress, survive application restarts, avoid
  repeat provider calls for 30 days, and fall back to expired data when a live
  request fails without swallowing cancellation.
- Added the shared code-backed metadata-source catalog. MusicBrainz and Cover
  Art Archive advertise typed capability descriptors and resolve as the same
  singleton instances through their specific interfaces and the common
  extension contract; no provider scripting language or imported source
  definitions were introduced.
- Added first-class local artwork operations to both surfaces. Workbench can
  preview a local front-cover replacement or front/all removal for its focused
  file; Library exposes the same operations for selected tracks, selected
  albums, visible results, or the complete library. Image reads and per-file
  planning report progress, accept cancellation, enforce native capabilities
  and policy, preserve non-front roles when requested, and apply through the
  shared recovery and undo path.
- Added native compressed-audio payload identities for FLAC frames, MP3 and
  WavPack data outside ID3/APEv2 tags, MP4 `mdat` atoms, DSF data before its
  metadata pointer, and Ogg packets excluding Vorbis/Opus comments. Identity
  calculation is streaming, progress-aware, and cancellable; unknown formats
  safely use a whole-file identity.
- Added a bounded application-local SQLite Chromaprint cache keyed by that
  payload identity. It survives restarts, follows files renamed to a new path,
  reuses fingerprints after metadata-only edits, and invalidates when
  compressed audio changes. Cache failures never block local `fpcalc`
  generation.
- Added a personal global offline-mode setting shared by every built-in online
  provider. AcoustID lookup results and Cover Art Archive release manifests now
  join MusicBrainz data in the bounded persistent provider cache. Fresh cache
  hits avoid network access, expired entries provide an explicit fallback when
  a provider fails, and offline mode never attempts a request. Audio discovery
  rows distinguish live, cached, and offline-cached candidates.
- Added the cross-platform secret-storage foundation required by authenticated
  providers. Windows Credential Manager and macOS Keychain are accessed through
  native APIs; Linux Secret Service uses `secret-tool` with an explicit
  argument list and sends secret values only through standard input. If a
  native facility is unavailable, the service latches to a process-local
  session store. Secrets are not written to app settings, portable library XML,
  logs, or diagnostics.
- Added the first native Discogs provider slice. A personal token is managed
  from Settings through `ISecretStore`; typed searches cover artist/album,
  barcode, catalog number, and direct release ID, followed by complete edition
  and track-detail loading. Workbench and Library expose the same comparison
  grid and commands. Requests use an identifiable user agent, one-per-second
  pacing, retry/backoff, the shared persistent cache and offline policy,
  progress reporting, and cancellation. Recorded fixtures keep CI independent
  of the live service.
- Added cancellable Discogs file-to-track mapping to Workbench and Library.
  Disc/track position, normalized title and artist, and duration contribute to
  ranked suggestions; ties and duplicate track assignments remain unselected.
  Users can replace or exclude every suggestion and choose title, artist,
  release identity, numbering, release details, genre/style, and Discogs-ID
  field groups. The result enters the ordinary staged metadata preview,
  capability and policy validation, recovery, and undo path. Discogs image
  download now uses the same authenticated request pacing and bounded artwork
  cache; a confirmed mapping can add the primary image to the standard staged
  artwork preview on either surface.
- Added the shared structured report service and matching editors to Workbench
  and Library. Reports select known, custom, file, and technical fields; support
  column ordering, typed sorting, grouping, one-file-per-group naming, and
  UTF-8 or UTF-16 output. Dedicated TXT, formula-safe CSV, encoded HTML, and
  escaped RTF renderers build immutable reviewed output plans. New and replaced
  files use the mutation coordinator and recovery journal, while preview and
  apply use each surface's existing progress indicator and cancellation
  command.
- Added the shared playlist workspace service and matching editors to
  Workbench and Library. Workbench preserves its explicit session order and
  already supports playlist/cuesheet loading plus add, remove, and reorder;
  Library resolves the same explicit selected-track, selected-album, filtered,
  or complete scope used by other operations. Both surfaces can preview and
  write M3U, M3U8, or WPL with path style, encoding, line-ending, and extended
  information controls. Optional metadata grouping generates multiple safely
  named playlists while preserving track order within each group. Output bytes
  and destination snapshots are reviewed before apply, existing files use
  recovery, and preview/apply expose progress and cancellation.
- Added personal, structured external-tool definitions shared by Workbench and
  Library. Each tool stores an executable, one argument per list entry, an
  optional working directory, and either once-for-selection or once-per-file
  invocation. Only the documented file, path-component, index, count, and
  multi-file placeholders expand; `{Files}` becomes separate argument-list
  entries and cannot be embedded in a shell-like command string. Preview shows
  every process invocation and snapshots the selected files, while run rejects
  stale selections, requires explicit confirmation, reports per-process
  progress, and kills the active process tree on cancellation. The native
  runner always sets `UseShellExecute` to false and populates
  `ProcessStartInfo.ArgumentList`; bounded output capture prevents an external
  process from growing diagnostics memory without limit.
- Added personal configurable Workbench shortcuts for common commands and
  saved recipes. A typed gesture parser canonicalizes modifier/key names,
  rejects malformed, duplicate, and shell-reserved gestures, and ignores
  shortcuts while text, combo-box, or numeric editors have focus. Command
  shortcuts retain the existing availability guards; recipe shortcuts always
  regenerate a fresh staged preview against the current session and require
  review before apply. Bindings use versioned `IAppSettings` persistence and
  tolerate deleted recipes without invoking stale work.
- Added configurable, persisted Workbench columns using the same grid-state
  conventions as Library. Visibility, display order, widths, and typed sort
  state survive restarts, and the chooser prevents hiding every column.
  Workbench and Library now expose tag type, codec type, sample rate, bit
  depth, bitrate, channels, file size, modification time, and path alongside
  ordinary metadata. Existing Library saved views continue to own their
  filter/column/sort snapshots, and hidden-column restoration is shared by all
  persisted grid layouts.
- Added typed user-defined metadata column descriptors shared by Workbench and
  Library. Users can choose any known field or enter a native custom field,
  assign a label, width, text/numeric/date sorting, and an optional supported
  Workbench inline-edit target. Descriptor and grid layout state are versioned
  in personal app settings, and saved Library views preserve dynamic column
  IDs like built-in columns. Workbench projects values from its lossless media
  documents; Library remains cache-first by bulk-projecting all normalized and
  native custom values from the metadata table. A database feature marker
  causes one progress-reporting, cancellable metadata refresh for existing
  caches, and is recorded only after a healthy completed scan.
- Added a typed visual Library filter alongside the existing text-query
  parser. Conditions target any cached known or native custom field plus every
  technical/file property, with present, missing, contains, equality, bounded
  regular-expression, and numeric comparison operators. Numbered condition
  groups express `(A AND B) OR (C AND D)`-style logic, root all/any mode and
  per-condition negation. Visual and text filters combine, run on the existing
  cancellable background filter path, persist in the workspace and saved
  views, and tolerate older settings that contain no visual expression.
- Hardened ingest progress delivery so operation completion cannot overtake
  already-reported row updates. Progress is synchronous in headless hosts and
  dispatched to the captured UI synchronization context in the app, with an
  explicit drain point before completion state is published. This removes a
  parallel-test race and keeps every output row for a multi-output source in
  sync.
- Generalized the native Ogg handler from Vorbis to Vorbis, Opus, and Speex.
  Codec identification now reads `OpusHead` and the native Speex header while
  each format retains its required comment-packet framing. Metadata and
  embedded artwork round trips preserve every non-comment packet, including
  multi-page comments and chained logical streams. `.opus` and `.spx` now
  advertise indexed metadata, artwork, remux, and transcode-source
  capabilities, and compressed-audio identities exclude their comment packets
  so tag-only changes retain cached Chromaprints.
- Added native RIFF/RF64 WAVE and FORM AIFF/AIFF-C chunk handlers backed by
  the existing ID3v2 frame implementation. Readers expose PCM technical
  properties and honor little-/big-endian chunk sizes, odd-byte alignment,
  AIFF extended sample rates, and RF64 `ds64` overrides. Writes replace or add
  only the native ID3 chunk, preserve audio and unrelated chunks byte-for-byte,
  update RIFF/FORM lengths and RF64 `riffSize`, and support the ordinary staged
  output path. The five extensions now pass metadata, artwork, real-encoder,
  unknown-chunk, output-staging, and PCM payload-identity release gates and are
  enabled for automatic indexing.
- Added native raw AAC/ADTS handling with distinct leading ID3v2 and trailing
  APEv2 metadata layers. The parser validates every ADTS frame and derives
  sample rate, channel count, duration, average bitrate, and peak frame bitrate
  across CRC and multi-raw-block variants. Untagged files create ID3v2 on first
  edit; APE-only files keep APE as their edit target; dual-layer files expose
  and preserve both layers while defaulting file-level edits to ID3. Atomic
  in-place and staged saves copy the complete ADTS range byte-for-byte, and
  payload identities ignore either outer tag so cached Chromaprints survive
  metadata-only changes. `.aac` now passes synthetic layer-matrix and real
  ffmpeg fixture gates and is enabled for automatic indexing.
- Added native Monkey's Audio handling backed by a reusable APEv2-tagged file
  persistence layer for the remaining tail-tag formats. The bounded parser
  supports both legacy 3.80-3.97 headers and descriptor-based 3.98+ streams,
  deriving bit depth, channels, sample rate, duration, average bitrate,
  compression level, and file version. A compact structural 3.99 fixture is
  accepted by FFmpeg's reference demuxer and covers metadata, arbitrary text,
  artwork, repeated atomic saves, staged output, leading ID3 preservation, and
  malformed bounds. Payload identities exclude the trailing APEv2 layer but
  track compressed-frame changes, and `.ape` is enabled for indexing and
  transcode-source workflows.
- Completed the APEv2 reuse family with native Musepack SV7/SV8, TTA1/TTA2,
  TAK, and OptimFROG Lossless/Float/DualStream readers. All formats share the
  atomic APEv2 metadata, arbitrary-text, and typed-artwork persistence layer,
  preserve codec payloads byte-for-byte across repeated and staged saves, and
  expose writable Workbench tag layers. Musepack and TAK structural fixtures
  pass FFmpeg's reference demuxers, TTA uses a real FFmpeg-encoded fixture, and
  OptimFROG follows the published modern chunk-header layout. Payload hashes
  ignore only the outer tag and detect compressed-data mutations. `.mpc`,
  `.tta`, `.tak`, `.ofr`, `.off`, and `.ofs` are released for indexing; only
  transform operations actually supported by the bundled codec toolchain are
  advertised.
- Added a native bounded EBML handler for Matroska and WebM. It projects
  FLAC, Opus, Vorbis, AAC, MPEG audio, PCM, WavPack, and TTA Track Entries;
  exposes ordered native tags and simple/nested chapters to the Workbench;
  and reads/writes typed JPEG/PNG Matroska cover attachments while preserving
  non-image attachments. Edits replace old metadata with equal-sized Void
  elements, append the new Info/Tags/Chapters/Attachments, and rebuild the
  early SeekHead, leaving every Cluster and Cue byte at its original offset.
  FFmpeg-generated `.mka`/`.webm` fixtures, reference decode/probe checks,
  repeated/staged saves, malformed inputs, and Cluster-only payload identities
  cover the release gate. `.mka` and `.weba` are indexed; `.mkv` and `.webm`
  remain direct-edit formats, and WebM correctly omits artwork capabilities
  because its subset does not support Attachments.
