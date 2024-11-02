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

        public static ImageFormat DetectImageFormat(byte[] b)
        {
            if (b.Length > 10)
            {
                string idstring = Encoding.ASCII.GetString(b, 6, 5);
                if ((idstring == "JFIF\x00") || (idstring == "Exif\x00"))
                    return ImageFormat.Jpeg;
            }
            if (b.Length > 6)
            {
                string gifstring = Encoding.ASCII.GetString(b, 0, 6);
                if ((gifstring == "GIF87a") || (gifstring == "GIF89a"))
                    return ImageFormat.Gif;
            }
            if ((b.Length > 8) && (Encoding.ASCII.GetString(b, 1, 7) == "PNG\x0d\x0a\x1a\x0a") && (b[0] == 0x89))
                return ImageFormat.Png;
            if ((b.Length > 12) && (Encoding.ASCII.GetString(b, 0, 2) == "BM") && (Encoding.ASCII.GetString(b, 14, 4) == "\x28\x00\x00\x00"))
                return ImageFormat.Bmp;
            return ImageFormat.Unknown;
        }

        private static (int Width, int Height) GetGifDimensions(byte[] b)
        {
            int width = Tools.UInt16AtLE(b, 6);
            int height = Tools.UInt16AtLE(b, 8);
            return (width, height);
        }

        private static (int Width, int Height) GetBmpDimensions(byte[] b)
        {
            int width = (int)Tools.UInt32AtLE(b, 18);
            int height = (int)Tools.UInt32AtLE(b, 22);
            return (width, height);
        }

        private static (int Width, int Height) GetPngDimensions(byte[] b)
        {
            int width = (int)Tools.UInt32AtBE(b, 16);
            int height = (int)Tools.UInt32AtBE(b, 20);
            return (width, height);
        }

        private static (int Width, int Height) GetJpegDimensions(byte[] b)
        {
            int offset = 0;
            while (offset < b.Length)
            {
                byte m1 = b[offset++];
                byte m2 = b[offset++];
                if (m1 != 0xff)
                    throw new InvalidDataException();
                if (standalonemarkers_.Contains(m2))
                    continue;
                byte l1 = b[offset++];
                byte l2 = b[offset++];
                int length = (((int)l1) << 8) | (int)l2;
                int toffset = offset;
                if (sofmarkers_.Contains(m2))
                {
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
            throw new InvalidDataException();
        }

        public static (int Width, int Height) GetImageDimensions(byte[] b)
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
