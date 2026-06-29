using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class ImageFileTests
    {
        private static byte[] BuildPng(uint width, uint height)
        {
            var b = new byte[24];
            // PNG signature
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            sig.CopyTo(b, 0);
            // IHDR length + type
            b[8] = 0; b[9] = 0; b[10] = 0; b[11] = 0x0D;
            b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
            // width (BE) at 16, height (BE) at 20
            b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
            b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
            return b;
        }

        private static byte[] BuildGif(ushort width, ushort height)
        {
            var b = new byte[10];
            foreach (var (i, c) in "GIF89a".Select((c, i) => (i, c))) b[i] = (byte)c;
            b[6] = (byte)width; b[7] = (byte)(width >> 8);    // LE
            b[8] = (byte)height; b[9] = (byte)(height >> 8);  // LE
            return b;
        }

        private static byte[] BuildBmp(uint width, uint height)
        {
            var b = new byte[26];
            b[0] = (byte)'B'; b[1] = (byte)'M';
            b[14] = 0x28; // BITMAPINFOHEADER size
            b[18] = (byte)width; b[19] = (byte)(width >> 8); b[20] = (byte)(width >> 16); b[21] = (byte)(width >> 24);
            b[22] = (byte)height; b[23] = (byte)(height >> 8); b[24] = (byte)(height >> 16); b[25] = (byte)(height >> 24);
            return b;
        }

        private static byte[] BuildJpeg(ushort width, ushort height)
        {
            return new byte[]
            {
                0xFF, 0xD8,                                     // SOI
                0xFF, 0xE0, 0x00, 0x10,                          // APP0, length 16
                (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
                0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11,                          // SOF0, length 17
                0x08,                                            // precision
                (byte)(height >> 8), (byte)height,
                (byte)(width >> 8), (byte)width,
                0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01
            };
        }

        [Fact]
        public void DetectsPngAndReadsDimensions()
        {
            var png = BuildPng(320, 240);
            Assert.Equal(ImageFile.ImageFormat.Png, ImageFile.DetectImageFormat(png));
            Assert.Equal((320, 240), ImageFile.GetImageDimensions(png));
        }

        [Fact]
        public void DetectsGifAndReadsDimensions()
        {
            var gif = BuildGif(640, 480);
            Assert.Equal(ImageFile.ImageFormat.Gif, ImageFile.DetectImageFormat(gif));
            Assert.Equal((640, 480), ImageFile.GetImageDimensions(gif));
        }

        [Fact]
        public void DetectsBmpAndReadsDimensions()
        {
            var bmp = BuildBmp(128, 64);
            Assert.Equal(ImageFile.ImageFormat.Bmp, ImageFile.DetectImageFormat(bmp));
            Assert.Equal((128, 64), ImageFile.GetImageDimensions(bmp));
        }

        [Fact]
        public void DetectsJpegAndReadsDimensions()
        {
            var jpg = BuildJpeg(200, 100);
            Assert.Equal(ImageFile.ImageFormat.Jpeg, ImageFile.DetectImageFormat(jpg));
            Assert.Equal((200, 100), ImageFile.GetImageDimensions(jpg));
        }

        [Fact]
        public void UnknownFormatReportedAsUnknownWithZeroDimensions()
        {
            var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
            Assert.Equal(ImageFile.ImageFormat.Unknown, ImageFile.DetectImageFormat(junk));
            Assert.Equal((0, 0), ImageFile.GetImageDimensions(junk));
        }
    }
}
