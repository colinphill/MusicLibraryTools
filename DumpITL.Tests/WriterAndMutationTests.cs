using System.Buffers.Binary;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class WriterAndMutationTests
{
    [Fact]
    public void BuildDoesNotMutateInputsAndPatchesOuterAndInnerAggregates()
    {
        ItlEnvelope envelope = SyntheticLibrary.CreateEnvelope();
        byte[] body = SyntheticLibrary.CreateBody();
        byte[] originalBody = (byte[])body.Clone();
        byte[] originalHeader = (byte[])envelope.RawHeader.Clone();

        byte[] file = ItlWriter.Build(envelope, body);

        Assert.Equal(originalBody, body);
        Assert.Equal(originalHeader, envelope.RawHeader);
        ItlEnvelope result = ItlEnvelope.Parse(file);
        Assert.Equal(7, result.SectionCount);
        Assert.Equal(1, result.TrackCount);
        Assert.Equal(1, result.PlaylistCount);
        Assert.Equal(1, result.AlbumCount);
        Assert.Equal(1, result.ArtistCount);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(result.Body.AsSpan(16 + 48)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.Body.AsSpan(16 + 68)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.Body.AsSpan(16 + 72)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.Body.AsSpan(16 + 76)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.Body.AsSpan(16 + 84)));
        Assert.NotEqual(0u, result.ModifiedDateSeconds);
        Assert.Equal(result.ModifiedDateSeconds,
            BinaryPrimitives.ReadUInt32LittleEndian(result.Body.AsSpan(16 + 112)));
    }

    [Fact]
    public void NoOpBuildPreservesModificationTimestamp()
    {
        ItlEnvelope envelope = ItlEnvelope.Parse(SyntheticLibrary.CreateFile());
        byte[] rebuilt = ItlWriter.Build(envelope, envelope.Body);
        ItlEnvelope result = ItlEnvelope.Parse(rebuilt);

        Assert.Equal(envelope.ModifiedDateSeconds, result.ModifiedDateSeconds);
    }

    [Fact]
    public void BuildRejectsPlaybackStateMutationWithoutAReproducibleToken()
    {
        ItlEnvelope envelope = ItlEnvelope.Parse(SyntheticLibrary.CreateFileWithPlaybackState());
        byte[] body = (byte[])envelope.Body.Clone();
        byte[] marker = "<string>4</string>"u8.ToArray();
        int markerOffset = body.AsSpan().IndexOf(marker);
        Assert.True(markerOffset >= 0);
        body[markerOffset + "<string>".Length] = (byte)'5';

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ItlWriter.Build(envelope, body));
        Assert.Contains("integrity token", exception.Message);
    }

    [Fact]
    public void SyntheticDocumentValidatesAndStructuralWritesPatchCounts()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);

        Assert.True(document.RemoveTrack(1));
        Assert.Empty(document.CloudTracks);
        Assert.Empty(document.Playlists.Single().Entries);
        string path = Path.Combine(Path.GetTempPath(), "dumpitl_gated_" + Guid.NewGuid().ToString("N") + ".itl");
        try
        {
            document.Save(path);
            ItlEnvelope written = ItlEnvelope.Load(path);
            Assert.Equal(0, written.TrackCount);
            Assert.DoesNotContain(ItlDocument.Parse(written).Validate(),
                issue => issue.Severity == ItlValidationSeverity.Error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidationChecksTheInnerEnvelopeMirror()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        byte[] mirror = document.Sections.Single(section => section.Type == 16).Raw!;
        BinaryPrimitives.WriteInt32LittleEndian(mirror.AsSpan(68), document.Envelope.TrackCount + 1);

        ItlValidationIssue issue = Assert.Single(document.Validate(), issue => issue.Code == "mfdh.track-count");
        Assert.Equal(ItlValidationSeverity.Error, issue.Severity);

        byte[] mhgh = document.Sections.Single(section => section.Type == 12).Raw!;
        BinaryPrimitives.WriteUInt32LittleEndian(mhgh.AsSpan(124), 1);
        Assert.Contains(document.Validate(), issue => issue.Code == "mhgh.playback-token");
    }

    [Fact]
    public void AddEditAndRemoveOperationsPreserveReferencesAndUniqueIds()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord template = document.Tracks.Single();
        ItlRecord added = document.AddTrack(template);
        Assert.Equal((uint)added.GetTrackId() + 1, ItlDocument.TrackSecondaryIdOf(added));
        document.SetTrackString(added, ItlDataType.Title, "New 東京 Track");
        Assert.NotNull(added.GetDateModified());
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(added.Field((int)ItlDataType.Title)!.Header.AsSpan(16)));
        document.SetTrackString(template, ItlDataType.Title, "A different title");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(template.Field((int)ItlDataType.Title)!.Header.AsSpan(16)));
        added.SetYear(2026);

        ItlRecord album = document.AddAlbum("New Album", "New Artist", document.Albums.Single());
        ItlRecord artist = document.AddArtist("New Artist", document.Artists.Single());
        added.SetAlbumId(ItlDocument.RecordIdOf(album));
        added.SetArtistId(ItlDocument.RecordIdOf(artist));

        ItlRecord originalPlaylist = document.Playlists.Single();
        ItlRecord playlist = document.AddPlaylist("Research", originalPlaylist);
        Assert.NotEqual(ItlDocument.PlaylistRecordIdOf(originalPlaylist), ItlDocument.PlaylistRecordIdOf(playlist));
        document.AddToPlaylist(playlist, added.GetTrackId());
        Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);

        Assert.True(document.RemoveTrack(added.GetTrackId()));
        Assert.Empty(playlist.Entries);
        Assert.True(document.RemoveAlbum("New Album"));
        Assert.True(document.RemoveArtist("New Artist"));
        Assert.True(document.RemovePlaylist("Research"));
        Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);
    }

    [Fact]
    public void ValidationRejectsAnInvalidSecondaryTrackId()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        BinaryPrimitives.WriteUInt32LittleEndian(
            document.Tracks.Single().Header.AsSpan(ItlDocument.TrackSecondaryIdOffset), 999);

        Assert.Contains(document.Validate(), issue => issue.Code == "ids.track-secondary");
    }
}
