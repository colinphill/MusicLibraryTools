# UI contact-sheet generator

This tool validates and converts the complete GUI-modernization screenshot
matrix into deterministic, captioned contact sheets. It refuses incomplete
required matrices, missing shipping-locale frames, duplicate capture
identities, nonempty output directories, and non-exact Git SHAs.

Run it only against a clean, immutable validation capture:

```powershell
$sourceSha = (git rev-parse HEAD).Trim()
dotnet run --project BuildTools\UiContactSheetGenerator `
  --configuration Release -- `
  --capture-directory .artifacts\gui-final-$($sourceSha.Substring(0, 12))\captures `
  --output-directory .artifacts\gui-final-$($sourceSha.Substring(0, 12))\contact-sheets `
  --source-sha $sourceSha
```

The output includes:

- captioned PNG sheets grouped by presentation;
- all ten shipping-locale minimum-size sheets;
- a JSON manifest binding the exact source SHA, ordered inputs, input hashes,
  sheet hashes, and generation time;
- a review template listing every sheet.

The generated review remains incomplete until a human inspects every tile,
records findings, confirms recaptures, and signs the template.
