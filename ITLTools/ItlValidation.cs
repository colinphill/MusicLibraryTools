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
        ValidateSmartPlaylists();
        ValidateMprhReferences();

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

        void CompareCount(string name, int envelopeValue, int actual)
        {
            if (envelopeValue != actual)
                Add($"envelope.{name}-count", ItlValidationSeverity.Error,
                    $"Envelope {name} count is {envelopeValue}; document contains {actual}.");
        }
    }
}
