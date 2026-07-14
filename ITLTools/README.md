# ITLTools

`ITLTools` is the reusable implementation behind the `DumpITL` command-line application. It parses,
validates, compares, edits, and rewrites Windows iTunes `.itl` libraries while retaining unmodeled
data byte-for-byte. Public APIs remain in the `iTunes.Binary` namespace for compatibility with the
original DumpITL implementation.

```csharp
using iTunes.Binary;

ItlDocument document = ItlDocument.Load("iTunes Library.itl");
IReadOnlyList<ItlValidationIssue> issues = document.Validate();

ItlRecord track = document.Tracks.First();
document.SetTrackString(track, ItlDataType.Title, "Updated title");
document.Save("updated.itl");
```

See the [format and research notes](../DumpITL/README.md) for confirmed field mappings, writer
policies, reverse-engineering commands, and the disposable native-iTunes acceptance workflow.
