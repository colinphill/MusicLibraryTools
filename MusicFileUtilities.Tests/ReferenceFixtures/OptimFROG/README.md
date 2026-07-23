# OptimFROG reference fixtures

These streams were generated with the official OptimFROG 5.100 Windows x64
command-line tools from the [OptimFROG download page](https://losslessaudio.org/Downloads.php).
The encoder package was verified against its published SHA-1:
`ABF21227C5417C984EE51BA391E2BCB5546D033B`.

The source is a deterministic 0.3-second, 440 Hz, 44.1 kHz stereo tone:

- `sample.ofr`: signed 16-bit PCM encoded by `ofr.exe`
- `sample.ofs`: signed 16-bit PCM encoded by `ofs.exe`
- `sample.off`: IEEE float32 PCM encoded by `off.exe`

The fixture generator appends the same baseline APEv2 fields used by the other
format tests. All three resulting streams pass their native encoder's
`--verify` command.

The command-line encoders are deliberately not committed. To regenerate these
files after extracting OptimFROG under `.artifacts/tools/optimfrog`, run:

```powershell
powershell -ExecutionPolicy Bypass -NoProfile `
  -File MusicFileUtilities.Tests/generate-fixtures.ps1 `
  -OutDir MusicFileUtilities.Tests/bin/Release/net10.0/TestFiles `
  -RegenerateOptimFrog
```

The generated reference files are byte-deterministic. Their SHA-256 hashes are:

| File | SHA-256 |
|---|---|
| `sample.ofr` | `62A6064738AB82FDA594FF6245B0B17C5936A44AFDB34EE6EADA69F87F60B5B3` |
| `sample.ofs` | `3CF058926C5F0239171C0308D048267151E4E5DA7FE9A1EBFF31466270CC0D21` |
| `sample.off` | `677279FFB2403E451315BC5D3D0241D2A97D7E7085552CA203F6731CBDE71A35` |
