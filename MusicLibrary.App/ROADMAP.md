# MusicLibrary.App roadmap

This roadmap records the next application improvements in priority order. Work should preserve the
existing preview/apply safety model, remain useful when network library roots are unavailable, and
avoid eagerly reading artwork or media data that is already represented in the metadata cache.

## 1. Daily library workflow

- [x] Named Library views that preserve filters, filter scope, columns, and widths.
- [x] Preserve typed grid sorting in named views and across reloads.
- [x] Library row actions: open Details, edit tags/artwork, reindex, reveal in Explorer, and copy path.
- [ ] Consistent keyboard shortcuts and persistent workspace state.

## 2. Actionable library health

- [ ] Keep analysis results instead of replacing the previous run.
- [ ] Group findings by problem and album, with completed/ignored/deferred state.
- [ ] Preview and apply batch repairs for missing or inconsistent album artist, numbering, totals,
      multi-disc naming, whitespace, and casing.
- [ ] Add an album metadata matrix with inconsistent cells highlighted.

## 3. Quarantine and recovery

- [ ] Discover ingest, organize, sync, and device-operation journals.
- [ ] Browse operations and quarantined files in their original hierarchy.
- [ ] Restore a run or selected files, identify interrupted operations, and purge by retention policy.

## 4. Network-aware indexing

- [ ] Report enumeration, metadata, database, and artwork phases with throughput and elapsed time.
- [ ] Expose bounded reader parallelism and provide a per-root concurrency benchmark.
- [ ] Record per-root scan health and last successful scan; distinguish unavailable roots from removals.
- [ ] Support cached/offline browsing, targeted reindexing, and scheduled delta scans.

## 5. Album and representation health

- [ ] Compare CD FLAC, paired FLAC, high-resolution, purchased, and generated AAC representations.
- [ ] Find missing counterparts and metadata, track-count, duration, artwork, or decoded-audio drift.
- [ ] Preview derivation, metadata-copy, and organization repairs.

## 6. Ingest workflow

- [ ] Named presets, recent source folders, drag-and-drop, and configuration preflight.
- [ ] Summary cards and filtering for albums, outputs, conflicts, and cleanup items.
- [ ] Persistent ingest history plus interrupted-run recovery.

## 7. Artwork health

- [ ] Cache-first audit for missing, mixed, oversized, unreadable, or duplicate artwork.
- [ ] Select a canonical album image and preview batch normalization or size savings.
- [ ] Hydrate full artwork only for visible or selected results.

## 8. Unified operations

- [ ] Bring playlist sync, cross-library sync, device updates, artwork repair, redundancy cleanup,
      and iTunes validation into a shared preview/apply job interface.
- [ ] Reuse common progress, cancellation, journaling, quarantine, recovery, and history components.
