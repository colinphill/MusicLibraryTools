# ITLTools and DumpITL binary format notes

`ITLTools` is the reusable class library for reading, diagnosing, editing, and rewriting the Windows
iTunes `iTunes Library.itl` format. `DumpITL` is a standalone command-line application over that
library and retains the existing command surface. Both preserve the established `iTunes.Binary`
namespace for source compatibility. The current evidence set is iTunes 12.13.10.3: an empty library
and a matching 47,511-track `.itl` / 47,494-track XML export. The private corpus is not committed.

## Confirmed envelope

The outer `hdfm` header is big-endian. Its body is a zlib stream whose first
`min(+92, compressed length)` bytes, rounded down to an AES block, are AES-128-ECB encrypted with
the long-standing `BHUILuilfghuila3` key. Decoded chunks are little-endian.

| Offset | Meaning | Writer policy |
| ---: | --- | --- |
| `+4` | envelope header length | preserve |
| `+8` | compressed file length | patch |
| `+48` | section count | patch outer and `mfdh` mirror |
| `+52` | library persistent ID (`u64`) | preserve |
| `+68` | track count | patch outer and mirror |
| `+72` | playlist count | patch outer and mirror |
| `+76` | album count | patch outer and mirror |
| `+84` | artist count | patch outer and mirror |
| `+88` | unresolved non-aggregate (`100` empty, `5915` full corpus) | preserve |
| `+92` | maximum encrypted prefix | preserve |
| `+100` | signed base UTC offset in seconds | preserve |
| `+108` | type-514 playback-state token, mirrored at `mhgh +120` | preserve |
| `+112` | UTC Mac-epoch library modification time | patch outer and mirror when the body changes |

Section 16 contains `mfdh`, a little-endian semantic mirror of the envelope. Its `+8` word is the
uncompressed total length including the outer header.

## Chunk and list conventions

Ordinary chunks begin with signature, header length, and total length. List headers instead use
their `+8` word as an item count. Confirmed editable lists are `mlth/mith`, `mlph/miph`,
`mlah/miah`, and `mlih/miih`. Section 13 is a parallel `mlth` whose track IDs refer to main tracks.

`mlrh` is the known exception to length-delimited record traversal: it contains exactly its declared
number of fixed 24-byte `mprh` items, and each `mprh +8` word is payload rather than a length.
Unknown sections remain opaque during writing.

Records cache `mhoh` child counts at `+12`; playlists cache `mtph` membership counts at `+16`.
Strings use a 16-byte preamble and encoding 1 (UTF-16LE), 2 (UTF-8), or 3 (Latin-1). Empty strings
are valid zero-byte string payloads when the encoding word is recognized.

Track `mith +500` is a secondary global identifier and always equals the primary track ID at `+16`
plus one. Playlist `miph +3392` is another globally unique numeric ID. Native iTunes may compact
these IDs and playlist-entry IDs on a later save, so the writer guarantees uniqueness and correct
links rather than attempting to preserve native allocation gaps.

For semantic track, album, and artist strings, `mhoh +16` is a per-type value key: equal values
reuse a key and new values receive the next key. Location and FileUrl instead retain structural
subtypes 1 and 2. Fresh native user playlists used child key 3 and matured to 4 on the next save.

## Evidence-backed fields and open questions

`ItlTrackFields` contains the fixed fields verified against XML, including identifiers, sizes,
duration, numbering, dates, counts, album/artist foreign keys, and flags. `ItlDataType` names known
variable objects. Smart-playlist objects 101/102 are byte-identical to XML Smart Criteria/Info.

Native one-change and reverse experiments proved:

- playlist creation/removal patches only the known playlist count, lengths, and modification time;
- importing/removing tracks patches the known track/album/artist counts and three built-in
  memberships (master, Downloaded, and Music);
- `+88` stays unchanged across all of those operations and is not a required structural aggregate;
- `+108` and `mhgh +120` become zero when iTunes removes the type-514 playback-state plist and do
  not change during metadata or structural mutations; the token is not CRC-32 or Adler-32 of the
  plist;
- `mhgh +233` changes from zero to one on the first native library mutation and then stays set;
- metadata edits advance both library `+112` and track `mith +32`; iTunes may also refresh opaque
  search/index section 20 and track `+656` caches.

Still unresolved:

- the semantic name and allocation policy behind envelope `+88`;
- the exact algorithm producing the type-514 playback-state token at `+108`/`mhgh +120`;
- the semantic identity behind type-514 playback-state plist keys (they are not raw or reversed
  16-byte windows in the fixed track header; five current identities match MD5 of a video
  filename/title or `Artist - Title`, while the other entries appear stale or use another branch);
- complete meanings of the ten `mprh` payload records and other opaque section types.

## Commands

Use `validate` for structural/referential checks and `compare` for structure-aligned before/after
diffs. The `re` family inventories fields, sections, IDs, blobs, aggregates, and playback-state
correlations. Run the executable without arguments for the complete command list.

Private corpus regressions use `DUMPITL_CORPUS_ITL` and `DUMPITL_CORPUS_XML`. Native acceptance uses
`Run-ItunesAcceptance.ps1`; it copies the candidate to `C:\tmp\DumpITL-acceptance`, hashes the live
library, backs up the binary iTunes preference plist, switches only the `Database Location`, opens
the copy through COM, validates the re-saved copy, and restores preferences byte-for-byte in a
`finally` block. Its Shift-launch chooser automation requires an interactive Windows desktop; use
`-ManualChooser` when a person will select the printed disposable path.

On iTunes 12.13.10.3, no-op and metadata-only rewrites of the full corpus opened successfully. In
the disposable empty-library laboratory, writer-created playlists, tracks, albums, artists, foreign
keys, and built-in playlist memberships also opened and re-saved successfully. iTunes retained all
intended values while compacting its internal numeric IDs. Every resaved candidate validated with
no diagnostics, and the harness verified the live-library and preference backups byte-for-byte.

## Remaining research plan

The final reverse-engineering work should proceed in this order:

1. Extend the guarded acceptance harness to capture several phases after one manual library choice:
   baseline, one mutation, quit/save, reopen/save, and reversal. Emit a machine-readable summary of
   envelope values, `mhgh` candidates, counts, IDs, validation diagnostics, and aligned byte diffs
   for every phase. Continue restoring the live-library hash and iTunes preference bytes in a
   `finally` block.
2. Characterize envelope `+88` with fresh libraries and reversible threshold experiments around
   counts 1, 99, 100, and 101. Vary one dimension at a time: playlists; tracks sharing an
   album/artist; tracks with distinct albums but one artist; and tracks with distinct albums and
   artists. Delete back below each threshold and reopen/save. This should distinguish a capacity or
   high-water value from an entity count or derived cache. Reproduce any apparent rule in a second
   fresh library before modeling it.
3. Deliberately create a type-514 playback-state plist using longer audio and video fixtures. Test
   one native operation at a time: play/stop position, remembered bookmark, `BookmarkTime`, play
   count, rating, and loved state. Snapshot before and after each operation and its reversal so the
   responsible plist entry and `mhgh` changes can be isolated.
4. Derive the type-514 key identity with a one-track, one-variable matrix. Change title, artist,
   filename, path, persistent ID, numeric track ID, library persistent ID, and media kind separately.
   Test normalized byte encodings and common digest families, and also search the complete decoded
   body for source identity material. A mapping is proven only when the key changes predictably and
   returns when the source value is restored.
5. Classify `+108`/`mhgh +120` from multiple controlled type-514 payload/token pairs. Test whether it
   is a checksum of the binary plist, plist XML, enclosing `mhoh`, or surrounding `mhgh` ranges; a
   revision or count; a random value; or a library-specific token. Until reproduced, preserve the
   field and reject playback-state mutations that would require recomputing it.
6. Promote findings into writer behavior only after repeated native evidence. Add a synthetic
   fixture and regression test for every proven rule, keep unresolved bytes opaque, and make an
   unsupported mutation fail before saving rather than synthesizing metadata.

Completion criteria for these remaining areas are: `+88` is identified or experimentally excluded
from required writer aggregates; `+108` has a safe preserve/recompute/reject policy; at least one
controlled playback entry is mapped to its track identity or conclusively shown to be blob-local;
each conclusion is reproduced in two fresh libraries; every candidate opens and re-saves in iTunes;
and the harness confirms the live library and preferences are unchanged.
