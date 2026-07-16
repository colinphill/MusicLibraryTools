using System.Buffers.Binary;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class WriterAndMutationTests
{
    [Fact]
    public void ItlLocationHandlesFileUrisPlainPathsAndMissingValues()
    {
        const string fileUri = "file:///C:/Music/Album/Track%2001.mp3";
        Assert.Equal(new Uri(fileUri).LocalPath, ItlLocation.ToLocalPath(fileUri));
        Assert.Equal("relative/Track.mp3", ItlLocation.ToLocalPath("relative/Track.mp3"));
        Assert.Null(ItlLocation.ToLocalPath(null));
        Assert.Null(ItlLocation.ToLocalPath("   "));
    }

    [Fact]
    public void ReadOnlyLibraryExposesStableIdsAndMasterPlaylistDisplayName()
    {
        ItlLibrary library = ItlLibrary.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlTrack track = Assert.Single(library.Tracks);
        ItlPlaylist master = Assert.Single(library.Playlists);

        Assert.Equal("1111111111111111", track.PersistentIdString);
        Assert.Equal("Library", master.DisplayName);
        Assert.Same(master, library.FindPlaylist("library"));
    }

    [Fact]
    public void CanonicalMediaPathUsesItunesComponentRules()
    {
        string root = Path.Combine(Path.GetTempPath(), "iTunes Media");
        string path = ItlMediaOrganization.CanonicalMusicPath(root, "Various Artists", "Track Artist",
            "An Album: Deluxe", 4, "A/B? " + new string('x', 50), compilation: true);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "Music", "Compilations",
            "An Album_ Deluxe", "04 A_B_ " + new string('x', 28) + ".m4a"), path);
        Assert.Equal(ItlMediaOrganization.ComponentLengthLimit,
            Path.GetFileName(path).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanonicalMediaPathFallsBackToArtistWhenAlbumArtistIsMissing(string? albumArtist)
    {
        string root = Path.Combine(Path.GetTempPath(), "iTunes Media");
        string path = ItlMediaOrganization.CanonicalMusicPath(root, albumArtist, "Track Artist",
            "Album", 1, "Title", compilation: false);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "Music", "Track Artist",
            "Album", "01 Title.m4a"), path);
    }

    [Fact]
    public void CanonicalMediaPathOmitsPrefixWhenTrackNumberIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "iTunes Media");
        string path = ItlMediaOrganization.CanonicalMusicPath(root, "Artist", "Artist",
            "Album", (int?)null, "Title", compilation: false, extension: ".flac");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "Music", "Artist",
            "Album", "Title.flac"), path);
    }

    [Fact]
    public void AacImportReplacesTemplateMetadataAndLinksEntities()
    {
        string media = Path.Combine(Path.GetTempPath(), $"itl-import-{Guid.NewGuid():N}.m4a");
        File.WriteAllBytes(media, [1, 2, 3, 4]);
        try
        {
            ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
            ItlRecord template = document.Tracks.Single();
            document.SetTrackString(template, ItlDataType.Kind, "AAC audio file");
            document.SetTrackString(template, ItlDataType.Location, "template.m4a");
            document.SetTrackString(template, ItlDataType.FileUrl, "file://localhost/template.m4a");
            document.SetTrackString(template, ItlDataType.Comment, "must not leak");

            ItlRecord imported = document.ImportAacTrack(new ItlAacTrackImport
            {
                Path = media,
                Title = "Imported Song",
                Artist = "Track Artist",
                AlbumArtist = "Album Artist",
                Album = "Imported Album",
                Genre = "Pop",
                TrackNumber = 3,
                TrackCount = 12,
                Duration = TimeSpan.FromSeconds(205),
                BitRate = 256,
                ArtworkCount = 1,
                Compilation = true,
            });

            Assert.Equal("Imported Song", imported.GetString(ItlDataType.Title));
            Assert.Equal(Path.GetFullPath(media), imported.GetString(ItlDataType.Location));
            Assert.Null(imported.GetString(ItlDataType.Comment));
            Assert.Equal(3, imported.GetTrackNumber());
            Assert.Equal(12, imported.GetTrackCount());
            Assert.True(imported.GetCompilation());
            Assert.Contains(document.Albums, album => ItlDocument.RecordIdOf(album) == imported.GetAlbumId());
            Assert.Contains(document.Artists, artist => ItlDocument.RecordIdOf(artist) == imported.GetArtistId());
            Assert.Same(imported, document.ImportAacTrack(new ItlAacTrackImport
            {
                Path = media, Title = "Ignored", Artist = "Ignored", AlbumArtist = "Ignored",
                Album = "Ignored", TrackNumber = 1, TrackCount = 1, Duration = TimeSpan.Zero,
                BitRate = 1, ArtworkCount = 0,
            }));
            Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);
        }
        finally
        {
            File.Delete(media);
        }
    }

    [Fact]
    public void MediaRefreshAndRelocationPreserveTrackIdentityAndPlaylistMembership()
    {
        string oldPath = Path.Combine(Path.GetTempPath(), $"itl-old-{Guid.NewGuid():N}.mp3");
        string newPath = Path.Combine(Path.GetTempPath(), $"itl-new-{Guid.NewGuid():N}.mp3");
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord track = document.Tracks.Single();
        document.SetTrackString(track, ItlDataType.Location, oldPath);
        document.SetTrackString(track, ItlDataType.FileUrl, new Uri(oldPath).AbsoluteUri);
        int trackId = track.GetTrackId();
        ulong persistentId = track.GetPersistentId();
        uint playlistEntryId = document.Playlists.Single().Entries.Single().EntryId;

        IReadOnlyList<ItlRecord> relocated = document.RelocateTracks(oldPath, newPath);
        document.RefreshLocalTrack(track, newPath, new ItlLocalTrackMetadata
        {
            Title = "Changed title",
            Artist = "Track artist",
            AlbumArtist = "Album artist",
            Album = "Changed album",
            Genre = "Rock",
            TrackNumber = 4,
            TrackCount = 9,
            DiscNumber = 2,
            DiscCount = 3,
            Year = 2026,
            Bpm = 123,
            Duration = TimeSpan.FromSeconds(201),
            BitRateKbps = 320,
            ArtworkCount = 2,
            Compilation = true,
            Gapless = true,
        }, 123456, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        Assert.Single(relocated);
        Assert.Equal(trackId, track.GetTrackId());
        Assert.Equal(persistentId, track.GetPersistentId());
        Assert.Equal(newPath, track.GetString(ItlDataType.Location));
        Assert.Equal("Changed title", track.GetString(ItlDataType.Title));
        Assert.Equal(4, track.GetTrackNumber());
        Assert.Equal(9, track.GetTrackCount());
        Assert.Equal(2, track.GetDiscNumber());
        Assert.Equal(3, track.GetDiscCount());
        Assert.Equal(2026, track.GetYear());
        Assert.Equal(123, track.GetBpm());
        Assert.Equal(123456ul, track.GetSize());
        Assert.True(track.GetCompilation());
        Assert.True(track.GetPartOfGaplessAlbum());
        Assert.Equal(playlistEntryId, document.Playlists.Single().Entries.Single().EntryId);
        Assert.Contains(document.Albums,
            album => ItlDocument.RecordIdOf(album) == track.GetAlbumId());
        Assert.Contains(document.Artists,
            artist => ItlDocument.RecordIdOf(artist) == track.GetArtistId());
        Assert.DoesNotContain(document.Validate(),
            issue => issue.Severity == ItlValidationSeverity.Error);
    }

    [Fact]
    public void GenericLocalImportUsesSameExtensionTemplateAndBuiltInMemberships()
    {
        string templatePath = Path.Combine(Path.GetTempPath(), "template.mp3");
        string importedPath = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(importedPath, [1, 2, 3, 4]);
        try
        {
            ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
            ItlRecord template = document.Tracks.Single();
            document.SetTrackString(template, ItlDataType.Location, templatePath);
            document.SetTrackString(template, ItlDataType.FileUrl, new Uri(templatePath).AbsoluteUri);
            document.SetTrackString(template, ItlDataType.Kind, "MPEG audio file");
            document.SetTrackString(template, ItlDataType.Comment, "template-only");

            ItlRecord imported = document.ImportLocalTrack(importedPath, new ItlLocalTrackMetadata
            {
                Title = "Imported MP3",
                Artist = "Artist",
                AlbumArtist = "Artist",
                Album = "Album",
                Duration = TimeSpan.FromSeconds(100),
                BitRateKbps = 192,
                TrackNumber = 1,
                TrackCount = 1,
            }, new FileInfo(importedPath).Length, File.GetLastWriteTimeUtc(importedPath));

            Assert.NotSame(template, imported);
            Assert.Equal(importedPath, imported.GetString(ItlDataType.Location));
            Assert.Equal("Imported MP3", imported.GetString(ItlDataType.Title));
            Assert.Null(imported.GetString(ItlDataType.Comment));
            Assert.Contains(document.Playlists.Single().Entries,
                entry => entry.TrackId == imported.GetTrackId());
            Assert.DoesNotContain(document.Validate(),
                issue => issue.Severity == ItlValidationSeverity.Error);
        }
        finally
        {
            File.Delete(importedPath);
        }
    }

    [Fact]
    public void PlaybackStateOrdinaryKeyUsesStoreIdOrTitleArtistAlbumMd5()
    {
        Assert.Equal("123456789", ItlPlaybackStateKey.ForOrdinaryMetadata(123456789, "ignored"));
        Assert.Null(ItlPlaybackStateKey.ForOrdinaryMetadata(0, null, "Artist", "Album"));
        Assert.Null(ItlPlaybackStateKey.ForOrdinaryMetadata(0, "", "Artist", "Album"));
        Assert.Equal("b78a3223503896721cca1303f776159b",
            ItlPlaybackStateKey.ForOrdinaryMetadata(0, "Title"));
        Assert.Equal("5636957656239ac7c476da27398cbfc1",
            ItlPlaybackStateKey.ForOrdinaryMetadata(0, "Title", "Artist"));
        Assert.Equal("b3d33068e3b1ff5276cd357868a1921b",
            ItlPlaybackStateKey.ForOrdinaryMetadata(0, "Title", "Artist", "Album"));
    }

    [Fact]
    public void PlaybackStatePodcastKeyNormalizesFeedAndEpisodeUrls()
    {
        Assert.Null(ItlPlaybackStateKey.ForPodcastMetadata(null, "https://example.com/episode"));
        Assert.Null(ItlPlaybackStateKey.ForPodcastMetadata("https://example.com/feed", "   "));
        Assert.Equal("60142198bcee949a3cb0616478a6d1f4",
            ItlPlaybackStateKey.ForPodcastMetadata(
                "http://example.com//feed/  ",
                "https:///cdn.example.com///episode.mp3   "));
    }

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
        Assert.Equal(result.RawWord88, result.NextLibraryChildId);
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
    public void BuildRefreshesMlqhCrossSectionOffsetsWithoutMutatingInput()
    {
        byte[] body = SyntheticLibrary.CreateBody(includeMlqh: true);
        byte[] original = (byte[])body.Clone();

        ItlEnvelope result = ItlEnvelope.Parse(ItlWriter.Build(SyntheticLibrary.CreateEnvelope(), body));

        Assert.Equal(original, body);
        ItlChunk[] sections = [.. ItlChunk.Walk(result.Body, 0, result.Body.Length)];
        ItlChunk cloud = Assert.Single(sections, section => section.Type == 13);
        ItlChunk query = Assert.Single(sections, section => section.Type == 20);
        ItlChunk mlqh = ItlChunk.Read(result.Body, query.BodyOffset);
        Assert.Equal((ulong)cloud.Offset + 0x90,
            BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(mlqh.Offset + 20)));
        Assert.Equal((ulong)cloud.Offset + 0xF0,
            BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(mlqh.Offset + 28)));
        Assert.DoesNotContain(ItlDocument.Parse(result).Validate(), issue => issue.Code.StartsWith("mlqh."));
    }

    [Fact]
    public void MlqhAnchorsMayExtendPastAnEmptyType13Section()
    {
        ItlEnvelope source = ItlEnvelope.Parse(ItlWriter.Build(
            SyntheticLibrary.CreateEnvelope(),
            SyntheticLibrary.CreateBody(includeMlqh: true)));
        ItlDocument document = ItlDocument.Parse(source);
        Assert.True(document.RemoveTrack(1));

        ItlEnvelope result = ItlEnvelope.Parse(ItlWriter.Build(source, document.Serialize()));

        ItlChunk[] sections = [.. ItlChunk.Walk(result.Body, 0, result.Body.Length)];
        ItlChunk cloud = Assert.Single(sections, section => section.Type == 13);
        Assert.True(cloud.TotalLength < 0xF0);
        ItlChunk query = Assert.Single(sections, section => section.Type == 20);
        ItlChunk mlqh = ItlChunk.Read(result.Body, query.BodyOffset);
        Assert.Equal((ulong)cloud.Offset + 0xF0,
            BinaryPrimitives.ReadUInt64LittleEndian(result.Body.AsSpan(mlqh.Offset + 28)));
    }

    [Fact]
    public void WriterRejectsDanglingMiqhCurrentLibraryTrackReference()
    {
        ItlEnvelope source = ItlEnvelope.Parse(ItlWriter.Build(
            SyntheticLibrary.CreateEnvelope(),
            SyntheticLibrary.CreateBody(includeMiqhReference: true)));
        ItlDocument document = ItlDocument.Parse(source);
        Assert.DoesNotContain(document.Validate(), issue => issue.Code.StartsWith("miqh."));

        Assert.True(document.RemoveTrack(1));
        Assert.Contains(document.Validate(), issue => issue.Code == "miqh.source-track-link");
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ItlWriter.Build(source, document.Serialize()));
        Assert.Contains("Type-20 miqh", exception.Message);
        Assert.Contains("native removal policy is unproven", exception.Message);
    }

    [Fact]
    public void BuildRejectsPlaybackStateMutationWithoutProvenNativeSemantics()
    {
        ItlEnvelope envelope = ItlEnvelope.Parse(SyntheticLibrary.CreateFileWithPlaybackState());
        ItlDocument document = ItlDocument.Parse(envelope);
        Assert.Equal(0x0944ACB6u, envelope.PlaybackStateDsid);
        Assert.Equal(0x0944ACB6ul, document.PlaybackStateDsid);
        Assert.Equal(0x0944ACB6ul, document.CachedAccountDsid);

        byte[] body = (byte[])envelope.Body.Clone();
        byte[] marker = "<string>4</string>"u8.ToArray();
        int markerOffset = body.AsSpan().IndexOf(marker);
        Assert.True(markerOffset >= 0);
        body[markerOffset + "<string>".Length] = (byte)'5';

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ItlWriter.Build(envelope, body));
        Assert.Contains("key-identity semantics", exception.Message);
    }

    [Fact]
    public void WriterRejectsDanglingMprhPlaylistEntryReference()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFileWithMprh()));
        Assert.DoesNotContain(document.Validate(), issue => issue.Code.StartsWith("mprh."));

        Assert.True(document.RemoveTrack(1));
        Assert.Contains(document.Validate(), issue => issue.Code == "mprh.entry-link");
        string path = Path.Combine(Path.GetTempPath(), $"dumpitl-mprh-{Guid.NewGuid():N}.itl");
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => document.Save(path));
            Assert.Contains("Type-15 mprh", exception.Message);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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
        Assert.Contains(document.Validate(), issue => issue.Code == "mhgh.playback-dsid");
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
    public void PlaylistReplacementResolvesTracksBeforeMutatingAndPreservesDuplicates()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord first = document.Tracks.Single();
        ItlRecord second = document.AddTrack(first);
        ItlRecord playlist = document.AddPlaylist("Offline editor", document.Playlists.Single());
        ulong secondPersistentId = ItlDocument.TrackPersistentIdOf(second);

        Assert.Same(second, document.FindTrackByPersistentId(secondPersistentId));
        document.ReplacePlaylistEntries(playlist,
            [second.GetTrackId(), first.GetTrackId(), second.GetTrackId()]);
        Assert.Equal([second.GetTrackId(), first.GetTrackId(), second.GetTrackId()],
            playlist.Entries.Select(entry => entry.TrackId));
        Assert.Equal(3, playlist.Entries.Select(entry => entry.EntryId).Distinct().Count());

        int[] before = [.. playlist.Entries.Select(entry => entry.TrackId)];
        Assert.Throws<ArgumentException>(() => document.ReplacePlaylistEntries(playlist, [int.MaxValue]));
        Assert.Equal(before, playlist.Entries.Select(entry => entry.TrackId));
    }

    [Fact]
    public void PlaylistRedirectPreservesEntryIdentityAndOrderDuringTrackConsolidation()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord duplicate = document.Tracks.Single();
        ItlRecord retained = document.AddTrack(duplicate);
        ItlRecord playlist = document.AddPlaylist("Duplicate membership", document.Playlists.Single());
        document.ReplacePlaylistEntries(playlist,
            [duplicate.GetTrackId(), retained.GetTrackId(), duplicate.GetTrackId()]);
        uint[] entryIds = [.. playlist.Entries.Select(entry => entry.EntryId)];
        uint[] orderKeys = [.. playlist.Entries.Select(entry => entry.OrderKey)];

        Assert.Equal(2, document.RedirectPlaylistEntries(
            playlist, duplicate.GetTrackId(), retained.GetTrackId()));
        Assert.All(playlist.Entries, entry => Assert.Equal(retained.GetTrackId(), entry.TrackId));
        Assert.Equal(entryIds, playlist.Entries.Select(entry => entry.EntryId));
        Assert.Equal(orderKeys, playlist.Entries.Select(entry => entry.OrderKey));
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
