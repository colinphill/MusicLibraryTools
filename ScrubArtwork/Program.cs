using SkiaSharp;

namespace ScrubArtwork;

internal static class Program
{
    private const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private static readonly int[] EncodeQualities = [75, 70, 60, 50];
    private static readonly SKSamplingOptions ResizeSampling =
        new(new SKCubicResampler(0, 0.5f));

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
        using DecodedImage decoded = Decode(filename);
        bool resize = decoded.Bitmap.Width > maxDimension ||
                      decoded.Bitmap.Height > maxDimension;
        bool transcode = resize || !Path.GetExtension(filename).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || new FileInfo(filename).Length > threshold;

        using SKBitmap? oriented = transcode
            ? NormalizeOrientation(decoded.Bitmap, decoded.Origin)
            : null;
        SKBitmap orientedImage = oriented ?? decoded.Bitmap;
        using SKBitmap? resized = resize
            ? ResizeToFit(orientedImage, maxDimension)
            : null;
        SKBitmap outputImage = resized ?? orientedImage;

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
                using SKBitmap? flattened =
                    FlattenTransparencyForJpeg(outputImage);
                SKBitmap encodingSource =
                    flattened ?? outputImage;
                using SKImage encodedImage = SKImage.FromBitmap(encodingSource) ??
                                             throw new InvalidDataException(
                                                 "The image could not be prepared for encoding.");
                foreach (int quality in EncodeQualities)
                {
                    using SKData candidate =
                        encodedImage.Encode(SKEncodedImageFormat.Jpeg, quality) ??
                        throw new InvalidDataException(
                            "The image could not be encoded as JPEG.");
                    encoded = candidate.ToArray();
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

    private static DecodedImage Decode(string path)
    {
        using SKCodec codec = SKCodec.Create(path) ??
                              throw new InvalidDataException(
                                  "The image format is not recognized.");
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 ||
            info.Height <= 0 ||
            (long)info.Width * info.Height > MaximumDecodedPixels)
            throw new InvalidDataException(
                $"The image dimensions {info.Width}×{info.Height} are invalid or too large.");

        var bitmap = new SKBitmap(info);
        try
        {
            IntPtr pixels = bitmap.GetPixels();
            if (pixels == IntPtr.Zero ||
                codec.GetPixels(bitmap.Info, pixels) != SKCodecResult.Success)
                throw new InvalidDataException(
                    "The image is truncated, corrupt, or could not be decoded.");
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return new(bitmap, codec.EncodedOrigin);
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
                    $"The image uses unsupported orientation '{origin}'.");
        }
    }

    private static SKBitmap ResizeToFit(SKBitmap image, int maximumDimension)
    {
        double scale = Math.Min(
            (double)maximumDimension / image.Width,
            (double)maximumDimension / image.Height);
        var target = new SKSizeI(
            Math.Max(1, (int)Math.Round(
                image.Width * scale,
                MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(
                image.Height * scale,
                MidpointRounding.AwayFromZero)));
        return image.Resize(target, ResizeSampling) ??
               throw new InvalidDataException("The image could not be resized.");
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
