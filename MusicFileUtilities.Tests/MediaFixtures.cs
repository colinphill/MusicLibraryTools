using System;
using System.IO;

namespace MusicFileUtilities.Tests
{
    /// <summary>
    /// Helpers for the real-media-file tests. The fixtures under TestFiles/ are tiny
    /// (0.3s, 44.1kHz/16-bit/stereo) tone clips generated with ffmpeg, each carrying the
    /// same baseline tags: Title=TestTitle, Artist=TestArtist, Album=TestAlbum,
    /// Date=2021, Genre=Rock, TrackNumber=3.
    /// </summary>
    internal static class MediaFixtures
    {
        public static string Dir => Path.Combine(AppContext.BaseDirectory, "TestFiles");

        public static string Path_(string name) => System.IO.Path.Combine(Dir, name);

        /// <summary>
        /// Copies a fixture to a unique temp file with the same extension so write tests
        /// can mutate it without touching the committed original. Caller disposes to delete.
        /// </summary>
        public static TempMedia Copy(string fixtureName)
        {
            string src = Path_(fixtureName);
            string ext = System.IO.Path.GetExtension(fixtureName);
            string tmp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mlt_" + Guid.NewGuid().ToString("N") + ext);
            File.Copy(src, tmp, overwrite: true);
            return new TempMedia(tmp);
        }

        public sealed class TempMedia : IDisposable
        {
            public string Path { get; }
            public TempMedia(string path) => Path = path;

            public void Dispose()
            {
                try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
                // Clean up any stray temp artifacts a Save() might leave behind.
                foreach (var suffix in new[] { ".tmp", ".tmp~" })
                    try { if (File.Exists(Path + suffix)) File.Delete(Path + suffix); } catch { }
            }
        }
    }
}
