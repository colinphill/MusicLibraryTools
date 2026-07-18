# Avalonia Studio optical parity assessment

## Reference set

The existing `MusicLibraryManager.Studio` and the Avalonia rewrite were captured in dark mode at the same 1440 x 900 logical viewport. The existing WPF/WebView window was rendered at the machine's 150% DPI scale (2160 x 1350 physical pixels); the comparison generator normalizes it back to 1440 x 900 beside the Avalonia headless render.

- Existing Studio references: `artifacts/studio-optical/original`
- Avalonia references: `artifacts/studio-optical/avalonia`
- Normalized side-by-side images: `artifacts/studio-optical/comparisons`

Comparisons: [Home](artifacts/studio-optical/comparisons/Home.png), [Library](artifacts/studio-optical/comparisons/Library.png), [Health](artifacts/studio-optical/comparisons/Health.png), [Ingest](artifacts/studio-optical/comparisons/Ingest.png), [Organize](artifacts/studio-optical/comparisons/Organize.png), [Operations](artifacts/studio-optical/comparisons/Operations.png), and [Settings](artifacts/studio-optical/comparisons/Settings.png).

The existing Studio restored the user's live `fulllibrary` configuration and was scanning while captured. The isolated Avalonia render fixture intentionally has no configuration. Counts, status text, command availability, and scanning banners therefore describe different application states and are not visual parity failures.

## Disposition

| Area | Optical finding | Disposition |
| --- | --- | --- |
| Shell geometry and palette | Rail, toolbar, page margins, cards, borders, and dark tokens closely match. | Pass. Keep the matched dark captures as the baseline. |
| Navigation | Avalonia icons and labels are larger than the existing Studio glyphs. | Accepted intentional difference: requested for legibility. Alignment is centered, the selected background fills the rail, and the teal left marker is restored. |
| Window captions | Avalonia minimize, maximize, and close vectors are larger and optically centered. | Accepted intentional difference: requested for legibility. |
| Search | The placeholder is vertically centered in Avalonia. | Pass; requested correction. |
| Global activity | The detailed scan message looked like an unexplained, poorly aligned status bubble. | Removed from the Avalonia toolbar. Scan progress remains in the relevant page status and indexing surfaces. |
| Page and button alignment | Page action buttons are visible and button contents are centered in Avalonia. Some existing-Studio actions are clipped or outside the captured layout. | Accept Avalonia behavior; it preserves intended commands and avoids clipping. |
| Tabs | The first Avalonia captures used Fluent blue selection indicators. | Fixed: Fluent accent resources now follow the Studio teal palette. |
| Library grid | Native headers and columns render, and the empty grid now says `No tracks match this view`. | Fixed. |
| Library inspector | The existing capture lets a persisted split width push the inspector outside the viewport. Avalonia clamps the split and shows a dedicated `No selection` state instead of empty Metadata/Artwork sections. | Accept Avalonia behavior as a stability/usability correction already requested. |
| Library columns | Avalonia uses a wider Artwork column so its header is readable and has no browser-style drag-grip dots. | Keep the readable header. Defer optional native header grip decoration; column reorder/resize behavior is more important than reproducing the dots. |
| Column selector | Avalonia supplies light-dismiss, Escape, and an explicit close button. | Accepted intentional improvement requested by the user. |
| Health launcher | The existing Studio keeps the analysis commands on one horizontally clipped row; Avalonia wraps them to two rows. | Accept the Avalonia responsive layout. All commands remain discoverable without horizontal scrolling. |
| Health findings | Avalonia initially omitted the `All findings` root and showed a blank result region. | Fixed: root, count, teal selection, and empty-state prompt are present. |
| Ingest | Overall structure matches. Avalonia keeps the filter and Progress column visible while the existing capture clips later controls. | Accept Avalonia layout; verify again with a seeded ingest preview. |
| Organize | Banner, status, and two-column grid geometry match closely. | Pass. |
| Operations | The first Avalonia render appeared constrained to intrinsic content width. | Fixed by forcing the routed content presenter to stretch; selected job and detail panes now fill the card. |
| Settings width | Existing Studio constrains settings content; Avalonia fills the available card width. | Accepted intentional difference requested by the user. |
| Native controls | Avalonia check boxes, combo boxes, scrollbars, disabled buttons, and grid headers are close but not pixel-identical to browser controls. | Accept native control behavior for cross-platform accessibility. Tune disabled-button contrast in a later polish pass if exact appearance remains important. |

## Remaining visual work

1. Add deterministic seeded capture scenarios so both implementations render the same configured/scanning, populated-grid, selected-track, open-column-selector, and populated-analysis states. This is the largest remaining gap in the comparison process.
2. Capture responsive baselines at approximately 1000 x 700 and a high-DPI scale to verify compact navigation, inspector overlay behavior, toolbar wrapping, and minimum split widths.
3. Run one native desktop capture on Windows, macOS, and Linux after packaging. Headless rendering verifies layout contracts but does not expose platform font rasterization, window chrome, or compositor differences.
4. Decide after those captures whether native DataGrid drag affordances and disabled-control contrast warrant custom templates. They are polish items, not functional blockers.

## Reproducing the references

Build the existing Studio in Release, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File MusicLibraryManager.Studio\Capture-OpticalReferences.ps1
```

Capture the Avalonia dark reference set by setting `STUDIO_CAPTURE_DIR` to `artifacts\studio-optical\avalonia` while running the Avalonia test project, then generate the side-by-side images:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File MusicLibraryManager.Studio\Create-OpticalComparisons.ps1
```

The screenshot directories are generated artifacts and intentionally ignored by Git. The capture scripts and this assessment remain in source control.
