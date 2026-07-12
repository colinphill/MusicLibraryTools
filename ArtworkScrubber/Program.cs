using MusicFileUtilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ArtworkScrubber;

internal static class Program
{
    private static IReadOnlyList<string> SplitCsv(string input)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < input.Length; index++)
        {
            char value = input[index];
            if (value == '"')
            {
                if (quoted && index + 1 < input.Length && input[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                    quoted = !quoted;
            }
            else if (value == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
                field.Append(value);
        }
        if (quoted)
            throw new FormatException("Unterminated quoted CSV field.");
        fields.Add(field.ToString());
        return fields;
    }

    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 3 ||
            (args.Length > 1 && !int.TryParse(args[1], out _)) ||
            (args.Length > 2 && !int.TryParse(args[2], out _)))
        {
            Console.Error.WriteLine("Usage: ArtworkScrubber <results.csv> [max-dimension=600] [jpeg-quality=75]");
            return 2;
        }

        string csv = Path.GetFullPath(args[0]);
        int maxDimension = args.Length > 1 ? int.Parse(args[1]) : 600;
        int quality = args.Length > 2 ? int.Parse(args[2]) : 75;
        if (!File.Exists(csv) || maxDimension <= 0 || quality is < 1 or > 100)
        {
            Console.Error.WriteLine("The CSV must exist, max dimension must be positive, and quality must be 1-100.");
            return 2;
        }

        int failures = 0;
        var files = new List<string>();
        int lineNumber = 1;
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            lineNumber++;
            try
            {
                var parts = SplitCsv(line).Skip(2).Take(2).ToArray();
                if (parts.Length == 2)
                    files.Add(Path.Combine(parts));
                else
                    throw new FormatException("Expected at least four CSV columns.");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"CSV line {lineNumber}: {ex.Message}");
            }
        }

        foreach (var file in files)
        {
            try
            {
                var provider = MediaFile.GetFile(file).Tags.First();
                var artwork = provider.GetImageMetadata().FirstOrDefault();
                if (artwork is null || artwork.Data.Length == 0)
                    continue;

                using var image = Image.Load(artwork.Data);
                if (image.Width > maxDimension || image.Height > maxDimension)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(maxDimension, maxDimension),
                    }));
                }

                string output = Path.Combine(Path.GetDirectoryName(file)!, "folder.jpg");
                string temp = Path.Combine(Path.GetDirectoryName(output)!, $".folder.{Guid.NewGuid():N}.tmp");
                try
                {
                    image.Save(temp, new JpegEncoder { Quality = quality });
                    File.Move(temp, output, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"{file}: {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
