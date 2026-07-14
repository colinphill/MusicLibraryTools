using iTunes.Binary;
using System.Text.Json;
using Xunit;

namespace DumpITL.Tests;

public sealed class ResearchSnapshotTests
{
    [Fact]
    public void CapturesDeterministicResearchState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dumpitl-snapshot-{Guid.NewGuid():N}.itl");
        try
        {
            File.WriteAllBytes(path, SyntheticLibrary.CreateFile());

            ItlResearchSnapshot first = ItlResearchSnapshot.Capture(path);
            ItlResearchSnapshot second = ItlResearchSnapshot.Capture(path);

            Assert.Equal(ItlResearchSnapshot.CurrentSchemaVersion, first.SchemaVersion);
            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            Assert.Equal(7, first.ParsedCounts.Sections);
            Assert.Equal(1, first.ParsedCounts.Tracks);
            Assert.Equal(1, first.ParsedCounts.Playlists);
            Assert.Equal(1, first.ParsedCounts.PlaylistEntries);
            Assert.Equal([1u], first.Identifiers.TrackIds);
            Assert.Equal([2u], first.Identifiers.TrackSecondaryIds);
            Assert.Equal([6u], first.Identifiers.PlaylistIds);
            Assert.Equal(0u, ItlDocument.Load(path).Tracks.Single().GetStoreItemId());
            Assert.NotNull(first.EnvelopeMirror);
            Assert.Equal(1u, first.EnvelopeMirror.TrackCount);
            Assert.NotNull(first.Mhgh);
            Assert.False(first.Mhgh.HasPlaybackState);
            Assert.DoesNotContain(first.Diagnostics, issue => issue.Severity == ItlValidationSeverity.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
