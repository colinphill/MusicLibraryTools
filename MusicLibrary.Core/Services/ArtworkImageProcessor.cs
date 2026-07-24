using SkiaSharp;

namespace MusicLibrary.Core.Services;

/// <summary>Shared Skia image decoding, orientation, resizing, and encoding helpers.</summary>
internal static class ArtworkImageProcessor
{
    private const long MaximumDecodedPixels = 64L * 1024 * 1024;
    private static readonly SKSamplingOptions ResizeSampling =
        new(new SKCubicResampler(0, 0.5f));

    /// <summary>
    /// Decodes an encoded image while retaining the source format and encoded orientation needed
    /// to make later transcodes visually equivalent to the source.
    /// </summary>
    public static DecodedArtwork Decode(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0)
            throw new InvalidDataException("The artwork image data is empty.");

        using SKData encoded = SKData.CreateCopy(source);
        using SKCodec codec = SKCodec.Create(encoded) ??
                              throw new InvalidDataException(
                                  "The artwork image format is not recognized.");
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 ||
            info.Height <= 0 ||
            (long)info.Width * info.Height > MaximumDecodedPixels)
            throw new InvalidDataException(
                $"The artwork image dimensions {info.Width}×{info.Height} are invalid or too large.");

        var bitmap = new SKBitmap(info);
        try
        {
            IntPtr pixels = bitmap.GetPixels();
            if (pixels == IntPtr.Zero ||
                codec.GetPixels(bitmap.Info, pixels) != SKCodecResult.Success)
                throw new InvalidDataException(
                    "The artwork image is truncated, corrupt, or could not be decoded.");

            return new(
                bitmap,
                codec.EncodedFormat,
                codec.EncodedOrigin,
                Math.Max(1, codec.FrameCount));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies an encoded EXIF origin to the pixels. A null result means that the source already
    /// uses the normal top-left orientation and can be used directly.
    /// </summary>
    public static SKBitmap? NormalizeOrientation(
        SKBitmap source,
        SKEncodedOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (origin == SKEncodedOrigin.TopLeft)
            return null;

        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or
            SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or
            SKEncodedOrigin.LeftBottom;
        int width = swapsAxes ? source.Height : source.Width;
        int height = swapsAxes ? source.Width : source.Height;
        var normalized = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            source.ColorSpace));

        try
        {
            using var canvas = new SKCanvas(normalized);
            canvas.Clear(SKColors.Transparent);
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
                        $"The artwork image uses unsupported orientation '{origin}'.");
            }
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

    /// <summary>
    /// Shrinks an image to fit inside a square bounding box without cropping, stretching, or
    /// enlarging it. A null result means that the image already fits and can be used directly.
    /// </summary>
    public static SKBitmap? ResizeToFit(SKBitmap image, int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maximumDimension <= 0 ||
            (image.Width <= maximumDimension && image.Height <= maximumDimension))
            return null;

        double scale = Math.Min(
            (double)maximumDimension / image.Width,
            (double)maximumDimension / image.Height);
        var target = new SKSizeI(
            Math.Max(1, (int)Math.Round(
                image.Width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(
                image.Height * scale, MidpointRounding.AwayFromZero)));
        return image.Resize(target, ResizeSampling) ??
               throw new InvalidDataException("The artwork image could not be resized.");
    }

    /// <summary>Encodes the supplied pixels in one of Skia's supported artwork formats.</summary>
    public static byte[] Encode(
        SKBitmap bitmap,
        SKEncodedImageFormat format,
        int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using SKBitmap? flattened = format == SKEncodedImageFormat.Jpeg
            ? FlattenTransparencyForJpeg(bitmap)
            : null;
        SKBitmap encodingSource = flattened ?? bitmap;
        using SKImage image = SKImage.FromBitmap(encodingSource) ??
                              throw new InvalidDataException(
                                  "The artwork image could not be prepared for encoding.");
        using SKData encoded = image.Encode(format, Math.Clamp(quality, 1, 100)) ??
                               throw new InvalidDataException(
                                   $"The artwork image could not be encoded as {format}.");
        return encoded.ToArray();
    }

    public static (string MimeType, string Extension) FormatDetails(
        SKEncodedImageFormat format) => format switch
        {
            SKEncodedImageFormat.Jpeg => ("image/jpeg", ".jpg"),
            SKEncodedImageFormat.Png => ("image/png", ".png"),
            SKEncodedImageFormat.Gif => ("image/gif", ".gif"),
            SKEncodedImageFormat.Webp => ("image/webp", ".webp"),
            SKEncodedImageFormat.Bmp => ("image/bmp", ".bmp"),
            SKEncodedImageFormat.Ico => ("image/x-icon", ".ico"),
            SKEncodedImageFormat.Wbmp => ("image/vnd.wap.wbmp", ".wbmp"),
            SKEncodedImageFormat.Dng => ("image/x-adobe-dng", ".dng"),
            SKEncodedImageFormat.Heif => ("image/heif", ".heif"),
            SKEncodedImageFormat.Avif => ("image/avif", ".avif"),
            SKEncodedImageFormat.Jpegxl => ("image/jxl", ".jxl"),
            _ => ("application/octet-stream", ".img"),
        };

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

    internal sealed class DecodedArtwork(
        SKBitmap bitmap,
        SKEncodedImageFormat format,
        SKEncodedOrigin origin,
        int frameCount) : IDisposable
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public SKEncodedImageFormat Format { get; } = format;
        public SKEncodedOrigin Origin { get; } = origin;
        public int FrameCount { get; } = frameCount;

        public void Dispose() => Bitmap.Dispose();
    }
}
