# Localization catalog generator

`LocalizationCatalogGenerator` deterministically projects the neutral
`MusicLibraryManager.Presentation/Resources/Strings.resx` catalog into the
nine shipping beta satellite catalogs.

The checked-in translation memory preserves composite-format placeholders,
paths, command-line syntax, gestures, product/provider names, and media/tag
format identifiers. Generation fails when a new application-owned value has
no translation-memory coverage, when placeholder signatures differ, or when a
CJK catalog retains unprotected Latin prose.

Native editorial improvements are stored in `EditorialOverrides.xml`. Each
entry supplies all nine shipping translations so a reviewed phrase remains
deterministic across regeneration. Do not edit generated `.resx` catalogs
without adding the corresponding override.

Generate catalogs:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release
```

Verify that checked-in catalogs match the neutral catalog and translation
memory:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- --check
```

Generate into an isolated directory while comparing editorial work:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- `
  --output-directory C:\tmp\localization-baseline
```

Use `--editorial-overrides <path>` to validate a proposed override file before
replacing the checked-in one.

After a complete editorial review of all nine shipping catalogs, capture the
union of existing reviewed overrides and values that differ from the reusable
translation memory:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- `
  --capture-editorial-overrides C:\tmp\EditorialOverrides.reviewed.xml
```

The capture command validates key parity, placeholders, protected CJK tokens,
and nonblank values before writing. Review the resulting diff, then validate
it with `--editorial-overrides` before replacing the checked-in file.

The catalogs are machine-assisted beta translations. Improve reusable
terminology in `Program.cs`; put key-specific editorial wording in
`EditorialOverrides.xml`, then regenerate all satellites.

## Editorial review provenance

`EditorialReviewManifest.xml` records review evidence for every neutral key.
Each record is bound to the neutral value and all nine translations with a
SHA-256 digest. It also records the actual generation route, review status,
batch, reviewer, date, and review disposition. A second aggregate manifest
digest binds the ordered key/value digest set and every status, route, and
provenance field. The supported statuses are:

- `Pending`: no editorial approval has been claimed.
- `InvariantApproved`: `InvariantApprovedValues.v1.tsv` contains the exact
  approved key/value digest and all ten catalog values remain byte-identical.
- `GlossaryReviewed`: a reviewed packet approved an unchanged glossary,
  exact-resource, autonym, or built-in translation.
- `EditorialReviewed`: a reviewed packet or committed editorial-override diff
  approved the override values.

Catalog equality never approves a new invariant automatically, and changing
an already equal value invalidates its old approval digest. The versioned TSV
contains 127 exact key/value approval rows for technical, product/provider,
format, unit, placeholder-layout, and autonym values inspected on 2026-07-25.
Twelve of those resources have stronger focused-editorial evidence, so status
precedence leaves 115 manifest entries as `InvariantApproved`:

```text
Pending=2,075
InvariantApproved=115
GlossaryReviewed=44
EditorialReviewed=1,310
```

`FocusedEditorialReviewEvidence.v1.xml` independently records the exact 847
ordered keys and catalog digests, the two source commits, review metadata, and
an aggregate identity. Later digest-bound packet batches record another 463
editorial overrides and 44 reviewed glossary or exact-resource translations
directly in the manifest. Ordinary manifest refresh preserves those current
packet approvals while loading the checked-in evidence, so CI does not require
historical Git objects and works in shallow clones:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- `
  --refresh-editorial-review-manifest
```

The evidence was reproducibly derived by comparing parsed override values at
the two commits below, rather than by counting XML diff lines. Recreate it
only in a repository that contains both historical objects:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- --check `
  --review-baseline-ref 61e9bc0f9e850a924289aba33679c5df13725201 `
  --reviewed-ref 4c3ab17bf4aed04cb436ad371e982499655cf27e `
  --review-batch gui-usability-editorial-2026-07-25 `
  --reviewer "Codex focused editorial batches" `
  --review-date 2026-07-25 `
  --export-reviewed-evidence `
    BuildTools\LocalizationCatalogGenerator\FocusedEditorialReviewEvidence.v1.xml
```

Refresh preserves current digest-bound `EditorialReviewed` and
`GlossaryReviewed` records. Any stale record is downgraded to `Pending`.
Before preserving existing approvals, refresh validates the existing
manifest's aggregate digest. The checked-in evidence verifies that the
current neutral and all nine override values still match the reviewed set.

Export a deterministic route/status/provenance audit:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- --check `
  --export-review-audit C:\tmp\editorial-review-audit.tsv
```

Export the pending keys for one domain. The packet includes the neutral value,
all nine translations, route, digest, placeholders, protected tokens, and
current provenance. Its identity binds the declared domain and canonical
ordered key/digest/route set; each entry has a separate review disposition
bound to that packet identity:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- --check `
  --review-domain Workbench `
  --export-review-packet C:\tmp\workbench-review.xml
```

After a reviewer accepts every unchanged value in that packet, import the
digest-bound packet rather than editing manifest statuses by hand:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- `
  --approve-review-packet C:\tmp\workbench-review.xml `
  --review-batch workbench-editorial-2 `
  --reviewer "Reviewer name" `
  --review-date 2026-07-25
```

Import rejects stale or injected keys, changed domains or ordered sets, values,
routes, digests, dispositions, duplicate cultures, and records that are no
longer pending. Override routes advance to
`EditorialReviewed`; all other translation routes advance to
`GlossaryReviewed`.

The normal `--check` remains a structural and provenance-integrity gate while
reviews are pending. Release completion can additionally require:

```powershell
dotnet run --project BuildTools\LocalizationCatalogGenerator `
  --configuration Release -- --check --strict-editorial-review
```

That strict command intentionally fails until no `Pending` records remain.
Unknown options, duplicate options, and positional arguments are rejected so
a misspelled strict-gate switch cannot silently weaken validation. Catalogs,
the manifest, audits, packets, and evidence are fully generated and validated
before any target is changed, then installed from flushed sibling staging
files as one rollback-capable output batch.

## Glossary conflict policy

Glossary source terms are case-insensitively unique. Identical duplicate rows
are coalesced, while conflicting duplicates fail before any catalog is
written. The initial conflict cleanup retained the generator's already
effective first row for these six sources, so no satellite value changed:

```text
information|Information|información|information|informazione|informação|情報|정보|信息|資訊
missing|fehlend|ausente|manquant|mancante|ausente|不足|누락|缺失|遺失
before|vorher|antes|avant|prima|antes|変更前|이전|之前|之前
after|nachher|después|après|dopo|depois|変更後|이후|之后|之後
token|Token|token|jeton|token|token|トークン|토큰|标记|權杖
required|erforderlich|obligatorio|requis|obbligatorio|obrigatório|必須|필수|必需|必要
```

`Common.Beta` now follows normal override precedence. Its checked-in values
did not change; the manifest records its route as `EditorialOverride`.
