using ConsoleTools;
using iTunes.Binary;
using MusicFileUtilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FixArtwork;

internal static class Program
{
    private const long Threshold = 225 * 1024;
    private const int MaximumDimension = 600;
    private const int JpegQuality = 75;

    private sealed record Options(string PlaylistName, string? LibraryPath, bool Apply);

    private static int Main(string[] args)
    {
        LogConsole.SwitchFile("FixArtwork.log");
        try
        {
            if (!TryParseArguments(args, out Options? options))
            {
                LogConsole.WriteLine("Usage: FixArtwork <playlist> [--library <file.itl>] [--apply]");
                return 2;
            }

            return Run(options!);
        }
        catch (Exception exception)
        {
            LogConsole.WriteLine($"FixArtwork: {exception.Message}");
            return 1;
        }
        finally
        {
            LogConsole.End();
        }
    }

    private static bool TryParseArguments(string[] args, out Options? options)
    {
        bool apply = false;
        string? libraryPath = null;
        var operands = new List<string>();

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
            else if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
            {
                libraryPath = args[index];
            }
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options = null;
                return false;
            }
            else
            {
                operands.Add(args[index]);
            }
        }

        options = operands.Count == 1 ? new Options(operands[0], libraryPath, apply) : null;
        return options is not null;
    }

    private static int Run(Options options)
    {
        string libraryPath = ItlFileEditor.ResolveLibraryPath(options.LibraryPath);
        if (options.Apply)
            ItlFileEditor.EnsureItunesIsClosed();

        ItlDocument document = ItlDocument.Load(libraryPath);
        ItlRecord[] matchingPlaylists = [.. document.FindPlaylists(
            options.PlaylistName, StringComparison.OrdinalIgnoreCase)];
        if (matchingPlaylists.Length != 1)
            throw new InvalidOperationException(
                $"Expected one playlist named '{options.PlaylistName}', found {matchingPlaylists.Length}.");

        if (!options.Apply)
            LogConsole.WriteLine("Dry run: pass --apply to update embedded artwork and the ITL file caches.");

        Dictionary<int, ItlRecord> tracksById = document.Tracks.ToDictionary(ItlDocument.TrackIdOf);
        int tracks = 0;
        int artwork = 0;
        int missing = 0;
        int errors = 0;
        int fixedArtwork = 0;
        bool libraryChanged = false;

        foreach (int trackId in matchingPlaylists[0].Entries.Select(entry => entry.TrackId).Distinct())
        {
            if (!tracksById.TryGetValue(trackId, out ItlRecord? track) || track.GetHasVideo())
                continue;

            string? path = ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location));
            if (string.IsNullOrWhiteSpace(path))
                continue;

            tracks++;
            try
            {
                LogConsole.WriteLine($"{tracks} Checking artwork: {path}");
                IMediaFile mediaFile = MediaFile.GetFile(path);
                IMetadataImage[] images = [.. mediaFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
                if (images.Length == 0)
                {
                    LogConsole.WriteLine($"Error: No embedded artwork: {path}");
                    missing++;
                    continue;
                }
                if (images.Length != 1)
                {
                    LogConsole.WriteLine($"Error: {images.Length} embedded artwork images: {path}");
                    errors++;
                    continue;
                }

                artwork++;
                IMetadataImage source = images[0];
                using Image image = Image.Load(source.Data);
                bool needsResize = image.Width > MaximumDimension || image.Height > MaximumDimension;
                bool isJpeg = source.ImageType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                              source.ImageType.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ||
                              source.ImageType.Equals("jpg", StringComparison.OrdinalIgnoreCase);
                bool needsChange = needsResize || !isJpeg || source.Size > Threshold;

                LogConsole.WriteLine(
                    $"Artwork: {source.ImageType}, {image.Width}x{image.Height}, {source.Size:N0} bytes.");
                if (!needsChange)
                    continue;

                if (needsResize)
                {
                    image.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(MaximumDimension, MaximumDimension),
                    }));
                }

                using var encodedStream = new MemoryStream();
                image.Save(encodedStream, new JpegEncoder { Quality = JpegQuality });
                byte[] encoded = encodedStream.ToArray();
                LogConsole.WriteLine(
                    $"Candidate: image/jpeg, {image.Width}x{image.Height}, {encoded.Length:N0} bytes.");
                if (encoded.LongLength > Threshold)
                {
                    LogConsole.WriteLine($"Error: Candidate remains above {Threshold:N0} bytes; artwork was not changed.");
                    errors++;
                    continue;
                }

                if (!options.Apply)
                {
                    LogConsole.WriteLine("Would replace the embedded artwork.");
                    fixedArtwork++;
                    continue;
                }

                IArtworkWriter? writer = mediaFile.Tags.OfType<IArtworkWriter>().FirstOrDefault();
                if (writer is null)
                    throw new NotSupportedException("This media format does not support embedded-artwork writes.");

                writer.SetImages([
                    new ArtworkImage(ID3v2Util.APICType.FrontCover, "image/jpeg", string.Empty, encoded),
                ]);
                mediaFile.SaveTags();

                // Reopen the file before touching the ITL cache. A successful write must be
                // readable and contain exactly the single normalized image we requested.
                IMediaFile verificationFile = MediaFile.GetFile(path, readOnly: true);
                IMetadataImage[] verified = [.. verificationFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
                if (verified.Length != 1 || verified[0].Size != encoded.Length)
                    throw new InvalidDataException("Artwork verification after saving the media file failed.");
                using (Image verifiedImage = Image.Load(verified[0].Data))
                {
                    if (verifiedImage.Width > MaximumDimension || verifiedImage.Height > MaximumDimension)
                        throw new InvalidDataException("Saved artwork dimensions exceed the configured maximum.");
                }

                var fileInfo = new FileInfo(path);
                track.SetArtworkCount(verified.Length);
                track.SetSize((ulong)fileInfo.Length);
                track.SetDateModified(fileInfo.LastWriteTimeUtc);
                libraryChanged = true;
                fixedArtwork++;
                LogConsole.WriteLine("Embedded artwork replaced and verified.");
            }
            catch (Exception exception)
            {
                errors++;
                LogConsole.WriteLine($"Problem with file: {path}");
                LogConsole.WriteLine($"{exception.Message} ({exception.GetType().FullName})");
            }
        }

        if (options.Apply && libraryChanged)
        {
            ItlFileEditor.SaveValidated(document, libraryPath);
            LogConsole.WriteLine($"Saved ITL cache updates to '{libraryPath}'.");
            LogConsole.WriteLine($"The previous ITL is retained as '{libraryPath}.bak'.");
        }

        LogConsole.WriteLine();
        LogConsole.WriteLine("Summary:");
        LogConsole.WriteLine($"Total Tracks Processed:         {tracks}");
        LogConsole.WriteLine($"Tracks With Artwork:            {artwork}");
        LogConsole.WriteLine($"Tracks Without Embedded Artwork:{missing,9}");
        LogConsole.WriteLine($"Tracks With Errors:             {errors}");
        LogConsole.WriteLine($"Tracks With Fixed Artwork:      {fixedArtwork}");
        return errors == 0 ? 0 : 1;
    }
}
