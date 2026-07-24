using MusicFileUtilities;
using SkiaSharp;

namespace ArtworkScrubber;

internal static class Program
{
    private const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private static readonly SKSamplingOptions ResizeSampling =
        new(new SKCubicResampler(0, 0.5f));

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
        if (args.Length is < 1 or > 4 ||
            (args.Length > 1 && !int.TryParse(args[1], out _)) ||
            (args.Length > 2 && !int.TryParse(args[2], out _)) ||
            (args.Length > 3 && !int.TryParse(args[3], out _)))
        {
            Console.Error.WriteLine(
                "Usage: ArtworkScrubber <results.csv> [max-dimension=600] [jpeg-quality=75] [parallelism=16]");
            return 2;
        }

        string csv = Path.GetFullPath(args[0]);
        int maxDimension = args.Length > 1 ? int.Parse(args[1]) : 600;
        int quality = args.Length > 2 ? int.Parse(args[2]) : 75;
        int parallelism = args.Length > 3 ? int.Parse(args[3]) : 16;
        if (!File.Exists(csv) || maxDimension <= 0 || quality is < 1 or > 100 || parallelism is < 1 or > 64)
        {
            Console.Error.WriteLine(
                "The CSV must exist, max dimension must be positive, quality must be 1-100, and parallelism must be 1-64.");
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

        // Different album folders are independent, so overlap their opens and metadata reads on
        // high-latency shares. Files in the same folder deliberately stay ordered: the previous
        // behavior allowed each usable image to replace folder.jpg, leaving the last one in place.
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var folderGroups = files.GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty, pathComparer);
        Parallel.ForEach(folderGroups, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, group =>
        {
            foreach (string file in group)
            {
                try
                {
                    var provider = MediaFile.GetFile(file, readOnly: true).Tags.First();
                    var artwork = provider.GetImageMetadata().FirstOrDefault();
                    if (artwork is null || artwork.Data.Length == 0)
                        continue;

                    using DecodedImage decoded = Decode(artwork.Data);
                    using SKBitmap? oriented = NormalizeOrientation(
                        decoded.Bitmap,
                        decoded.Origin);
                    SKBitmap orientedImage = oriented ?? decoded.Bitmap;
                    using SKBitmap? resized = ResizeToFit(
                        orientedImage,
                        maxDimension);
                    SKBitmap outputImage = resized ?? orientedImage;

                    string output = Path.Combine(Path.GetDirectoryName(file)!, "folder.jpg");
                    string temp = Path.Combine(Path.GetDirectoryName(output)!, $".folder.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        EncodeJpeg(outputImage, temp, quality);
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
                    Interlocked.Increment(ref failures);
                    Console.Error.WriteLine($"{file}: {ex.Message}");
                }
            }
        });

        return failures == 0 ? 0 : 1;
    }

    private static DecodedImage Decode(byte[] data)
    {
        using SKData encoded = SKData.CreateCopy(data);
        using SKCodec codec = SKCodec.Create(encoded) ??
                              throw new InvalidDataException(
                                  "The embedded artwork image format is not recognized.");
        SKBitmap bitmap = DecodeBitmap(codec, "embedded artwork");
        return new(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap DecodeBitmap(
        SKCodec codec,
        string description)
    {
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 ||
            info.Height <= 0 ||
            (long)info.Width * info.Height > MaximumDecodedPixels)
            throw new InvalidDataException(
                $"The {description} dimensions {info.Width}×{info.Height} are invalid or too large.");

        var bitmap = new SKBitmap(info);
        try
        {
            IntPtr pixels = bitmap.GetPixels();
            if (pixels == IntPtr.Zero ||
                codec.GetPixels(bitmap.Info, pixels) != SKCodecResult.Success)
                throw new InvalidDataException(
                    $"The {description} is truncated, corrupt, or could not be decoded.");
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap? NormalizeOrientation(
        SKBitmap source,
        SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return null;

        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or
            SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or
            SKEncodedOrigin.LeftBottom;
        var normalized = new SKBitmap(new SKImageInfo(
            swapsAxes ? source.Height : source.Width,
            swapsAxes ? source.Width : source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            source.ColorSpace));
        try
        {
            using var canvas = new SKCanvas(normalized);
            canvas.Clear(SKColors.Transparent);
            ApplyOrientationTransform(canvas, source, origin);
            canvas.DrawBitmap(
                source,
                0,
                0,
                new SKSamplingOptions(SKFilterMode.Nearest),
                null);
            canvas.Flush();
            return normalized;
        }
        catch
        {
            normalized.Dispose();
            throw;
        }
    }

    private static void ApplyOrientationTransform(
        SKCanvas canvas,
        SKBitmap source,
        SKEncodedOrigin origin)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(source.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(source.Width, source.Height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, source.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(source.Height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(source.Height, source.Width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, source.Width);
                canvas.RotateDegrees(-90);
                break;
            default:
                throw new InvalidDataException(
                    $"The embedded artwork uses unsupported orientation '{origin}'.");
        }
    }

    private static SKBitmap? ResizeToFit(SKBitmap image, int maximumDimension)
    {
        if (image.Width <= maximumDimension && image.Height <= maximumDimension)
            return null;

        SKSizeI target = FitSize(image.Width, image.Height, maximumDimension);
        return image.Resize(target, ResizeSampling) ??
               throw new InvalidDataException("The embedded artwork could not be resized.");
    }

    private static SKSizeI FitSize(int width, int height, int maximumDimension)
    {
        double scale = Math.Min(
            (double)maximumDimension / width,
            (double)maximumDimension / height);
        return new(
            Math.Max(1, (int)Math.Round(
                width * scale,
                MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(
                height * scale,
                MidpointRounding.AwayFromZero)));
    }

    private static void EncodeJpeg(SKBitmap bitmap, string path, int quality)
    {
        using SKBitmap? flattened = FlattenTransparencyForJpeg(bitmap);
        SKBitmap encodingSource = flattened ?? bitmap;
        using SKImage image = SKImage.FromBitmap(encodingSource) ??
                              throw new InvalidDataException(
                                  "The embedded artwork could not be prepared for encoding.");
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality) ??
                               throw new InvalidDataException(
                                   "The embedded artwork could not be encoded as JPEG.");
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoded.SaveTo(stream);
    }

    private static SKBitmap? FlattenTransparencyForJpeg(SKBitmap source)
    {
        if (source.AlphaType == SKAlphaType.Opaque)
            return null;

        var flattened = new SKBitmap(new SKImageInfo(
            source.Width,
            source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque,
            source.ColorSpace));
        try
        {
            using var canvas = new SKCanvas(flattened);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                source,
                0,
                0,
                new SKSamplingOptions(SKFilterMode.Nearest),
                null);
            canvas.Flush();
            return flattened;
        }
        catch
        {
            flattened.Dispose();
            throw;
        }
    }

    private sealed class DecodedImage(
        SKBitmap bitmap,
        SKEncodedOrigin origin) : IDisposable
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public SKEncodedOrigin Origin { get; } = origin;

        public void Dispose() => Bitmap.Dispose();
    }
}
