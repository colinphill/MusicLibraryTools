# Library profiles

MusicLibraryManager schema version 2 separates library policy from machine-specific paths. New
configurations start with the **Catalog only** profile and a root with no write capabilities. An
existing unversioned configuration continues to use **Legacy MusicLibraryTools** until it is
explicitly saved.

## Built-in profiles

- **Catalog only** indexes and browses music without changing files, tags, artwork, or catalogs.
- **Preserve layout + tag editing** permits explicit metadata and artwork edits but never
  reorganizes files.
- **Artist/Album organizer** enables conventional template-driven organization while preserving
  disc metadata.
- **iTunes Media** uses iTunes-compatible media paths without requiring iTunes as the catalog.
- **Legacy MusicLibraryTools** retains the previous archive naming, ingest, disc-suffix, repair,
  and cleanup behavior.

The Settings page exposes common profile selection and per-root permissions first. The Advanced
policy tab edits templates, collision and disc behavior, quality thresholds, health rules, ingest
recipes, metadata fidelity, artwork, and sidecar handling. Effective policy shows the resolved
behavior and an example path before saving.

## Root safety

Each root has stable identity and independently grants these capabilities:

- metadata writes
- artwork writes
- organization moves
- ingest output
- synchronization output

Workflows resolve permissions from the most restrictive matching nested root. A path outside all
configured roots is not writable. Preview plans carry the immutable policy fingerprint and Apply
rejects a preview after its profile, root, format, recipe, or binding changes.

Index formats and include/exclude patterns are root-specific. Offline roots are warnings so cached
browsing remains available; invalid format names, templates, references, and contradictory
permissions are save-blocking errors.

## Portable and local configuration

`<MachineBindings File="..."/>` moves root paths, export destinations/options, the metadata
database, FFmpeg, and the optional iTunes library into a companion file keyed by stable library,
root, and export-profile IDs. Inline paths remain accepted for legacy and single-machine
configurations. Unknown XML attributes and elements are preserved by the editor.

Both documents are validated in memory and written atomically. The first explicit save of a
legacy configuration creates a backup and emits schema version 2.

## Naming and identity

One resolver is shared by organization, recipe ingest, representation repair, and synchronization.
Templates accept `{AlbumArtist}`, `{Artist}`, `{Compilation}`, `{Year}`, `{Genre}`, `{Album}`,
`{Disc}`, `{Track}`, `{Title}`, `{OriginalName}`, and `{Extension}`. Optional fragments use square
brackets, such as `[{Year} - ]{Album}`.

Profiles specify number padding, missing-value fallbacks, compilation naming, Unicode
normalization, invalid-character replacement, component and full-path limits, and collision
behavior (`Stop`, `Suffix`, `Hash`, or `PreserveExisting`). Generic profiles preserve edition and
format suffixes and stop on collisions. Legacy-only sanitization remains isolated in the legacy
profile.

Disc policy can preserve tags, suffix Album, add a disc folder, prefix filenames, or flatten track
numbers. Album grouping, duplicate analysis, and representation checks share the album-identity
policy so edition qualifiers are not silently merged.

## Formats, ingest, and exports

The media registry is the source of truth for indexing, metadata/artwork writes, and transcode or
remux eligibility. An ingest recipe declares its input match, action, destination root, output
codec, quality transform, naming profile, fidelity rules, and collision behavior. Sources and
unknown sidecars are preserved by default; quarantine or deletion must be selected explicitly.

Playlist input is provider-based. iTunes ITL remains optional, and M3U/M3U8 sources can drive file
playlist export without a catalog integration. Playlist targets control path style, encoding,
line endings, EXTINF records, size limits, transformations, and collision behavior.

Export profiles combine selection, transforms, naming, artwork/playlists, transport, and
reconciliation. Android, Car Card, and Smart Storage definitions remain disabled until a library
explicitly configures them. Enabled generic filesystem profiles appear as named jobs in Operations;
their preview enforces output-root permissions, selection, shared naming, collision, replacement,
quarantine/deletion limits, recovery journaling, and policy fingerprints. Policy dimensions that
do not yet have a built-in executor are shown as blocking preview issues instead of being ignored.
