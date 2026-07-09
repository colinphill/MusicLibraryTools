using System.Buffers.Binary;
using System.Xml.Linq;
using iTunes.Binary;

if (args.Length < 2)
{
    Console.Error.WriteLine("""
        Usage: DumpITL <command> <iTunes Library.itl> [args]

        Read
          info                              envelope + section table
          tracks [count]                    list tracks (default 20)
          layout                            child structure of every section
          mhoh [trackIndex]                 decoded data objects of one track
          probe <trackId>                   raw string bytes of one track
          mfdh                              the internal copy of the envelope
          mtph                              raw playlist track entries
          counts                            which header words cache child counts
          cloud <trackId>                   the parallel cloud track list

        Check
          verify <Library.xml>              cross-check tracks and playlists
          discover <Library.xml> [sample]   locate numeric fields by correlation
          plprobe <Library.xml>             playlist entry and persistent-id layout
          identity                          parse and re-serialize; must be byte-identical

        Write (prototype -- always work on a copy, with iTunes closed)
          roundtrip <out.itl>               re-encode unchanged, prove the body survives
          set <trackId> <field> <value> <out.itl>
          demo <out.itl>                    exercise every add/remove/edit operation
        """);
    return 1;
}

string command = args[0];
string itl = args[1];

switch (command)
{
    case "info":
        Info(ItlLibrary.Load(itl));
        break;

    case "tracks":
        Tracks(ItlLibrary.Load(itl), args.Length > 2 ? int.Parse(args[2]) : 20);
        break;

    case "mhoh":
        Mhoh(ItlLibrary.Load(itl), args.Length > 2 ? int.Parse(args[2]) : 0);
        break;

    case "probe":
        Probe(ItlLibrary.Load(itl), int.Parse(args[2]));
        break;

    case "discover":
        FieldDiscovery.Run(ItlLibrary.Load(itl), args[2], args.Length > 3 ? int.Parse(args[3]) : 500);
        break;

    case "mfdh":
        Mfdh(ItlLibrary.Load(itl));
        break;

    case "layout":
        Layout(ItlLibrary.Load(itl));
        break;

    case "mtph":
        Mtph(ItlLibrary.Load(itl));
        break;

    case "plprobe":
        PlaylistProbe.Run(ItlLibrary.Load(itl), args[2]);
        break;

    case "counts":
        Counts(ItlLibrary.Load(itl));
        break;

    case "roundtrip":
        Roundtrip(itl, args[2]);
        break;

    case "identity":
        Identity(itl);
        break;

    case "cloud":
        Cloud(ItlLibrary.Load(itl), int.Parse(args[2]));
        break;

    case "re":
        switch (args[2])
        {
            case "keys": ReverseEngineer.Keys(ItlLibrary.Load(itl), args[3]); break;
            case "map": ReverseEngineer.Map(ItlLibrary.Load(itl), args[3]); break;
            case "strings": ReverseEngineer.Strings(ItlLibrary.Load(itl), args[3]); break;
            case "flags": ReverseEngineer.Flags(ItlLibrary.Load(itl), args[3]); break;
            case "numbers": ReverseEngineer.Numbers(ItlLibrary.Load(itl), args[3]); break;
            case "sections": ReverseEngineer.Sections(ItlLibrary.Load(itl)); break;
            case "plists": ReverseEngineer.Plists(ItlLibrary.Load(itl)); break;
            case "blob": ReverseEngineer.Blob(ItlLibrary.Load(itl), int.Parse(args[3])); break;
            case "ids": ReverseEngineer.Ids(ItlLibrary.Load(itl), args[3]); break;
            case "fk": ReverseEngineer.ForeignKeys(ItlLibrary.Load(itl)); break;
            case "mhgh": ReverseEngineer.Mhgh(ItlLibrary.Load(itl)); break;
            case "smart": ReverseEngineer.Smart(ItlLibrary.Load(itl), args[3]); break;
            case "links": ReverseEngineer.Links(ItlLibrary.Load(itl)); break;
            case "predict": ReverseEngineer.Predict(ItlLibrary.Load(itl), args[3]); break;
            case "kinds": ReverseEngineer.Kinds(ItlLibrary.Load(itl), args[3]); break;
            case "values": ReverseEngineer.Values(ItlLibrary.Load(itl), int.Parse(args[3])); break;
            case "aggregates": ReverseEngineer.Aggregates(ItlLibrary.Load(itl), itl); break;
            case "envelope": ReverseEngineer.Envelope(itl); break;
            default: Console.Error.WriteLine($"Unknown 're' subcommand '{args[2]}'."); return 1;
        }
        break;

    case "demo":
        Demo(itl, args[2]);
        break;

    case "set":
        Set(itl, int.Parse(args[2]), Enum.Parse<ItlDataType>(args[3]), args[4], args[5]);
        break;

    case "verify":
        Verify(ItlLibrary.Load(itl), args[2]);
        break;

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        return 1;
}

return 0;

static void Info(ItlLibrary library)
{
    Console.WriteLine($"iTunes version    : {library.Envelope.Version}");
    Console.WriteLine($"Library persistent: {library.Envelope.LibraryPersistentId:X16}");
    Console.WriteLine($"Sections declared : {library.Envelope.SectionCount}");
    Console.WriteLine($"Sections found    : {library.Sections.Count}");
    Console.WriteLine($"Inflated body     : {library.Envelope.Body.Length:N0} bytes");
    Console.WriteLine($"Tracks            : {library.Tracks.Count:N0}");
    Console.WriteLine();
    Console.WriteLine($"{"Offset",12}  {"Length",12}  {"Type",4}  Inner");
    foreach (ItlSection section in library.Sections)
        Console.WriteLine($"{section.Chunk.Offset,12:N0}  {section.Chunk.TotalLength,12:N0}  {section.Chunk.Type,4}  {section.InnerSignature}");
}

static void Tracks(ItlLibrary library, int count)
{
    foreach (ItlTrack track in library.Tracks.Take(count))
    {
        Console.WriteLine($"[{track.Id}] {track.PersistentId:X16}");
        Console.WriteLine($"    {track.Artist} - {track.Title}");
        Console.WriteLine($"    album={track.Album} albumArtist={track.AlbumArtist} genre={track.Genre}");
        Console.WriteLine($"    track {track.TrackNumber}/{track.TrackCount}  year={track.Year}  {track.Duration:mm\\:ss}  {track.BitRate}kbps  {track.Size:N0} bytes");
        Console.WriteLine($"    plays={track.PlayCount} skips={track.SkipCount} bpm={track.Bpm} added={track.DateAdded:u} played={track.PlayDate:u}");
        Console.WriteLine($"    {track.Location}");
    }
}

static void Mhoh(ItlLibrary library, int index)
{
    ItlTrack track = library.Tracks[index];
    Console.WriteLine($"Track [{track.Id}] {track.Artist} - {track.Title}");
    foreach (ItlDataObject o in track.DataObjects)
    {
        string name = Enum.IsDefined(typeof(ItlDataType), o.Type) ? ((ItlDataType)o.Type).ToString() : "?";
        string head = Convert.ToHexString(o.Raw.AsSpan(0, Math.Min(16, o.Raw.Length)));
        string value = o.IsString
            ? $"\"{Truncate(o.Text!, 60)}\""
            : $"<blob {o.Raw.Length} bytes>";
        Console.WriteLine($"  type={o.Type,-3} {name,-16} preamble={head} {value}");
    }
}

/// <summary>
/// Tests, for every record type, whether the word at +12 (and a few nearby) caches a child count.
/// A serializer has to keep any such cache in step with the children it writes.
/// </summary>
static void Counts(ItlLibrary library)
{
    byte[] body = library.Envelope.Body;
    var stats = new Dictionary<string, (int Records, int[] Agree, int HeaderLength)>();

    foreach (ItlSection section in library.Sections)
    {
        if (section.InnerSignature is not ['m', 'l', _, 'h'])
            continue;

        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
        foreach (ItlChunk record in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
        {
            int all = 0, mhoh = 0, other = 0;
            foreach (ItlChunk child in ItlChunk.Walk(body, record.BodyOffset, record.EndOffset))
            {
                all++;
                if (child.Signature == "mhoh") mhoh++; else other++;
            }

            (int Records, int[] Agree, int HeaderLength) entry =
                stats.TryGetValue(record.Signature, out var existing)
                    ? existing
                    : (0, new int[3], record.HeaderLength);
            int word12 = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(record.Offset + 12));
            if (word12 == all) entry.Agree[0]++;
            if (word12 == mhoh) entry.Agree[1]++;
            if (word12 == other) entry.Agree[2]++;
            entry.Records++;
            stats[record.Signature] = entry;
        }
    }

    Console.WriteLine($"{"record",-8} {"hlen",5} {"count",8}   +12 == totalChildren / mhohChildren / otherChildren");
    foreach ((string sig, var s) in stats.OrderBy(kv => kv.Key))
    {
        string Pct(int n) => $"{(double)n / s.Records:P0}";
        Console.WriteLine($"{sig,-8} {s.HeaderLength,5} {s.Records,8:N0}   {Pct(s.Agree[0]),6} {Pct(s.Agree[1]),6} {Pct(s.Agree[2]),6}");
    }

    // A playlist's +12 counts only its mhoh attributes, so the number of member tracks must be
    // cached somewhere else in the 3500-byte miph header. Find every offset that always agrees.
    ItlSection playlists = library.Sections.First(s => s.Chunk.Type == 2);
    ItlChunk playlistList = ItlChunk.Read(body, playlists.Chunk.BodyOffset);
    var records = new List<(byte[] Header, int Mtph)>();

    foreach (ItlChunk miph in ItlChunk.Walk(body, playlistList.HeaderEnd, playlists.Chunk.EndOffset))
    {
        int mtph = ItlChunk.Walk(body, miph.BodyOffset, miph.EndOffset).Count(c => c.Signature == "mtph");
        records.Add((body.AsSpan(miph.Offset, miph.HeaderLength).ToArray(), mtph));
    }

    Console.WriteLine($"\nmiph offsets whose u32 equals the mtph count on all {records.Count} playlists:");
    int distinct = records.Select(r => r.Mtph).Distinct().Count();
    for (int offset = 12; offset + 4 <= records.Min(r => r.Header.Length); offset++)
    {
        if (records.All(r => BinaryPrimitives.ReadInt32LittleEndian(r.Header.AsSpan(offset)) == r.Mtph))
            Console.WriteLine($"  +{offset} ({distinct} distinct counts)");
    }
}

/// <summary>
/// Parses the library into the editable tree, serializes it back with no edits, and demands the
/// result be byte-identical. This is what proves the tree captures every byte of the format.
/// </summary>
static void Identity(string itl)
{
    ItlEnvelope envelope = ItlEnvelope.Load(itl);
    byte[] original = (byte[])envelope.Body.Clone();

    ItlDocument document = ItlDocument.Parse(envelope);
    byte[] rebuilt = document.Serialize();

    Console.WriteLine($"sections  : {document.Sections.Count}");
    Console.WriteLine($"tracks    : {document.Tracks.Count:N0}");
    Console.WriteLine($"albums    : {document.Albums.Count:N0}");
    Console.WriteLine($"artists   : {document.Artists.Count:N0}");
    Console.WriteLine($"playlists : {document.Playlists.Count:N0}");
    Console.WriteLine($"\noriginal body : {original.Length:N0} bytes");
    Console.WriteLine($"rebuilt body  : {rebuilt.Length:N0} bytes");

    if (original.AsSpan().SequenceEqual(rebuilt))
    {
        Console.WriteLine("\nbyte-identical: the tree round-trips losslessly");
        return;
    }

    Console.WriteLine("\nDIFFERS");
    int limit = Math.Min(original.Length, rebuilt.Length);
    int shown = 0;
    for (int i = 0; i < limit && shown < 8; i++)
    {
        if (original[i] == rebuilt[i])
            continue;
        Console.WriteLine($"  byte {i,12:N0}: {original[i]:X2} -> {rebuilt[i]:X2}");
        shown++;
    }
}

/// <summary>
/// Section 13 is a second "mlth" we keep opaque. Does it reuse the main track ids? If so, deleting
/// a track from the main list leaves a dangling reference behind.
/// </summary>
static void Cloud(ItlLibrary library, int trackId)
{
    byte[] body = library.Envelope.Body;
    ItlSection section = library.Sections.First(s => s.Chunk.Type == 13);
    ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);

    var ids = new List<int>();
    foreach (ItlChunk record in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
    {
        if (record.Signature != "mith")
            break;
        ids.Add(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(record.Offset + 16)));
    }

    var mainIds = library.Tracks.Select(t => t.Id).ToHashSet();
    int shared = ids.Count(ids => mainIds.Contains(ids));

    Console.WriteLine($"section 13 holds {ids.Count:N0} 'mith' records (list declares {list.ItemCount:N0})");
    Console.WriteLine($"  ids also present in the main track list: {shared:N0} of {ids.Count:N0}");
    Console.WriteLine($"  id range: {ids.Min():N0}..{ids.Max():N0}   (main list: {mainIds.Min():N0}..{mainIds.Max():N0})");
    Console.WriteLine($"  track {trackId} present in section 13: {ids.Contains(trackId)}");
}

/// <summary>Exercises every edit operation, then reparses the result with the independent reader.</summary>
static void Demo(string itl, string outPath)
{
    ItlDocument document = ItlDocument.Load(itl);

    ItlRecord trackTemplate = document.Tracks[0];
    int templateId = trackTemplate.GetTrackId();

    Console.WriteLine($"start: {document.Tracks.Count:N0} tracks, {document.Albums.Count:N0} albums, " +
                      $"{document.Artists.Count:N0} artists, {document.Playlists.Count:N0} playlists");

    // 1. Edit fields on an existing track, both strings and numerics.
    trackTemplate.SetString(ItlDataType.Title, "No Diggity (Edited by DumpITL)");
    trackTemplate.SetYear(1997);
    trackTemplate.SetBpm(123);
    trackTemplate.SetPlayCount(42);

    // 2. Add a track. Unknown header fields are inherited from the template.
    ItlRecord added = document.AddTrack(trackTemplate);
    added.SetString(ItlDataType.Title, "Brand New Track");
    added.SetString(ItlDataType.Artist, "DumpITL");
    added.SetString(ItlDataType.Album, "Synthetic Album");
    added.SetString(ItlDataType.Location, @"Z:\iTunes\AAC\Music\DumpITL\Synthetic Album\01 Brand New Track.m4a");
    added.SetTrackNumber(1);
    added.SetTrackCount(1);
    added.SetYear(2026);
    added.SetSize(1234567);
    added.SetDuration(TimeSpan.FromSeconds(210));
    int addedId = added.GetTrackId();
    Console.WriteLine($"added track {addedId} \"{added.GetString(ItlDataType.Title)}\"");

    // 3. Add an album and an artist, and point the new track at them via its foreign keys.
    ItlRecord newAlbum = document.AddAlbum("Synthetic Album", "DumpITL", document.Albums[0]);
    ItlRecord newArtist = document.AddArtist("DumpITL", document.Artists[0]);
    added.SetAlbumId(ItlDocument.RecordIdOf(newAlbum));
    added.SetArtistId(ItlDocument.RecordIdOf(newArtist));
    Console.WriteLine($"added album id {ItlDocument.RecordIdOf(newAlbum)}, artist id {ItlDocument.RecordIdOf(newArtist)}");

    // 4. Add a playlist and put both tracks in it.
    ItlRecord? manual = document.Playlists.FirstOrDefault(p => ItlDocument.PlaylistNameOf(p) == "Purchased")
                        ?? document.Playlists.Last(p => !ItlDocument.IsMasterPlaylist(p));
    ItlRecord playlist = document.AddPlaylist("DumpITL Test", manual);
    document.AddToPlaylist(playlist, templateId);
    document.AddToPlaylist(playlist, addedId);
    Console.WriteLine($"added playlist \"DumpITL Test\" with {playlist.Entries.Count()} tracks");

    // 5. Remove one of them again.
    document.RemoveFromPlaylist(playlist, templateId);
    Console.WriteLine($"after removal it has {playlist.Entries.Count()} tracks");

    // 6. Remove an existing track entirely, and confirm its playlist entries go with it.
    int doomed = document.Tracks[1].GetTrackId();
    int before = document.Playlists.Sum(p => p.Entries.Count(e => e.TrackId == doomed));
    int cloudBefore = document.CloudTracks.Count(t => ItlDocument.TrackIdOf(t) == doomed);
    document.RemoveTrack(doomed);
    int after = document.Playlists.Sum(p => p.Entries.Count(e => e.TrackId == doomed));
    int cloudAfter = document.CloudTracks.Count(t => ItlDocument.TrackIdOf(t) == doomed);
    Console.WriteLine($"removed track {doomed}: playlist entries {before} -> {after}, cloud copies {cloudBefore} -> {cloudAfter}");

    document.Save(outPath);
    Console.WriteLine($"\nwrote {outPath} ({new FileInfo(outPath).Length:N0} bytes)");

    // Reparse with the independent reader, which validates every declared count as it goes.
    ItlLibrary reloaded = ItlLibrary.Load(outPath);
    Console.WriteLine($"reparsed: {reloaded.Tracks.Count:N0} tracks, {reloaded.Albums.Count:N0} albums, " +
                      $"{reloaded.Artists.Count:N0} artists, {reloaded.Playlists.Count:N0} playlists");

    ItlTrack check = reloaded.Tracks.First(t => t.Id == addedId);
    Console.WriteLine($"  new track  : [{check.Id}] {check.Artist} - {check.Title} ({check.Year}) {check.Duration:mm\\:ss} {check.Size:N0} bytes");
    Console.WriteLine($"  its path   : {check.Location}");

    ItlTrack edited = reloaded.Tracks.First(t => t.Id == templateId);
    Console.WriteLine($"  edited     : \"{edited.Title}\" year={edited.Year} bpm={edited.Bpm} plays={edited.PlayCount}");

    ItlPlaylist added2 = reloaded.Playlists.First(p => p.Name == "DumpITL Test");
    Console.WriteLine($"  playlist   : \"{added2.Name}\" -> [{string.Join(", ", added2.TrackIds)}]");
    Console.WriteLine($"  removed {doomed} still present: {reloaded.Tracks.Any(t => t.Id == doomed)}");

    // Identifiers must stay unique, and the written file must itself round-trip losslessly.
    ItlDocument written = ItlDocument.Load(outPath);
    uint[] trackIds = [.. written.Tracks.Select(t => (uint)ItlDocument.TrackIdOf(t))];
    uint[] albumIds = [.. written.Albums.Select(ItlDocument.RecordIdOf)];
    uint[] artistIds = [.. written.Artists.Select(ItlDocument.RecordIdOf)];
    uint[] entryIds = [.. written.Playlists.SelectMany(p => p.Entries).Select(e => e.EntryId)];

    Console.WriteLine($"\n  track ids unique          : {trackIds.Length == trackIds.Distinct().Count()}");
    Console.WriteLine($"  album ids unique          : {albumIds.Length == albumIds.Distinct().Count()}");
    Console.WriteLine($"  artist ids unique         : {artistIds.Length == artistIds.Distinct().Count()}");
    Console.WriteLine($"  entry ids unique          : {entryIds.Length == entryIds.Distinct().Count()}");

    // Every object in an iTunes library draws its id from one counter, so nothing may collide.
    uint[] everything = [.. trackIds, .. albumIds, .. artistIds, .. entryIds];
    Console.WriteLine($"  all ids globally unique   : {everything.Length == everything.Distinct().Count()} ({everything.Length:N0} ids)");

    // The new track's foreign keys must resolve to the album and artist we created.
    ItlRecord check2 = written.FindTrack(addedId)!;
    Console.WriteLine($"  new track album id resolves : {albumIds.Contains(check2.GetAlbumId())}");
    Console.WriteLine($"  new track artist id resolves: {artistIds.Contains(check2.GetArtistId())}");

    ItlEnvelope reread = ItlEnvelope.Load(outPath);
    byte[] before2 = (byte[])reread.Body.Clone();
    Console.WriteLine($"  output re-serializes identically: {ItlDocument.Parse(reread).Serialize().AsSpan().SequenceEqual(before2)}");

    // The master playlist must list exactly the tracks the library holds.
    ItlPlaylist master = reloaded.Playlists.First(p => p.IsMaster);
    bool sameSet = master.TrackIds.Order().SequenceEqual(reloaded.Tracks.Select(t => t.Id).Order());
    Console.WriteLine($"  master playlist == track list: {sameSet} ({master.TrackIds.Count:N0} entries, {reloaded.Tracks.Count:N0} tracks)");
}

/// <summary>Re-encodes the library unchanged and checks the decoded body survives a write/read cycle.</summary>
static void Roundtrip(string itl, string outPath)
{
    ItlEnvelope original = ItlEnvelope.Load(itl);
    byte[] originalBody = (byte[])original.Body.Clone();

    ItlWriter.Save(original, original.Body, outPath);

    ItlEnvelope reloaded = ItlEnvelope.Load(outPath);

    Console.WriteLine($"original file : {new FileInfo(itl).Length:N0} bytes");
    Console.WriteLine($"rewritten file: {new FileInfo(outPath).Length:N0} bytes");
    Console.WriteLine($"original body : {originalBody.Length:N0} bytes");
    Console.WriteLine($"reloaded body : {reloaded.Body.Length:N0} bytes");

    // The mfdh total-length word is the one byte range we intentionally rewrite, and here it should
    // land on the same value it already had.
    bool identical = originalBody.AsSpan().SequenceEqual(reloaded.Body);
    Console.WriteLine($"\nbody identical: {identical}");

    if (!identical)
    {
        for (int i = 0; i < Math.Min(originalBody.Length, reloaded.Body.Length); i++)
        {
            if (originalBody[i] != reloaded.Body[i])
            {
                Console.WriteLine($"  first difference at byte {i}: {originalBody[i]:X2} -> {reloaded.Body[i]:X2}");
                break;
            }
        }
        return;
    }

    ItlLibrary library = ItlLibrary.Parse(reloaded);
    Console.WriteLine($"reparsed: {library.Tracks.Count:N0} tracks, {library.Playlists.Count:N0} playlists, " +
                      $"{library.Albums.Count:N0} albums, {library.Artists.Count:N0} artists");
}

/// <summary>Rewrites one string field of one track and saves a new library file.</summary>
static void Set(string itl, int trackId, ItlDataType type, string value, string outPath)
{
    ItlDocument document = ItlDocument.Load(itl);
    ItlRecord track = document.FindTrack(trackId) ?? throw new InvalidOperationException($"No track {trackId}.");

    Console.WriteLine($"before: [{trackId}] {type} = \"{track.GetString(type)}\"");
    track.SetString(type, value);
    document.Save(outPath);

    // Re-read from disk with the independent reader: the only check that matters is that it parses.
    ItlLibrary reloaded = ItlLibrary.Load(outPath);
    ItlTrack after = reloaded.Tracks.First(t => t.Id == trackId);

    Console.WriteLine($"after : [{trackId}] {type} = \"{after[type]}\"");
    Console.WriteLine($"\nwrote {outPath} ({new FileInfo(outPath).Length:N0} bytes)");
    Console.WriteLine($"reparsed: {reloaded.Tracks.Count:N0} tracks, {reloaded.Playlists.Count:N0} playlists, " +
                      $"{reloaded.Albums.Count:N0} albums, {reloaded.Artists.Count:N0} artists");
}

/// <summary>Hex-dumps the first few playlist track entries, to locate the track id inside them.</summary>
static void Mtph(ItlLibrary library)
{
    byte[] body = library.Envelope.Body;
    ItlSection section = library.Sections.First(s => s.Chunk.Type == 2);
    ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
    ItlChunk playlist = ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset).First();

    Console.WriteLine($"playlist hlen={playlist.HeaderLength} total={playlist.TotalLength}");

    int shown = 0;
    foreach (ItlChunk child in ItlChunk.Walk(body, playlist.BodyOffset, playlist.EndOffset))
    {
        if (child.Signature != "mtph")
            continue;
        if (shown++ >= 4)
            break;

        Console.WriteLine($"  mtph hlen={child.HeaderLength} total={child.TotalLength}");
        for (int i = 0; i < child.HeaderLength; i += 16)
        {
            int n = Math.Min(16, child.HeaderLength - i);
            Console.WriteLine($"    +{i,-4} {Convert.ToHexString(body.AsSpan(child.Offset + i, n))}");
        }
    }
}

/// <summary>Prints the child structure under each section, to map record types not yet modelled.</summary>
static void Layout(ItlLibrary library)
{
    byte[] body = library.Envelope.Body;

    foreach (ItlSection section in library.Sections)
    {
        if (section.InnerSignature is not ['m', 'l', _, 'h'])
        {
            Console.WriteLine($"section type {section.Chunk.Type,-3} inner '{section.InnerSignature}' (not a list)");
            continue;
        }

        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
        Console.WriteLine($"section type {section.Chunk.Type,-3} {list.Signature} hlen={list.HeaderLength} declares {list.ItemCount:N0} items");

        int shown = 0;
        foreach (ItlChunk item in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
        {
            if (shown++ >= 2) break;

            var children = new List<string>();
            foreach (ItlChunk child in ItlChunk.Walk(body, item.BodyOffset, item.EndOffset))
            {
                string tag = child.Signature;
                if (child.Signature == "mhoh")
                {
                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    tag += o.IsString ? $"({child.Type}:\"{Truncate(o.Text!, 24)}\")" : $"({child.Type}:blob)";
                }
                children.Add(tag);
                if (children.Count >= 12) { children.Add("..."); break; }
            }

            Console.WriteLine($"    item '{item.Signature}' hlen={item.HeaderLength} total={item.TotalLength}");
            Console.WriteLine($"      children: {string.Join(" ", children)}");
        }
        Console.WriteLine();
    }
}

/// <summary>Hex-dumps the internal copy of the envelope, to see which envelope fields it duplicates.</summary>
static void Mfdh(ItlLibrary library)
{
    ItlSection section = library.Sections[0];
    byte[] body = library.Envelope.Body;
    int start = section.Chunk.BodyOffset;
    int length = section.Chunk.TotalLength - section.Chunk.HeaderLength;

    Console.WriteLine($"Envelope file length was {library.Envelope.FileLength:N0} (0x{library.Envelope.FileLength:X})");
    Console.WriteLine($"Inflated body length is  {body.Length:N0} (0x{body.Length:X})\n");

    for (int i = 0; i < length; i += 16)
    {
        int n = Math.Min(16, length - i);
        string hex = Convert.ToHexString(body.AsSpan(start + i, n));
        string ascii = string.Concat(body.Skip(start + i).Take(n).Select(b => b is >= 32 and <= 126 ? (char)b : '.'));
        Console.WriteLine($"  +{i,-4} {hex,-32} {ascii}");
    }
}

/// <summary>Dumps the undecoded string bytes of one track, to identify unfamiliar encodings.</summary>
static void Probe(ItlLibrary library, int trackId)
{
    ItlTrack track = library.Tracks.First(t => t.Id == trackId);
    Console.WriteLine($"Track [{track.Id}] persistent={track.PersistentId:X16}");
    foreach (ItlDataObject o in track.DataObjects.Where(o => o.Payload.Length is > 0 and < 64))
    {
        bool ascii = o.Payload.All(b => b < 0x80);
        string name = Enum.IsDefined(typeof(ItlDataType), o.Type) ? ((ItlDataType)o.Type).ToString() : $"type{o.Type}";
        Console.WriteLine($"  {name,-16} enc={o.Encoding} len={o.Payload.Length,-3} ascii={ascii,-5} {Convert.ToHexString(o.Payload)}");
    }
}

static void Verify(ItlLibrary library, string xmlPath)
{
    XDocument doc = XDocument.Load(xmlPath);
    XElement root = doc.Root!.Element("dict")!;
    XElement tracksDict = root.Elements("key").First(k => k.Value == "Tracks").ElementsAfterSelf().First();

    var xmlTracks = new Dictionary<int, Dictionary<string, string>>();
    foreach (XElement dict in tracksDict.Elements("dict"))
    {
        var values = new Dictionary<string, string>();
        foreach (XElement key in dict.Elements("key"))
            values[key.Value] = ((XElement)key.NextNode!).Value;
        xmlTracks[int.Parse(values["Track ID"])] = values;
    }

    Console.WriteLine($"XML tracks : {xmlTracks.Count:N0}");
    Console.WriteLine($"ITL tracks : {library.Tracks.Count:N0}\n");

    // Each check is a field name and a way to read it from both sides.
    (string Name, Func<ItlTrack, string?> Itl, Func<Dictionary<string, string>, string?> Xml)[] checks =
    [
        ("title",        t => t.Title,                        x => Get(x, "Name")),
        ("artist",       t => t.Artist,                       x => Get(x, "Artist")),
        ("album",        t => t.Album,                        x => Get(x, "Album")),
        ("persistentId", t => $"{t.PersistentId:X16}",        x => Get(x, "Persistent ID")?.ToUpperInvariant()),
        ("duration",     t => ((int)t.Duration.TotalMilliseconds).ToString(), x => Get(x, "Total Time")),
        ("trackNumber",  t => Zero(t.TrackNumber),            x => Int(x, "Track Number")),
        ("trackCount",   t => Zero(t.TrackCount),             x => Int(x, "Track Count")),
        ("year",         t => Zero(t.Year),                   x => Int(x, "Year")),
        ("bitRate",      t => Zero(t.BitRate),                x => Int(x, "Bit Rate")),
        ("size",         t => t.Size.ToString(),              x => Get(x, "Size")),
        ("playCount",    t => Zero(t.PlayCount),              x => Int(x, "Play Count")),
        ("skipCount",    t => Zero(t.SkipCount),              x => Int(x, "Skip Count")),
        ("bpm",          t => Zero(t.Bpm),                    x => Int(x, "BPM")),
        ("dateAdded",    t => t.DateAdded?.ToString("u"),     x => Iso(Get(x, "Date Added"))),
        ("playDate",     t => t.PlayDate?.ToString("u"),      x => Iso(Get(x, "Play Date UTC"))),

        // Fields recovered by reverse engineering; checked the same way as the rest.
        ("discNumber",   t => Zero(t.DiscNumber),             x => Int(x, "Disc Number")),
        ("discCount",    t => Zero(t.DiscCount),              x => Int(x, "Disc Count")),
        ("artworkCount", t => Zero(t.ArtworkCount),           x => Int(x, "Artwork Count")),
        ("fileFolders",  t => Zero(t.FileFolderCount),        x => Int(x, "File Folder Count")),
        ("libFolders",   t => Zero(t.LibraryFolderCount),     x => Int(x, "Library Folder Count")),
        ("season",       t => Zero(t.Season),                 x => Int(x, "Season")),
        ("episodeOrder", t => Zero(t.EpisodeOrder),           x => Int(x, "Episode Order")),
        ("releaseDate",  t => t.ReleaseDate?.ToString("u"),   x => Iso(Get(x, "Release Date"))),
        ("grouping",     t => t[ItlDataType.Grouping],        x => Get(x, "Grouping")),
        ("series",       t => t[ItlDataType.Series],          x => Get(x, "Series")),
        ("episode",      t => t[ItlDataType.Episode],         x => Get(x, "Episode")),
        ("contentRating",t => t[ItlDataType.ContentRating],   x => Get(x, "Content Rating")),
        ("sortAlbumArt", t => t[ItlDataType.SortAlbumArtist], x => Get(x, "Sort Album Artist")),
        ("sortComposer", t => t[ItlDataType.SortComposer],    x => Get(x, "Sort Composer")),
        ("compilation",  t => Flag(t.Compilation),            x => Bool(x, "Compilation")),
        ("hasVideo",     t => Flag(t.HasVideo),               x => Bool(x, "Has Video")),
        ("gapless",      t => Flag(t.PartOfGaplessAlbum),     x => Bool(x, "Part Of Gapless Album")),
        ("explicit",     t => Flag(t.Advisory == 1),          x => Bool(x, "Explicit")),
        ("clean",        t => Flag(t.Advisory == 2),          x => Bool(x, "Clean")),
    ];

    var failures = new Dictionary<string, int>();
    var examples = new Dictionary<string, string>();
    int missing = 0, compared = 0;

    foreach (ItlTrack track in library.Tracks)
    {
        if (!xmlTracks.TryGetValue(track.Id, out var x))
        {
            missing++;
            continue;
        }

        compared++;
        foreach ((string name, var itlOf, var xmlOf) in checks)
        {
            string? expected = xmlOf(x);
            if (expected is null)
                continue;   // iTunes omits absent/zero fields from the XML entirely.

            string? actual = itlOf(track);
            if (actual == expected)
                continue;

            failures[name] = failures.GetValueOrDefault(name) + 1;
            examples.TryAdd(name, $"      id={track.Id} itl=\"{actual}\" xml=\"{expected}\"");
        }
    }

    Console.WriteLine($"Compared {compared:N0} tracks ({missing} present only in the .itl)\n");
    foreach ((string name, _, _) in checks)
    {
        int bad = failures.GetValueOrDefault(name);
        Console.WriteLine($"  {name,-13} {(bad == 0 ? "ok" : $"{bad:N0} mismatched")}");
        if (bad > 0)
            Console.WriteLine(examples[name]);
    }

    VerifyPlaylists(library, root);
    Console.WriteLine($"\nAlbums  : {library.Albums.Count:N0}");
    Console.WriteLine($"Artists : {library.Artists.Count:N0}");
    return;

    static string? Get(Dictionary<string, string> x, string key) => x.TryGetValue(key, out string? v) ? v : null;
    static string Zero(int v) => v.ToString();
    static string? Iso(string? v) => v is null ? null : DateTime.Parse(v).ToUniversalTime().ToString("u");
    static string Flag(bool v) => v ? "true" : "false";

    // iTunes omits zero-valued integers, so an absent key means zero, not "unknown".
    static string Int(Dictionary<string, string> x, string key) => Get(x, key) ?? "0";

    // iTunes omits false booleans entirely, so an absent key means false rather than "unknown".
    static string Bool(Dictionary<string, string> x, string key) => x.ContainsKey(key) ? "true" : "false";
}

static void VerifyPlaylists(ItlLibrary library, XElement root)
{
    XElement playlistsArray = root.Elements("key").First(k => k.Value == "Playlists").ElementsAfterSelf().First();

    var xmlPlaylists = new List<(string Name, List<int> TrackIds)>();
    foreach (XElement dict in playlistsArray.Elements("dict"))
    {
        string? name = dict.Elements("key").FirstOrDefault(k => k.Value == "Name")?.ElementsAfterSelf().First().Value;
        XElement? items = dict.Elements("key").FirstOrDefault(k => k.Value == "Playlist Items")?.ElementsAfterSelf().First();
        List<int> ids = items is null
            ? []
            : [.. items.Elements("dict").Select(d => int.Parse(d.Element("integer")!.Value))];
        xmlPlaylists.Add((name ?? "", ids));
    }

    Console.WriteLine($"\nXML playlists : {xmlPlaylists.Count:N0}");
    Console.WriteLine($"ITL playlists : {library.Playlists.Count:N0}");

    // Names are not unique (three playlists here are called "Downloaded"), so consume each XML
    // playlist once rather than matching every same-named .itl playlist to the first one.
    var byName = xmlPlaylists
        .GroupBy(p => p.Name)
        .ToDictionary(g => g.Key, g => new Queue<(string Name, List<int> TrackIds)>(g));

    int exact = 0, notFound = 0, differing = 0;
    foreach (ItlPlaylist playlist in library.Playlists)
    {
        // The master playlist is "####!####" internally but exported as "Library".
        string name = playlist.IsMaster ? "Library" : playlist.Name ?? "";
        if (!byName.TryGetValue(name, out var queue) || queue.Count == 0)
        {
            notFound++;
            continue;
        }

        var match = queue.Dequeue();

        if (match.TrackIds.SequenceEqual(playlist.TrackIds))
        {
            exact++;
        }
        else
        {
            differing++;
            // The XML omits PDF booklets and iTunes LPs, so its list should still be an ordered
            // subsequence of the .itl list. Anything else would be a real parsing error.
            bool subsequence = IsSubsequence(match.TrackIds, playlist.TrackIds);
            int extra = playlist.TrackIds.Count - match.TrackIds.Count;
            Console.WriteLine($"  \"{name}\": itl {playlist.TrackIds.Count:N0}, xml {match.TrackIds.Count:N0}" +
                              $" ({extra:+#;-#;0}) {(subsequence ? "xml is an ordered subsequence" : "DIVERGENT")}");
        }
    }

    Console.WriteLine($"  identical track lists : {exact:N0}");
    Console.WriteLine($"  differing             : {differing:N0}");
    Console.WriteLine($"  not in XML            : {notFound:N0}");
    return;

    static bool IsSubsequence(List<int> inner, IReadOnlyList<int> outer)
    {
        int i = 0;
        foreach (int value in outer)
            if (i < inner.Count && inner[i] == value)
                i++;
        return i == inner.Count;
    }
}

static string Truncate(string s, int max)
{
    s = s.ReplaceLineEndings(" ");
    return s.Length <= max ? s : s[..max] + "â€¦";
}







