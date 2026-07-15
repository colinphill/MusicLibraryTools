using ConsoleTools;
using iTunes.Binary;
using MusicFileUtilities;

namespace DumpArtworkSizes;

internal static class Program
{
    private sealed record Options(string PlaylistName, string? LibraryPath, string OutputPath, int Parallelism);

    private sealed record WorkItem(int TrackNumber, string Path, string Artist, string Album);

    private sealed record InspectionResult(string? ReportLine, string? Message, bool NoArtwork, bool Error);

    private static int Main(string[] args)
    {
        LogConsole.SwitchFile("DumpArtworkSizes.log");
        try
        {
            if (!TryParseArguments(args, out Options? options))
            {
                LogConsole.WriteLine(
                    "Usage: DumpArtworkSizes <playlist> [--library <file.itl>] [--output <report.dat>] [--parallelism <1-64>]");
                return 2;
            }

            return Run(options!);
        }
        catch (Exception exception)
        {
            LogConsole.WriteLine($"DumpArtworkSizes: {exception.Message}");
            return 1;
        }
        finally
        {
            LogConsole.End();
        }
    }

    private static bool TryParseArguments(string[] args, out Options? options)
    {
        string? libraryPath = null;
        string outputPath = "ArtworkSizes.dat";
        int parallelism = 16;
        var operands = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                libraryPath = args[index];
            else if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                outputPath = args[index];
            else if (args[index].Equals("--parallelism", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out int parsedParallelism))
                parallelism = parsedParallelism;
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options = null;
                return false;
            }
            else
                operands.Add(args[index]);
        }

        options = operands.Count == 1 && parallelism is >= 1 and <= 64
            ? new Options(operands[0], libraryPath, Path.GetFullPath(outputPath), parallelism)
            : null;
        return options is not null;
    }

    private static int Run(Options options)
    {
        ItlLibrary library = ItlLibrary.Load(ItlFileEditor.ResolveLibraryPath(options.LibraryPath));
        ItlPlaylist[] matchingPlaylists = [.. library.Playlists.Where(playlist =>
            string.Equals(playlist.Name, options.PlaylistName, StringComparison.OrdinalIgnoreCase))];
        if (matchingPlaylists.Length != 1)
            throw new InvalidOperationException(
                $"Expected one playlist named '{options.PlaylistName}', found {matchingPlaylists.Length}.");

        Dictionary<int, ItlTrack> tracksById = library.Tracks.ToDictionary(track => track.Id);
        int tracks = 0;
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<WorkItem>();

        foreach (int trackId in matchingPlaylists[0].TrackIds)
        {
            if (!tracksById.TryGetValue(trackId, out ItlTrack? track) || track.HasVideo ||
                string.Equals(track.Genre, "Podcast", StringComparison.OrdinalIgnoreCase))
                continue;

            string? path = ItlLocation.ToLocalPath(track.Location);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            tracks++;
            string artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist ?? string.Empty : track.AlbumArtist;
            string album = track.Album ?? string.Empty;
            if (!albums.Add(artist + "\0" + album))
                continue;

            work.Add(new WorkItem(tracks, path, artist, album));
        }

        var results = new InspectionResult[work.Count];
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism }, index =>
        {
            WorkItem item = work[index];
            try
            {
                IMediaFile mediaFile = MediaFile.GetFile(item.Path, readOnly: true);
                IMetadataImage[] images = [.. mediaFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
                if (images.Length == 1)
                {
                    IMetadataImage image = images[0];
                    results[index] = new InspectionResult(
                        $"{item.Artist}|{item.Album}|{image.Width}|{image.Height}|{image.Size}", null, false, false);
                }
                else
                {
                    results[index] = new InspectionResult(
                        $"{item.Artist}|{item.Album}|0|0",
                        images.Length == 0
                            ? "No embedded artwork."
                            : $"Expected one embedded artwork image, found {images.Length}.",
                        true,
                        false);
                }
            }
            catch (Exception exception)
            {
                results[index] = new InspectionResult(
                    null, $"Unable to inspect '{item.Path}': {exception.Message}", false, true);
            }
        });

        int noArtwork = 0;
        int errors = 0;
        using var writer = new StreamWriter(options.OutputPath);
        for (int index = 0; index < work.Count; index++)
        {
            WorkItem item = work[index];
            InspectionResult result = results[index];
            LogConsole.WriteLine($"{item.TrackNumber}) Checking track: {item.Path}");
            if (result.ReportLine is not null)
                writer.WriteLine(result.ReportLine);
            if (result.Message is not null)
                LogConsole.WriteLine(result.Message);
            if (result.NoArtwork)
                noArtwork++;
            if (result.Error)
                errors++;
        }

        LogConsole.WriteLine($"Analyzed Tracks: {tracks}");
        LogConsole.WriteLine($"Analyzed Albums: {albums.Count}");
        LogConsole.WriteLine($"Albums Without One Embedded Artwork Image: {noArtwork}");
        LogConsole.WriteLine($"Errors: {errors}");
        LogConsole.WriteLine($"Report: {options.OutputPath}");
        return errors == 0 ? 0 : 1;
    }
}
