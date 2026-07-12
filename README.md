# MusicLibraryTools

A personal toolkit for managing a large local music library: a Visual Studio solution of
small C# console utilities built on two shared libraries. Targets **.NET 10**.

The design center is a set of **hand-written audio metadata parsers** (no TagLibSharp) that
read and write tags across every format in the library, plus a SQLite cache so tools don't
re-parse tens of thousands of files on every run. Everything is driven by an XML library
configuration that describes where the library, sync targets, playlists, and cache database
live.

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

### Tools

Sync and devices:

- **CrossSyncMusic / CrossSyncPlaylists / CrossSyncPlaylistFiles** — keep library copies and
  their playlists in sync across locations.
- **BackSyncPlaylists** — sync playlists back from a device/copy, with path remapping.
- **AndroidSync** — push music to an Android phone over ADB.
- **UpdateCarCard** — maintain the car's SD card copy (with rebalancing and error fixing).
- **UpdateSmartStorage** — build a device image with its own file/artwork databases.

Artwork:

- **FixArtwork / ScrubArtwork / ArtworkScrubber** — repair, re-encode, and clean embedded
  cover art.
- **DumpArtworkSizes** — report artwork dimensions/sizes for a playlist.

Auditing and diagnostics:

- **AnalyzeMetadata** — index the full library and report on metadata.
- **FindNonLossless** — find files that aren't lossless where they should be.
- **CheckRedundancies / FixiTunesDupes** — find duplicate/redundant tracks.
- **DumpTags / DumpTags2** — dump raw tag contents for a file (DumpTags2 uses TagLibSharp
  for cross-checking against the hand-written parsers).

Organization:

- **OrganizeFiles / SortDownloads / Comingle** — file layout and merging of new music into
  the library.

`MetadataDBWork` and `PlayProject` are scratch projects for experiments.

## Configuration

Most tools take a library configuration XML path as their first argument. It defines the
library root(s), index locations, sync targets, playlist targets, the cache database file
(default `cache.db`), and length limits. See `LibraryConfiguration` in MusicFileUtilities
for the schema.

## Safe execution

High-impact sync, organization, iTunes, and device commands now default to a dry run or refuse to
write until `--apply` is supplied. Commands that can remove stale entries also default to a removal
ceiling of zero; set an intentional limit with `--max-removals <count>`. Device-image tools require
an initialized target marker (`--initialize` creates it after you verify the path), and removals are
moved into timestamped quarantine/recovery directories rather than being deleted immediately.
`UpdateCarCard --recover <journal.tsv>` can roll back an interrupted journaled update.

## Building and testing

The repository pins the .NET 10 SDK in `global.json` and centralizes NuGet versions in
`Directory.Packages.props`. The four utilities that automate iTunes
(`BackSyncPlaylists`, `DumpArtworkSizes`, `FixArtwork`, and `FixiTunesDupes`) contain COM references,
which require the full Visual Studio MSBuild host. Build everything in Visual Studio, or from a
Developer PowerShell with:

```
msbuild MusicLibraryTools.sln /restore /p:Configuration=Release
```

All projects that do not require iTunes COM automation are also collected in a solution filter and
can be built with the cross-platform .NET SDK:

```
dotnet build MusicLibraryTools.Portable.slnf
```

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

- The parsers are deliberately hand-written rather than using TagLibSharp: full control over
  what gets read (a library scan reads only what it needs), byte-exact round-tripping on
  save, and no dependency drift. TagLibSharp appears only in throwaway diagnostic tools.
- Parsing is optimized for scanning huge libraries over a network share: large sequential
  read buffers, minimal per-file round trips, lazy artwork decoding, and no reflection or
  exception-driven control flow on the hot path.
- Saves are conservative: unknown tag frames/atoms/blocks are preserved, artwork
  round-trips byte-for-byte, and in-place fast paths fall back to full rewrites whenever
  structure changes.
