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
