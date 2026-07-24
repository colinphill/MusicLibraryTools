using System.Buffers.Binary;
using SkiaSharp;

namespace MusicLibrary.Core.Tests;

internal static class TestImageFactory
{
    public static byte[] Png(
        int width,
        int height,
        SKColor color) =>
        Encode(width, height, color, SKEncodedImageFormat.Png);

    public static byte[] Jpeg(
        int width,
        int height,
        SKColor color,
        int quality = 90) =>
        Encode(width, height, color, SKEncodedImageFormat.Jpeg, quality);

    public static byte[] Webp(
        int width,
        int height,
        SKColor color,
        int quality = 100) =>
        Encode(width, height, color, SKEncodedImageFormat.Webp, quality);

    public static byte[] QuadrantPng(
        int width,
        int height)
    {
        using SKBitmap bitmap = NewBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(
                    x,
                    y,
                    (x < width / 2, y < height / 2) switch
                    {
                        (true, true) => SKColors.Red,
                        (false, true) => SKColors.Lime,
                        (true, false) => SKColors.Blue,
                        _ => SKColors.Yellow,
                    });
            }
        }

        return Encode(bitmap, SKEncodedImageFormat.Png, 100);
    }

    public static byte[] Orientation6Jpeg()
    {
        using SKBitmap bitmap = NewBitmap(4, 2);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(
                    x,
                    y,
                    x < bitmap.Width / 2
                        ? SKColors.Red
                        : SKColors.Blue);
            }
        }

        byte[] jpeg = Encode(
            bitmap,
            SKEncodedImageFormat.Jpeg,
            100);
        byte[] exif =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, // little-endian TIFF header
            0x08, 0x00, 0x00, 0x00, // first IFD offset
            0x01, 0x00,             // one IFD entry
            0x12, 0x01,             // orientation tag
            0x03, 0x00,             // SHORT
            0x01, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00, // RightTop / rotate 90 degrees clockwise
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        int segmentLength = checked(exif.Length + 2);
        byte[] result = new byte[
            checked(jpeg.Length + exif.Length + 4)];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(
            result.AsSpan(4),
            checked((ushort)segmentLength));
        exif.CopyTo(result.AsSpan(6));
        jpeg.AsSpan(2).CopyTo(
            result.AsSpan(
                6 + exif.Length));
        return result;
    }

    public static byte[] AlphaBandsPng()
    {
        using SKBitmap bitmap = NewBitmap(
            width: 96,
            height: 32);
        for (var y = 0;
             y < bitmap.Height;
             y++)
        {
            for (var x = 0;
                 x < bitmap.Width;
                 x++)
            {
                bitmap.SetPixel(
                    x,
                    y,
                    x < 32
                        ? SKColors.Transparent
                        : x < 64
                            ? new SKColor(
                                255,
                                0,
                                0,
                                128)
                            : SKColors.Blue);
            }
        }

        return Encode(
            bitmap,
            SKEncodedImageFormat.Png,
            100);
    }

    public static byte[] StaticGif2x1()
    {
        // Two pixels (red, green), deliberately hand-authored so codec tests do not use the
        // production image library to create their input.
        return
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // GIF89a
            0x02, 0x00, 0x01, 0x00,             // logical width/height
            0x80, 0x00, 0x00,                   // two-entry global color table
            0xFF, 0x00, 0x00,                   // red
            0x00, 0xFF, 0x00,                   // green
            0x2C,                               // image descriptor
            0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x01, 0x00,
            0x00,
            0x02,                               // LZW minimum code size
            0x02, 0x44, 0x0A,                   // clear, red, green, end
            0x00,
            0x3B,                               // trailer
        ];
    }

    public static byte[] AnimatedGif2x1()
    {
        // Two complete 2x1 image descriptors over the same global palette. The frame payloads
        // intentionally stay tiny and independent of Skia so FrameCount exercises the decoder.
        return
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
            0x02, 0x00, 0x01, 0x00,
            0x80, 0x00, 0x00,
            0xFF, 0x00, 0x00,
            0x00, 0xFF, 0x00,
            0x2C,
            0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x01, 0x00,
            0x00,
            0x02,
            0x02, 0x44, 0x0A,
            0x00,
            0x2C,
            0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x01, 0x00,
            0x00,
            0x02,
            0x02, 0x4C, 0x0A,
            0x00,
            0x3B,
        ];
    }

    public static byte[] BaselineTiff(
        int width,
        int height)
    {
        const int entryCount = 10;
        const int ifdOffset = 8;
        const int pixelOffset =
            ifdOffset + 2 + entryCount * 12 + 4;
        int pixelBytes = checked(width * height);
        byte[] result =
            new byte[checked(pixelOffset + pixelBytes)];
        result[0] = (byte)'I';
        result[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(2),
            42);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(4),
            ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(ifdOffset),
            entryCount);

        int entryOffset = ifdOffset + 2;
        WriteTiffEntry(result, ref entryOffset, 256, 4, (uint)width);
        WriteTiffEntry(result, ref entryOffset, 257, 4, (uint)height);
        WriteTiffEntry(result, ref entryOffset, 258, 3, 8);
        WriteTiffEntry(result, ref entryOffset, 259, 3, 1);
        WriteTiffEntry(result, ref entryOffset, 262, 3, 1);
        WriteTiffEntry(result, ref entryOffset, 273, 4, pixelOffset);
        WriteTiffEntry(result, ref entryOffset, 277, 3, 1);
        WriteTiffEntry(result, ref entryOffset, 278, 4, (uint)height);
        WriteTiffEntry(result, ref entryOffset, 279, 4, (uint)pixelBytes);
        WriteTiffEntry(result, ref entryOffset, 284, 3, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(entryOffset),
            0);
        result.AsSpan(pixelOffset).Fill(0x80);
        return result;
    }

    private static void WriteTiffEntry(
        Span<byte> destination,
        ref int offset,
        ushort tag,
        ushort type,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[offset..],
            tag);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[(offset + 2)..],
            type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[(offset + 4)..],
            1);
        if (type == 3)
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[(offset + 8)..],
                checked((ushort)value));
        else
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination[(offset + 8)..],
                value);
        offset += 12;
    }

    public static byte[] Bmp(
        int width,
        int height)
    {
        int rowBytes = checked((width * 3 + 3) & ~3);
        int pixelBytes = checked(rowBytes * height);
        byte[] result = new byte[checked(54 + pixelBytes)];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(34), pixelBytes);

        for (var y = 0; y < height; y++)
        {
            int encodedY = height - y - 1;
            for (var x = 0; x < width; x++)
            {
                SKColor color = x < width / 2
                    ? SKColors.Red
                    : SKColors.Blue;
                int offset = 54 + encodedY * rowBytes + x * 3;
                result[offset] = color.Blue;
                result[offset + 1] = color.Green;
                result[offset + 2] = color.Red;
            }
        }

        return result;
    }

    public static SKBitmap Decode(
        byte[] data) =>
        SKBitmap.Decode(data) ??
        throw new InvalidDataException(
            "SkiaSharp could not decode the test image.");

    public static (int Width, int Height) Dimensions(
        byte[] data)
    {
        using SKBitmap bitmap = Decode(data);
        return (bitmap.Width, bitmap.Height);
    }

    public static string WriteTemporaryPng(
        int width,
        int height,
        SKColor color)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "img_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Png(width, height, color));
        return path;
    }

    private static byte[] Encode(
        int width,
        int height,
        SKColor color,
        SKEncodedImageFormat format,
        int quality = 100)
    {
        using SKBitmap bitmap = NewBitmap(width, height);
        bitmap.Erase(color);
        return Encode(bitmap, format, quality);
    }

    private static byte[] Encode(
        SKBitmap bitmap,
        SKEncodedImageFormat format,
        int quality)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, quality) ??
            throw new InvalidOperationException(
                $"SkiaSharp could not encode {format} test data.");
        return data.ToArray();
    }

    private static SKBitmap NewBitmap(
        int width,
        int height) =>
        new(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
}
