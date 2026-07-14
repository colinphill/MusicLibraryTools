using ConsoleTools;
using iTunes.Binary;
using MusicFileUtilities;

namespace DumpArtworkSizes;

internal static class Program
{
    private sealed record Options(string PlaylistName, string? LibraryPath, string OutputPath);

    private static int Main(string[] args)
    {
        LogConsole.SwitchFile("DumpArtworkSizes.log");
        try
        {
            if (!TryParseArguments(args, out Options? options))
            {
                LogConsole.WriteLine(
                    "Usage: DumpArtworkSizes <playlist> [--library <file.itl>] [--output <report.dat>]");
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
        var operands = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                libraryPath = args[index];
            else if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                outputPath = args[index];
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options = null;
                return false;
            }
            else
                operands.Add(args[index]);
        }

        options = operands.Count == 1
            ? new Options(operands[0], libraryPath, Path.GetFullPath(outputPath))
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
        using var writer = new StreamWriter(options.OutputPath);
        int tracks = 0;
        int noArtwork = 0;
        int errors = 0;
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            LogConsole.WriteLine($"{tracks}) Checking track: {path}");
            try
            {
                IMediaFile mediaFile = MediaFile.GetFile(path);
                IMetadataImage[] images = [.. mediaFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
                if (images.Length == 1)
                {
                    IMetadataImage image = images[0];
                    writer.WriteLine($"{artist}|{album}|{image.Width}|{image.Height}|{image.Size}");
                }
                else
                {
                    writer.WriteLine($"{artist}|{album}|0|0");
                    noArtwork++;
                    LogConsole.WriteLine(images.Length == 0
                        ? "No embedded artwork."
                        : $"Expected one embedded artwork image, found {images.Length}.");
                }
            }
            catch (Exception exception)
            {
                errors++;
                LogConsole.WriteLine($"Unable to inspect '{path}': {exception.Message}");
            }
        }

        LogConsole.WriteLine($"Analyzed Tracks: {tracks}");
        LogConsole.WriteLine($"Analyzed Albums: {albums.Count}");
        LogConsole.WriteLine($"Albums Without One Embedded Artwork Image: {noArtwork}");
        LogConsole.WriteLine($"Errors: {errors}");
        LogConsole.WriteLine($"Report: {options.OutputPath}");
        return errors == 0 ? 0 : 1;
    }
}
