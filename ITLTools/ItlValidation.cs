using System.Buffers.Binary;

namespace iTunes.Binary;

public enum ItlValidationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ItlValidationIssue(string Code, ItlValidationSeverity Severity, string Message);

public sealed class ItlWriteOptions
{
    /// <summary>
    /// Retained for source compatibility. Native experiments proved +88/+108 are not structural
    /// aggregates, so validated count-changing writes no longer require an override.
    /// </summary>
    public bool AllowUnverifiedStructuralChanges { get; init; }
}

public sealed partial class ItlDocument
{
    public IReadOnlyList<ItlValidationIssue> Validate()
    {
        var issues = new List<ItlValidationIssue>();
        void Add(string code, ItlValidationSeverity severity, string message) =>
            issues.Add(new ItlValidationIssue(code, severity, message));

        if (Envelope.SectionCount != Sections.Count)
            Add("envelope.section-count", ItlValidationSeverity.Error,
                $"Envelope declares {Envelope.SectionCount} sections; document contains {Sections.Count}.");

        ValidateEnvelopeMirror();
        ValidatePlaybackDsidMirror();
        ValidateSharedStringKeys();
        ValidateSmartPlaylists();
        ValidateMprhReferences();
        ValidateMlqhAnchors();
        ValidateStshGlobalState();
        ValidateSpecialPlaylistPartition();
        ValidatePodcastStations();

        AggregateCounts current = CurrentCounts;
        if (current == _originalCounts)
        {
            CompareCount("track", Envelope.TrackCount, current.Tracks);
            CompareCount("playlist", Envelope.PlaylistCount, current.Playlists);
            CompareCount("album", Envelope.AlbumCount, current.Albums);
            CompareCount("artist", Envelope.ArtistCount, current.Artists);
        }
        else
        {
            Add("envelope.aggregate-pending", ItlValidationSeverity.Info,
                "Record counts changed; known envelope and mfdh aggregate words will be patched during writing.");
        }

        uint[] trackIds = [.. Tracks.Select(t => (uint)TrackIdOf(t))];
        uint[] trackSecondaryIds = [.. Tracks.Select(TrackSecondaryIdOf)];
        uint[] albumIds = [.. Albums.Select(RecordIdOf)];
        uint[] artistIds = [.. Artists.Select(RecordIdOf)];
        uint[] playlistIds = [.. Playlists.Select(PlaylistRecordIdOf)];
        uint[] entryIds = [.. Playlists.SelectMany(p => p.Entries).Select(e => e.EntryId)];
        uint[] globalIds = [.. trackIds, .. trackSecondaryIds, .. albumIds, .. artistIds, .. playlistIds, .. entryIds];

        uint[] duplicateIds = [.. globalIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)];
        if (duplicateIds.Length > 0)
            Add("ids.duplicate", ItlValidationSeverity.Error,
                $"{duplicateIds.Length} identifiers collide across tracks, albums, artists, playlists and playlist entries.");

        int badTrackSecondaryIds = Tracks.Count(track => TrackSecondaryIdOf(track) != (uint)TrackIdOf(track) + 1);
        if (badTrackSecondaryIds > 0)
            Add("ids.track-secondary", ItlValidationSeverity.Error,
                $"{badTrackSecondaryIds} tracks do not carry track ID + 1 at mith +500.");

        int badStoreItemMirrors = Tracks.Count(track => track.GetStoreItemId() != track.GetStoreItemIdMirror());
        if (badStoreItemMirrors > 0)
            Add("ids.store-item-mirror", ItlValidationSeverity.Error,
                $"{badStoreItemMirrors} tracks have different Store Item IDs at mith +168 and +428.");

        ulong[] persistentIds =
        [
            .. Tracks.Select(t => BinaryPrimitives.ReadUInt64LittleEndian(t.Header.AsSpan(128))),
            .. Albums.Select(a => BinaryPrimitives.ReadUInt64LittleEndian(a.Header.AsSpan(20))),
            .. Artists.Select(a => BinaryPrimitives.ReadUInt64LittleEndian(a.Header.AsSpan(20))),
            .. Playlists.Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.Header.AsSpan(PlaylistPersistentIdOffset))),
            .. Playlists.SelectMany(p => p.Entries).Select(e => e.PersistentId),
        ];
        ulong[] duplicatePersistentIds = [.. persistentIds.Where(id => id != 0).GroupBy(id => id)
            .Where(group => group.Count() > 1).Select(group => group.Key)];
        if (duplicatePersistentIds.Length > 0)
            Add("ids.persistent-duplicate", ItlValidationSeverity.Error,
                $"{duplicatePersistentIds.Length} persistent identifiers are duplicated across modeled records.");

        var trackSet = trackIds.ToHashSet();
        var albumSet = albumIds.ToHashSet();
        var artistSet = artistIds.ToHashSet();

        int badAlbumLinks = Tracks.Count(t => t.GetAlbumId() != 0 && !albumSet.Contains(t.GetAlbumId()));
        int badArtistLinks = Tracks.Count(t => t.GetArtistId() != 0 && !artistSet.Contains(t.GetArtistId()));
        int badPlaylistLinks = Playlists.SelectMany(p => p.Entries).Count(e => !trackSet.Contains((uint)e.TrackId));
        int badCloudLinks = CloudTracks.Count(t => !trackSet.Contains((uint)TrackIdOf(t)));

        if (badAlbumLinks > 0) Add("links.album", ItlValidationSeverity.Error, $"{badAlbumLinks} track album links do not resolve.");
        if (badArtistLinks > 0) Add("links.artist", ItlValidationSeverity.Error, $"{badArtistLinks} track artist links do not resolve.");
        if (badPlaylistLinks > 0) Add("links.playlist", ItlValidationSeverity.Error, $"{badPlaylistLinks} playlist entries reference missing tracks.");
        if (badCloudLinks > 0) Add("links.cloud", ItlValidationSeverity.Error, $"{badCloudLinks} cloud records reference missing main tracks.");

        var albumsById = Albums.ToDictionary(RecordIdOf);
        var artistsById = Artists.ToDictionary(RecordIdOf);
        int[] albumTextMismatches = [.. Tracks.Where(track =>
        {
            string? trackText = track.GetString(ItlDataType.Album);
            string? entityText = albumsById.GetValueOrDefault(track.GetAlbumId())?
                .Field((int)ItlDataType.AlbumRecordName)?.Text;
            return trackText is not null && entityText is not null && trackText != entityText;
        }).Select(TrackIdOf)];
        int[] artistTextMismatches = [.. Tracks.Where(track =>
        {
            string? trackText = track.GetString(ItlDataType.AlbumArtist);
            if (trackText is null)
                return false;
            ItlRecord? album = albumsById.GetValueOrDefault(track.GetAlbumId());
            ItlRecord? artist = artistsById.GetValueOrDefault(track.GetArtistId());
            string? albumText = album?.Field((int)ItlDataType.AlbumRecordArtist)?.Text;
            string? artistText = artist?.Field((int)ItlDataType.ArtistRecordName)?.Text;
            return albumText is not null && trackText != albumText ||
                   artistText is not null && trackText != artistText;
        }).Select(TrackIdOf)];
        if (albumTextMismatches.Length > 0)
            Add("metadata.album-link-text", ItlValidationSeverity.Warning,
                $"{albumTextMismatches.Length} tracks have album text that disagrees with their linked album record " +
                $"(track IDs {string.Join(", ", albumTextMismatches.Take(8))}).");
        if (artistTextMismatches.Length > 0)
            Add("metadata.artist-link-text", ItlValidationSeverity.Warning,
                $"{artistTextMismatches.Length} tracks have album-artist text that disagrees with linked album or artist records " +
                $"(track IDs {string.Join(", ", artistTextMismatches.Take(8))}).");

        ItlRecord? master = Playlists.FirstOrDefault(IsMasterPlaylist);
        if (master is null)
        {
            Add("playlist.master-missing", ItlValidationSeverity.Error, "The master library playlist is missing.");
        }
        else
        {
            int[] masterIds = [.. master.Entries.Select(e => e.TrackId).Order()];
            int[] expected = [.. Tracks.Select(TrackIdOf).Order()];
            if (!masterIds.SequenceEqual(expected))
                Add("playlist.master-members", ItlValidationSeverity.Error,
                    $"Master playlist has {masterIds.Length} entries for {expected.Length} tracks and the memberships differ.");
        }

        return issues;

        void ValidateSharedStringKeys()
        {
            CheckDomain("album", Albums.SelectMany(record => record.Fields.Where(field =>
                    field.Type == (int)ItlDataType.AlbumRecordName))
                .Concat(Tracks.SelectMany(record => record.Fields.Where(field =>
                    field.Type == (int)ItlDataType.Album))));
            CheckDomain("artist", Artists.SelectMany(record => record.Fields.Where(field =>
                    field.Type == (int)ItlDataType.ArtistRecordName))
                .Concat(Albums.SelectMany(record => record.Fields.Where(field =>
                    field.Type == (int)ItlDataType.AlbumRecordArtist ||
                    field.Type == (int)ItlDataType.AlbumRecordSortArtist)))
                .Concat(Tracks.SelectMany(record => record.Fields.Where(field =>
                    field.Type == (int)ItlDataType.Artist ||
                    field.Type == (int)ItlDataType.AlbumArtist))));

            void CheckDomain(string name, IEnumerable<ItlField> fields)
            {
                var collisions = fields.GroupBy(field =>
                        BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16)))
                    .Select(group => new
                    {
                        group.Key,
                        Values = group.Select(field => field.Text).OfType<string>()
                            .Distinct(StringComparer.Ordinal).ToArray(),
                    })
                    .Where(item => item.Values.Length > 1)
                    .ToArray();
                if (collisions.Length == 0)
                    return;
                string examples = string.Join("; ", collisions.Take(3).Select(collision =>
                    $"key {collision.Key} names {string.Join(" / ", collision.Values.Take(3).Select(value => $"'{value}'"))}"));
                Add($"metadata.{name}-key-collision", ItlValidationSeverity.Error,
                    $"{collisions.Length} shared {name} string keys identify different text values ({examples}).");
            }
        }

        void ValidateEnvelopeMirror()
        {
            ItlSectionNode? section = Sections.FirstOrDefault(s => s.Type == EnvelopeCopySectionType);
            byte[]? mirror = section?.Raw;
            if (mirror is null)
            {
                Add("mfdh.missing", ItlValidationSeverity.Error, "Envelope mirror section 16 is missing or unexpectedly structured.");
                return;
            }
            if (mirror.Length < 88 || System.Text.Encoding.ASCII.GetString(mirror, 0, 4) != "mfdh")
            {
                Add("mfdh.malformed", ItlValidationSeverity.Error, "Section 16 does not contain a complete mfdh envelope mirror.");
                return;
            }

            CompareMirror("section", 48, Envelope.SectionCount);
            CompareMirror("track", 68, Envelope.TrackCount);
            CompareMirror("playlist", 72, Envelope.PlaylistCount);
            CompareMirror("album", 76, Envelope.AlbumCount);
            CompareMirror("artist", 84, Envelope.ArtistCount);
            if (mirror.Length >= 116)
            {
                CompareMirrorWord("word88", 88, Envelope.RawWord88);
                CompareMirrorWord("modified-date", 112, Envelope.ModifiedDateSeconds);
            }

            int mirroredLength = BinaryPrimitives.ReadInt32LittleEndian(mirror.AsSpan(8));
            int expectedLength = Envelope.RawHeader.Length + Envelope.Body.Length;
            if (mirroredLength != expectedLength)
                Add("mfdh.total-length", ItlValidationSeverity.Error,
                    $"mfdh total length is {mirroredLength}; decoded envelope and body span {expectedLength} bytes.");

            void CompareMirror(string name, int offset, int outerValue)
            {
                int innerValue = BinaryPrimitives.ReadInt32LittleEndian(mirror.AsSpan(offset));
                if (innerValue != outerValue)
                    Add($"mfdh.{name}-count", ItlValidationSeverity.Error,
                        $"mfdh {name} count is {innerValue}; outer envelope declares {outerValue}.");
            }

            void CompareMirrorWord(string name, int offset, uint outerValue)
            {
                uint innerValue = BinaryPrimitives.ReadUInt32LittleEndian(mirror.AsSpan(offset));
                if (innerValue != outerValue)
                    Add($"mfdh.{name}", ItlValidationSeverity.Error,
                        $"mfdh {name} is 0x{innerValue:X8}; outer envelope declares 0x{outerValue:X8}.");
            }
        }

        void ValidatePlaybackDsidMirror()
        {
            ItlSectionNode? section = Sections.FirstOrDefault(candidate => candidate.Type == 12);
            byte[]? mhgh = section?.Raw;
            if (mhgh is null || mhgh.Length < 128 || System.Text.Encoding.ASCII.GetString(mhgh, 0, 4) != "mhgh")
                return;

            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(mhgh.AsSpan(4));
            if (headerLength < 128)
                return;

            uint innerValue = BinaryPrimitives.ReadUInt32LittleEndian(mhgh.AsSpan(124));
            if (innerValue != Envelope.RawWord108)
                Add("mhgh.playback-dsid", ItlValidationSeverity.Error,
                    $"mhgh playback-state DSID is {innerValue}; outer envelope declares {Envelope.PlaybackStateDsid}.");
        }

        void ValidateSmartPlaylists()
        {
            HashSet<ulong> playlistPersistentIds = Playlists.Select(playlist =>
                BinaryPrimitives.ReadUInt64LittleEndian(playlist.Header.AsSpan(PlaylistPersistentIdOffset))).ToHashSet();
            foreach (ItlRecord playlist in Playlists)
            {
                ItlField? info = playlist.Field((int)ItlDataType.SmartInfo);
                ItlField? criteria = playlist.Field((int)ItlDataType.SmartCriteria);
                string name = PlaylistNameOf(playlist) ?? "(unnamed)";
                if ((info is null) != (criteria is null))
                {
                    Add("smart.missing-pair", ItlValidationSeverity.Error,
                        $"Playlist '{name}' has only one of Smart Info and Smart Criteria.");
                    continue;
                }
                if (info is null) continue;
                try
                {
                    ItlSmartPlaylist smart = ItlSmartPlaylist.Parse(info.Payload, criteria!.Payload);
                    foreach (ItlSmartRule rule in Flatten(smart.Criteria))
                    {
                        if (rule.ValueKind == ItlSmartValueKind.Playlist &&
                            rule.PlaylistPersistentId is { } persistentId &&
                            !playlistPersistentIds.Contains(persistentId))
                            Add("smart.playlist-link", ItlValidationSeverity.Error,
                                $"Playlist '{name}' has a smart rule referencing missing playlist {persistentId:X16}.");
                    }
                }
                catch (InvalidDataException exception)
                {
                    Add("smart.malformed", ItlValidationSeverity.Error,
                        $"Playlist '{name}' has malformed smart-playlist data: {exception.Message}");
                }
            }

            static IEnumerable<ItlSmartRule> Flatten(ItlSmartCriteria criteria) =>
                criteria.Rules.SelectMany(rule => rule.NestedCriteria is null
                    ? [rule]
                    : new[] { rule }.Concat(Flatten(rule.NestedCriteria)));
        }

        void ValidateMprhReferences()
        {
            ItlSectionNode? section = Sections.FirstOrDefault(candidate => candidate.Type == 15);
            if (section is null) return;
            byte[]? raw = section.Raw;
            if (raw is null)
            {
                Add("mprh.layout", ItlValidationSeverity.Error,
                    "Type-15 section is unexpectedly modeled instead of preserving its mlrh payload.");
                return;
            }

            try
            {
                ItlChunk list = ItlChunk.Read(raw, 0);
                if (list.Signature != "mlrh")
                {
                    Add("mprh.layout", ItlValidationSeverity.Warning,
                        $"Type-15 section has unrecognized inner layout '{list.Signature}'.");
                    return;
                }

                var playlistsByPersistentId = new Dictionary<ulong, ItlRecord>();
                foreach (ItlRecord playlist in Playlists)
                {
                    ulong persistentId = BinaryPrimitives.ReadUInt64LittleEndian(
                        playlist.Header.AsSpan(PlaylistPersistentIdOffset));
                    playlistsByPersistentId.TryAdd(persistentId, playlist);
                }
                IReadOnlyList<ItlFixedItem> records = ItlTraversal.WalkFixedItems(raw, list, raw.Length);
                foreach ((ItlFixedItem record, int index) in records.Select((record, index) => (record, index)))
                {
                    ulong playlistPersistentId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(record.Offset + 16));
                    uint entryId = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(record.Offset + 12));
                    if (!playlistsByPersistentId.TryGetValue(playlistPersistentId, out ItlRecord? playlist))
                    {
                        Add("mprh.playlist-link", ItlValidationSeverity.Error,
                            $"Type-15 record {index} references missing playlist {playlistPersistentId:X16}.");
                    }
                    else if (!playlist.Entries.Any(entry => entry.EntryId == entryId))
                    {
                        Add("mprh.entry-link", ItlValidationSeverity.Error,
                            $"Type-15 record {index} references missing entry {entryId} in playlist " +
                            $"'{PlaylistNameOf(playlist)}'.");
                    }
                }
            }
            catch (InvalidDataException exception)
            {
                Add("mprh.layout", ItlValidationSeverity.Error,
                    $"Type-15 section is malformed: {exception.Message}");
            }
        }

        void ValidateMlqhAnchors()
        {
            int offset = 0;
            int? cloudSectionOffset = null;
            ItlSectionNode? querySection = null;
            foreach (ItlSectionNode section in Sections)
            {
                if (section.Type == 13)
                    cloudSectionOffset = offset;
                else if (section.Type == 20)
                    querySection = section;
                offset += section.Length;
            }

            if (querySection is null)
                return;
            if (cloudSectionOffset is null)
            {
                Add("mlqh.anchor-section", ItlValidationSeverity.Error,
                    "Type-20 mlqh media references are present without the type-13 anchor section.");
                return;
            }

            byte[]? raw = querySection.Raw;
            if (raw is null || raw.Length < 36 || !raw.AsSpan(0, 4).SequenceEqual("mlqh"u8) ||
                BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4)) < 36)
            {
                Add("mlqh.layout", ItlValidationSeverity.Error,
                    "Type-20 section does not contain a complete opaque mlqh header.");
                return;
            }

            CheckAnchor(20, checked((ulong)cloudSectionOffset.Value + 0x90));
            CheckAnchor(28, checked((ulong)cloudSectionOffset.Value + 0xF0));

            try
            {
                int headerLength = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
                ItlChunk[] children = [.. ItlChunk.Walk(raw, headerLength, raw.Length)];
                ItlChunk[] metadata = [.. children.Where(child => child.Signature == "mhoh")];
                ItlChunk[] references = [.. children.Where(child => child.Signature == "miqh")];
                if (metadata.Length + references.Length != children.Length)
                    Add("mlqh.child-layout", ItlValidationSeverity.Error,
                        "mlqh contains an unsupported child record type.");
                int declaredMetadataCount = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12));
                int declaredReferenceCount = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(16));
                if (declaredMetadataCount != metadata.Length)
                    Add("mlqh.metadata-count", ItlValidationSeverity.Error,
                        $"mlqh declares {declaredMetadataCount} metadata fields but contains {metadata.Length} mhoh records.");
                if (declaredReferenceCount != references.Length)
                    Add("mlqh.record-count", ItlValidationSeverity.Error,
                        $"mlqh declares {declaredReferenceCount} media references but contains {references.Length} miqh records.");

                HashSet<ulong> trackPersistentIds = Tracks.Select(track =>
                    BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(128))).ToHashSet();
                foreach ((ItlChunk reference, int index) in references.Select((reference, index) => (reference, index)))
                {
                    if (reference.Signature != "miqh" || reference.HeaderLength < 140)
                    {
                        Add("miqh.layout", ItlValidationSeverity.Error,
                            $"Type-20 media-reference record {index} has an unsupported layout.");
                        continue;
                    }
                    CheckTrackLink(reference, index, 28, 36, "source");
                    CheckTrackLink(reference, index, 124, 132, "mapped");
                }

                void CheckTrackLink(ItlChunk reference, int index, int libraryOffset, int trackOffset, string role)
                {
                    ulong owner = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(reference.Offset + libraryOffset));
                    ulong track = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(reference.Offset + trackOffset));
                    if (owner == Envelope.LibraryPersistentId && track != 0 && !trackPersistentIds.Contains(track))
                        Add($"miqh.{role}-track-link", ItlValidationSeverity.Error,
                            $"Type-20 record {index} references missing current-library track {track:X16} as its {role}.");
                }
            }
            catch (InvalidDataException exception)
            {
                Add("miqh.layout", ItlValidationSeverity.Error,
                    $"Type-20 media-reference list is malformed: {exception.Message}");
            }

            void CheckAnchor(int fieldOffset, ulong expected)
            {
                ulong actual = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(fieldOffset));
                if (actual != expected)
                    Add($"mlqh.anchor-{fieldOffset}", ItlValidationSeverity.Error,
                        $"mlqh +{fieldOffset} is decoded-body offset 0x{actual:X}; expected 0x{expected:X}.");
            }
        }

        void ValidateStshGlobalState()
        {
            foreach (ItlSectionNode section in Sections.Where(candidate => candidate.Type == 23))
            {
                byte[]? raw = section.Raw;
                if (raw is null)
                {
                    Add("stsh.layout", ItlValidationSeverity.Error,
                        "Type-23 section is unexpectedly modeled instead of preserving its stsh payload.");
                    continue;
                }

                try
                {
                    ItlChunk stsh = ItlChunk.Read(raw, 0);
                    if (stsh.Signature != "stsh")
                    {
                        Add("stsh.layout", ItlValidationSeverity.Warning,
                            $"Type-23 section has unrecognized inner layout '{stsh.Signature}'.");
                        continue;
                    }

                    _ = ItlTraversal.WalkStshDataObjects(raw, stsh, raw.Length);
                }
                catch (InvalidDataException exception)
                {
                    Add("stsh.layout", ItlValidationSeverity.Error,
                        $"Type-23 global-state container is malformed: {exception.Message}");
                }
            }
        }

        void ValidateSpecialPlaylistPartition()
        {
            foreach (ItlSectionNode section in Sections.Where(candidate => candidate.Type == 14))
            {
                byte[]? raw = section.Raw;
                if (raw is null)
                {
                    Add("mlph14.layout", ItlValidationSeverity.Error,
                        "Type-14 section is unexpectedly modeled instead of preserving its special playlist partition.");
                    continue;
                }

                try
                {
                    ItlChunk mlph = ItlChunk.Read(raw, 0);
                    if (mlph.Signature != "mlph")
                    {
                        Add("mlph14.layout", ItlValidationSeverity.Warning,
                            $"Type-14 section has unrecognized inner layout '{mlph.Signature}'.");
                        continue;
                    }
                    _ = ItlTraversal.WalkMlphRecords(raw, mlph, raw.Length);
                }
                catch (InvalidDataException exception)
                {
                    Add("mlph14.layout", ItlValidationSeverity.Error,
                        $"Type-14 special playlist partition is malformed: {exception.Message}");
                }
            }
        }

        void ValidatePodcastStations()
        {
            foreach (ItlSectionNode section in Sections.Where(candidate => candidate.Type == 21))
            {
                byte[]? raw = section.Raw;
                if (raw is null)
                {
                    Add("mlsh.layout", ItlValidationSeverity.Error,
                        "Type-21 section is unexpectedly modeled instead of preserving its podcast stations.");
                    continue;
                }

                try
                {
                    ItlChunk mlsh = ItlChunk.Read(raw, 0);
                    if (mlsh.Signature != "mlsh")
                    {
                        Add("mlsh.layout", ItlValidationSeverity.Warning,
                            $"Type-21 section has unrecognized inner layout '{mlsh.Signature}'.");
                        continue;
                    }
                    _ = ItlTraversal.WalkPodcastStations(raw, mlsh, raw.Length);
                }
                catch (InvalidDataException exception)
                {
                    Add("mlsh.layout", ItlValidationSeverity.Error,
                        $"Type-21 podcast-station collection is malformed: {exception.Message}");
                }
            }
        }

        void CompareCount(string name, int envelopeValue, int actual)
        {
            if (envelopeValue != actual)
                Add($"envelope.{name}-count", ItlValidationSeverity.Error,
                    $"Envelope {name} count is {envelopeValue}; document contains {actual}.");
        }
    }
}
