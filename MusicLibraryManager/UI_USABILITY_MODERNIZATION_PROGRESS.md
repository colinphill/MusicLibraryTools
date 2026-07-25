# GUI Usability Modernization Progress

Branch: `feature/gui-usability-modernization`

Base: `cb797b3` (`feature/workbench-transcode-engine`)

Started: 2026-07-25

Last updated: 2026-07-25

This ledger mirrors the approved GUI usability and visual-consistency plan. An item is marked **Complete** only when its implementation and current verification evidence are recorded here.

## Status legend

- **Not Started** — implementation has not begun.
- **In Progress** — implementation or required validation is underway.
- **Blocked** — completion requires an external decision or dependency.
- **Complete** — implementation is finished and its verification evidence is recorded.
- **Deferred** — intentionally moved to documented follow-up work.

## Checkpoint evidence

- Release build, `MusicLibraryManager.Tests`: 0 warnings, 0 errors.
- Complete `MusicLibraryManager.Tests` run: 287/287 passed.
- Release build, `MusicLibraryManager.UI.Tests`: 0 warnings, 0 errors.
- Complete `MusicLibraryManager.UI.Tests` run: 128/128 passed in 4 minutes 44 seconds.
- Focused adaptive/interaction UI run: 99/99 passed.
- Localization generator validation: nine satellite catalogs with exact parity at 3,543 resources each.
- Editorial localization coverage: 2,225 validated overrides, including 849 reviewed source keys across six focused editorial batches.
- Shared protected-term validation now covers generator output, editorial overrides, and tests from one source of truth.
- The test suite executes the real generator `--check`; isolated capture-mode coverage proves a changed protected term fails without writing an override file.
- Source, catalog, placeholder, plural, protected-token, and modernization-quality gates are included in the green 261-test run.
- `git diff --check`: clean.
- Compatibility filename audit: no database, schema, migration, index, scan, recovery-journal, history, or library-configuration implementation files changed.
- Existing retained visual artifacts: 283 PNG captures and 23 contact-sheet PNGs. These are not accepted as final evidence because 112 captures and current source changes postdate the written review.

## Delivery and compatibility

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| DEL-01 | Create the dedicated implementation branch and progress ledger | Complete | 2026-07-25 | This document; Git branch | Created `feature/gui-usability-modernization` from clean commit `cb797b3`; the ledger was created before implementation. |
| DEL-02 | Deliver release blockers and shared foundations as a reviewable phase | Complete | 2026-07-25 | Shared controls, styles, adaptive tests | `AdaptivePage`, adaptive `PageHeader`, shared styles, vector icons, density projection, and dialog action roles are implemented. Release builds and the complete presentation/UI suites pass. |
| DEL-03 | Deliver shell, Library handoff, and reviewed-mutation coordination | Complete | 2026-07-25 | Shell; Library; Workbench coordinator; latent editors | Shell overlay/collapse, shared search, Library handoff, immutable mutation intents, Fields routing, and file-operation routing are implemented. Composition and failure-retention regressions pass within the 261-test suite. |
| DEL-04 | Deliver Workbench section and drawer corrections | In Progress | 2026-07-25 | Workbench host, sections, drawers | Section and drawer redesign is implemented and the Workbench responsive/interaction matrix passes. Independent forward/reverse focus-cycle coverage is now green for Inspector, Review Changes, Columns, and Transcode. Completion still requires the remaining exact compact-height breakpoint evidence. |
| DEL-05 | Deliver cross-application corrections | Complete | 2026-07-25 | Home, Health, Ingest, Organize, Devices, Operations, Settings, About, dialogs | All listed destinations were modernized. The complete 124-test UI run includes minimum-size, routing, layout, dialog, and Health navigation coverage. |
| DEL-06 | Deliver locale polish, accessibility, and expanded validation | In Progress | 2026-07-25 | Resources, generator, source validators, UI tests | Structural localization, shared glossary enforcement, broad editorial expansion, and drawer/navigation keyboard coverage are green. Complete nine-locale editorial proof, explicit label/help association, full cross-app matrices, and fresh human-reviewed contact sheets remain. |
| DEL-07 | Preserve schemas, scan behavior, configuration compatibility, IDs, journals, and history | In Progress | 2026-07-25 | Whole change set | No prohibited implementation files changed. Stable section IDs and new isolated appearance preferences are preserved. Final full-solution/publish audit is still required. |

## Phase 1 — Shared adaptive UI foundation

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| FND-01 | Add content-host adaptive page scaffold and layout constraints | Complete | 2026-07-25 | `Controls/AdaptivePage.cs`; responsive view code | Content-host width drives layouts. Exact −1/0/+1 tests cover shared width/height thresholds and principal destination breakpoints; Workbench and Library central-width invariants pass. |
| FND-02 | Replace fixed PageHeader title reservation with adaptive semantic command bar | Complete | 2026-07-25 | `Controls/PageHeader.*`; migrated pages | Primary, secondary, and More slots are implemented with legacy compatibility. The measured 966/967/968 test proves primary/More visibility and row stability; heading nonfocusability is source-validated. |
| FND-03 | Add shared labeled fields, cards, frames, sticky footers, empty states, navigation, overlays, and scalable typography | Complete | 2026-07-25 | `Styles/AppTheme.axaml`; shared controls; destination views | Shared style families and approved spacing/radius/control scales are in use. Unresolved-class, raw-control, literal-copy, vector-icon, spacing, and radius source gates pass. |
| FND-04 | Add persisted Standard/Compact `UiDensity` preference | Complete | 2026-07-25 | `AppearancePreferences.cs`; Settings; theme resources | Persists under `manager.appearance.density.v1` with Standard default. Preference round-trip and representative Standard/Compact rendering tests pass. |
| FND-05 | Separate dialog tone from action role and correct destructive styling | Complete | 2026-07-25 | Dialog contracts/service/host | Added destructive confirmation semantics independent of message tone. Dialog keyboard/focus tests and source checks pass. |

## Phase 2 — Shell, Library, and reviewed media coordination

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| SHL-01 | Add adaptive collapsible shell rail with persisted manual preference and narrow overlay | Complete | 2026-07-25 | `MainWindow.*`; appearance preferences | Shell uses 64/220 widths; below the safe width, expansion is an overlay. Persistence, monotonic resize, Escape, scrim dismissal, and focus restoration tests pass. |
| SHL-02 | Keep every destination navigable with contextual configuration states | Complete | 2026-07-25 | Shell and destination views | All destinations remain routed without configuration. Setup cards own configuration guidance while dependent actions are disabled and explained. Complete destination routing/render tests pass. |
| SHL-03 | Consolidate to one shell search with Library handoff behavior | Complete | 2026-07-25 | `MainWindow.*`; `LibraryViewModel` | One shell search provides Library live filtering and cross-page Enter handoff; Ctrl/Cmd+K focus behavior and search-state tests pass. |
| LIB-01 | Remove duplicated advanced editor and add stable Workbench handoffs | Complete | 2026-07-25 | `LibraryView.*`; `WorkbenchHandoff.cs` | Added selected, visible, and all-result handoffs to stable Workbench destinations while preserving Library state. Handoff round-trip tests pass. |
| LIB-02 | Add visible Library selection actions and mirrored grid context menu | Complete | 2026-07-25 | Library view/view model | Selection actions cover Workbench handoff, reveal/copy path, and affected-path refresh; context-menu behavior is mirrored. Library interaction tests pass. |
| LIB-03 | Make Library inspector docking selection- and space-aware | Complete | 2026-07-25 | `LibraryView.*` | Inspector docking respects selection, explicit preference, and the 760 px central-content invariant. Overlay Escape/scrim/focus-cycle and threshold tests pass. |
| PND-01 | Generalize pending coordinator to immutable reviewed media-mutation intents | Complete | 2026-07-25 | `ReviewedMediaMutationIntent.cs`; `WorkbenchViewModel` | Metadata, artwork, tag layers, file operations, and transcodes coexist as deterministic per-source units. Queue-wide snapshot/policy/blocker/collision prevalidation and same-source three-kind tests pass. Existing file-operation transactions remain separate by compatibility design; a new race between transaction boundaries remains an existing semantic limitation, not a journal change. |
| PND-02 | Add stable `WorkbenchHandoffRequest` | Complete | 2026-07-25 | `WorkbenchHandoff.cs`; Library/Workbench | Stable section, scope, and captured-path identities are implemented without changing persisted Workbench section IDs. Presentation handoff tests pass. |

## Phase 3 — Workbench sections and drawers

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| WB-01 | Reduce compact Workbench chrome to two rows and correct action emphasis | Complete | 2026-07-25 | `WorkbenchView.*` | Compact chrome, neutral `Changes (0)`, source-aware Add Files emphasis, and source-independent strip behavior are implemented. Responsive matrix tests pass. |
| WB-02 | Standardize section frame, scroll ownership, empty state, and sticky workflow footer | Complete | 2026-07-25 | Workbench section views | Section frames, empty states, scroll ownership, and sticky actions were normalized. The 216-combination Workbench matrix finds no page-level horizontal overflow. |
| WB-03 | Correct Session, Bulk edit, and All fields responsive layouts | Complete | 2026-07-25 | Session/Bulk/All Fields views | Session retains at least 220 px of unobscured grid height; Bulk remains operation-contextual; All Fields uses split/drill-in behavior. Exact-width and minimum-size tests pass. |
| WB-04 | Correct File operations and Online metadata workflows | Complete | 2026-07-25 | File Operations; Online Metadata | File operations queue reviewed intents; Online Metadata uses progressive summaries with the active step expanded. All five result tabs and both artwork tabs are exercised at minimum size under German, Japanese, Simplified Chinese, and expanded pseudo text. |
| WB-05 | Correct Reports, Playlists, External tools, and Shortcuts | Complete | 2026-07-25 | Output/Automate views | Local Preview→Write/Run semantics are preserved. Reports hide irrelevant grouping controls, tools use overflow, and Shortcuts has a real narrow drill-in/empty CTA. Representative state and responsive tests pass. |
| WB-06 | Unify Inspector, Review Changes, Columns, and Transcode drawer host | Complete | 2026-07-25 | Workbench drawer host/views | Shared host, bounds, scrim, Escape, sticky footer, focus restoration, and forward/reverse focus cycling are implemented. Independent interaction tests cover Inspector, Review Changes, Columns, and Transcode in the green 128-test UI suite. |
| WB-07 | Route inspector, grid, artwork, and tag-layer drafts only through Review Changes | Complete | 2026-07-25 | Inspector; Fields; pending coordination | Direct Save tags/artwork and latent Fields/file-operation Apply paths were removed. Drafts survive failed application and clear only after success. Mutation safety and failure-retention tests pass. |
| WB-08 | Compact artwork and tag-layer editors | Complete | 2026-07-25 | `SelectionInspectorView`; inspector drawer | Artwork uses a thumbnail list with selected details and overflow actions; tag layers show Present/Missing state with per-layer overflow and advanced conversion controls. Source/UI tests pass. |
| WB-09 | Replace pending grid and rebuild Columns editor | Complete | 2026-07-25 | Pending/Columns drawer views | Pending changes use stacked Before→After rows; Columns is searchable, reorderable, and explains unavailable inline editing. Interaction and source tests pass. |
| WB-10 | Bring Transcode drawer onto shared styles and accessibility patterns | Complete | 2026-07-25 | Transcode drawer/view model | Shared field/control styles, durable labels, correction/layout/concurrency controls, blockers, preset overflow, width bounds, and sticky Preview are implemented. Transcode presentation and responsive tests pass. |

## Phase 4 — Cross-application corrections

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| APP-01 | Correct Home and Organize setup/action layouts | Complete | 2026-07-25 | Home/Organize views | Removed duplicate Home setup action, added adaptive metric layouts, and preserved Organize Preview→Apply with contextual setup. Exact breakpoints and destination renders pass. |
| APP-02 | Modernize Health result navigation and scrolling | Complete | 2026-07-25 | Health view/view model | Health uses filtered grouped rail/picker navigation, contextual Similar artist threshold, and master/detail layouts. Empty group headers are removed; live locale updates preserve labels. Populated 900×600 and routing tests pass. |
| APP-03 | Clarify Ingest preflight and responsive summaries | Complete | 2026-07-25 | Ingest view | Preflight is visible beside readiness; summaries/details adapt at narrow widths. Exact width/height and source-quality tests pass. |
| APP-04 | Clarify Devices lifecycle and responsive editor/results | Complete | 2026-07-25 | Devices view | The next valid lifecycle action owns primary emphasis; Restore moved to recovery/More; narrow configuration/results adapt. Exact breakpoint and routing tests pass. |
| APP-05 | Modernize Operations master/detail and restore footer | Complete | 2026-07-25 | Operations view | Narrow pickers replace fixed panes, empty output collapses, restore stays sticky, and Recovery remains separate from maintenance. Exact breakpoint and danger-menu tests pass. |
| APP-06 | Modernize Settings navigation, forms, summaries, and Effective Policy prose | Complete | 2026-07-25 | Settings view/view model | Content-width rail/picker, adaptive forms, summary cards, narrow mappings, and prose policy presentation are implemented. Settings-focused UI tests pass 11/11 within the complete suite. |
| APP-07 | Refine About short-height behavior and license expanders | Complete | 2026-07-25 | About view | Existing hero, product identity, copyright, and package licenses are preserved; short-height spacing and themed expanders are implemented. About rendering/license tests pass. |
| APP-08 | Correct Fields editor close confirmation and field-add affordances | Complete | 2026-07-25 | Dialog/Fields editor | Close/Escape route through confirmation; standard and custom-string additions are distinct. Preview now states counts, confirms nothing was written, and routes to Review Changes. Dialog safety and localization tests pass. |

## Phase 5 — Localization, accessibility, and validation

| ID | Plan item | Status | Date | Components | Verification, decisions, and follow-up |
|---|---|---|---|---|---|
| LOC-01 | Remove shipping scaffold copy and standardize en-US semantics/technical terms | Complete | 2026-07-25 | Neutral catalog; source gates; `TechnicalLabelResourceKeys.cs` | Scaffold phrases, raw glyph buttons, obsolete direct-write strings, duplicate technical labels, and ASCII three-dot ellipses are removed. Shared semantic resources cover standard formats, encodings, line endings, ID3 versions, and MusicBrainz IDs. The complete source-quality suite passes. |
| LOC-02 | Polish all nine beta satellite catalogs | In Progress | 2026-07-25 | Nine satellite catalogs; `EditorialOverrides.xml` | Structural parity is green at 9×3,543. Editorial overrides now cover 2,225 keys after six focused review batches; 1,318 entries still rely on generated composition/fallback and require completion evidence. Confirmed gesture, MIME, CLI, path, OptimFROG, CUE, CJK spacing, and Windows Media Player naming defects found by review are corrected. |
| A11Y-01 | Add durable labels, help associations, semantic navigation, and scoped keyboard behavior | In Progress | 2026-07-25 | Application views; source/UI tests | Accessible names/visible labels, PageHeader headings, scoped Workbench Menu key, all four drawer focus cycles, shell navigation keys, Settings wide-list Home/End and compact-picker synchronization, Library focus traps, and shell overlay focus restoration are tested. Explicit label/help association and deterministic Library Shift+F10/Menu-key handling remain. |
| VAL-01 | Add source validation for scaffold copy, unresolved classes, raw controls, labels, literals, and protected spellings | Complete | 2026-07-25 | Test projects; generator; `LocalizationProtectedTerms.cs` | Source gates reject scaffold copy, raw literals, unresolved classes, font glyph buttons, missing input names, noncanonical technical spellings, ASCII action ellipses, and catalog structural defects. An independent mandatory glossary baseline guards the shared implementation, capture validates protected terms before authoring overrides, and the test suite runs the real generator drift check. All gates pass in the 287-test suite. |
| VAL-02 | Expand responsive/state/locale/density/keyboard screenshot matrix | In Progress | 2026-07-25 | UI tests and screenshot fixtures | Workbench has a 216-combination render matrix plus ten-locale minimum-size coverage and representative state fixtures. Cross-app 1200×700, full density/text/locale matrices, several state cross-products, and missing exact thresholds remain. |
| VAL-03 | Add non-overlap, reachability, monotonic-layout, and primary-action assertions | In Progress | 2026-07-25 | UI tests | Session height, page overflow, selected overlap regions, central-width constraints, one-primary Workbench regions, destructive scope, and principal monotonic breakpoints are covered. Generic action reachability, all destructive workflows, and remaining compact-height/app-shell boundaries remain. |
| VAL-04 | Run complete Release build/tests and human-review contact sheets | In Progress | 2026-07-25 | Whole solution; artifacts | Manager presentation/UI projects are green at 287/287 and 128/128. A fresh current-worktree capture run, regenerated contact sheets, written human review of every required locale/state, complete solution tests, and publish/package verification remain. |

## Decisions, deviations, blockers, and follow-up

- “Brand” terminology is not introduced. Existing palette, typography direction, product mark, About hierarchy, reviewed-change model, localization, keyboard context menus, and focus restoration remain foundations.
- Reports, playlists, and external tools intentionally retain local Preview→Write/Run flows; recoverable media mutations use global Review Changes.
- File operations retain their existing transaction after the existing metadata/transcode transaction. Queue-wide known blockers are checked before live mutation, but a new race between those existing transaction boundaries cannot be made all-or-recover without changing history/redo and journal contracts. Those contracts are explicitly preserved.
- No database/index schema, scan behavior, library-configuration format, recovery journal, or rescan behavior is changed.
- Existing visual artifacts are stale relative to the current tree and are not completion evidence.
- Open blockers: none.

## Completion summary

- Completed scope: adaptive foundation; shell; Library handoff; reviewed-mutation coordination; primary Workbench and cross-application redesign; neutral localization semantics; shared glossary enforcement; all shared-drawer focus behavior; broad automated validation.
- Remaining scope: prove complete nine-locale editorial quality; close Library keyboard-menu and explicit label/help association gaps; complete exact-breakpoint/state matrices; generate and review fresh screenshots/contact sheets; run complete solution and publish/package verification.
- Current test results: 287/287 presentation tests, 128/128 UI tests, 9×3,543 localization resources; all Release builds above have 0 warnings and 0 errors.
- Compatibility: current changes are confined to UI, presentation coordination, isolated appearance preferences, localization resources/generator, and tests. Stable navigation/section identities, recipes, grid state, themes, culture preference, recovery/history formats, and scan behavior remain unchanged.
