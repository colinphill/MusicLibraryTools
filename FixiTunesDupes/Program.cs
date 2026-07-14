using System.Security.Cryptography;
using iTunes.Binary;

namespace FixiTunesDupes;

internal static class Program
{
    private readonly record struct TrackIdentity(
        int Disc,
        int Track,
        string Title,
        string Artist,
        string Album,
        int DurationSeconds);

    private sealed record TrackCandidate(
        int TrackId,
        DateTime DateAdded,
        string Path);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool TryParseArguments(
        string[] args,
        out bool apply,
        out int maxRemovals,
        out string? libraryPath)
    {
        apply = false;
        maxRemovals = 0;
        libraryPath = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
            else if (args[index].Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out maxRemovals) && maxRemovals >= 0)
            {
            }
            else if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
            {
                libraryPath = args[index];
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static string? TryHashFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Skipping '{path}' because its content could not be verified: {exception.Message}");
            return null;
        }
    }

    private static string? LocalPathOf(ItlRecord track)
    {
        string? value = track.GetString(ItlDataType.Location);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            return uri.LocalPath.Replace(@"\localhost\", @"\", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    /// <summary>
    /// The observed iTunes 12.13 manual user-playlist partition has byte +24 equal to one. Smart,
    /// master, and known distinguished Purchased playlists are excluded because their memberships
    /// are owned by iTunes and should shrink naturally when a duplicate track is removed.
    /// </summary>
    private static bool IsOrdinaryManualPlaylist(ItlRecord playlist)
    {
        string? name = ItlDocument.PlaylistNameOf(playlist);
        return !ItlDocument.IsMasterPlaylist(playlist) &&
               ItlDocument.SmartPlaylistOf(playlist) is null &&
               playlist.Header.Length > 24 && playlist.Header[24] == 1 &&
               !string.Equals(name, "Purchased", StringComparison.OrdinalIgnoreCase);
    }

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FixiTunesDupes: {exception.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (!TryParseArguments(args, out bool apply, out int maxRemovals, out string? specifiedLibrary))
        {
            Console.WriteLine("Usage: FixiTunesDupes [--library <file.itl>] [--apply] [--max-removals <count>]");
            return 2;
        }

        string libraryPath = ItlFileEditor.ResolveLibraryPath(specifiedLibrary);
        if (apply)
            ItlFileEditor.EnsureItunesIsClosed();

        ItlDocument document = ItlDocument.Load(libraryPath);
        if (!apply)
            Console.WriteLine("Dry-run mode; pass --apply to update playlist references and remove byte-identical duplicate entries.");

        ItlRecord[] manualPlaylists = [.. document.Playlists.Where(IsOrdinaryManualPlaylist)];
        var metadataGroups = new Dictionary<TrackIdentity, List<TrackCandidate>>();
        foreach (ItlRecord track in document.Tracks)
        {
            if (track.GetHasVideo())
                continue;
            string? path = LocalPathOf(track);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var identity = new TrackIdentity(
                track.GetDiscNumber(),
                track.GetTrackNumber(),
                Normalize(track.GetString(ItlDataType.Title)),
                Normalize(track.GetString(ItlDataType.Artist)),
                Normalize(track.GetString(ItlDataType.Album)),
                (int)track.GetDuration().TotalSeconds);

            if (!metadataGroups.TryGetValue(identity, out List<TrackCandidate>? candidates))
                metadataGroups.Add(identity, candidates = []);
            candidates.Add(new TrackCandidate(
                ItlDocument.TrackIdOf(track),
                track.GetDateAdded() ?? DateTime.MinValue,
                path));
        }

        var replacements = new Dictionary<int, int>();
        var duplicates = new List<TrackCandidate>();
        foreach (KeyValuePair<TrackIdentity, List<TrackCandidate>> metadataGroup in
                 metadataGroups.Where(group => group.Value.Count > 1))
        {
            var verifiedGroups = metadataGroup.Value
                .Select(candidate => (Candidate: candidate, Hash: TryHashFile(candidate.Path)))
                .Where(item => item.Hash is not null)
                .GroupBy(item => item.Hash!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var verifiedGroup in verifiedGroups)
            {
                TrackCandidate retained = verifiedGroup.Select(item => item.Candidate)
                    .OrderByDescending(candidate => candidate.DateAdded).First();
                Console.WriteLine($"Keeping: {retained.Path}");

                foreach (TrackCandidate duplicate in verifiedGroup.Select(item => item.Candidate)
                             .Where(candidate => candidate.TrackId != retained.TrackId))
                {
                    replacements.Add(duplicate.TrackId, retained.TrackId);
                    duplicates.Add(duplicate);
                    int occurrences = manualPlaylists.Sum(playlist =>
                        playlist.Entries.Count(entry => entry.TrackId == duplicate.TrackId));
                    Console.WriteLine($"Duplicate: {duplicate.Path} ({occurrences} ordinary playlist occurrence(s))");
                }
            }
        }

        if (!apply)
        {
            Console.WriteLine($"Verified duplicate entries: {duplicates.Count}. No changes made.");
            return 0;
        }

        if (duplicates.Count == 0)
        {
            Console.WriteLine("No verified duplicate entries; the ITL was not rewritten.");
            return 0;
        }

        if (duplicates.Count > maxRemovals)
        {
            Console.WriteLine($"Safety stop: {duplicates.Count} duplicate removals exceeds --max-removals " +
                              $"{maxRemovals}. The ITL was not changed.");
            return 3;
        }

        int redirected = 0;
        foreach (ItlRecord playlist in manualPlaylists)
        {
            foreach ((int oldTrackId, int replacementTrackId) in replacements)
                redirected += document.RedirectPlaylistEntries(playlist, oldTrackId, replacementTrackId);
        }

        int removed = 0;
        foreach (TrackCandidate duplicate in duplicates)
        {
            if (document.RemoveTrack(duplicate.TrackId))
                removed++;
        }

        // Validation and writer reference guards run before the atomic replace. If a duplicate is
        // named by mprh or miqh state whose native removal policy is unknown, the live file remains
        // untouched and the operation fails with a controlled diagnostic.
        ItlFileEditor.SaveValidated(document, libraryPath);
        Console.WriteLine($"Redirected ordinary playlist occurrences: {redirected}.");
        Console.WriteLine($"Verified duplicate entries removed: {removed}.");
        Console.WriteLine($"Saved '{libraryPath}'; previous file retained as '{libraryPath}.bak'.");
        return 0;
    }
}
