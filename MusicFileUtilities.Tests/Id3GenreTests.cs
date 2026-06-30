using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Covers ID3v2Util.GenreMapping's parenthesized ID3v1 genre references, which the
    // standard text-field round-trips don't reach.
    public class Id3GenreTests
    {
        private static byte[] SyncSafe32(int n) =>
            new[] { (byte)((n >> 21) & 0x7f), (byte)((n >> 14) & 0x7f), (byte)((n >> 7) & 0x7f), (byte)(n & 0x7f) };

        // ID3v2.3 frame: plain 32-bit big-endian size.
        private static void AppendTcon(List<byte> dst, string text)
        {
            byte[] data = new byte[] { 0x00 }.Concat(Encoding.ASCII.GetBytes(text)).ToArray(); // ISO-8859-1
            dst.AddRange(Encoding.ASCII.GetBytes("TCON"));
            dst.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
            dst.AddRange(new byte[] { 0, 0 });
            dst.AddRange(data);
        }

        private static string ReadGenre(string tcon)
        {
            var body = new List<byte>();
            AppendTcon(body, tcon);
            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("ID3"));
            file.AddRange(new byte[] { 3, 0, 0 });        // v2.3
            file.AddRange(SyncSafe32(body.Count));
            file.AddRange(body);

            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(tmp, file.ToArray());
            try
            {
                return MediaFile.GetFile(tmp).Tags.First()
                    .GetKnownMetadata().First(kv => kv.Key == TagFields.Genre).Value;
            }
            finally { File.Delete(tmp); }
        }

        [Theory]
        [InlineData("(17)", "Rock")]   // ID3v1 genre #17
        [InlineData("(9)", "Metal")]   // ID3v1 genre #9
        [InlineData("(RX)", "Remix")]  // special reference
        [InlineData("(CR)", "Cover")]  // special reference
        [InlineData("Shoegaze", "Shoegaze")] // plain refinement text passes through
        public void ParenthesizedGenresDecode(string stored, string expected)
        {
            Assert.Equal(expected, ReadGenre(stored));
        }
    }
}
