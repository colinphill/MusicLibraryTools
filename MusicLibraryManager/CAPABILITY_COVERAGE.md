# MusicLibraryManager Capability Coverage

Last audited: 2026-07-23

This is the product-language capability checklist for MusicLibraryManager. It
describes user outcomes and the application workflow that owns each outcome;
it does not mirror another application's menus, terminology, action syntax, or
configuration format.

## Classification

- **Native** — available directly in Workbench, Library, or another named
  MusicLibraryManager workflow.
- **Broader workflow** — intentionally owned by a policy-driven or dedicated
  workflow that provides the same outcome with stronger context.
- **Intentionally different** — the outcome is supported with a deliberately
  different interaction or storage model.
- **Not implemented** — an accepted product capability still has no shipped
  workflow. These rows are release gaps.

There are currently no accepted earlier-phase parity capabilities classified
as **Not implemented**. Remaining Phase 6 work is integration, migration,
platform, accessibility, scale, and corpus hardening rather than missing
editing outcomes.

## Metadata and batch editing

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Open arbitrary media without creating a library | Native | Metadata Workbench file, folder, playlist, cuesheet, recent-location, and drag-and-drop loading |
| Browse indexed metadata while roots are offline | Native | Cache-first Library view |
| Inspect all known and native custom fields | Native | Workbench All Fields and Library field/inspector views use the lossless document reader |
| Preserve ordered multiple values | Native | Shared metadata documents, typed value edits, native dry-run validation, and multi-value writers |
| Assign or remove fields | Native | Shared typed operation catalog in Workbench and Library |
| Copy or combine fields | Native | Shared typed operation catalog |
| Literal or regular-expression replacement | Native | Shared typed operation catalog with previewed validation |
| Case conversion | Native | Upper, lower, title, sentence, and configurable case operations |
| Whitespace cleanup | Native | Trim and internal-whitespace normalization operations |
| Split, join, deduplicate, and reorder values | Native | Typed ordered-value operations |
| Extract values from file or folder paths | Native | Typed path-component extraction with optional capture patterns |
| Import CSV, TSV, or semicolon-delimited metadata | Native | Scope-constrained delimited import with quoted/multiline parsing and explicit empty-cell handling |
| Sequential track/disc numbering and totals | Native | Ordered sequence operation with start, step, padding, and total field |
| Conditions per operation | Native | Typed field conditions in the shared recipe editor |
| Ordered reusable recipes | Native | Personal versioned recipes in application settings |
| Shared-library editing policy | Intentionally different | Versioned library configuration and profiles own shared behavior; personal recipes remain application-local |
| General-purpose scripting expressions | Intentionally different | Typed operations and conditions are used instead of an unbounded expression language |
| Import another tagger's action/preset files | Intentionally different | Outcomes are represented as native typed recipes; foreign rule syntax is not emulated |

## Paths and files

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Generate canonical folders and filenames from metadata | Broader workflow | Profile-controlled path layout used by Organize, ingest, repair, export, and sync |
| Rearrange filename components | Broader workflow | Configurable directory and filename templates in the same path-layout policy |
| Ad-hoc copy, move, rename, or quarantine | Native | Shared reviewed Files editor in Workbench and Library |
| Preserve relative layout during ad-hoc file operations | Native | Files editor relative-layout option |
| Resolve file collisions | Native | Reviewed stop-or-suffix policy; policy workflows additionally support profile collision modes |
| Recover moves and quarantines | Native | Durable journals, rollback, Operations browse/restore, and catalog relocation |
| Session-only source ordering | Native | Workbench session ordering; deliberately absent from Library because it does not mutate library files |

## Artwork

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Inspect complete embedded artwork sets | Native | Lossless artwork documents and staged Workbench artwork set |
| Add, replace, classify, describe, reorder, resize, export, or remove artwork | Native | Multi-image Workbench editor and shared Library artwork previews |
| Find release artwork | Native | Cover Art Archive and Discogs candidates with explicit selection |
| Enforce embedded/sidecar/disabled artwork policy | Broader workflow | Library profiles, ingest, repair, and artwork normalization own multi-artifact policy behavior |
| Audit and normalize oversized artwork | Native | Health/analysis and artwork normalization workflows |

## Online identification and enrichment

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Generate an audio fingerprint locally | Native | Chromaprint/fpcalc fingerprint service with payload-identity cache |
| Fingerprint OptimFROG Lossless, Float, and DualStream | Native | Configured official OptimFROG decoder bridge to isolated temporary PCM |
| Look up AcoustID candidates | Native | Cancellable discovery preserving every candidate, confidence, and recording ID |
| Resolve MusicBrainz recordings, releases, editions, and track mappings | Native | Shared Workbench/Library MusicBrainz workflow with explicit confirmation |
| Search and map Discogs releases | Native | Credential-aware Discogs search/details/mapping workflow |
| Apply provider metadata or identifiers | Native | Ordinary shared metadata preview/apply/recovery path |
| Use live provider services in CI | Intentionally different | Recorded provider fixtures are required; CI must remain independent of live services |

## Reports, playlists, and tools

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Generate structured reports | Broader workflow | Dedicated shared report preview/output workflow rather than a metadata recipe side effect |
| Generate playlists | Broader workflow | Dedicated shared playlist preview/output workflow with explicit scope and path style |
| Run a configured external tool | Native | Argument-safe, shell-free, explicitly confirmed tool preview in both surfaces |
| Recover arbitrary external-tool side effects | Intentionally different | External tools are visibly outside application recovery; native mutations remain recovery-backed |

## History and safety

| Capability | Classification | MusicLibraryManager workflow |
|---|---|---|
| Preview authoritative before/after metadata | Native | Shared lossless preview service with native writer dry run |
| Live representative preview while editing a recipe | Native | Debounced, cancellable, non-applyable draft preview on both surfaces |
| Reject stale plans | Native | Whole-plan snapshot and policy-fingerprint revalidation before the first write |
| Apply atomically with rollback | Native | Shared mutation coordinator, staged output, durable journal, and reverse rollback |
| Undo after restart | Native | Persistent edit history plus retained recovery containers |
| Redo safely | Native | Regenerates a new preview against current files; never reapplies a stale plan |
| Repeat the latest recipe | Native | Workbench session or explicit Library scope |
| Keep the Library cache current after mutation | Native | Immediate reindex of changed or restored Library paths |

## Native media and tag-layer coverage

| Family | Classification | Released coverage |
|---|---|---|
| MP3 and ID3-backed containers | Native | ID3v2.2/2.3/2.4, encoding controls, artwork, custom text, ID3v1/1.1 read/edit/add/remove/copy |
| FLAC and Ogg audio | Native | Vorbis comments, ordered values, artwork, and payload preservation for FLAC, Vorbis, Opus, and Speex |
| MP4 audio | Native | AAC/ALAC metadata, ordered supported attributes, artwork, and payload preservation |
| APEv2 families | Native | WavPack, Monkey's Audio, Musepack, TTA, TAK, OptimFROG/OFS, and raw AAC |
| ASF/WMA | Native | Standard/custom fields, repeated descriptors, artwork, and Data Object preservation |
| Matroska/WebM audio | Native | Tags, chapters, permitted image attachments, and Cluster/Cue preservation |
| Video-oriented aliases | Intentionally different | Direct Workbench editing remains available; automatic music-library indexing is limited to appropriate audio extensions |
| Unknown native structures and audio payloads | Native | Preservation gates retain unedited tag structures, container objects, attachments, and codec payload bytes |

## Release-hardening gaps

The following are Phase 6 validation gaps, not missing capability outcomes:

- large selections, network shares, and offline roots;
- keyboard, accessibility, high-DPI, malformed artwork, and three-platform
  behavior;
- complete operation/condition, provider, fingerprint, library-scope, output,
  and parser corpora.

The tracked source of truth for closing those gaps is
[`CAPABILITY_PARITY_ROADMAP.md`](CAPABILITY_PARITY_ROADMAP.md).
