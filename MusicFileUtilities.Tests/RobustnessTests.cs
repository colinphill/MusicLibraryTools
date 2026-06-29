using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Regression tests for the malformed/atypical-input hardening. Most build the on-disk
    // structures in memory so no extra fixtures are needed.
    public class RobustnessTests
    {
        // ---- Vorbis comments ---------------------------------------------------------------

        private static byte[] BuildVorbis(string vendor, params string[] comments)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms); // BinaryWriter is little-endian, matching Vorbis
            var v = Encoding.UTF8.GetBytes(vendor);
            bw.Write(v.Length);
            bw.Write(v);
            bw.Write(comments.Length);
            foreach (var c in comments)
            {
                var b = Encoding.UTF8.GetBytes(c);
                bw.Write(b.Length);
                bw.Write(b);
            }
            bw.Flush();
            return ms.ToArray();
        }

        [Fact]
        public void VorbisCommentWithoutEqualsIsSkipped()
        {
            var vc = new VorbisComments(BuildVorbis("vendor", "NOEQUALS", "TITLE=Hello", "ARTIST=World"));
            Assert.Equal("Hello", vc["TITLE"]);
            Assert.Equal("World", vc["ARTIST"]);
            Assert.DoesNotContain(vc.Comments, c => c.Key == "NOEQUALS");
        }

        [Fact]
        public void VorbisCommentWithOversizedLengthStopsCleanly()
        {
            // Hand-build a block whose declared comment length runs past the buffer.
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var v = Encoding.UTF8.GetBytes("v");
            bw.Write(v.Length); bw.Write(v);
            bw.Write(1);              // one comment
            bw.Write(int.MaxValue);   // bogus length, no data follows
            bw.Flush();

            var ex = Record.Exception(() => new VorbisComments(ms.ToArray()));
            Assert.Null(ex);
        }

        [Fact]
        public void MalformedEmbeddedPictureIsSkippedNotFatal()
        {
            // METADATA_BLOCK_PICTURE whose base64 decodes to a too-short block (mimelen=100).
            string pic = "METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(new byte[] { 0, 0, 0, 3, 0, 0, 0, 100 });
            var vc = new VorbisComments(BuildVorbis("v", pic, "TITLE=ok"));
            Assert.Empty(vc.Artworks);
            Assert.Equal("ok", vc["TITLE"]);
        }

        [Fact]
        public void VorbisArtworkRejectsOutOfBoundsLengths()
        {
            Assert.Throws<InvalidDataException>(() => new VorbisArtwork(new byte[] { 0, 0, 0, 3, 0, 0, 0, 100 }));
        }

        // ---- APE ---------------------------------------------------------------------------

        [Fact]
        public void ApeReadTagSurvivesCorruptItemLength()
        {
            var t = new APETag();
            t.SetField(TagFields.Title, "Hi");
            byte[] bytes = t.ToByteArray();
            // First item begins right after the 32-byte header; clobber its value length.
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 32);

            var t2 = new APETag();
            var ex = Record.Exception(() => t2.ReadTag(new MemoryStream(bytes)));
            Assert.Null(ex);
            Assert.DoesNotContain(t2.GetKnownMetadata(), kv => kv.Key == TagFields.Title);
        }

        [Fact]
        public void ApeReadTagRejectsBogusTagSize()
        {
            // Valid preamble at the end, but a footer size field larger than the stream.
            byte[] buf = new byte[64];
            Encoding.ASCII.GetBytes("APETAGEX").CopyTo(buf, 32); // footer preamble at end-32
            BitConverter.GetBytes(int.MaxValue).CopyTo(buf, 36); // size field
            var t = new APETag();
            bool ok = false;
            var ex = Record.Exception(() => ok = t.ReadTag(new MemoryStream(buf)));
            Assert.Null(ex);
            Assert.False(ok);
        }

        // ---- JPEG dimension parsing --------------------------------------------------------

        private static byte[] JpegPrefix() => new byte[]
        {
            0xFF, 0xD8,                                       // SOI
            0xFF, 0xE0, 0x00, 0x10,                            // APP0, length 16
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,  // JFIF\0 -> detected as JPEG
            0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
        };

        [Fact]
        public void MalformedJpegWithZeroLengthSegmentDoesNotHang()
        {
            // A SOF marker with length 0 would never advance the cursor (old infinite loop).
            var bad = JpegPrefix().Concat(new byte[] { 0xFF, 0xC0, 0x00, 0x00 }).ToArray();
            Assert.Equal((0, 0), ImageFile.GetImageDimensions(bad));
        }

        [Fact]
        public void TruncatedJpegSofReturnsZero()
        {
            var bad = JpegPrefix().Concat(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 }).ToArray(); // claims 17 bytes, has 1
            Assert.Equal((0, 0), ImageFile.GetImageDimensions(bad));
        }

        // ---- ID3 empty / single-byte frames ------------------------------------------------

        private static byte[] Be32(int n) =>
            new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };

        private static byte[] SyncSafe32(int n) =>
            new[] { (byte)((n >> 21) & 0x7f), (byte)((n >> 14) & 0x7f), (byte)((n >> 7) & 0x7f), (byte)(n & 0x7f) };

        private static void AppendFrame(List<byte> dst, string id, byte[] data)
        {
            dst.AddRange(Encoding.ASCII.GetBytes(id));
            dst.AddRange(Be32(data.Length)); // ID3v2.3 frame sizes are plain 32-bit
            dst.AddRange(new byte[] { 0, 0 }); // flags
            dst.AddRange(data);
        }

        [Fact]
        public void Mp3WithEmptyAndSingleByteTextFramesReadsWithoutThrowing()
        {
            var body = new List<byte>();
            AppendFrame(body, "TIT2", Array.Empty<byte>());                                  // zero-length frame
            AppendFrame(body, "TPE1", new byte[] { 0x00 });                                  // encoding byte only, no text
            AppendFrame(body, "TALB", new byte[] { 0x00 }.Concat(Encoding.ASCII.GetBytes("RealAlbum")).ToArray());

            var header = new List<byte>();
            header.AddRange(Encoding.ASCII.GetBytes("ID3"));
            header.AddRange(new byte[] { 3, 0, 0 });   // v2.3, no flags
            header.AddRange(SyncSafe32(body.Count));
            byte[] file = header.Concat(body).ToArray();

            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(tmp, file);
            try
            {
                var mf = MediaFile.GetFile(tmp);
                var tags = mf.Tags.First();
                Assert.Equal("", tags.Title);
                Assert.Equal("", tags.Artist);
                Assert.Equal("RealAlbum", tags.Album);
            }
            finally { File.Delete(tmp); }
        }

        // ---- MP4 without an iTunes metadata list -------------------------------------------

        private static uint ReadBe32(byte[] b, int p) =>
            ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];

        private static void WriteBe32(byte[] b, int p, uint v)
        {
            b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v;
        }

        // Removes the moov.udta atom (which holds meta/ilst). Safe without chunk-offset fixups
        // because ffmpeg places mdat before moov, so audio offsets don't move.
        private static byte[] StripUdta(byte[] mp4)
        {
            int pos = 0;
            while (pos + 8 <= mp4.Length)
            {
                uint size = ReadBe32(mp4, pos);
                string type = Encoding.ASCII.GetString(mp4, pos + 4, 4);
                if (size < 8) break;
                if (type == "moov")
                {
                    int mpos = pos + 8;
                    int moovEnd = pos + (int)size;
                    while (mpos + 8 <= moovEnd)
                    {
                        uint ssize = ReadBe32(mp4, mpos);
                        string stype = Encoding.ASCII.GetString(mp4, mpos + 4, 4);
                        if (ssize < 8) break;
                        if (stype == "udta")
                        {
                            var outBytes = new byte[mp4.Length - (int)ssize];
                            Array.Copy(mp4, 0, outBytes, 0, mpos);
                            Array.Copy(mp4, mpos + (int)ssize, outBytes, mpos, mp4.Length - (mpos + (int)ssize));
                            WriteBe32(outBytes, pos, size - ssize); // shrink moov
                            return outBytes;
                        }
                        mpos += (int)ssize;
                    }
                }
                pos += (int)size;
            }
            return mp4;
        }

        [Fact]
        public void Mp4WithoutItunesMetadataOpensAndReadsAsEmpty()
        {
            byte[] original = File.ReadAllBytes(MediaFixtures.Path_("sample_aac.m4a"));
            byte[] stripped = StripUdta(original);
            Assert.NotEqual(original.Length, stripped.Length); // sanity: udta really removed

            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".m4a");
            File.WriteAllBytes(tmp, stripped);
            try
            {
                var mf = MediaFile.GetFile(tmp);            // must not NRE in the ctor
                Assert.Empty(mf.Tags.First().GetKnownMetadata());
                Assert.Equal(44100u, mf.Codecs.First().Samplerate); // audio offsets intact
            }
            finally { File.Delete(tmp); }
        }
    }
}
