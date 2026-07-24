# UI Modernization Progress

Branch: `feature/workbench-ui-localization`
Base: `feature/capability-parity-workbench`
Started: 2026-07-24
Last updated: 2026-07-24

## Status legend

- **Not Started** — implementation has not begun.
- **In Progress** — implementation or validation is underway.
- **Blocked** — work cannot continue without an external decision or dependency.
- **Complete** — implementation is finished and verification evidence is recorded.
- **Deferred** — intentionally moved to a documented follow-up.

## Delivery and compatibility

| ID | Plan item | Status | Evidence / notes |
|---|---|---|---|
| DEL-01 | Create the dedicated implementation branch | Complete | Completed 2026-07-24. Created `feature/workbench-ui-localization` from clean `feature/capability-parity-workbench` HEAD `ff6c821`. |
| DEL-02 | Maintain this progress ledger throughout implementation | In Progress | Created before implementation and updated after each completed batch and before this commit. Keep updating it while LOC-07/08 and TST-02 remain open. |
| DEL-03 | Keep database, index, library configuration, and serialization schemas unchanged | Complete | Completed 2026-07-24 for this commit. A path and semantic diff audit found no changes to database/index schemas, configuration models, existing serialization versions, or rescan triggers. The new culture and Workbench UI values use isolated `SetPreference` keys, which do not raise `ConfigurationChanged`. |

## Workbench redesign

| ID | Plan item | Status | Evidence / notes |
|---|---|---|---|
| WB-01 | Replace peer tabs with grouped rail and compact section picker | Complete | Completed 2026-07-24. Nine stable destinations use Workspace/Edit/Enrich/Output/Automate groups; the section picker replaces the rail below 880 content pixels. Responsive synchronization is included in the 73/73 UI pass. |
| WB-02 | Persist stable Workbench section and inspector preferences | Complete | Completed 2026-07-24. Stable `WorkbenchSection` values and inspector preference persist under `manager.workbench.ui.v1`; round-trip and culture-preservation tests pass without configuration/schema changes. |
| WB-03 | Apply Workbench-owned wide, medium, compact, and compact-height layouts | Complete | Completed 2026-07-24. The final 216-state matrix passed at 900×600, 1200×700, and 1440×900 across light/dark, 14/18 px, and neutral/40%-expanded pseudo text. Session retains at least 220 px of grid height. Visual review found and corrected transient compact-width splitter clamping. |
| WB-04 | Simplify the header and source command hierarchy | Complete | Completed 2026-07-24. Header actions are Review Changes, Inspector, and More; Add is a `SplitButton`; session Import Metadata/Columns and matching overflow/context commands are consolidated. Mouse, Apps-key, and Shift+F10 tests pass. |
| WB-05 | Coordinate Inspector, Pending Changes, and column management | Complete | Completed 2026-07-24. One bounded drawer host provides mutual exclusion, initial focus, Escape, real-pointer scrim dismissal, focus restoration, and inspector resumption. Workbench interaction tests pass 3/3. |
| WB-06 | Move tag-layer and ID3 tools into contextual inspector UI | Complete | Completed 2026-07-24. Tag layers, ID3v1/v2 conversion, and encoding controls live in the Inspector with advanced options collapsed. Localized accessibility actions for ID3v1/APEv2/ID3v2 pass integrated UI tests. |
| WB-07 | Compact inspector artwork management | Complete | Completed 2026-07-24. Artwork uses a compact thumbnail list, selected-item detail, Add/Optimize actions, and an accessible per-item overflow. Artwork staging/reordering/action tests pass. |
| WB-08 | Render operation-specific Bulk Operation forms | Complete | Completed 2026-07-24. Exhaustive tests cover all operation descriptors and verify that only relevant editor panels appear; shared conditions are under Advanced and Preview remains the primary action. |
| WB-09 | Simplify Online Metadata into a progressive flow | Complete | Completed 2026-07-24. Scope, provider, search, results, mapping, field groups, artwork, and preview are staged progressively. Provider switching and all result destinations pass focused tests. |
| WB-10 | Refine Output and Automate layouts and inspector behavior | Complete | Completed 2026-07-24. Reports, Playlists, Tools, and Shortcuts adapt to one column; Tools uses Load plus overflow; Output/Automate suppress the inspector while retaining its preference. |
| WB-11 | Split the oversized Workbench view into focused section views | Complete | Completed 2026-07-24. The host owns navigation/drawers and nine destination views plus three drawer views live under `Views/WorkbenchSections`; extraction and responsive tests pass. |

## Shared UI foundations and cross-application corrections

| ID | Plan item | Status | Evidence / notes |
|---|---|---|---|
| UI-01 | Add shared sizing, spacing, density, icon, surface, and command styles | Complete | Completed 2026-07-24. Shared spacing, radius, height, icon, density, surface, quiet-toolbar, overflow, primary, and danger styles are in `AppTheme.axaml`; light/dark lookup tests pass. |
| UI-02 | Add accessible shared command-menu and drawer behavior | Complete | Completed 2026-07-24. Menus/drawers have bounded viewports, Escape/light-dismiss, focus restoration, and active-only Cancel buttons. Apps/Shift+F10 and real-pointer Workbench/Library tests pass; the complete UI suite passes 73/73. |
| UI-03 | Correct Library header and oversized overlays | Complete | Completed 2026-07-24. Library operations and pending surfaces are mutually exclusive and locally bounded; compact inspector pointer/Escape/close/focus behavior passes 1/1 at minimum size. |
| UI-04 | Consolidate Health audit and repair command arrays | Complete | Completed 2026-07-24. Seven audit and seven repair actions are grouped into two discoverable overflow menus; command-count and minimum-size tests pass. |
| UI-05 | Clarify Ingest preview/apply/preflight progression | Complete | Completed 2026-07-24. The flow is Preview then Apply reviewed plan; Preflight is in More and Cancel is active-only. Workflow and UI tests pass. |
| UI-06 | Separate Operations recovery and destructive maintenance | Complete | Completed 2026-07-24. Recovery remains visible while retention preview/purge live in a maintenance overflow; purge keeps danger styling. |
| UI-07 | Make Settings navigation and forms responsive | Complete | Completed 2026-07-24. Grouped rail/picker preserves selection; forms adapt 4→2→1 and actions remain outside scrolling. Settings tests pass at 900×600, 18 px, and sub-700 widths. |
| UI-08 | Correct missing theme resources and standardize icon buttons | Complete | Completed 2026-07-24. Missing resources, typed radii, error styling, icon sizing, and close/overflow controls are standardized; Release build and theme/accessibility tests pass. |

## Localization

| ID | Plan item | Status | Evidence / notes |
|---|---|---|---|
| LOC-01 | Add neutral en-US resources and localization service | Complete | Completed 2026-07-24. Neutral `Strings.resx` contains 2,946 unique validated entries; service tests cover fallback, formatting, counts, and visible `⟦key⟧` missing-key behavior. |
| LOC-02 | Add live Avalonia resource projection and startup initialization | Complete | Completed 2026-07-24. Localization initializes before windows/view models and projects `Loc.*` dynamic resources; live headless updates pass. |
| LOC-03 | Add observable typed localized choices | Complete | Completed 2026-07-24. `LocalizedChoice<T>` refreshes labels in place while immutable semantic values survive culture changes; Settings, Workbench, Health, Ingest, and Devices identity tests pass. |
| LOC-04 | Add live Display Language setting and preference | Complete | Completed 2026-07-24. English (United States) is live and persists under `manager.appearance.culture.v1`; only `CurrentUICulture` changes. |
| LOC-05 | Externalize shell, dialogs, shared controls, and accessibility text | Complete | Completed 2026-07-24. All application XAML, shell/dialog labels, accessibility names, and scoped code-created headers resolve through catalog keys or documented invariants. The recursive XAML verifier passes. |
| LOC-06 | Externalize Workbench, inspector, and Workbench runtime messages | Complete | Completed 2026-07-24. Workbench host, nine sections, three drawers, inspector, editor VMs, status/progress/dialog text, choices, and grid headers are localized. Guarded merge added 746 Workbench entries with zero conflicts. |
| LOC-07 | Externalize remaining desktop UI and presentation messages | In Progress | Static desktop XAML plus Settings, Library/Inspector, Health, Ingest, Organize, Devices, Home, Indexing, shell, and service runtime text are complete. Remaining audited runtime work is concentrated in `Workflows/OperationsViewModel.cs` and `Workflows/FieldsDialogViewModel.cs`; perform one final whole-Presentation literal audit after those migrations. |
| LOC-08 | Add localized external-failure summaries and missing-key behavior | In Progress | Missing keys are visible and tested. Home/Index, Library/Inspector, Health, Ingest, Organize, and Devices expose localized summaries with raw diagnostic detail. Operations and Fields-dialog failure paths remain with LOC-07. |
| LOC-09 | Refresh grid headers without changing persisted layout | Complete | Completed 2026-07-24. `HeaderResourceKey` and in-place refresh preserve column identity/order/width/visibility/sort; a live Workbench culture test exposed and verified programmatic-sort suppression. |

## Validation

| ID | Plan item | Status | Evidence / notes |
|---|---|---|---|
| TST-01 | Add localization unit and resource-completeness tests | Complete | Completed 2026-07-24. Catalog uniqueness, composite formats, count pairs, placeholder parity, fallback, missing keys, stable choices, live resources, and 40%-expanded pseudo text pass; localization/source group passes 18/18. |
| TST-02 | Add literal-string and unresolved-resource validation | In Progress | Recursive all-XAML literal/`Loc.*` validation and scoped shell, Settings, Library/Inspector, Workbench, Health, and workflow runtime verifiers pass. Extend the runtime verifier across Operations/Fields when LOC-07 completes. |
| TST-03 | Add Workbench responsive and command-menu tests | Complete | Completed 2026-07-24. The final matrix rendered and size-checked 216 states; exhaustive Bulk descriptors, menus, extraction, one-primary-action checks, and 27 representative captures pass. |
| TST-04 | Add drawer/flyout keyboard, focus, and bounds tests | Complete | Completed 2026-07-24. Mouse, Apps key, Shift+F10, enabled state, mutual exclusion, Escape, scrim, focus restoration, drawer bounds, and Apply/Revert behavior pass. |
| TST-05 | Add live-culture and grid-layout preservation tests | Complete | Completed 2026-07-24. Culture changes preserve Workbench destination, inspector preference, theme, file selection, recipe step identity/values, and grid column identity/order/width/sort. |
| TST-06 | Expand cross-application minimum-size and accessibility tests | Complete | Completed 2026-07-24. All shell destinations render at minimum size in light/dark; Settings, Library, Health nested panes, menus, overlays, theme resources, and accessible icon buttons pass in the 73/73 UI suite. |
| TST-07 | Complete full build, test, diff, and screenshot validation | Complete | Completed 2026-07-24 for this commit. `dotnet build MusicLibraryTools.Portable.slnf -c Release --no-restore` passed with 0 warnings/errors; full portable tests passed 1,666/1,666; `git diff --check` is clean apart from environment line-ending/config warnings; 27 representative screenshots were captured and spot-checked. |

## Decisions, deviations, and blockers

- NuGet audit/source access is unavailable in the execution environment. Offline restore succeeds with `--ignore-failed-sources -p:NuGetAudit=false`; validation commands use the local package cache.
- Product names retain their canonical display spelling in localized text (for example, `FFmpeg`), while persisted executable paths and identifiers remain unchanged.
- The Workbench audit found that the initial redesign overstated compact-height layouts, the Add split command, the shared drawer, online Results selection, and physical extraction. Each was implemented and verified rather than deferred.
- Screenshot review exposed a shared splitter defect that retained a temporary narrow-width clamp as the wide-layout preference. `PersistedSplitView` now retains the user’s preferred width and restores it when space returns; a new regression test passes.
- `C:\tmp` was not writable from the headless test process, so ignored workspace path `.artifacts/ui-modernization-20260724` stores the 27 validation captures.

## Final summary

- Completed scope: Workbench redesign, shared UI foundations, Settings navigation, cross-app layout corrections, en-US localization infrastructure, all static XAML, and runtime localization for Workbench, Settings, Library/Inspector, Health, Ingest, Organize, Devices, Home/Index, shell, and shared services.
- Remaining issues: LOC-07/08 and TST-02 remain open for runtime text/diagnostics in `Workflows/OperationsViewModel.cs` and `Workflows/FieldsDialogViewModel.cs`, followed by a final whole-Presentation literal audit. No Workbench redesign item remains open.
- Test results: Release build 0 warnings/errors. Portable suites: DumpITL 78, MusicFileUtilities 506, Core 885, Manager 124, UI 73; total 1,666 passed, 0 failed. Responsive matrix: 216 states and 27 representative captures.
- Compatibility: no database/index schema, library-configuration schema, migration/version, or rescan-triggering behavior changed. Stable IDs, theme values, provider/format identifiers, user metadata, and persisted grid semantics remain invariant.
