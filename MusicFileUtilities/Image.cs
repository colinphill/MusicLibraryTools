using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace MusicFileUtilities
{
    public class ImageFile
    {
        private static readonly HashSet<byte> standalonemarkers_ = new HashSet<byte>() { 0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xf1 };
        private static readonly HashSet<byte> sofmarkers_ = new HashSet<byte>() { 0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf };

        public enum ImageFormat { Gif, Jpeg, Png, Bmp, Unknown };

        // Span-based so callers can probe an embedded image in place (a slice of the tag's
        // frame/atom buffer) without copying the whole payload out first. byte[] callers
        // convert implicitly.
        public static ImageFormat DetectImageFormat(ReadOnlySpan<byte> b)
        {
            if (b.Length > 10)
            {
                if (b.Slice(6, 5).SequenceEqual("JFIF\x00"u8) || b.Slice(6, 5).SequenceEqual("Exif\x00"u8))
                    return ImageFormat.Jpeg;
            }
            if (b.Length > 6)
            {
                if (b.Slice(0, 6).SequenceEqual("GIF87a"u8) || b.Slice(0, 6).SequenceEqual("GIF89a"u8))
                    return ImageFormat.Gif;
            }
            if ((b.Length > 8) && b.Slice(1, 7).SequenceEqual("PNG\x0d\x0a\x1a\x0a"u8) && (b[0] == 0x89))
                return ImageFormat.Png;
            if ((b.Length > 12) && b.Slice(0, 2).SequenceEqual("BM"u8) && b.Slice(14, 4).SequenceEqual("\x28\x00\x00\x00"u8))
                return ImageFormat.Bmp;
            return ImageFormat.Unknown;
        }

        private static (int Width, int Height) GetGifDimensions(ReadOnlySpan<byte> b)
        {
            int width = b[6] | (b[7] << 8);
            int height = b[8] | (b[9] << 8);
            return (width, height);
        }

        private static (int Width, int Height) GetBmpDimensions(ReadOnlySpan<byte> b)
        {
            int width = b[18] | (b[19] << 8) | (b[20] << 16) | (b[21] << 24);
            int height = b[22] | (b[23] << 8) | (b[24] << 16) | (b[25] << 24);
            return (width, height);
        }

        private static (int Width, int Height) GetPngDimensions(ReadOnlySpan<byte> b)
        {
            int width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            int height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return (width, height);
        }

        private static (int Width, int Height) GetJpegDimensions(ReadOnlySpan<byte> b)
        {
            int offset = 0;
            // Defensive against truncated/corrupt JPEGs: every read is bounds-checked and a
            // segment length < 2 (which would fail to advance the cursor and spin forever)
            // bails out with (0,0) rather than hanging or throwing.
            while (offset + 2 <= b.Length)
            {
                byte m1 = b[offset++];
                byte m2 = b[offset++];
                if (m1 != 0xff)
                    return (0, 0);
                if (standalonemarkers_.Contains(m2))
                    continue;
                if (offset + 2 > b.Length)
                    return (0, 0);
                byte l1 = b[offset++];
                byte l2 = b[offset++];
                int length = (((int)l1) << 8) | (int)l2;
                if (length < 2)
                    return (0, 0);
                int toffset = offset;
                if (sofmarkers_.Contains(m2))
                {
                    if (offset + 5 > b.Length)
                        return (0, 0);
                    byte p = b[offset++];
                    byte y1 = b[offset++];
                    byte y2 = b[offset++];
                    byte x1 = b[offset++];
                    byte x2 = b[offset++];
                    int width = (((int)x1) << 8) | (int)x2;
                    int height = (((int)y1) << 8) | (int)y2;
                    return (width, height);
                }
                offset = toffset + length - 2;
            }
            return (0, 0);
        }

        public static (int Width, int Height) GetImageDimensions(ReadOnlySpan<byte> b)
        {
            switch (DetectImageFormat(b))
            {
                case ImageFormat.Bmp:
                    return GetBmpDimensions(b);

                case ImageFormat.Png:
                    return GetPngDimensions(b);

                case ImageFormat.Jpeg:
                    return GetJpegDimensions(b);

                case ImageFormat.Gif:
                    return GetGifDimensions(b);

                case ImageFormat.Unknown:
                    return (0, 0);

                default:
                    throw new InvalidDataException();
            }
        }

    }
}
