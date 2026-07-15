# MusicLibraryTools

A personal toolkit for managing a large local music library: a Visual Studio solution of
small C# console utilities built on two shared libraries. Targets **.NET 10**.

The design center is a set of **hand-written audio metadata parsers** (no TagLibSharp) that
read and write tags across every format in the library, plus a SQLite cache so tools don't
re-parse tens of thousands of files on every run. Everything is driven by an XML library
configuration that describes where the library, sync targets, playlists, and cache database
live. iTunes library state itself is read directly from the binary `.itl` through ITLTools; the
legacy MusicFileUtilities iTunes-XML parser has been removed.

## Supported formats

| Extension | Container / codec | Tag format | Read | Write |
|---|---|---|---|---|
| `.mp3` | MPEG audio | ID3v2.2/2.3/2.4 | ✔ | ✔ |
| `.dsf` | DSD | ID3v2 (at metadata pointer) | ✔ | ✔ |
| `.flac` | FLAC | Vorbis comments + PICTURE blocks | ✔ | ✔ |
| `.ogg` | Ogg Vorbis | Vorbis comments | ✔ | ✔ |
| `.m4a` `.mp4` `.m4p` `.m4r` | MP4 (AAC / ALAC) | iTunes-style atoms | ✔ | ✔ |
| `.wv` | WavPack | APEv2 | ✔ | ✔ |

Tag coverage is a superset (`TagFields`) that includes MusicBrainz identifiers, ReplayGain,
AcoustID, production credits (producer/engineer/mixer), and classical work/movement fields.
Embedded artwork is parsed, hashed, measured, and preserved byte-for-byte across tag edits.

## Layout

### Shared libraries

- **MusicFileUtilities** — the core library. Per-format binary parsers (`ID3.cs`, `FLAC.cs`,
  `MP4File.cs`, `Vorbis.cs`, `APE.cs`, `WavPack.cs`) behind common interfaces:
  `MediaFile.GetFile(path)` dispatches by extension to an `IMediaFile`, which exposes codec
  properties (`ICodecProvider`), tag reading (`IMetadataProvider`), and tag writing
  (`IMetadataWriter`: `SetField` / `RemoveField` / `Save`). Also home to the iTunes library
  XML reader, playlist handling, fuzzy string matching, image-format probing, and
  `LibraryConfiguration` (the XML config) with `MusicFileEnumerator`, a streaming
  filesystem walker tuned for scanning large trees over SMB.
- **MetadataCaching** — SQLite-backed metadata cache (`Microsoft.Data.Sqlite`). Indexes the
  library (file properties, tags, artwork hashes) so tools query instead of re-parsing, and
  maps iTunes library data onto indexed files.

### Desktop app

- **MusicLibrary.App** — an Avalonia library browser and editor for indexing, cached filtering,
  tag and artwork editing, analysis, ingest, and organization. The prioritized application backlog
  is tracked in [`MusicLibrary.App/ROADMAP.md`](MusicLibrary.App/ROADMAP.md).

### Tools

Sync and devices:

- **CrossSyncMusic / CrossSyncPlaylists / CrossSyncPlaylistFiles** — keep library copies and
  their playlists in sync across locations.
- **BackSyncPlaylists** — sync playlists back from a device/copy, with path remapping. It reads
  and updates the binary `.itl` directly through ITLTools; iTunes must be closed for `--apply`.
- **AndroidSync** — push music to an Android phone over ADB.
- **UpdateCarCard** — maintain the car's SD card copy (with rebalancing and error fixing).
- **UpdateSmartStorage** — build a device image with its own file/artwork databases.

Artwork:

- **FixArtwork / ScrubArtwork / ArtworkScrubber** — repair, re-encode, and clean embedded
  cover art. `FixArtwork` resolves playlist membership from the binary `.itl`, reads and writes
  media tags through MusicFileUtilities, and uses ImageSharp for portable JPEG conversion and
  resizing.
- **DumpArtworkSizes** — report embedded artwork dimensions/sizes for an `.itl` playlist using
  MusicFileUtilities, without launching iTunes.

Auditing and diagnostics:

- **AnalyzeMetadata** — index the full library and report on metadata.
- **FindNonLossless** — find files that aren't lossless where they should be.
- **ITLTools / DumpITL** — a reusable library plus standalone application for inspecting,
  validating, comparing, and conservatively rewriting binary iTunes `.itl` libraries. See
  [`DumpITL/README.md`](DumpITL/README.md) for the evidence-backed format map and disposable-library
  acceptance workflow.
- **CheckRedundancies / FixiTunesDupes** — find duplicate/redundant tracks. `FixiTunesDupes`
  redirects ordinary playlist memberships and removes verified duplicate ITL records offline.
- **DumpTags** — dump raw tag contents for a file.

Organization:

- **OrganizeFiles / SortDownloads / Comingle** — file layout and merging of new music into
  the library.
- **IngestMusic** — preview and import incoming FLAC/ALAC albums: normalize multi-disc tags,
  create approved CD-quality counterparts, encode 256 kbit/s AAC, and route each version into
  the canonical library layout. The same workflow is available on the app's **Ingest** tab,
  including a dialog for creating and editing ingestion configurations.

`MetadataDBWork` and `PlayProject` are scratch projects for experiments.

## Configuration

Most tools take a library configuration XML path as their first argument. It defines the
library root(s), index locations, sync targets, playlist targets, the cache database file
(default `cache.db`), and length limits. See `LibraryConfiguration` in MusicFileUtilities
for the schema.

Library indexing uses up to 16 concurrent metadata readers so high-latency SMB shares can
overlap file opens. Set `MLT_INDEX_PARALLELISM` to a value from 1 through 64 to tune the global
reader cap for a particular NAS; 8-16 is a sensible starting range, while higher values should
be validated against the share rather than assumed to be faster. Multiple scan roots are
discovered concurrently under that same cap, and root-file snapshots from discovery are reused so
each root directory is listed only once per indexing pass.
MusicLibrary.App exposes this cap beside **Index library** and includes a bounded, read-only
per-root benchmark. The benchmark reads disjoint metadata samples without artwork or database writes and recommends
the smallest reader count within 5% of each root's measured peak; its conservative all-root setting
uses the lowest of those recommendations.
Each index attempt also records per-root health and the last fully successful scan. If a share is
offline or a subtree cannot be completely enumerated, indexing continues for healthy roots and
does not interpret unvisited cached files as removals.

## Safe execution

High-impact sync, organization, iTunes, and device commands now default to a dry run or refuse to
write until `--apply` is supplied. Commands that can remove stale entries also default to a removal
ceiling of zero; set an intentional limit with `--max-removals <count>`. Device-image tools require
an initialized target marker (`--initialize` creates it after you verify the path), and removals are
moved into timestamped quarantine/recovery directories rather than being deleted immediately.
`UpdateCarCard --recover <journal.tsv>` can roll back an interrupted journaled update.

Offline ITL editors accept `--library <file.itl>`, then fall back to the `ITUNES_ITL` environment
variable and finally `%USERPROFILE%\Music\iTunes\iTunes Library.itl`. They refuse to apply while
iTunes is running, validate before saving, atomically replace the selected library, and retain the
previous file as `<file.itl>.bak`. `BackSyncPlaylists` requires `--template <manual-playlist>` only
when it must create a playlist that does not already exist.

Read-only library consumers such as the cross-sync, redundancy, car-card, and smart-storage tools
also use `ITUNES_ITL` and the same default `.itl` location. `ITUNES_XML` is no longer supported.

`IngestMusic` previews by default and requires `--apply`. If a high-resolution track has no
CD-quality FLAC counterpart, apply asks for confirmation album-by-album before doing any work; one
declined album cancels the whole run. Transcodes are staged and validated, source files are moved to
a sibling `.IngestMusic-quarantine` tree only after an album commits by default, and ffmpeg jobs run
in parallel up to the machine's CPU-core count. Preview uses one buffered directory walk and up to 16
concurrent metadata readers so network opens overlap; set `MLT_INGEST_PARALLELISM` to tune that cap
(1-64) for a particular share. Set `DeleteSourcesAfterIngest` to `true` to delete
successfully committed sources instead. Set `RemoveNonMusicAfterIngest` to `true` to apply that same
delete/quarantine disposition to unsupported and non-audio files after every album succeeds, and to
remove emptied folders from the incoming tree. Start from
[`IngestMusicConfiguration.example.xml`](IngestMusic/IngestMusicConfiguration.example.xml), then run:

```
IngestMusic <incoming-directory> <ingest-config.xml> [--apply]
```

The configured ffmpeg build must provide the `libfdk_aac` encoder.

Set the optional `ItunesLibrary` configuration element to an `.itl` path to import generated AAC
files directly. In this mode, the library's own media-folder setting determines the destination;
tracks are organized below `Music` using iTunes' artist/album/track naming rules and then added to
the binary library. iTunes must be closed during apply. Each album's library update is validated and
atomically saved with a backup. If `ItunesLibrary` is omitted, AAC files continue to go to the
configured `AacDestination` (normally `Z:\iTunes\AddAAC`).

The machine-specific `MusicLibrary.NonFree.Tests` project is deliberately absent from both solution
files and therefore from CI. On the workstation that has `C:\ffmpeg\nonfree\ffmpeg.exe`, run its
real libfdk AAC integration test explicitly with:

```
dotnet test MusicLibrary.NonFree.Tests/MusicLibrary.NonFree.Tests.csproj
```

## Building and testing

The repository pins the .NET 10 SDK in `global.json` and centralizes NuGet versions in
`Directory.Packages.props`. Build everything in Visual Studio, or from a Developer PowerShell with:

```
msbuild MusicLibraryTools.sln /restore /p:Configuration=Release
```

All projects that do not require platform-specific integration, including `BackSyncPlaylists`,
`FixiTunesDupes`, `FixArtwork`, and `DumpArtworkSizes`, are also collected in a solution filter and
can be built with the cross-platform .NET SDK:

```
dotnet build MusicLibraryTools.Portable.slnf
```

The artwork tools read the library selected by `--library <file.itl>`, then `ITUNES_ITL`, then the
standard iTunes library location. Examples:

```
DumpArtworkSizes "My Playlist" --library "C:\Music\iTunes Library.itl" --output ArtworkSizes.dat --parallelism 16
FixArtwork "My Playlist" --library "C:\Music\iTunes Library.itl"
FixArtwork "My Playlist" --library "C:\Music\iTunes Library.itl" --apply
```

`FixArtwork` changes embedded media-file artwork only; artwork that exists solely in iTunes'
downloaded artwork cache is reported as missing. Apply mode requires iTunes to be closed, verifies
each rewritten media file, updates the corresponding `.itl` size/date/artwork caches, validates the
library, and retains the previous `.itl` as `<library>.bak`.

`DumpArtworkSizes` inspects distinct albums concurrently (16 readers by default); use
`--parallelism` to tune the global cap for a network share. `ArtworkScrubber` likewise accepts a
fourth positional parallelism argument and processes different folders concurrently while retaining
the input order within each folder.

```
dotnet test MusicFileUtilities.Tests/MusicFileUtilities.Tests.csproj
dotnet test MusicLibrary.Core.Tests/MusicLibrary.Core.Tests.csproj
```

The xUnit suite covers the parsers, the tag write paths (copy → mutate → save → reopen,
including artwork preservation), the SQLite indexer, application services, and `.itl` safety checks.
Real-media fixtures are generated
at build time by `generate-fixtures.ps1` — **ffmpeg must be on the PATH** (or in
`C:\ffmpeg\bin`) to build the test project; the fixtures are not committed.

## Design notes

- The parsers are deliberately hand-written: full control over
  what gets read (a library scan reads only what it needs), byte-exact round-tripping on
  save, and no dependency drift.
- Parsing is optimized for scanning huge libraries over a network share: large sequential
  read buffers, minimal per-file round trips, lazy artwork decoding, and no reflection or
  exception-driven control flow on the hot path. Indexing opens MP4/M4A files in a read-only
  projection that discards unrelated sample-table payloads; tag-writing paths reopen the full
  editable atom tree so unknown data is still preserved on save.
- New and modified cache entries defer embedded artwork bytes during the metadata pass. Artwork
  signatures, thumbnails, and detail views hydrate only the requested files under the same bounded
  reader cap, after verifying that size and timestamp still match the indexed source. Existing
  databases migrate as already hydrated, avoiding an unexpected library-wide artwork rescan.
- Format-specific readers avoid empty network work: MP3 jumps over declared ID3 padding, FLAC
  does not seek across a final padding block, and WavPack reads its APE tail tag without probing
  the file front or re-reading the optional header. DSF reads its fixed format header before making
  one jump to tail metadata.
- Full tag rewrites use large sequential input/output buffers and pooled copy storage. FLAC reuses
  its parsed audio offset, MP4 streams deferred media without a per-rewrite megabyte allocation,
  and Ogg caches the source length instead of querying it for every page.
- Batched cache refreshes reuse artist, album, artwork, and metadata-key lookups for the whole
  transaction. Existing SQLite caches are automatically upgraded with indexes for album/path,
  file-path, and artwork-hash lookups.
- Saves are conservative: unknown tag frames/atoms/blocks are preserved, artwork
  round-trips byte-for-byte, and in-place fast paths fall back to full rewrites whenever
  structure changes.
