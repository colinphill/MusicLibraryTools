using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Regression coverage for the MP4 in-place save fast path (RootAtom.TrySaveInPlace):
    // tag edits must persist WITHOUT copying or moving the audio payload, and the result must
    // remain a valid, decodable container. The ffmpeg-decode checks are the non-negotiable
    // guard learned from the Ogg SETUP-header data-loss bug: our own parser round-tripping a
    // file it wrote is NOT proof the file is still valid to a real decoder.
    public class Mp4InPlaceSaveTests
    {
        public static IEnumerable<object[]> Mp4Files => new[]
        {
            new object[] { "sample_alac.m4a" },  // lossless
            new object[] { "sample_aac.m4a" },   // lossy
        };

        private static Dictionary<TagFields, string> Read(string path)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var t in MediaFile.GetFile(path).Tags)
                foreach (var kv in t.GetKnownMetadata())
                    d[kv.Key] = kv.Value;
            return d;
        }

        // ---- fast path fired, audio untouched, still decodes ---------------------------------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void SetFieldTakesInPlacePathAndPreservesAudio(string file)
        {
            using var tmp = MediaFixtures.Copy(file);
            byte[] audioBefore = ReadMdat(tmp.Path);
            byte[] pcmBefore = Ffmpeg.DecodePcm(tmp.Path);

            var mp4 = (MP4File)MediaFile.GetFile(tmp.Path);
            mp4.SetField(TagFields.Title, "Rewritten In Place");
            mp4.Save();

            Assert.True(mp4.LastSaveWasInPlace, "expected the in-place fast path to fire (moov is last)");
            Assert.Equal("Rewritten In Place", Read(tmp.Path)[TagFields.Title]);
            // The audio blob must be byte-for-byte identical: it was never copied or moved.
            Assert.Equal(audioBefore, ReadMdat(tmp.Path));
            // And a real decoder must still produce the same PCM.
            Assert.Equal(pcmBefore, Ffmpeg.DecodePcm(tmp.Path));
        }

        // ---- large growth (artwork): tail extends, audio still intact ------------------------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void AddingArtworkGrowsTailWithoutTouchingAudio(string file)
        {
            using var tmp = MediaFixtures.Copy(file);
            byte[] audioBefore = ReadMdat(tmp.Path);
            byte[] pcmBefore = Ffmpeg.DecodePcm(tmp.Path);
            long lenBefore = new FileInfo(tmp.Path).Length;

            byte[] jpeg = Ffmpeg.EncodeJpeg(256, 256);  // a real, decodable cover image
            var mp4 = (MP4File)MediaFile.GetFile(tmp.Path);
            ((IArtworkWriter)mp4).SetFrontCover(jpeg, "image/jpeg");
            mp4.Save();

            Assert.True(mp4.LastSaveWasInPlace, "artwork add on a moov-last file should still be in-place");
            Assert.True(new FileInfo(tmp.Path).Length > lenBefore, "adding cover art should grow the file");
            Assert.Equal(audioBefore, ReadMdat(tmp.Path));
            Assert.Equal(pcmBefore, Ffmpeg.DecodePcm(tmp.Path));

            // Artwork reads back.
            var art = MediaFile.GetFile(tmp.Path).Tags.SelectMany(t => t.GetImageMetadata()).ToList();
            Assert.NotEmpty(art);
        }

        // ---- shrink (remove field): tail truncates, audio still intact -----------------------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void RemovingFieldShrinksTailWithoutTouchingAudio(string file)
        {
            using var tmp = MediaFixtures.Copy(file);
            Assert.Equal("Rock", Read(tmp.Path)[TagFields.Genre]);
            byte[] audioBefore = ReadMdat(tmp.Path);
            byte[] pcmBefore = Ffmpeg.DecodePcm(tmp.Path);

            var mp4 = (MP4File)MediaFile.GetFile(tmp.Path);
            mp4.RemoveField(TagFields.Genre);
            mp4.Save();

            Assert.True(mp4.LastSaveWasInPlace);
            Assert.False(Read(tmp.Path).ContainsKey(TagFields.Genre));
            Assert.Equal(audioBefore, ReadMdat(tmp.Path));
            Assert.Equal(pcmBefore, Ffmpeg.DecodePcm(tmp.Path));
        }

        // ---- repeated in-place saves stay valid (stale-offset guard) -------------------------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void RepeatedInPlaceSavesStayValid(string file)
        {
            using var tmp = MediaFixtures.Copy(file);
            byte[] audioBefore = ReadMdat(tmp.Path);

            for (int i = 0; i < 3; i++)
            {
                var mp4 = (MP4File)MediaFile.GetFile(tmp.Path);
                mp4.SetField(TagFields.Title, "Iteration " + i);
                mp4.Save();
                Assert.True(mp4.LastSaveWasInPlace, $"save #{i} should be in-place");
            }

            Assert.Equal("Iteration 2", Read(tmp.Path)[TagFields.Title]);
            Assert.Equal(audioBefore, ReadMdat(tmp.Path));
            Ffmpeg.DecodePcm(tmp.Path); // throws if the container no longer decodes
        }

        // ---- the fallback still works when the fast path can't apply -------------------------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void SaveToOtherPathUsesRewriteAndIsValid(string file)
        {
            using var src = MediaFixtures.Copy(file);
            using var dst = MediaFixtures.Copy(file);

            var mp4 = (MP4File)MediaFile.GetFile(src.Path);
            mp4.SetField(TagFields.Title, "Branched");
            mp4.Save(dst.Path);   // explicit output path => never in-place

            Assert.False(mp4.LastSaveWasInPlace);
            Assert.Equal("Branched", Read(dst.Path)[TagFields.Title]);
            Assert.Equal("TestTitle", Read(src.Path)[TagFields.Title]);
            Ffmpeg.DecodePcm(dst.Path);
        }

        // ---- phase 2: seed padding into faststart files so later edits go in place -----------

        [Theory]
        [MemberData(nameof(Mp4Files))]
        public void FaststartFileGetsPaddingThenEditsInPlace(string file)
        {
            using var tmp = Ffmpeg.Faststart(MediaFixtures.Path_(file));
            // A faststart remux puts moov before mdat with no padding inside moov.
            Assert.True(TopLevelIndex(tmp.Path, "moov") < TopLevelIndex(tmp.Path, "mdat"),
                "faststart remux should place moov before mdat");

            // First edit can't be in place (moov precedes mdat and has no pad) — it falls back to a
            // full rewrite, which seeds the pad for next time.
            var m1 = (MP4File)MediaFile.GetFile(tmp.Path);
            m1.SetField(TagFields.Title, "First Edit");
            m1.Save();
            Assert.False(m1.LastSaveWasInPlace, "first edit on a padless faststart file must rewrite");
            Assert.Equal("First Edit", Read(tmp.Path)[TagFields.Title]);
            // Seeding the pad must not relocate moov or corrupt the container.
            Assert.True(TopLevelIndex(tmp.Path, "moov") < TopLevelIndex(tmp.Path, "mdat"));
            Ffmpeg.DecodePcm(tmp.Path);

            // Second edit is absorbed by the seeded pad — in place, audio untouched.
            byte[] audio = ReadMdat(tmp.Path);
            byte[] pcm = Ffmpeg.DecodePcm(tmp.Path);
            var m2 = (MP4File)MediaFile.GetFile(tmp.Path);
            m2.SetField(TagFields.Title, "Second Edit");
            m2.Save();
            Assert.True(m2.LastSaveWasInPlace, "second edit should be absorbed by the seeded pad (Path A)");
            Assert.Equal("Second Edit", Read(tmp.Path)[TagFields.Title]);
            Assert.Equal(audio, ReadMdat(tmp.Path));
            Assert.Equal(pcm, Ffmpeg.DecodePcm(tmp.Path));
        }

        // ---- helpers -------------------------------------------------------------------------

        // Index of the first top-level atom of the given type, or -1.
        private static int TopLevelIndex(string path, string type)
        {
            using var fs = File.OpenRead(path);
            long len = fs.Length, pos = 0;
            byte[] hdr = new byte[8];
            int idx = 0;
            while (pos + 8 <= len)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                fs.ReadExactly(hdr, 0, 8);
                ulong size = ((ulong)hdr[0] << 24) | ((ulong)hdr[1] << 16) | ((ulong)hdr[2] << 8) | hdr[3];
                string t = Encoding.ASCII.GetString(hdr, 4, 4);
                if (size == 1)
                {
                    byte[] ext = new byte[8];
                    fs.ReadExactly(ext, 0, 8);
                    size = 0;
                    for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
                }
                else if (size == 0)
                {
                    size = (ulong)(len - pos);
                }
                if (t == type) return idx;
                pos += (long)size;
                idx++;
            }
            return -1;
        }

        // Returns the payload bytes of the first top-level 'mdat' atom (the audio), or throws.
        private static byte[] ReadMdat(string path)
        {
            using var fs = File.OpenRead(path);
            long len = fs.Length;
            long pos = 0;
            byte[] hdr = new byte[8];
            while (pos + 8 <= len)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                fs.ReadExactly(hdr, 0, 8);
                ulong size = ((ulong)hdr[0] << 24) | ((ulong)hdr[1] << 16) | ((ulong)hdr[2] << 8) | hdr[3];
                string type = Encoding.ASCII.GetString(hdr, 4, 4);
                long headsz = 8;
                if (size == 1)
                {
                    byte[] ext = new byte[8];
                    fs.ReadExactly(ext, 0, 8);
                    size = 0;
                    for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
                    headsz = 16;
                }
                else if (size == 0)
                {
                    size = (ulong)(len - pos);
                }
                if (type == "mdat")
                {
                    long payload = (long)size - headsz;
                    byte[] buf = new byte[payload];
                    fs.Seek(pos + headsz, SeekOrigin.Begin);
                    fs.ReadExactly(buf, 0, buf.Length);
                    return buf;
                }
                pos += (long)size;
            }
            throw new InvalidDataException("no mdat atom found in " + path);
        }
    }

    // Locates ffmpeg the same way generate-fixtures.ps1 does and decodes a media file to raw
    // little-endian 16-bit stereo PCM. Since the fixtures are ffmpeg-generated at build time,
    // ffmpeg is available wherever the tests run; if it truly isn't, decoding throws and the
    // ffmpeg-dependent tests fail loudly rather than silently skipping the corruption check.
    internal static class Ffmpeg
    {
        private static string _path;
        private static string Path => _path ??= Resolve();

        private static string Resolve()
        {
            foreach (var cand in new[]
            {
                "ffmpeg",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            })
            {
                try
                {
                    var psi = new ProcessStartInfo(cand, "-version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    p.WaitForExit(5000);
                    if (p.HasExited && p.ExitCode == 0)
                        return cand;
                }
                catch { /* try next candidate */ }
            }
            throw new InvalidOperationException("ffmpeg not found on PATH or in known locations.");
        }

        public static byte[] DecodePcm(string file)
        {
            var psi = new ProcessStartInfo(
                Path,
                $"-hide_banner -loglevel error -i \"{file}\" -f s16le -ac 2 -ar 44100 -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            string err = errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0)
                throw new InvalidDataException($"ffmpeg failed to decode {file} (exit {p.ExitCode}): {err}");
            return ms.ToArray();
        }

        // Remuxes a fixture to a faststart layout (moov moved before mdat) in a temp file, so tests
        // can exercise the moov-before-mdat / padding-seeding path. Caller disposes.
        public static MediaFixtures.TempMedia Faststart(string srcFixture)
        {
            string dst = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mlt_fs_" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(srcFixture));
            var psi = new ProcessStartInfo(
                Path,
                $"-hide_banner -loglevel error -y -i \"{srcFixture}\" -c copy -movflags +faststart \"{dst}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidDataException($"ffmpeg faststart remux failed (exit {p.ExitCode}): {err}");
            return new MediaFixtures.TempMedia(dst);
        }

        // Produces a real, decodable JPEG (a solid-colour frame) so artwork tests embed a valid
        // image rather than a byte blob a real decoder would reject.
        public static byte[] EncodeJpeg(int width, int height)
        {
            var psi = new ProcessStartInfo(
                Path,
                $"-hide_banner -loglevel error -f lavfi -i color=c=teal:s={width}x{height} -frames:v 1 -c:v mjpeg -f mjpeg -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            string err = errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0 || ms.Length == 0)
                throw new InvalidDataException($"ffmpeg failed to encode a JPEG (exit {p.ExitCode}): {err}");
            return ms.ToArray();
        }
    }
}
