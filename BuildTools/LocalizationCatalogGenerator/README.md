# Localization catalog generator

`LocalizationCatalogGenerator` deterministically projects the neutral
`MusicLibraryManager.Presentation/Resources/Strings.resx` catalog into the
nine shipping beta satellite catalogs.

The checked-in translation memory preserves composite-format placeholders,
paths, command-line syntax, gestures, product/provider names, and media/tag
format identifiers. Generation fails when a new application-owned value has
no translation-memory coverage, when placeholder signatures differ, or when a
CJK catalog retains unprotected Latin prose.

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

The catalogs are machine-assisted beta translations. Native-language review
can improve a term or phrase by editing its row in `Program.cs` and
regenerating all satellites; generated `.resx` files should not be edited by
hand.
