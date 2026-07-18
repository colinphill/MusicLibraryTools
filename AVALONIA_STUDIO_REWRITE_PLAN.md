# MusicLibraryManager.Studio Avalonia rewrite plan

## Objective

Replace the WPF/Blazor WebView implementation of `MusicLibraryManager.Studio` with a fully
native Avalonia desktop application for Windows, macOS, and Linux while preserving Studio's
layout, theming, styling, interactions, workflows, and persisted state.

The Avalonia application will be developed alongside the WPF application. The WPF build remains
the visual and behavioral reference until parity is accepted, after which the Avalonia executable
will assume the `MusicLibraryManager.Studio` product identity.

## Parity contract

The rewrite preserves:

- The 44-DIP title bar, 220-DIP navigation rail, 56-DIP toolbar, 1440x900 default window, and
  900x600 minimum window.
- All palette values, spacing, control heights, borders, radii, focus states, and selection states
  defined by `MusicLibraryManager.Studio/wwwroot/css/studio.css`.
- Light, dark, system, high-contrast, and reduced-motion behavior.
- Responsive transitions at 1100, 1050, and 900 DIPs.
- Navigation, global search, keyboard shortcuts, activity drawer, modal dialogs, grids, splitters,
  drag/drop, view persistence, and window persistence.
- Existing `MusicLibraryManager.Presentation` and `MusicLibrary.Core` workflows and safety models.
- Existing preference keys so users retain their saved Studio layout.

Font rasterization and platform-native surfaces such as file dialogs may differ by operating
system. Application geometry, colors, content, state, and behavior remain equivalent.

## Architecture

Create `MusicLibraryManager.Studio.Avalonia` as a `net10.0` Avalonia executable referencing:

- `MusicLibrary.Core`
- `MusicLibraryManager.Presentation`
- the shared workflow integration service

The project contains native AXAML views, shared Studio controls, theme resources, and Avalonia
implementations of the presentation platform interfaces. It does not include a WebView, Razor
runtime, CSS runtime, or JavaScript bridge.

The current WPF Studio stays runnable throughout the migration. At cutover, remove its WebView2
and WPF dependencies and promote the Avalonia project to the existing product/executable name.

## Execution phases

### 1. Reference baseline

- Freeze the WPF Studio as the parity reference.
- Capture deterministic light and dark screenshots for every destination at 1440x900, 1050x800,
  and 900x700.
- Record keyboard, pointer, drag/drop, dialog, grid, splitter, and persistence behavior.

### 2. Avalonia foundation

- Add the new project to the solution and portable solution filter.
- Build the Avalonia application, DI composition root, platform services, and startup handling.
- Reuse the existing presentation ViewModels and core services.
- Preserve settings keys for the Studio window, grid, and split panes.

### 3. Theme and shell

- Translate Studio's CSS palette and measurements into Avalonia dynamic resources.
- Implement the custom title bar, navigation rail, toolbar, search box, configuration chip,
  activity chip/drawer, navigation host, responsive states, and theme switching.
- Recreate the brand mark and navigation glyphs as native vector content.

### 4. Shared controls

- Implement `StudioSplitView` with pointer/keyboard resizing and persisted widths.
- Implement the styled, virtualized Studio data grid with dynamic columns, frozen columns,
  sorting, resizing, reordering, keyboard navigation, selection, activation, and persistence.
- Implement the modal dialog host, page header, cards, tabs, pills, banners, lists, progress,
  and form control styles.

### 5. Workflow pages

Port in this order:

1. Home
2. Organize
3. Ingest
4. Operations
5. Settings
6. Library and selection inspector
7. Health and hierarchical repair/result views

Only page-local visual state belongs to the Avalonia views. Commands and workflow state remain in
the existing shared ViewModels.

### 6. Platform integration

- Use Avalonia `StorageProvider` for file/folder/save dialogs.
- Use Avalonia clipboard and drag/drop APIs.
- Reveal files with Explorer, `open -R`, or `xdg-open` as appropriate.
- Return Avalonia bitmap objects from `IThumbnailService`.
- Restore windows safely onto available screens.
- Track system light/dark and accessibility preferences.

### 7. Tests and parity gates

- Replace BUnit UI tests with Avalonia headless tests.
- Test command enablement, reactive updates, grid interaction, splitters, dialogs, themes,
  responsive transitions, and health disposition propagation.
- Add deterministic screenshots for every page and theme with exact resource/layout assertions
  and per-platform perceptual baselines for text rendering.
- Run startup, navigation, shortcut, storage, clipboard, drag/drop, grid, theme, persistence,
  and large-library smoke tests on Windows, macOS, and Linux.

### 8. Packaging and cutover

- Publish self-contained `win-x64`, `osx-arm64`, `osx-x64`, and `linux-x64` packages.
- Extend the existing Studio packaging workflow.
- Retain WPF Studio for one validation release.
- Cut over only when all seven destinations, shared dialogs, platform adapters, automated tests,
  screenshot gates, and performance checks pass.

## Primary implementation risks

- Build and validate the grid before Library and Health because it carries the largest interaction
  and accessibility surface.
- Centralize responsive state so breakpoint behavior cannot drift between pages.
- Use platform-specific screenshot baselines for font rasterization while asserting geometry and
  resource colors exactly.
- Explicitly define page-local state lifetime because cached Avalonia views and recreated Razor
  components otherwise behave differently.
- Require virtualization and representative large-library benchmarks before parity sign-off.

## Initial milestone

The first vertical slice consists of the complete native project foundation, shared palette,
custom shell, Home page, data-grid prototype, platform services, theme switching, and persisted
window state. It must build without warnings and run without any WPF or WebView dependency before
the remaining pages are migrated.
