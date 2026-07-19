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
| `+88` | next native library child-object ID (`100` empty, `5915` full corpus) | preserve |
| `+92` | maximum encrypted prefix | preserve |
| `+100` | signed base UTC offset in seconds | preserve |
| `+108` | account DSID associated with type-514 playback state; mirrored at `mhgh +124` | preserve |
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
Each item is a timestamped playlist-entry reference: `+8` is a Mac-epoch timestamp, `+12` is an
`mtph` entry ID, and the 64-bit value at `+16` is the owning playlist persistent ID. Unknown
sections remain opaque during writing.

Static inspection of iTunes 12.13.10.3 shows the corresponding in-memory elements are a timestamp
and two live object links. The serializer resolves those links back to the current entry ID and
owning playlist persistent ID, explaining why native ID compaction rewrites `+12` without changing
the other values. The sole runtime producer is called from the playback-event handler. It requires
two native eligibility bits, rejects another, excludes several special playlist classes,
de-duplicates the current entry, drops oldest entries until fewer than ten remain, appends the
current timestamp, and marks the library dirty.

The consumer constructs the Windows taskbar Jump List's **Resume Playing** category. It instantiates
the system `EnumerableObjectCollection` COM class, walks the history newest-first, and creates one
shell link per surviving entry with `/PlayItem <playlist-id> <entry-id>`, the track title, and a
media-kind icon. Invoking a link removes its old history instance before playback can refresh it.
Thus `mprh` is the persistent Windows Resume Playing Jump List history, not a generic playlist-edit
log. The human-readable meanings of the producer's internal eligibility bits remain unknown.

Records cache `mhoh` child counts at `+12`; playlists cache `mtph` membership counts at `+16`.
Strings use a 16-byte preamble and encoding 1 (UTF-16LE), 2 (UTF-8), or 3 (Latin-1). Empty strings
are valid zero-byte string payloads when the encoding word is recognized.

Track `mith +500` is a secondary global identifier and always equals the primary track ID at `+16`
plus one. Playlist `miph +3392` is another globally unique numeric ID. Native iTunes may compact
these IDs and playlist-entry IDs on a later save, so the writer guarantees uniqueness and correct
links rather than attempting to preserve native allocation gaps.

### Type-20 media references

Section 20 is an `mlqh` collection of `miqh` media/playback-reference records. Unlike ordinary list
headers, `mlqh +8` is zero, `+12` counts optional list-level `mhoh` metadata, and `+16` counts the
`miqh` records. The observed files have no list metadata. Its 64-bit values at `+20`
and `+28` are absolute decoded-body anchors. Repeated native saves prove they are always the start
of the type-13 `msdh` section plus `0x90` and `0xF0`, respectively; the latter can legitimately fall
beyond a short empty type-13 section. The writer refreshes both anchors after layout changes and
`Validate()` reports stale values and mismatched record counts.

Each observed `miqh` has two cached display strings: type 702 is track title and type 703 is artist,
album, or the combined `Artist — Album` display. The 128-bit identity beginning at `miqh +28`
contains source-library and source-track persistent IDs. Bytes `+80/+84` are four-character source
tags: corpus device records use `FILE`/`iPod`, while controlled local video playback uses
`FILE`/`lib `. The corpus's optional identity at `+124/+132` contains the current library and mapped
local-track persistent IDs, confirming that those two records map iPod sources to library tracks.
`+72` is a Mac-epoch event timestamp where nonzero. Controlled video-position playback created a
local record. All records in one native save share the 64-bit value at `+140`; it changes with each
fresh iTunes process and has canonical Windows heap-pointer form. It is therefore a process-local,
pointer-derived runtime-context token rather than a durable semantic ID; the exact source object is
unknown. Two independent video runs write `mhgh +252` 2–4 seconds after the `miqh +72` event, while
metadata edits and later reopen/reversal saves leave it unchanged, identifying `mhgh +252` as the
last media-reference/playback update timestamp. The exact queue/import lifecycle and remaining flags
are not yet proven, so records stay opaque. The validator checks current-library source and mapped-
track foreign keys, and the writer rejects a track removal that would leave one dangling rather than
guessing the native record-removal policy.

### Type-23 global state

Section 23 is an `stsh` global-state container with a 96-byte header. Its `+8` word is zero and
`+12` counts optional `mhoh` children. Native iTunes 12.13 code emits at most two children: one each
of types 900 and 901. Both the private full corpus and the native empty fixture have a zero count,
so the values' application-level meanings remain unresolved. Traversal and validation handle empty
and populated layouts, preserve payloads opaquely, and reject stale counts, duplicate values, or
unproven child types before writing.

### Type-14 special playlists

Section 14 is a second `mlph`/`miph` playlist-shaped partition, not part of the ordinary type-2
playlist count. Static inspection of iTunes 12.13 shows the library serializer invokes the same
routine for section 2 and then section 14. It routes internal playlist object kinds `0x20` and
`0x23` to type 14 and all other eligible playlist objects to type 2; only the type-2 count updates
the library's public playlist aggregate. The observed full and empty libraries contain zero type-14
records. The parser, validator, and writer now enforce its 92-byte `mlph` header, item count, and
`miph` child signatures while preserving the records opaquely. The user-facing meanings of kinds
`0x20` and `0x23`, and their mutation lifecycle, remain unresolved.

### Type-21 podcast stations

Section 21 is an `mlsh` list of `msph` podcast-station records. Native serialization proves the
44-byte `mlsh` word at `+8` counts stations, each 48-byte `msph +12` counts its data objects, and a
written station contains exactly one `mhoh` type 800 XML settings plist. The corpus has one station,
whose plist title and UUID are `Most Recent` and `PlaylistMostRecent`; it also carries episode,
sorting, podcast-selection, cloud-sync, and update-date settings. Type 800 remains exposed as
`PodcastSettingsPlist`. The validator and writer now enforce the proven container shape while
preserving the XML opaquely; station editing is not yet supported.

Track `mith +168` is the Apple Store/catalog item ID and is duplicated at `+428`. Eighty current
tracks prove the mapping because type-514 uses the exact decimal value as its outer playback-state
key; one additional decimal plist key is stale. This field is exposed read-only until native edit
semantics are established.

Track `mith +703` bit 1 (`0x02`) is Loved. `ItlTrack.Loved` reads it, editable records expose
`GetLoved`/`SetLoved`, and `set-loved` creates a validated disposable writer candidate.

For semantic track, album, and artist strings, `mhoh +16` is a per-type value key: equal values
reuse a key and new values receive the next key. Location and FileUrl instead retain structural
subtypes 1 and 2. Fresh native user playlists used child key 3 and matured to 4 on the next save.

## ITC2 artwork cache

`ITLTools` now parses and extracts the Windows iTunes `.itc2` artwork-cache format. ITC2 framing is
big-endian. A file starts with a 284-byte `itch`/`artw` container header; the big-endian word at
offset zero is that header length. Starting there, every image is a length-delimited `item` record.
The observed item header length is 196 bytes and includes the leading record-length word and the
trailing `data` tag, so the image payload starts at `record offset + 196`.

| Record offset | Meaning |
| ---: | --- |
| `+0` | total item length, including header and image payload |
| `+4` | `item` signature |
| `+8` | item header length (`196`) |
| `+12/+16/+20` | invariant observed words `1/2/1`; application meanings unresolved |
| `+24` | source kind: observed `0` local and `2` Cloud Purchases |
| `+28` | library persistent ID, big-endian `u64` |
| `+36` | artwork persistent ID, big-endian `u64` |
| `+44` | origin FourCC: observed `locl` and `CLPU` |
| `+48` | pixel/storage FourCC or code: `bGRA`, `ARGb`, or numeric `13` for observed JPEGs |
| `+56/+60` | image width and height |
| `+76/+80` | secondary width and height for raw local images; zero for compressed cloud images |
| `+192` | `data` signature; payload follows at `+196` |

The reference corpus contains 3,917 valid files (2,746,729,792 bytes) and 9,355 image records with
no parse failures. All 9,355 records carry the current ITL library ID. All filenames and paths obey
the following layout:

- the directory immediately below a cache category is the 16-hex library persistent ID;
- the next three directories are the low twelve bits of the artwork ID, one nibble at a time from
  least significant to most significant, written as **decimal** `00` through `15`;
- local `Cache` files are `<library-id>-<track-persistent-id>.itc2`. The filename's second ID and
  every item header's artwork ID are identical and resolve to a current `mith +128` track persistent
  ID. The corpus has 2,719 such files, each for a distinct album record;
- every local file contains three lossless raw BGRA images whose bounding boxes are 128, 256, and
  400 pixels. Non-square artwork preserves aspect ratio, so one dimension can be below the bound;
- `Cloud Purchases` files contain one JPEG, most commonly 600x600. Their first filename component
  is the high-shard decimal pair followed by the 16-hex artwork ID; the second 16-hex identity does
  not resolve to a current track in this corpus and remains unnamed. There are 1,198 such files;
- `Custom`, `Download`, `Generated`, and `Store` are empty in the reference tree, so their record
  variants remain unobserved.

Of 47,586 current tracks, 47,577 have a nonzero ITL Artwork Count, while the local cache holds one
file for only 2,719 distinct albums. The cache is therefore a rendered album-level working set, not
a complete store of every track's embedded artwork. Searching aligned 64-bit `mith` header values
finds every local filename identity at the known primary persistent ID `+128` and ten additional
matches at `mith +316`. In all ten, `+316` duplicates that same track's own `+128` ID rather than
pointing to another track. Across the whole library, however, `+316` is nonzero on 4,542 tracks and
only 69 values equal or resolve to a current track ID. The ten cache intersections may therefore be
a sparse identity mirror or a numeric coincidence in a composite field; they do not demonstrate an
artwork-source reference.

`Itc2File` parses metadata without loading image payloads. `Itc2Item.Extract` copies JPEG/PNG bytes
unchanged and wraps raw BGRA in a top-down 32-bit BMP without color conversion or recompression;
observed `ARGb` is reordered losslessly into BMP BGRA. The implementation validates every boundary,
signature, raw-pixel byte count, library ID, filename convention, and shard path. The independently
developed [itc2 extractor](https://gist.github.com/hidez8891/aa296f4f9538782b305ccfd7f27ff513)
corroborates the container tags, big-endian framing, 196-byte image prefix, BGRA/ARGB layouts, and
compressed-payload branch, but does not identify the persistent IDs or sharding rules established
from this corpus.

## Smart playlists

Playlist `mhoh` type 102 is the 112-byte big-endian Smart Info preference block. It carries live
updating, rule matching, checked-only filtering, limit enable/unit/size, sort field, and sort
direction. Type 101 is Smart Criteria: a recursive big-endian `SLst` rule set with a 136-byte header,
56-byte rule headers, and typed values. Confirmed value families are UTF-16BE strings, 68-byte
integer/boolean/date/media/cloud/location records, 68-byte playlist references, and nested rule
sets. Field IDs and operator bits agree with the independently implemented
[rclancey/itunes smart-playlist parser](https://github.com/rclancey/itunes/blob/master/loader/smart.go)
and the older [libgpod smart-playlist documentation](https://tmz.fedorapeople.org/docs/libgpod/libgpod-Smart-Playlists.html).

`ItlSmartPlaylist` exposes typed decode/encode models and factories for proven rule families.
`ItlPlaylist.Smart` provides read access; `ItlDocument.SmartPlaylistOf` reads criteria and
`SetSmartPlaylist` edits an existing smart playlist or converts a manual one. Conversion inserts
zero-key Smart Criteria/Info fields without changing the fixed playlist header.
`ItlDocument.AddSmartPlaylist` creates a new smart playlist by cloning a
native smart-playlist template and requires an explicit initial membership snapshot. Parse then
encode is byte-identical for all 15 smart playlists in the private corpus. Unknown field/operator
values retain their raw bytes.

Native iTunes accepted a length-changing edit to a disposable user-created smart playlist, retained
the new nested rule `Genre contains "Country"`, and recalculated its membership. It also retained a Smart
Info live-updating toggle and a factory-created `Play Count > 5` integer rule. The latter re-exported
to XML with Smart Info and Smart Criteria byte-identical to the resaved ITL blobs. A built-in Music
rule edit was accepted but regenerated to the canonical Music mask on save, proving distinguished
built-ins are system-owned and should not be used as editable-rule templates.

Two complementary native probes establish that `0xA4` is Boolean for the observed rule form but do
establish its semantic behavior. Scoped to Movie and TV Show media, `is 1` selected none of the
local videos and `is 0` selected all 14 surviving local videos. iTunes retained both 68-byte values
and exported byte-identical criteria. Applying the Movie/TV scope also moved the probe playlist out
of the Music group and into the Movies/TV UI. Static inspection then closed the semantic mapping:
iTunes' native field descriptor for track key `is-ams-video` contains smart field ID `0xA4` and
Boolean type 1. The field is exposed as `ItlSmartField.AppleMediaServicesVideo`; a positive sample
would still be useful corpus coverage but is no longer required to name or type the rule.

Operator `0x0800` is now modeled as `AllowedAndRequiredBits`. Two guarded native recalculations used
the same user smart playlist and complementary media masks. Operands `33/32` selected exactly the
39 music videos; operands `1/32` selected none. iTunes retained each rule and exported byte-identical
criteria. Together with the independent libgpod description, this proves a media kind matches when
it uses only bits from the first mask and intersects the second mask. Reopening alone preserved the
old membership snapshot, while opening Edit Smart Playlist and clicking OK triggered recalculation.

Two guarded manual-to-smart conversions proved that there are no additional fixed-header flags. The
writer added only zero-key Smart Criteria and Smart Info fields to the 7-member `Cafe Disco` and
70-member `2000s` manual playlists. iTunes recognized both as smart, retained every member,
re-exported both pairs of blobs byte-identically, and left each playlist's manual-header
classification bytes unchanged.

Native creation and reopen proved playlist-reference rules use a 68-byte comparison value rather
than a bare 8-byte persistent ID. The referenced playlist ID is duplicated at value offsets `+0`
and `+24`, with big-endian presence markers of one at `+20` and `+44`. The Windows UI also wraps the
rule in its media-kind scope. The typed factory reproduces that value layout; an 8-byte candidate
was deliberately retained as negative evidence after iTunes discarded its Smart Criteria. The
corrected writer-created playlist-reference rule then opened and re-saved successfully: iTunes
retained all 7 seeded members, and the resulting 112-byte Smart Info and 892-byte Smart Criteria
were byte-identical to its XML export.

A four-phase native creation experiment proved that new Smart Info and Smart Criteria fields both
use child key zero. A writer-created smart playlist with the same nested media-kind rules was then
opened and re-saved by iTunes with no validation diagnostics: its seeded track membership and smart
blobs survived, while iTunes replaced only allocation-sensitive playlist and entry IDs. A companion
candidate with no seeded entries remained empty, proving that reopening a live smart playlist does
not by itself materialize its membership. New smart playlists therefore clone a native smart header
and carry a caller-supplied membership snapshot rather than depending on iTunes to populate it.

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
- static inspection resolves `+88` as a native per-parent child-object allocator. The library-object
  constructor initializes the field to 100; attachment paths atomically fetch-and-increment it and
  assign the returned ID to a child; the envelope serializer reads it directly and the loader
  restores any nonzero stored value. `ItlEnvelope.NextLibraryChildId` exposes the semantic name while
  `RawWord88` remains for compatibility. The writer preserves it because modeled record IDs use a
  separate proven allocation domain;
- `+108` and its active `mhgh +124` mirror become zero when iTunes removes the type-514 playback-state
  plist and do not change during metadata or structural mutations. `mhgh +212` retains the prior
  value after that native resave. Static native-code inspection resolves these values as Apple
  Directory Services IDs (DSIDs), not integrity hashes: the library object initializes `+212` from
  the active iTunes account field, and that global field is consumed under the literal key `dsid`.
  The library parser and serializer copy the full 64-bit `mhgh +124` and `+212` values directly;
  the outer envelope stores the low 32-bit active DSID. As additional negative evidence, the corpus
  DSID matches none of CRC-32, CRC-32C, Adler-32,
  FNV-1/FNV-1a, DJB2, SDBM, Jenkins, Murmur3, or truncated MD5/SHA values over the payload,
  enclosing `mhoh`, `mhgh`, section, or library-ID-salted variants in either byte order;
- changing one type-514 record-version digit from `4` to the otherwise observed value `5`, without
  changing payload length or the preserved DSID, produced a structurally valid candidate that iTunes
  rejected twice before exposing COM and left byte-identical. This proves the plist has native
  semantic validation, but no longer implies a checksum relationship. The writer still rejects all
  type-514 additions, removals, and byte edits until its key identities and valid update protocol are
  proven;
- `mhgh +233` changes from zero to one on the first native library mutation and then stays set;
- metadata edits advance both library `+112` and track `mith +32`; iTunes may also refresh track
  `+656` caches and the proven cross-section anchors in media-reference section 20;
- controlled audio and video bookmark changes update fixed `mith` fields but do not create type-514;
  direct play-count changes likewise stay in `mith`; and partial or completed video playback creates
  or refreshes a type-20 local-library media reference and advances `mhgh +252`, still without
  type-514 or `+108`. This suggests
  the preserved corpus plist is legacy or sync-specific state rather than current local playback
  state;
- reversible rating and unplayed-state runs also leave type 514 absent. Rating changes `mith +108`;
  unplayed state changes `mith +238` and the playback/bookmark word at `+624`. The Windows COM
  interface does not expose loved state. Two guarded manual UI runs independently toggled Love and
  Unlove on the same track, proving the flag is bit 1 (`0x02`) at `mith +703`; both directions
  survived reopen, reversed to `0x00`, and left type 514 absent. `ItlTrack.Loved` and the editable
  `GetLoved`/`SetLoved` accessors expose the field. A writer-created `0x00 -> 0x02` candidate then
  opened and re-saved in iTunes with the byte retained and no validation diagnostics;
- the preserved type-514 plist has 2,024 dictionaries containing bookmark time (`bktm`), played
  state (`hbpl`), play count (`plct`), timestamp (`tstm`), and record version. Eighty of its 81
  decimal outer keys resolve to current Store Item IDs at `mith +168/+428`; the remaining decimal
  key is stale. Its other 1,943 keys are 32-character hexadecimal identities. Exhaustive whole-field
  MD5 checks resolve four distinct current identities as video title keys (also filenames where
  equal). The proven combined-field construction resolves two more current keys as Title plus Artist,
  for six distinct exact MD5 identities total. Several other audio tracks produce one of the video
  digests only when tested with a non-native `Artist - Title` display string. Only one entry's
  `plct`/`tstm` pair aligns with current fixed playback
  state, and none of the 50 nonzero `bktm` values exactly matches the controlled-bookmark word at
  `mith +624` when rounded to milliseconds.
  The bulk of this plist is therefore historical/device state or uses identities no longer present;
- static inspection resolves the type-514 key generator and importer. The generator first returns a
  decimal Store Item ID when one exists. Otherwise its ordinary branch creates an MD5 context and
  hashes the UTF-8 Title (`mhoh` 2), Artist (`mhoh` 4), and Album (`mhoh` 3), in that order and
  without separators. Title is required; absent Artist or Album values are omitted. It emits the
  16-byte digest as 32 lowercase hexadecimal characters. A special media-flag branch instead hashes two
  8-bit fields after removing trailing spaces and collapsing duplicate `/` characters except the
  pair in `://`. The importer uses the same helper: it hashes each plist key for lookup and then
  directly compares it with the generated per-track key. The 80 live decimal matches and six exact
  ordinary MD5 matches in the corpus independently validate that output branch. The special branch
  is the podcast identity: it prefers Podcast Feed URL (`mhoh` 58), falls back to Podcast RSS URL
  (`mhoh` 37), and then hashes Podcast Episode URL (`mhoh` 19). Its precise media-flag selection rule
  remains to be mapped;
- static inspection of iTunes 12.13.10.3 places `plct`, `hpbl`, `hbpl`, `tstm`, and `bktm` beside
  `com.apple.upp`, `playedState`, `bookmarkTimeInMS`, and `Play Data.plist`. The corresponding import
  routine converts `bktm` seconds to the millisecond bookmark field and maps the other abbreviated
  keys into track playback state. Independent libgpod source identifies Play Data/PlayCounts plists
  as iPhone/iPod synchronization input keyed by the device track's 64-bit persistent ID. This narrows
  type 514 to imported device/Universal Playback Position state rather than ordinary local playback;
- all ten type-15 `mprh` records resolve to entries in the built-in Music playlist. Nine timestamps
  were written seconds apart on 2022-06-06 and one on 2024-06-22. Across native no-op, metadata,
  smart-rule, and rewrite saves, iTunes preserves each timestamp and playlist persistent ID while
  rewriting `mprh +12` whenever it compacts the referenced `mtph` entry IDs. The validator now checks
  both foreign keys, snapshots include all four payload words and hashes, and the writer rejects a
  structural mutation that would leave a dangling record. Native-code inspection further proves
  this is the de-duplicated newest-ten Windows **Resume Playing** Jump List history populated by the
  playback-event path. Native code creates `/PlayItem` shell links from these entries and removes an
  old entry when its link is invoked. The exact names of its internal eligibility bits remain unknown;
- smart operator `0x0800` evaluates allowed/required media masks: `33/32` selected all 39 music
  videos and `1/32` selected none after native confirmation. Both criteria remained byte-identical
  to the XML export, so the operator is exposed as `AllowedAndRequiredBits`.

Still unresolved:

- the valid native update protocol for type-514 playback state and the exact event that clears its
  active DSID while retaining the account DSID at `mhgh +212`;
- the precise media-flag selection rule for the podcast playback-key branch, and reconciliation of
  the predominantly stale 1,943-key corpus with historical metadata;
- the human-readable meanings of the native eligibility bits for the `mprh` Resume Playing history,
  the exact lifecycle/flags and runtime-context source of type-20 media references, and the meanings of other
  opaque section types.

## Commands

Use `validate` for structural/referential checks, `compare` for structure-aligned before/after
diffs, and `snapshot` for deterministic JSON containing envelope/mirror values, parsed counts,
numeric IDs, section layout, `mhgh` candidates, playback-state hashes, and diagnostics. The `re`
family inventories fields, sections, IDs, blobs, aggregates, `mprh` links, playback-state
correlations, and `smartmembers` membership/header candidates. Research snapshot schema 2 includes
a nullable type-15 record matrix. Run
the executable without arguments for the complete command list.
`artwork-file <file.itc2> [out-dir]` prints every item and optionally extracts its image.
`artwork-cache <library.itl> <Album Artwork> [max-files]` inventories a whole cache or a bounded
sample and correlates its library, artwork, filename, shard, album, track-header, and image-format
identities without reading image payloads.
For changed ranges of up to 16 bytes, `compare` includes the before/after hex values so one-bit
experiments can be interpreted directly from the research bundle.
`smart-mask-probe` and `smart-field-probe` create disposable criteria for guarded native evaluation;
they are research commands, not general-purpose smart-playlist editors.

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
pre-tagged fixtures. Playback probes include `SetFirstTrackBookmark`, `SetFirstTrackPlayCount`,
`SetFirstTrackRating`, `SetFirstTrackUnplayed`, and
`PlayFirstTrackAtPosition`. All manual chooser prompts place the disposable `.itl` path on the
clipboard. `ManualCreateSmartPlaylist` waits for one specifically named native smart playlist; the
harness captures it, reopens it, removes it through COM, and records every phase. Pass
`-ManualInstructions` to describe a one-variable native rule such as `Playlist is Cafe Disco`.
`ManualRefreshSmartPlaylist` waits for confirming an existing rule to change its COM membership,
which distinguishes native rule evaluation from a merely preserved membership snapshot.
`ManualToggleFirstTrackLoved` identifies one track by name, artist, and album and displays a guarded
dialog for toggling its Love status once in the mutation phase and once again in the reversal phase.
It requires `-MultiPhase`; unlike the smart-playlist probes, it does not depend on a named playlist.

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
2. **Envelope `+88` resolved:** Native code identifies it as the next child-object ID on the library
   object, initialized to 100 and advanced with an atomic allocator. It is not any modeled entity,
   section, or semantic value-key count; reversible boundary runs through 257 entities leave it
   unchanged. The writer preserves this native runtime high-water counter.
3. **Ordinary playback paths tested; no plist generated:** Play/stop position, remembered audio and
   video bookmarks, completed playback, play count, rating, and unplayed state all update ordinary
   track fields without creating type 514. Two reversible manual Loved-state runs additionally prove
   Loved is `mith +703` bit 1 and does not create type 514. Static evidence identifies the plist as
   imported Apple device/Universal Playback Position state. Capturing a fresh payload/DSID pair now
   requires a disposable iPhone/iPod sync or an independently supplied Play Data/PlayCounts artifact.
4. **Ordinary playback key algorithm and fields resolved:** Native serialization maps its required and
   optional UTF-16 inputs to Title, Artist, and Album. `ItlPlaybackStateKey` exposes the proven decimal
   Store Item ID, ordinary lowercase-MD5, and podcast URL construction. Use a one-track,
   one-variable device-import matrix to map the podcast branch's native media-flag eligibility test.
   A mapping is proven only when the generated key changes predictably and returns when the source
   value is restored.
5. **`+108`/`mhgh +124` and `mhgh +212` resolved as DSIDs:** Native code copies the active iTunes
   account value under the literal `dsid` key and caches it at the library field serialized to
   `mhgh +212`; the corpus's active `+124` value and outer low-32-bit mirror are identical. Preserve
   both identities. Continue rejecting type-514 mutations because the plist's valid update and
   hexadecimal key-identity rules, rather than a token-recomputation algorithm, remain unproven.
6. **Smart-playlist decoding, editing, typed construction, and safe creation completed:** Smart Info and
   recursive Smart Criteria decode and byte-exactly re-encode; string, integer, Boolean, date,
   relative-date, media/location-mask, playlist-reference, nested, and unknown raw values are typed
   or preserved. Native iTunes retained a length-changing string edit, a live-updating toggle, and a
   factory-created integer rule while regenerating membership. It also accepted and normalized a
   new writer-created smart playlist cloned from a native smart template, with explicit initial
   membership; native creation proves the type 101/102 child keys are zero. A native
   playlist-reference capture established its 68-byte value layout, and the corrected typed factory
   survived a native re-save with byte-identical XML blobs and membership. Complementary native
   evaluations resolve operator `0x0800` as allowed/required media masks. Complementary field
   `0xA4` probes prove Boolean behavior, and the native descriptor maps it directly to track key
   `is-ams-video`, now exposed as `AppleMediaServicesVideo`. Finally, a native re-save proves
   manual-to-smart conversion requires only the two zero-key blobs and no header edits;
   `SetSmartPlaylist` now supports it.
7. **Type-20 media-reference structure partially resolved:** `mlqh +16` is the record count;
   `+20/+28` are proven decoded-body anchors that the writer now refreshes; `miqh` source and optional
   destination library/track identities, display fields, timestamps, and `FILE`/`iPod` versus
   `FILE`/`lib ` tags are mapped. `miqh +140` is a pointer-derived process-local context token and
   `mhgh +252` is the last media-reference/playback update timestamp. Use isolated local playback
   and disposable-device import/removal snapshots to determine record retention and flags.
8. **Type-23 container structure resolved, values unresolved:** `stsh +12` counts optional `mhoh`
   global-state objects and native code emits at most one each of types 900 and 901. The writer and
   validator enforce this structure while preserving the payloads. Capture a native fixture in
   which either value is present before assigning semantic names or exposing mutation APIs.
9. **Type-14 partition structure resolved, kinds unresolved:** the second `mlph` contains ordinary
   `miph` serialization selected for native playlist kinds `0x20`/`0x23`, and does not contribute to
   the public playlist aggregate. Capture one nonempty native example to name those kinds and map
   their reference/removal behavior before exposing them as editable playlists.
10. **Type-21 podcast-station structure resolved:** `mlsh` contains counted `msph` records with one
    type-800 settings plist apiece. Capture create/edit/delete snapshots for a disposable station
    before exposing XML mutation or station lifecycle APIs.
11. Promote findings into writer behavior only after repeated native evidence. Add a synthetic
   fixture and regression test for every proven rule, keep unresolved bytes opaque, and make an
   unsupported mutation fail before saving rather than synthesizing metadata.

Completion criteria for these remaining areas are: type-514 has a safe preserve/update/reject policy; at least one
controlled playback entry is mapped to its track identity or conclusively shown to be blob-local;
smart playlists decode and re-encode byte-identically, supported edits and template-based creation
and manual conversion survive native resave, while unsupported rule forms fail before saving;
each conclusion is reproduced in two fresh libraries; every candidate opens and re-saves in iTunes;
and the harness confirms the live library and preferences are unchanged.
