# Music Library Manager localization glossary

This glossary is normative for application resource catalogs. The first
non-English catalogs are machine-assisted beta translations; reviewers should
preserve the protected forms below while improving surrounding prose.

## Product and service names

Do not translate or transliterate these names:

- Music Library Manager
- MusicBrainz
- Discogs
- AcoustID
- Cover Art Archive
- FFmpeg
- ffprobe
- WavPack
- OptimFROG
- Matroska
- WebM
- iTunes
- Avalonia UI / AvaloniaUI
- SkiaSharp
- MIT License
- Colin Hill

## Tag, codec, and container terminology

Keep these identifiers unchanged wherever they identify a technical format:

- ID3, ID3v1, ID3v2, ID3v2.3, ID3v2.4
- APEv2
- Vorbis Comment
- ReplayGain
- FLAC, Ogg, Opus, MP3, MP4, AAC, ALAC
- WAV, AIFF, DSF, ASF, WMA, APE, MPC, TTA, TAK
- UTF-8, UTF-16, UTF-16LE, UTF-16BE, ISO-8859-1, Windows-1252
- SHA-256

A localized explanation may follow a protected identifier, but must not replace
it. File extensions such as `.mp3`, `.flac`, `.m4a`, and `.wv` remain literal.

## Syntax that must remain byte-for-byte intact

- Composite-format placeholders, including their alignment and format
  components: `{0}`, `{1:N0}`, `{2,-12}`.
- File-system paths, environment-variable names, URIs, and provider IDs.
- Command-line flags and values beginning with `-` or `--`.
- Keyboard gestures and key names used by the application, such as `Ctrl+Z`,
  `Shift+F10`, `Esc`, and `Enter`.
- Resource keys, serialized enum values, navigation tags, theme IDs, and
  preference keys.

Translators may reorder complete placeholders to produce natural sentences.
They must not add, remove, split, or edit a placeholder token.

## Language-choice policy

Language choices use stable native autonyms. English (United States) is the
fallback language. German, Spanish, French, Italian, Brazilian Portuguese,
Japanese, Korean, Simplified Chinese, and Traditional Chinese are marked Beta
using the active UI language's translation of “Beta.”

Only `CurrentUICulture` changes with the display-language setting. Numeric,
date, parsing, sorting, and regional formatting continue to use
`CurrentCulture`.

## CJK font expectation

Music Library Manager uses the operating system's font fallback rather than
shipping a CJK font. Current Windows and macOS installations provide suitable
fallback fonts. Linux desktop images must install and configure a CJK-capable
system font through fontconfig; the Noto CJK family is a suitable option.
Packaging intentionally does not require one particular font package because
package names and language coverage differ across distributions.
