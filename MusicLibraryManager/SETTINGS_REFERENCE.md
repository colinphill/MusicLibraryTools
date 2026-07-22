# Settings reference

This document describes every setting exposed by the Music Library Manager Settings window. It covers what each value controls, how it is stored, and typical reasons to change it.

## Configuration model and save behavior

Most settings are stored in the active library configuration XML file. Settings are edited as a draft: use **Save** to replace the active file, **Save as** to write the draft to another file, or **Discard** to reload the last saved version.

Three details are worth knowing before editing:

- Relative paths are resolved relative to the configuration file, which makes a configuration directory portable as a unit.
- **Machine-local bindings companion** can move computer-specific paths out of the main XML file.
- **Appearance** is an application preference. It applies immediately and is not part of the library configuration.

Empty list rows are ignored when saving. Errors shown in the validation banner block saving; warnings, such as an offline library root, do not.

## Configuration tab

This tab manages configuration files rather than individual policy values.

| Control | Description | Example |
| --- | --- | --- |
| Active configuration | Displays the configuration currently loaded by the application. | Confirm that the production library is active before organizing files. |
| Open configuration | Opens an existing configuration XML file. | Open a configuration copied from another computer. |
| Edit full configuration | Reloads the active configuration into the full Settings form and moves to **Library roots**. Unsaved draft changes must be discarded first. | Start editing the configuration currently used by the application. |
| New configuration | Creates a new draft configuration. New configurations initially use the safe **Catalog only** profile. | Start a read-only catalog for an external archive drive. |
| Guided setup | Walks through the common choices needed for an initial configuration. | Create a first library without editing XML. |
| Recent configurations | Lists configuration files opened recently. | Switch between a home archive and a portable-device configuration. |
| Load selected | Loads the selected recent configuration. | Return to the archive configuration after working on a test library. |
| Clear history | Removes entries from the recent-file list; it does not delete configuration files. | Remove obsolete or relocated paths from the list. |
| Configuration path | Shows the path that **Save** will write. | Check whether the active draft is the shared or machine-specific copy. |

### Configuration use case

Keep separate configurations for an archival library and a portable player. Open either file from **Recent configurations**, make changes as a draft, review **Effective policy**, and save only after validation succeeds.

## Library roots tab

Library roots define where music is indexed, what each root may be used for, and how roots participate in named library sets.

### Root settings

| Parameter | Description | Example |
| --- | --- | --- |
| Root path | Directory to index. A relative path is resolved beside the configuration file. An offline path produces a warning rather than making the configuration unsavable. | D:\Music or ../Library |
| Naming and root policy | Policy assigned to this root. Policies are created and edited on **Root/Naming policy**. Ingest output sent to this root always uses this policy's naming rules. | Select **Artist/Album organizer** for an archive root and **iTunes Media** for an iTunes-compatible root. |
| Formats to index | Comma-separated registered extensions to index. Leave blank to use all supported formats. This affects catalog indexing. | .flac, .m4a |
| Include patterns | Optional semicolon-separated simple glob patterns. A pattern containing a slash matches the root-relative path; otherwise it matches the filename. Matching is case-insensitive on Windows and case-sensitive on macOS/Linux. | Music/**; *.flac |
| Exclude patterns | Optional glob patterns using the same rules as includes. Exclusions win over inclusions. | Temp/**; *.tmp |
| Cross-set comparison extensions | Extensions used by cross-set comparison workflows. Commas, semicolons, and pipes are accepted. This is separate from the index format filter. | .flac, .m4a |
| Metadata | Grants permission to write audio metadata in this root. | Enable on a curated library where tag corrections are allowed. |
| Artwork | Grants permission to change embedded or sidecar artwork. | Enable when repairing oversized cover art. |
| Organize files | Grants permission to move or rename files according to a naming profile. | Enable for an Artist/Album managed library. |
| Ingest output | Allows enabled ingest recipes to create output in this root. | Enable on the normalized destination library. |
| Sync output | Allows sync or export operations to mutate this root. | Enable on a mirrored portable-device library. |

Permissions are deny-by-default. A path outside configured roots cannot be written, and when roots overlap, the most restrictive applicable root controls the operation.

### Root sets and memberships

Sets group roots for comparisons, playlist exports, and other multi-library workflows.

| Parameter | Description | Example |
| --- | --- | --- |
| Add set / set names | Creates one or more set memberships. Names may contain ASCII letters and digits and are compared case-insensitively. Separate names with commas, semicolons, or whitespace. | Archive, Portable |
| Set membership | Includes the root in the named set. | Put the FLAC root in Archive and the AAC root in Portable. |
| Shared offset | Replaces the filesystem root prefix when a playlist path is generated. A root cannot have conflicting offsets for targets that use it. | With root D:\Music and offset Music, a track is written as Music/Artist/Album/song.flac. |

### Library roots use case

Create an **Archive** set containing a read-only FLAC root and a **Portable** set containing a writable AAC root. Grant the portable root **Ingest output** and **Sync output**, select it as the cross-library sync target on the **Playlists** tab, and use a shared offset of Music so exported playlists contain device-friendly paths.

## Playlists tab

This tab covers playlist discovery, cross-sync cleanup, file playlist export, and full library export profiles.

### Sync playlists

| Parameter | Description | Example |
| --- | --- | --- |
| Name | Playlist name to include in cross-library synchronization, including playlists sourced from a configured media catalog. Blank rows are omitted. | Road Trip |

### File playlist sources

| Parameter | Description | Example |
| --- | --- | --- |
| Location | An M3U/M3U8 file or a directory containing playlist files. Relative paths are resolved beside the configuration. | Playlists or Playlists/Favorites.m3u8 |
| Type | Playlist parser. The current value is **m3u**, which covers M3U and M3U8 files. | m3u |
| Recursive | When the location is a directory, also scans its subdirectories. | Enable for Playlists/Genres/Rock.m3u8. |

### Cross-library synchronization

| Parameter | Description | Example |
| --- | --- | --- |
| Cross-library sync target | Selects one configured library root as the destination for cross-library music synchronization, or **No sync target**. Selecting a root grants its **Sync output** permission. | Select the portable-device root that should receive archive changes. |

The sync target is selected here because it is part of the synchronization workflow, not an intrinsic property edited on each root card.

#### Cleanup

These switches authorize destructive cleanup and should be enabled deliberately.

| Parameter | Description | Example |
| --- | --- | --- |
| Permanently delete stale cross-sync files | Deletes stale destination files instead of placing them in quarantine. | Enable only for a reproducible device mirror with backups. |
| Clean playlist export targets before cross-syncing playlists | Removes old target playlist output before writing the synchronized set. | Enable when renamed playlists must not leave stale files behind. |

### Playlist export targets

| Parameter | Description and values | Example |
| --- | --- | --- |
| Target | Directory where playlist files are written. | D:\Device\Playlists |
| Type | **m3u**, **m3u8**, or **wpl**. | m3u8 |
| Sets | One or more existing library set names to scan. | Portable |
| Path style | **legacy** and **provided** are aliases that retain the mapped path, **absolute** writes a full path, and **relative** writes a path relative to the target directory. | relative for playlists stored beside the music tree. |
| Encoding | **utf-8**, **utf-16 LE**, **utf-16be**, or **ascii**. | utf-8 |
| Line endings | **platform**, **crlf**, or **lf**. | crlf for a Windows-oriented player. |
| Filename transform | **legacy** uses format-specific historical behavior, **preserve** keeps the playlist name, **sanitize** replaces invalid path characters, and **sonos** uses legacy Sonos-compatible sanitizing and padding. | sanitize |
| Write BOM | Writes an encoding byte-order mark where supported. | Enable for a player that requires a UTF-8 BOM. |
| Write EXTINF | Adds extended track-information lines to M3U/M3U8 output. | Enable to show artist and duration in a player. |
| Maximum tracks | Positive maximum number of tracks per generated playlist. The default is 500. | 500 |
| Collision | **Stop**, **Suffix**, **Hash**, or **PreserveExisting** when multiple playlists map to the same output name. | Stop for strict CI validation. |

A new target defaults to M3U, legacy/provided paths, UTF-8 with BOM, platform line endings, EXTINF enabled, legacy filename transformation, 500 tracks, and **Stop** on collision.

### Export profiles

Export profiles describe a selection of music, its destination layout, optional conversion, generated playlists, transport, and reconciliation. An enabled profile must have a valid transport provider and destination.

#### Identity and selection

| Parameter | Description and values | Example |
| --- | --- | --- |
| Enabled | Makes the profile available for preview and execution. Disabled profiles may remain as drafts. | Disable an unfinished cloud export. |
| ID | Stable unique identifier, 1–64 characters. It must start with an ASCII letter or digit; remaining characters may also include period, underscore, and hyphen. | car-aac |
| Name | Human-readable profile name. | Car AAC Library |
| Selection | **EntireLibrary**, **Playlists**, **SavedView**, or **ExplicitTracks**. | Playlists |
| Values | Comma-, semicolon-, or newline-separated values for the selected mode. Playlist and explicit-track selections require values. | Road Trip; Favorites |
| Saved-view query | Query used by a **SavedView** selection. | rating >= 4 |

#### Transform and naming

| Parameter | Description and values | Example |
| --- | --- | --- |
| Transform | **Preserve**, **Copy**, **Remux**, **Transcode**, or **SpecializedProvider**. | Transcode |
| Recipe ID | Optional ingest/conversion recipe used by the transform. | aac-portable |
| Transform provider | Provider that implements a specialized or otherwise provider-backed transform. Required for **SpecializedProvider**. | vendor-export |
| Codec | Requested output codec. | aac |
| Container | Requested output container. | m4a |
| Naming profile ID | Existing library profile whose naming rules should be used. | artist-album |
| Preserve source layout | Retains source-relative directories and filenames. | Enable for a straight backup copy. |
| Folder template | Folder template used when source layout is not preserved and no naming profile supplies one. | {AlbumArtist}/{Album} |
| Filename template | Filename template under the same conditions. | {Track:00} - {Title}{Extension} |
| Use naming-profile collision | Uses the selected library profile's collision behavior. | Enable so exports match normal organization. |
| Collision | Profile-specific **Stop**, **Suffix**, **Hash**, or **PreserveExisting** behavior when the naming profile does not control it. | Suffix |

Remux and transcode profiles must identify enough conversion behavior through a recipe, provider, codec, or container. The preview reports a blocking issue if the chosen dimension is valid configuration but the installed executor/provider cannot implement it.

#### Artwork

| Parameter | Description and values | Example |
| --- | --- | --- |
| Artwork mode | **None**, **Embedded**, **Sidecar**, or **EmbeddedAndSidecar**. | EmbeddedAndSidecar |
| Maximum dimension | Optional positive pixel limit. | 1000 |
| Maximum bytes | Optional positive encoded-size limit. | 512000 |
| Front cover only | Exports only the front-cover role. | Enable for a space-limited player. |
| Preserve artwork encoding | Keeps the original JPEG/PNG encoding when possible. | Disable when a provider requires JPEG. |

#### Generated playlists and transport

| Parameter | Description and values | Example |
| --- | --- | --- |
| Generate playlists | Produces playlists as part of the export. | Enable for a device library. |
| Playlist format | **m3u**, **m3u8**, or **wpl**. | m3u8 |
| Relative playlist paths | Writes paths relative to the playlist destination. | Enable when music and playlists move together. |
| EXTINF | Includes track-information records. | Enable for rich M3U8 output. |
| Playlist encoding | **utf-8**, **utf-16 LE**, **utf-16be**, or **ascii**. | utf-8 |
| Maximum playlist tracks | Optional positive per-playlist limit. | 500 |
| Write playlist BOM | Adds the selected encoding's byte-order mark. | Enable for older device firmware. |
| Playlist line ending | **platform**, **crlf**, or **lf**. | lf |
| Transport provider | Destination implementation. The standard local provider ID is **local-filesystem**. | local-filesystem |
| Transport options | Semicolon-separated name=value provider options. | region=west; tier=archive |
| Transport destination | Provider-specific destination. | D:\Exports\Car |

#### Reconciliation

| Parameter | Description and values | Example |
| --- | --- | --- |
| Extra files | **Preserve**, **Quarantine**, or **Delete** files that exist at the destination but are not in the plan. | Quarantine |
| Replace changed files | Replaces destination files whose planned content differs. | Enable for a true mirror. |
| Remove empty directories | Removes directories left empty by reconciliation. | Enable after quarantining stale albums. |
| Maximum removals | Optional nonnegative safety cap. Blank means no configured cap; zero prevents a plan containing removals. | 25 |

### Playlists use case

For a car player, select the **Portable** set, export M3U8 playlists with relative UTF-8 paths and a 500-track limit, and use a **local-filesystem** export profile that transcodes to AAC. Set extra files to **Quarantine** and cap removals so a bad selection cannot empty the device.

## Tools tab

### Machine and integration paths

| Parameter | Description | Example |
| --- | --- | --- |
| Machine-local bindings companion | Optional XML file for computer-specific paths. When set, root paths, export destinations/options, database, FFmpeg, WavPack, and iTunes paths are stored there under stable IDs, leaving the main configuration machine-neutral. A relative path is resolved beside the main configuration. | bindings/my-mac.xml |
| Metadata database | Metadata cache/database location. It cannot be blank. The default is cache.db; a relative path is resolved beside the configuration. A sqlite: value must contain a database path. | cache.db |
| iTunes library | Optional iTunes library file used for catalog integration and recipes that add output to the media catalog. | /Users/me/Music/iTunes/iTunes Library.itl |
| FFmpeg | FFmpeg command name or full executable path used by conversion operations. The default is ffmpeg. | /opt/homebrew/bin/ffmpeg |
| WavPack | WavPack command name or full executable path used for lossless DSF-to-WavPack DSD encoding. The default is wavpack. | /opt/homebrew/bin/wavpack |

### Tools use case

Commit a portable main configuration to source control and set **Machine-local bindings companion** to bindings/my-mac.xml. Put Homebrew's FFmpeg and WavPack paths, the local cache database, iTunes catalog, root paths, and export destinations in that companion without changing shared policy.

## Health tab

Analysis thresholds decide when artwork is reported as oversized. Repair targets decide the size a proposed repair should produce; they are intentionally independent.

| Parameter | Description | Default | Example |
| --- | --- | --- | --- |
| Oversized encoded size (MiB) | Flags artwork whose encoded size exceeds this value. Allowed range: 0.25–1024 MiB. | 2 MiB | Use 1 MiB for a constrained mobile library. |
| Oversized width/height (px) | Flags artwork whose width or height exceeds this value. Allowed range: 64–100000 pixels. | 2000 | Flag covers larger than 1500 px. |
| Repair target size (MiB) | Target maximum encoded size for artwork repair. Allowed range: 0.0625–1024 MiB. | 225 KiB, approximately 0.2197 MiB | Target 0.5 MiB for a tablet. |
| Repair target dimension (px) | Target maximum width or height for artwork repair. Allowed range: 64–100000 pixels. | 600 | Target 1000 px for a high-DPI player. |

Artwork equal to a threshold is not oversized; it must exceed the size or dimension threshold.

### Health use case

For a phone library, flag covers above 2 MiB or 2000 px while repairing them to at most 600 px and roughly 225 KiB. This keeps analysis broad but makes the proposed output consistently device-friendly.

## Root/Naming policy tab

A root/naming policy bundles naming, metadata, artwork, health, quality-classification, and sidecar behavior. The selector on this tab chooses the policy being edited and the defaults for newly added roots; it does not reassign existing roots. Root assignments are made explicitly on **Library roots**.

| Built-in policy | Intended behavior |
| --- | --- |
| Catalog only | Indexes a library without granting write or ingest permissions. |
| Preserve layout + tag editing | Allows metadata and artwork updates while preserving the existing file layout. |
| Artist/Album organizer | Allows metadata, artwork, moves, and renames using generic Artist/Album naming. |
| iTunes Media | Organizes with iTunes-compatible naming and layout. |
| Legacy MusicLibraryTools | Preserves the historical MusicLibraryTools naming, organization, metadata, artwork, health, and sidecar choices. |

**New** creates a custom policy. **Duplicate** is the normal way to customize a built-in policy. **Delete** removes a custom policy and reassigns its root and export references after confirmation.

### Naming

| Parameter | Description | Example |
| --- | --- | --- |
| Profile name | Human-readable custom profile name. Built-in names are protected. | Curated FLAC |
| Folder template | Required relative folder layout. | {AlbumArtist}/{Album} |
| File template | Required filename pattern. | {Track:00} - {Title}{Extension} |
| Track padding | Positive default digit width for track numbers. | 2 produces 01. |
| Disc padding | Positive default digit width for disc numbers. | 2 produces 01. |
| Collision behavior | **Stop** reports an error; **Suffix** adds a numeric suffix; **Hash** adds a stable source-path hash; **PreserveExisting** leaves the existing destination and skips that remap. | Stop for a curated archive. |
| iTunes canonical naming | Uses iTunes-compatible canonical folder and filename behavior for every root assigned to this policy. Legacy per-root overrides are converted to custom policies when the configuration is opened and saved. | Enable on a policy assigned to an iTunes Media directory. |
| Invalid-character replacement | Replacement used when sanitizing path components. It cannot contain a directory separator. | _ |
| Preserve Unicode | Retains Unicode characters during name sanitization and normalization. When disabled, names are folded to ASCII. | Enable for multilingual metadata. |
| Missing artist fallback | Nonblank replacement when Artist is unavailable. | Unknown Artist |
| Missing album fallback | Nonblank replacement when Album is unavailable. | Unknown Album |
| Missing title fallback | Nonblank replacement when Title is unavailable. | Unknown Title |
| Compilation token | Name used for compilation grouping. | Compilations |
| Unicode normalization | **None**, **FormC**, **FormD**, **FormKC**, or **FormKD**. | FormC |
| Component length limit | Positive maximum for one path component. Existing `LengthLimit` values migrate here so the policy remains the source of naming behavior. | 120 |
| Disc-album length limit | Positive maximum for an album component before the legacy `(Disc N)` suffix is appended. Existing `DiscNumLengthLimit` values migrate here independently from the general component limit. | 110 |
| Complete path limit | Optional positive maximum for the complete output path. Blank uses platform behavior. | 240 |

Available template tokens are **AlbumArtist**, **Artist**, **Album**, **Title**, **Compilation**, **Year**, **Genre**, **Disc**, **Track**, **OriginalName**, and **Extension**. A format such as {Track:00} pads a number. Square brackets make a fragment optional, for example [{Year} - ]{Album}.

### Disc projection and album identity

| Parameter | Description and values | Example |
| --- | --- | --- |
| Disc strategy | Selects one authoritative destination representation. **PreserveTags** keeps the base album and disc number/total tags (iTunes canonical naming reflects them as `N-TT`). **AlbumSuffix** removes disc tags and writes `(Disc N)` only in the album. **DiscFolder** removes disc tags and adds a `Disc N` folder. **FileNamePrefix** removes disc tags and adds `N-TT` to the filename. **FlattenContinuous** removes disc tags and continuously renumbers tracks across the album. | AlbumSuffix produces `Some Album (Disc 2)/03 Title` without disc tags. |
| Track-total scope | **PerDisc** treats totals as disc totals; **Album** treats them as whole-album totals. | PerDisc for 1/10 on each disc. |
| Infer legacy album suffixes | Recognizes historical album names ending in forms such as (Disc 2). | Enable while migrating an old layout. |
| Use Album Artist for identity | Uses Album Artist rather than track Artist to group album context. | Enable for compilations with many performers. |
| Strip format suffixes | Removes recognized format suffixes such as (Hi-Res), (DSD), or (DVD-A) from album identity comparisons. | Group Album and Album (Hi-Res) as one album. |
| Strip Disc N suffixes | Removes disc suffixes when determining album identity. | Group Album (Disc 1) and Album (Disc 2). |
| Include release year | Includes year in album identity, separating same-title releases. | Distinguish a 1990 issue from a 2020 remaster. |

**FlattenContinuous** needs album context to calculate continuous numbering reliably.

Older configurations may contain a `PreserveDiscTags` attribute. It is accepted for compatibility but is no longer an independent choice and is omitted on the next save: **PreserveTags** retains disc number/total metadata, while every other strategy removes it.

### Metadata preservation

Core track metadata is projected normally. These switches preserve additional metadata families:

| Parameter | Description | Example |
| --- | --- | --- |
| ReplayGain | Preserves ReplayGain values. | Keep album gain in an archive normalization pass. |
| MusicBrainz IDs | Preserves MusicBrainz identifiers. | Retain release matching across a remux. |
| Custom fields | Preserves nonstandard/custom tag fields where supported. | Keep a personal mood tag. |
| Compilation semantics | Preserves compilation flags and related interpretation. | Keep Various Artists grouping stable. |

### Quality classification

| Parameter | Description | Example |
| --- | --- | --- |
| High-resolution sample rate | Positive sample-rate threshold. Audio meeting either the sample-rate or bit-depth threshold is high resolution. | 48000 |
| High-resolution bits per sample | Positive bit-depth threshold. | 24 |

The generic defaults are 48 kHz and 24-bit. The legacy profile uses thresholds chosen to preserve historical behavior.

## Ingest policy tab

The **Active ingest profile** selector chooses the workflow used by ingest. **New**, **Duplicate**, and **Delete** manage ingest profiles without changing root/naming policies or root assignments. The stable ingest profile ID preserves this selection across renames. A recipe chooses a destination root, and that root's policy always controls output naming and layout.

| Parameter | Description and values | Example |
| --- | --- | --- |
| Enable ingest recipes | Allows enabled recipe matching and output. Root-level **Ingest output** permission is still required. | Enable on a staging-to-library workflow. |
| Source disposition | **Preserve**, **Quarantine**, or **Delete** source audio after successful ingest. | Quarantine while proving a new transcode recipe. |
| Preserve sidecars by default | Legacy compatibility switch. For the **Legacy MusicLibraryTools** ingest profile it forces sidecars to be preserved; other workflows use the destination root's ordered sidecar policy below. | Enable when reproducing the legacy ingest behavior. |

### Successful-ingest cleanup

| Parameter | Description | Example |
| --- | --- | --- |
| Delete source music after successful ingest | Legacy top-level compatibility switch for deleting successfully ingested source audio. The active ingest profile's **Source disposition** is authoritative. | Prefer **Quarantine** while validating a new recipe. |
| Remove non-music files after successful ingest | Enables cleanup of non-audio sidecars after successful ingest. Ordered sidecar rules determine whether matching files are preserved, quarantined, deleted, or follow source disposition. | Preserve cover images and cue sheets but quarantine logs. |

Cleanup is considered only after a successful operation; preview and validation still enforce root permissions and applicable policy.

### Ingest recipes

Recipes are evaluated in order by their matching policy and define how selected source audio is written. More than one recipe may match a source. Recipes do not contain naming-profile overrides: selecting a destination root unambiguously selects its naming and collision policy.

#### Recipe identity and matching

| Parameter | Description and values | Example |
| --- | --- | --- |
| Enabled | Allows the recipe to match. | Disable a draft recipe. |
| ID | Stable unique identifier following the same 1–64 character ID rules as export profiles. | flac-archive |
| Name | Human-readable recipe name. | Archive FLAC copy |
| Accepted extensions | Registered extensions separated by commas, semicolons, or whitespace. An enabled recipe requires at least one. | .flac, .wav |
| Require lossless | Checked matches lossless, unchecked matches lossy, and indeterminate matches either. | Checked for an archival FLAC recipe. |
| Action | **Copy**, **Remux**, or **Transcode**. Copy cannot change extension; remux must use a compatible container family. Transcode creates a new encoding unless codec, container, sample rate, bit depth, and channels already match and no bitrate, explicit encoder, or extra FFmpeg options require processing; that identity case is planned as a copy. | Transcode |
| Minimum sample rate | Optional positive threshold. | 96000 |
| Minimum bits per sample | Optional positive threshold. | 24 |
| Input channels | **Stereo** matches exactly two channels; **Multi** matches more than two. Blank accepts either. | Multi |
| Match either quality minimum | When both quality thresholds are set, uses OR instead of AND. | Match 96 kHz/16-bit or 48 kHz/24-bit material. |
| Album match | **Any**, **HasHighResolution**, or **HasNoHighResolution**, using the profile quality thresholds. | HasHighResolution |
| Source selection | **HighestQuality** orders sources by sample rate then bit depth; **PreferCdQuality** favors a non-high-resolution source before falling back. | PreferCdQuality |
| Require approval when matched CD missing | Requires approval when **PreferCdQuality** must fall back to a high-resolution source. | Prevent automatic down-conversion of the only master. |

#### Recipe output

| Parameter | Description and values | Example |
| --- | --- | --- |
| Destination root | Existing root that grants **Ingest output**. It may be omitted only for a supported catalog-only destination flow. | D:\Music\Portable |
| Output extension | Required when remuxing or transcoding. Use .wv with a .dsf-only recipe for lossless WavPack DSD. | .m4a |
| Codec | Output codec. Use wavpack or wv with a .wv WavPack DSD destination. | aac |
| Encoder | Optional explicit FFmpeg encoder; blank lets the conversion layer choose. WavPack DSD recipes must leave this blank and use the executable configured in **Tools**. | aac_at |
| Bitrate (kbps) | Optional positive audio bitrate. | 256 |
| Output sample rate | Optional positive output sample rate. | 48000 |
| Extra FFmpeg options | Additional tokenized FFmpeg arguments for FFmpeg transcode recipes. WavPack DSD recipes must leave this blank. | -movflags +faststart |
| Add output to configured media catalog | Adds successful output to the configured catalog and therefore requires an iTunes library path. | Enable for an iTunes-managed destination. |
| Output bits per sample | Optional positive output bit depth. | 24 |
| Output channels | **Stereo** forces two channels; **Multi** preserves multichannel output. | Stereo |
| Preserve metadata | Copies supported metadata to output. | Enable for normal library ingest. |
| Preserve artwork | Copies artwork subject to the artwork policy. | Enable for normal library ingest. |
| Use destination root collision | Uses the destination root profile's collision behavior. | Enable for consistent organization. |
| Collision override | Recipe-specific **Stop**, **Suffix**, **Hash**, or **PreserveExisting** behavior. | Hash for deterministic batch imports. |

For lossless DSF compression, configure a transcode recipe whose only accepted extension is .dsf, output extension is .wv, and codec is wavpack or wv. The operation uses the WavPack executable rather than FFmpeg, imports the DSF ID3v2 metadata into the WavPack APEv2 tag, preserves the source DSD sample rate, bit depth, and channel count, and runs WavPack's verification pass before the staged output can be committed.

## Root/Naming policy: artwork, health, and sidecars

These remaining sections are also part of the selected policy on the **Root/Naming policy** tab.

### Artwork policy

| Parameter | Description and values | Example |
| --- | --- | --- |
| Storage | **None**, **Embedded**, **Sidecar**, or **Both**. | Both |
| Roles | **FrontCoverOnly** or **AllRoles**. | FrontCoverOnly |
| Encoding | **PreserveSource**, **Jpeg**, or **Png**. | Jpeg |
| Maximum dimension | Maximum width/height; zero means unlimited. | 1000 |
| Maximum bytes | Maximum encoded bytes; zero means unlimited. | 512000 |
| JPEG quality | 1–100 quality used for JPEG output. The default is 90. | 85 |
| Sidecar template | Filename-only template supporting {Role} and {Extension}; it must include {Role} and cannot contain directories. | {Role}{Extension} |

### Health rules

Each health rule has four controls:

- **Enabled** runs the analysis.
- **Severity** is **Information**, **Warning**, or **Error**.
- **Propose** allows a repair suggestion to be generated.
- **Apply** allows the proposed repair to be applied.

The available built-in rules are:

| Rule | What it detects or normalizes |
| --- | --- |
| Lossy file | Audio identified as lossy; it has no automatic repair. |
| Missing album artist | Missing Album Artist metadata. |
| Missing track total | Incomplete track-number totals. |
| Disc metadata | Inconsistent or incomplete disc metadata. |
| ID3 version | ID3 data that does not meet the selected policy. |
| Normalize whitespace | Metadata whitespace needing normalization. |
| Disc album title | Disc suffix/title inconsistencies. |

Use **Propose** without **Apply** for a review-only audit. Enable **Apply** only for repairs that the workflow is authorized to perform.

### Sidecar rules

Sidecar rules are ordered: the first enabled, case-insensitive glob match wins.

| Parameter | Description and values | Example |
| --- | --- | --- |
| Unknown disposition | **Preserve**, **Quarantine**, **Delete**, or **FollowSourceDisposition** for files that match no rule. | Preserve |
| Enabled | Includes the rule in matching. | Disable a temporary migration rule. |
| ID | Stable unique identifier following the shared ID rules. | preserve-cue |
| Name | Human-readable rule name. | Preserve cue sheets |
| Patterns | One or more comma- or semicolon-separated globs. A slash matches a source-relative path; otherwise the pattern matches a filename. | *.cue; *.log |
| Disposition | **Preserve**, **Quarantine**, **Delete**, or **FollowSourceDisposition**. | Quarantine |

The generic policy starts conservatively, preserving common cover images, cue sheets, logs, lyrics, PDFs, and checksum files.

### Root/Naming and ingest policy use case

Duplicate **Artist/Album organizer** as **Curated FLAC**, assign it to the archive root, use FormC normalization and strict collision stopping, and classify 48 kHz or 24-bit files as high resolution. Separately duplicate an ingest profile, add a lossless-copy recipe targeting that archive root, and quarantine successful sources until the workflow has been reviewed. The copied file uses **Curated FLAC** naming because that is the destination root's policy.

## Effective policy tab

This tab is read-only. It composes the current unsaved editor values into the policy the application would use and summarizes:

- selected root policy, per-root assignments and permissions, naming, disc, metadata, quality, artwork, health, and sidecar behavior;
- selected ingest profile, source handling, recipes, and destination roots;
- configured roots, sets, formats, example destinations, and collision decisions;
- database, FFmpeg, iTunes, machine-binding, playlist, and export integrations;
- offline-path warnings, missing references, policy contradictions, and other validation issues.

Use this tab as a pre-save review. Invalid profile references, contradictory permissions, malformed IDs, and unsupported required settings can block saving even when each individual row looks plausible.

### Effective policy use case

After changing a naming template and recipe destination, inspect the example output path and validation details before saving. This catches a recipe pointing at a root without **Ingest output** or an export referencing a deleted profile.

## Appearance tab

| Parameter | Description | Example |
| --- | --- | --- |
| Theme | **System**, **Light**, **Dark**, or **Steel Blue**. The choice applies immediately and is stored as an application preference rather than in the library configuration. **System** follows the operating-system preference. | Use Dark for low-light work without altering the shared configuration. |

### Appearance use case

Two users can open the same library configuration with different themes. Their choice changes only their local application appearance and does not make the shared configuration dirty.

## Safe configuration patterns

- Start with **Catalog only** when indexing an unfamiliar library, then grant the smallest required write permissions.
- Prefer **Quarantine** over **Delete** until an ingest, sync, or reconciliation plan has been reviewed repeatedly.
- Use a machine-local bindings companion for paths that differ between Windows, macOS, and CI runners.
- Leave collision behavior at **Stop** while validating naming templates; adopt suffix or hash behavior only when duplicates are expected.
- Preview effective policy and export/ingest plans before enabling automatic application or permanent cleanup.
