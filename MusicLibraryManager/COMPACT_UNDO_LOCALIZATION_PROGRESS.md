# Compact Undo and Common-Language Localization Progress

Branch: `feature/compact-undo-common-locales`

Base commit: `20aa13e`

Started: 2026-07-24

Last updated: 2026-07-24

## Status legend

- **Not Started** - implementation has not begun.
- **In Progress** - implementation or verification is underway.
- **Blocked** - work cannot continue without an external decision or dependency.
- **Complete** - implementation is finished and verification evidence is recorded.
- **Deferred** - intentionally moved to documented follow-up work.

## Delivery and compatibility

| ID | Plan item | Status | Completion | Files / components | Verification evidence, decisions, and follow-up |
|---|---|---|---|---|---|
| DEL-01 | Create the requested feature branch from clean `20aa13e` | Complete | 2026-07-24 | Git branch `feature/compact-undo-common-locales` | Branch creation was verified against exact base commit `20aa13e`. |
| DEL-02 | Maintain this progress ledger before implementation and before every commit | Complete | 2026-07-24 | This document | Created before source implementation, updated after implementation batches, and finalized with validation evidence before the branch commit. |
| DEL-03 | Preserve database/index schemas and avoid rescan behavior | Complete | 2026-07-24 | Compatibility audit | Final audit: 63 changed files, 0 schema/index/configuration migration paths, and 0 added schema/rescan lines. No rescan behavior was introduced. |
| DEL-04 | Preserve existing journal, history, recipe, and retention compatibility | Complete | 2026-07-24 | Recovery models; `FileMutationPlanExecutor`; `OperationJournalService`; `EditHistoryService` | Legacy TSV records, journal locations, `manager.workbench.history.v1`, recipe serialization, direct-edit redo behavior, and the existing 90-day retention preference remain compatible. Full solution tests pass. |

## Compact metadata undo

| ID | Plan item | Status | Completion | Files / components | Verification evidence, decisions, and follow-up |
|---|---|---|---|---|---|
| UND-01 | Add opt-in `RecoveryPayloadPolicy`; select adaptive deltas only for reviewed metadata paths | Complete | 2026-07-24 | `RecoveryPayloadModels.cs`; `LibraryOperationModels.cs`; `MetadataWorkbenchServices.cs` | `FullOriginal` remains the default. Only the shared reviewed metadata operation used by Workbench, Library, Inspector, and Fields selects `AdaptiveReverseDelta`; ingest, delete, quarantine, ordinary file operations, direct tag transactions, and artwork normalization keep full/move recovery. |
| UND-02 | Implement a bounded-memory streaming reverse-delta codec and descriptor | Complete | 2026-07-24 | `ReverseDeltaModels.cs`; `ReverseDeltaService.cs`; `ReverseDeltaServiceTests.cs` | Versioned 160-byte header; pre/post lengths and SHA-256 hashes; timestamp/attributes; payload checksum; 16/64/256 KiB content-defined chunks; copy commands; Brotli literals; bounded indexing; strict streaming decode. The final size bound includes tiny-copy overhead and is tested through multi-gigabyte declared lengths. |
| UND-03 | Generate, flush, validate, and adaptively retain the delta under the mutation lease | Complete | 2026-07-24 | `FileMutationPlanExecutor.cs`; compact undo tests | Delta creation, durable flush, full reconstruction validation, and final capacity/payload selection occur under the mutation lease before replacing the live file. Staging is sibling/same-volume and replacement is atomic. Payload plus journal overhead must be smaller than full recovery or the executor falls back to the legacy path. |
| UND-04 | Add versioned compact journal records while retaining legacy TSV parsing | Complete | 2026-07-24 | `FileMutationPlanExecutor.cs`; `OperationJournalService.cs`; journal tests | Added `PLAN_COMPACT_REPLACE`, `DELTA_READY`, and `COMPACT_REPLACE`; legacy records remain readable. Older readers do not mistake compact payloads for whole originals. `ROLLBACK_FAILED` keeps incomplete recovery discoverable instead of purge-eligible. |
| UND-05 | Extend recovery models and operation reporting with payload metadata and storage savings | Complete | 2026-07-24 | `RecoveryPayloadModels.cs`; `OperationJournalModels.cs`; `MetadataWorkbenchModels.cs`; Operations view/view model | Recovery actions expose payload kind, retained bytes, hashes, delta path, and recorded metadata. Apply results optionally expose aggregate retained size/savings, and Operations identifies compact versus full payloads using localized text. |
| UND-06 | Implement exact, stale-safe, atomic compact restore | Complete | 2026-07-24 | `OperationJournalService.cs`; `ReverseDeltaService.cs`; compact and format-matrix tests | Every action and post-edit hash is prevalidated before any mutation. A stale compact base refuses the whole undo without consuming history. Originals are reconstructed and verified in sibling temporary files, restored atomically, and recover bytes, last-write time, and standard attributes exactly. |
| UND-07 | Add batch prevalidation/application and two-phase restore/history transition | Complete | 2026-07-24 | `IOperationJournalService`; `OperationJournalService`; `EditHistoryService`; compact tests | Durable BEGIN/APPLIED/COMMIT/CONSUMED transitions distinguish unapplied, committed, and consumed undo. Restart reconciles partial multi-journal/multi-volume restores. Cleanup failures now remain retryable and preserve pending history; deterministic obstruction/retry tests cover post-COMMIT and prepared-temp cleanup. |
| UND-08 | Preserve recipe redo and unsupported direct-edit redo behavior | Complete | 2026-07-24 | `EditHistoryService`; metadata history tests | Recipe-based redo and existing direct-edit limitations are unchanged. Apply/restart/undo/recipe-redo coverage passes across the writable-format matrix. |
| UND-09 | Update preview and final recovery-space checks | Complete | 2026-07-24 | `MetadataWorkbenchServices.cs`; `FileMutationPlanExecutor.cs`; tests | Preview estimates the whole-file staging requirement. Apply performs a conservative final delta-capacity check and ensures full-fallback capacity before changing a live file. Longest matching volume roots are used for mounted-volume accounting. |

## Localization expansion

| ID | Plan item | Status | Completion | Files / components | Verification evidence, decisions, and follow-up |
|---|---|---|---|---|---|
| LOC-01 | Add shared cardinal plural resolution | Complete | 2026-07-24 | `CardinalPluralResolver.cs`; `ResourceLocalizationService.cs`; `LocalizedText.cs`; pseudo-localization | One resolver supports Zero, One, Two, Few, Many, and Other. Resource, static, and pseudo paths use it; shipping-locale category and `pt-BR` zero/one regressions pass. |
| LOC-02 | Add supported-locale descriptors, native beta autonyms, OS mapping, and persisted first-run resolution | Complete | 2026-07-24 | `LocalizationCultureRegistry.cs`; `ResourceLocalizationService.cs`; `SettingsViewModel.cs` | Ten stable locale IDs are exposed. Nine non-English choices use native beta autonyms. Exact/family matching includes required Hans/Hant mappings, persists the resolved locale, falls back to en-US, and changes only `CurrentUICulture`. |
| LOC-03 | Create and enforce the protected terminology glossary | Complete | 2026-07-24 | `LOCALIZATION_GLOSSARY.md`; catalog generator and tests | Product/provider names, tag formats, extensions, paths, gestures, CLI flags, and composite/dynamic tokens are documented and validated. |
| LOC-04 | Add complete `de-DE`, `es-ES`, and `fr-FR` beta catalogs | Complete | 2026-07-24 | `Strings.de-DE.resx`; `Strings.es-ES.resx`; `Strings.fr-FR.resx` | Each satellite has exact parity with all 3,387 neutral resources, with no blanks, fallback markers, or placeholder/protected-token mismatches. |
| LOC-05 | Add complete `it-IT`, `pt-BR`, and `ja-JP` beta catalogs | Complete | 2026-07-24 | `Strings.it-IT.resx`; `Strings.pt-BR.resx`; `Strings.ja-JP.resx` | Each satellite has exact 3,387-key parity and passes deterministic value, placeholder, token, and plural validation. |
| LOC-06 | Add complete `ko-KR`, `zh-CN`, and `zh-TW` beta catalogs | Complete | 2026-07-24 | `Strings.ko-KR.resx`; `Strings.zh-CN.resx`; `Strings.zh-TW.resx` | Each satellite has exact 3,387-key parity and passes deterministic value, placeholder, token, plural, and CJK-residue validation. |
| LOC-07 | Preserve live culture switching and state identity | Complete | 2026-07-24 | Localization service; editors; `AppDataGrid`; Workbench section views; UI tests | All shipping locales preserve destination, selected section/files, recipe IDs and semantics, theme, inspector preference, and grid order/width/sort. Headers refresh in place without rebuilding columns. Untouched app-owned editor defaults relocalize; user names remain unchanged. |
| LOC-08 | Use platform CJK font fallback and document Linux font expectations | Complete | 2026-07-24 | Existing platform fallback; `DEPLOYMENT.md`; screenshot matrix | German and four CJK minimum-size matrices pass in light/dark at 18 px. Representative captures show no missing glyph boxes. Linux documents `fontconfig` and a suitable system CJK font such as Noto CJK. |
| LOC-09 | Verify and recursively sign/package first-party satellite assemblies | Complete | 2026-07-24 | `Package.ps1`; `WindowsPackageSigning.ps1`; CI workflows; `DEPLOYMENT.md` | Every RID publish asserts all nine satellites. Windows catalog creation and verification share an exact 17-file definition and reject missing, duplicate, stale, or unexpected entries. Local win-x64/linux-x64 packages and win/linux/osx-x64/osx-arm64 publishes contain all nine satellites. Credentialed Authenticode and DMG verification remain host-protected release-CI gates. |

## Validation

| ID | Plan item | Status | Completion | Files / components | Verification evidence, decisions, and follow-up |
|---|---|---|---|---|---|
| TST-01 | Test reverse-delta algorithms, validation, corruption resistance, cancellation, and streaming scale | Complete | 2026-07-24 | `ReverseDeltaServiceTests.cs` | The final compact/codec group passes 50/50. It covers insertion, deletion, replacement, both ends, repeated blocks, payload/header corruption, malicious offsets, cancellation, exact metadata, 64 MiB streaming, virtual multi-gigabyte streaming/cancellation, and 64-bit length/bound calculations. |
| TST-02 | Meet adaptive retention size bounds | Complete | 2026-07-24 | `CompactMetadataUndoTests.cs`; codec bound tests | A representative 64 MiB title-only edit retains less than `min(5%, 1 MiB)`. Adaptive payloads are compared with original plus actual journal overhead; incompressible data uses full fallback. Conservative encoded bounds include the worst tiny-copy case. |
| TST-03 | Exercise byte-exact apply/restart/undo/redo across writable fixture families | Complete | 2026-07-24 | `MetadataWorkbenchServicesTests.cs` | Twenty cases pass for MP3, DSF, WAV, AIFF, AAC, FLAC, Ogg, AAC/ALAC MP4, WavPack, APE, MPC, TTA, TAK, OptimFROG variants, WMA/ASF, Matroska, and WebM. Each applies, restarts history, restores exact bytes/time/attributes, and recipe-redoes. |
| TST-04 | Test mixed payloads, stale bases, atomic batches, catalog failures, and crash boundaries | Complete | 2026-07-24 | Compact, journal, media-catalog, and metadata tests | The broader recovery subset passes 76/76. Coverage includes mixed compact/full batches, stale same-length/time bases, no-partial-apply refusal, catalog rollback, forward rollback failure, prepared/APPLIED/COMMIT crash states, cross-journal retries, and cleanup obstruction/reconciliation. Existing field, artwork, ID3v1/v2, APEv2, tag-layer, version, and encoding regressions remain green. |
| TST-05 | Verify legacy recovery discovery, restore, purge, and history compatibility | Complete | 2026-07-24 | `OperationJournalServiceTests.cs`; metadata history tests; compact fallback tests | Legacy full recovery remains readable/restorable/purgeable. Original-only `DELTA_READY` is classified safely for retention, while failed rollback remains interrupted and recoverable. Existing history preference and recipe payloads are unchanged. |
| TST-06 | Validate every satellite and plural/fallback behavior | Complete | 2026-07-24 | `LocalizationCatalogGenerator`; localization tests | Deterministic `--check` validates 9 satellites x 3,387 resources: exact keys, nonblank values, format signatures, protected/dynamic tokens, required plural categories, no untranslated fallback markers, and visible neutral missing-key behavior. |
| TST-07 | Test persisted startup and live switching for every locale | Complete | 2026-07-24 | Presentation/UI localization tests | Startup mapping, persisted choices, all-ten live switching, unchanged `CurrentCulture`, fallback, editor defaults, and navigation/selection/recipe/theme/grid identity all pass. |
| TST-08 | Run minimum-size German/CJK and expanded pseudo-locale visual matrices | Complete | 2026-07-24 | `WorkbenchResponsiveMatrixTests`; headless captures | Both responsive tests pass. The pseudo matrix covers all destinations at 900x600, 1200x700, and 1440x900 in light/dark, 14/18 px, normal/40%-expanded text. German/CJK cover every destination at 900x600, light/dark, 18 px. Captures are retained under `.artifacts/compact-undo-locales-20260724`. |
| TST-09 | Complete Release build/test and Windows/Linux/macOS publish/package verification | Complete | 2026-07-24 | Portable solution; package outputs; signing catalog | Final Release build: 0 warnings/errors. Final tests after the About follow-up: 1,831/1,831 (955 Core, 211 presentation, 81 UI, 506 utilities, 78 DumpITL). The portable publish contains all nine satellites, both loose third-party agreements, and the previously verified platform packages retain their existing layout. Real Authenticode verification needs release credentials; DMG verification runs on macOS CI. |

## Post-plan About-page follow-up

| ID | Follow-up item | Status | Completion | Files / components | Verification evidence, decisions, and follow-up |
|---|---|---|---|---|---|
| FUP-01 | Add a branded, responsive About destination | Complete | 2026-07-24 | `BrandMark`; `AboutView`; `MainWindow`; `ShellDestination` | Added a stable final enum value and bottom-rail destination, reusable vector branding, responsive one/two-column package cards, localized accessible names, and exact assembly copyright/version display. Headless 1440x900 and isolated light/dark 900x600 captures were visually inspected. |
| FUP-02 | Include referenced package names and complete license agreements | Complete | 2026-07-24 | `AboutView`; `Assets/Licenses`; `.gitattributes`; project/package files | Displays Avalonia UI 12.1.0 and ImageSharp 3.1.12 with expandable, selectable, copyable full agreements. Byte-integrity tests cover pinned Avalonia MIT and ImageSharp Split License assets; the scoped Git attribute prevents checkout line-ending conversion. A portable publish contains both agreements under `ThirdPartyLicenses`. |
| FUP-03 | Localize the About experience across every shipping locale | Complete | 2026-07-24 | Neutral/satellite catalogs; glossary; catalog generator | Added 28 complete resources to en-US and all nine beta satellites. Generator and catalog tests validate exact 3,387-key parity, placeholders, protected package/license names, and CJK residue. Live en-US to German to Japanese switching preserves route, expanded agreements, and invariant legal content. |
| FUP-04 | Prevent localization-validator crash dialogs | Complete | 2026-07-24 | `CatalogGeneratorCommandLine`; generator tests | The command-line boundary catches validation exceptions, writes a plain diagnostic, and returns exit code 1. Regression tests cover failure and success paths; the generator was run through its DLL host and completed generation plus `--check` without an unhandled Windows exception. |
| FUP-05 | Verify routing, responsive behavior, legal content, and packaging | Complete | 2026-07-24 | UI/unit/source-verifier tests; portable publish | Focused tests cover routing, brand/copyright/version text, complete legal bodies, both copy commands, compact reflow, live localization, and the real Settings-discard navigation guard. Full Release build succeeded with 0 warnings/errors and all 1,831 tests passed. |

## Decisions, deviations, blockers, and follow-up

- Compact recovery is deliberately opt-in. Existing mutation callers retain full-file/move recovery unless explicitly selected by the shared reviewed metadata path.
- Exact undo means identical bytes plus recorded last-write time and standard attributes. Creation time, ACLs, alternate data streams, and undo after an external modification remain outside scope.
- Compact journal operations use distinct names so older versions safely ignore them instead of treating a delta as a whole original.
- Cleanup or rollback failure remains retryable and retains recovery/history. It is never journaled as consumed or purge-eligible.
- Shipping translations are machine-assisted beta translations and use native autonyms. Installer and package metadata remain English.
- CJK uses platform font fallback. Linux deployments need `fontconfig` and an installed CJK family.
- The current Windows host validated all four self-contained publishes, Windows/Linux archives, checksums, satellite layout, and signing-catalog coverage. Authenticode signatures and macOS DMGs require release credentials/macOS and remain enforced by the existing host-specific CI jobs rather than being simulated locally.
- No implementation blocker or intentional scope deviation remains.

## Final summary

- Completed scope: adaptive reverse-delta metadata recovery, exact stale-safe restartable undo, legacy compatibility, nine complete beta locales, live localized grid/editor behavior, deterministic catalog tooling, resource-aware packaging/signing, the branded About destination with complete third-party agreements, and the requested validation coverage.
- Remaining issues: no code issue is open. Credentialed signing and macOS DMG creation are external release gates.
- Test results: Release build succeeded with 0 warnings/errors; 1,831/1,831 solution tests passed; localization generator validated 9 x 3,387 resources; responsive matrix passed 2/2; focused compact/codec passed 50/50 and broader recovery passed 76/76.
- Compatibility: no database/index schema, library-configuration schema, migration, serialized recipe/history identity, retention preference, or rescan behavior changed.
