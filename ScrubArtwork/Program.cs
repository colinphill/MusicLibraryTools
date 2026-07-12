using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ScrubArtwork;

internal static class Program
{
    private static readonly int[] EncodeQualities = [75, 70, 60, 50];

    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 4 ||
            (args.Length > 2 && (!int.TryParse(args[2], out _) || int.Parse(args[2]) <= 0)) ||
            (args.Length > 3 && (!int.TryParse(args[3], out _) || int.Parse(args[3]) <= 0)))
        {
            Console.Error.WriteLine("Usage: ScrubArtwork <source-folder> <output-folder> [max-dimension=800] [threshold-kb=225]");
            return 2;
        }

        string source = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        int maxDimension = args.Length > 2 ? int.Parse(args[2]) : 800;
        long threshold = 1024L * (args.Length > 3 ? int.Parse(args[3]) : 225);
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine("Source folder does not exist.");
            return 2;
        }
        Directory.CreateDirectory(output);

        var plan = Directory.EnumerateFiles(source)
            .Select(file => (Source: file, Destination: Path.Combine(
                output, Path.GetFileNameWithoutExtension(file) + ".jpg")))
            .ToList();
        var collisions = plan
            .GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();
        if (collisions.Count != 0)
        {
            foreach (var collision in collisions)
                Console.Error.WriteLine($"Output collision at {collision.Key}: {string.Join(", ", collision.Select(item => item.Source))}");
            return 1;
        }

        int failures = 0;
        foreach (var item in plan)
        {
            try
            {
                Process(item.Source, item.Destination, maxDimension, threshold);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"{item.Source}: {ex.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Process(string filename, string destination, int maxDimension, long threshold)
    {
        Console.WriteLine(filename);
        using var image = Image.Load(filename);
        bool resize = image.Width > maxDimension || image.Height > maxDimension;
        bool transcode = resize || !Path.GetExtension(filename).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || new FileInfo(filename).Length > threshold;

        if (resize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxDimension, maxDimension),
            }));
        }

        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (!transcode)
            {
                File.Copy(filename, temp);
            }
            else
            {
                byte[]? encoded = null;
                int usedQuality = EncodeQualities[^1];
                foreach (int quality in EncodeQualities)
                {
                    using var ms = new MemoryStream();
                    image.Save(ms, new JpegEncoder { Quality = quality });
                    encoded = ms.ToArray();
                    usedQuality = quality;
                    if (encoded.LongLength <= threshold)
                        break;
                }
                File.WriteAllBytes(temp, encoded!);
                Console.WriteLine(encoded!.LongLength <= threshold
                    ? $"Encoded at quality {usedQuality}."
                    : $"Warning: result remains above {threshold:N0} bytes at quality {usedQuality}.");
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
