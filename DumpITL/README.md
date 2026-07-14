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
| `+108` | type-514 playback-state token, actively mirrored at `mhgh +124` | preserve |
| `+112` | UTC Mac-epoch library modification time | patch outer and mirror when the body changes |

Section 16 contains `mfdh`, a little-endian semantic mirror of the envelope. Its `+8` word is the
uncompressed total length including the outer header. Not every same-numbered word is a mirror:
in particular, `mfdh +108` is zero in the full corpus while outer `+108` is mirrored at `mhgh +124`.

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

Track `mith +168` is the Apple Store/catalog item ID and is duplicated at `+428`. Eighty current
tracks prove the mapping because type-514 uses the exact decimal value as its outer playback-state
key; one additional decimal plist key is stale. This field is exposed read-only until native edit
semantics are established.

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
- `+88` stays unchanged across all of those operations and is not a required structural aggregate.
  Fresh reversible runs held it at `100` with exact binary playlist counts 99, 100, and 101; 101
  tracks sharing one album/artist; 101 distinct albums sharing one artist; and 101 distinct albums
  and artists. A further 257-track run created 257 distinct albums and artists, crossed the 128 and
  256 boundaries, and assigned per-type `mhoh +16` value keys through 257 while `+88` remained 100.
  Each candidate reopened and returned to its baseline entity counts. This excludes `+88` from
  modeled entity counts, tested capacity boundaries through 256, and semantic value-key high-water;
- `+108` and its active `mhgh +124` mirror become zero when iTunes removes the type-514 playback-state
  plist and do not change during metadata or structural mutations. `mhgh +212` retains the prior
  token after that native resave, so it is a historical or otherwise inactive copy. The token matches
  none of CRC-32, CRC-32C, Adler-32,
  FNV-1/FNV-1a, DJB2, SDBM, Jenkins, Murmur3, or truncated MD5/SHA values over the payload,
  enclosing `mhoh`, `mhgh`, section, or library-ID-salted variants in either byte order;
- `mhgh +233` changes from zero to one on the first native library mutation and then stays set;
- metadata edits advance both library `+112` and track `mith +32`; iTunes may also refresh opaque
  search/index section 20 and track `+656` caches;
- controlled audio and video bookmark changes update fixed `mith` fields but do not create type-514;
  direct play-count changes likewise stay in `mith`; and partial or completed video playback creates
  or refreshes section 20 and advances `mhgh +252`, still without type-514 or `+108`. This suggests
  the preserved corpus plist is legacy or sync-specific state rather than current local playback
  state;
- the preserved type-514 plist has 2,024 dictionaries containing bookmark time (`bktm`), played
  state (`hbpl`), play count (`plct`), timestamp (`tstm`), and record version. Eighty of its 81
  decimal outer keys resolve to current Store Item IDs at `mith +168/+428`; the remaining decimal
  key is stale. Its other 1,943 keys are 32-character hexadecimal identities. Exhaustive whole-field
  MD5 checks resolve only five current identities: four title keys (also episode/filename where equal)
  and one `Artist - Title` key. Only one entry's `plct`/`tstm` pair aligns with current fixed playback
  state, and none of the 50 nonzero `bktm` values exactly matches the controlled-bookmark word at
  `mith +624` when rounded to milliseconds.
  The bulk of this plist is therefore historical/device state or uses identities no longer present;

Still unresolved:

- the semantic name and allocation policy behind envelope `+88`;
- the exact algorithm producing the type-514 playback-state token at `+108`/`mhgh +124`, and the
  lifecycle of the retained copy at `mhgh +212`;
- the semantic identity behind the 1,943 hexadecimal type-514 playback-state plist keys (they are
  not raw or reversed 16-byte windows in the fixed track header; five current identities match MD5
  of a video filename/title or `Artist - Title`, while the other entries appear stale or use another
  branch; Store Item ID and StoreIdentifier hashes add no matches);
- complete meanings of the ten `mprh` payload records and other opaque section types.

## Commands

Use `validate` for structural/referential checks, `compare` for structure-aligned before/after
diffs, and `snapshot` for deterministic JSON containing envelope/mirror values, parsed counts,
numeric IDs, section layout, `mhgh` candidates, playback-state hashes, and diagnostics. The `re`
family inventories fields, sections, IDs, blobs, aggregates, and playback-state correlations. Run
the executable without arguments for the complete command list.

Private corpus regressions use `DUMPITL_CORPUS_ITL` and `DUMPITL_CORPUS_XML`. Native acceptance uses
`Run-ItunesAcceptance.ps1`; it copies the candidate to `C:\tmp\DumpITL-acceptance`, hashes the live
library, backs up the binary iTunes preference plist, switches only the `Database Location`, opens
the copy through COM, validates the re-saved copy, and restores preferences byte-for-byte in a
`finally` block. Its Shift-launch chooser automation requires an interactive Windows desktop; use
`-ManualChooser` when a person will select the printed disposable path. Manual mode copies that
full path to the Windows clipboard before prompting.

Add `-MultiPhase` to a reversible experiment to capture `00-baseline`, `01-mutated`, `02-reopened`,
and `03-reversed` libraries. Only the first launch asks for a library; subsequent phases reopen the
selected disposable copy normally. Each phase has snapshot JSON, validation output, and an aligned
diff from its predecessor, while `experiment.json` records the phase files, status, failure message,
and restoration guards.

`CreatePlaylistsToCount` targets an exact parsed binary playlist count. `ImportFilesToCount` uses
`-ExperimentTargetCount` and accepts either one media file to copy repeatedly or a directory of
pre-tagged fixtures. Playback probes include `SetFirstTrackBookmark`, `SetFirstTrackPlayCount`, and
`PlayFirstTrackAtPosition`. All manual chooser prompts place the disposable `.itl` path on the
clipboard.

On iTunes 12.13.10.3, no-op and metadata-only rewrites of the full corpus opened successfully. In
the disposable empty-library laboratory, writer-created playlists, tracks, albums, artists, foreign
keys, and built-in playlist memberships also opened and re-saved successfully. iTunes retained all
intended values while compacting its internal numeric IDs. Every resaved candidate validated with
no diagnostics, and the harness verified the live-library and preference backups byte-for-byte.

## Remaining research plan

The final reverse-engineering work should proceed in this order:

1. **Implemented:** Extend the guarded acceptance harness to capture several phases after one manual library choice:
   baseline, one mutation, quit/save, reopen/save, and reversal. Emit a machine-readable summary of
   envelope values, `mhgh` candidates, counts, IDs, validation diagnostics, and aligned byte diffs
   for every phase. Continue restoring the live-library hash and iTunes preference bytes in a
   `finally` block.
2. **Aggregate and key-allocation exclusions completed; semantic name remains:** Envelope `+88` is
   proven unnecessary for structural writes and excluded from track, playlist, album, artist,
   section, and semantic value-key counts, including reversible boundary runs through 257 entities.
   Preserve it opaquely unless a future independent library exposes a repeatable semantic rule.
3. **Ordinary playback paths tested; no plist generated:** Deliberately create a type-514 playback-state plist using longer audio and video fixtures. Test
   one native operation at a time: play/stop position, remembered bookmark, `BookmarkTime`, play
   count, rating, and loved state. Snapshot before and after each operation and its reversal so the
   responsible plist entry and `mhgh` changes can be isolated.
4. **Decimal Store Item ID branch resolved; hexadecimal branch remains:** Derive the type-514 key identity with a one-track, one-variable matrix. Change title, artist,
   filename, path, persistent ID, numeric track ID, library persistent ID, and media kind separately.
   Test normalized byte encodings and common digest families, and also search the complete decoded
   body for source identity material. A mapping is proven only when the key changes predictably and
   returns when the source value is restored.
5. Classify `+108`/`mhgh +124` from multiple controlled type-514 payload/token pairs. Test whether it
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
