# ITLTools

`ITLTools` is the reusable implementation behind the `DumpITL` command-line application. It parses,
validates, compares, edits, and rewrites Windows iTunes `.itl` libraries while retaining unmodeled
data byte-for-byte. Public APIs remain in the `iTunes.Binary` namespace for compatibility with the
original DumpITL implementation.

`ItlLibrary` is the read-only application model for tracks and playlists. It exposes local file
paths, fixed-width persistent-ID strings, the master playlist as display name `Library`, and the
source music folder decoded from the `mhgh` type-511 field. Applications should resolve their input
with `ItlFileEditor.ResolveLibraryPath()`, which uses `ITUNES_ITL` before the standard location.

```csharp
using iTunes.Binary;

ItlDocument document = ItlDocument.Load("iTunes Library.itl");
IReadOnlyList<ItlValidationIssue> issues = document.Validate();

ItlRecord track = document.Tracks.First();
document.SetTrackString(track, ItlDataType.Title, "Updated title");
document.Save("updated.itl");
```

Applications performing an in-place offline edit can use `ItlFileEditor`. It resolves the
`ITUNES_ITL` convention, refuses to write while iTunes is running, validates the document, uses the
writer's atomic replacement, and leaves the previous library as `.bak`:

```csharp
string path = ItlFileEditor.ResolveLibraryPath();
ItlDocument document = ItlDocument.Load(path);
// mutate document
ItlFileEditor.SaveValidated(document, path);
```

See the [format and research notes](../DumpITL/README.md) for confirmed field mappings, writer
policies, reverse-engineering commands, and the disposable native-iTunes acceptance workflow.
