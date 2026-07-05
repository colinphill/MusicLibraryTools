namespace MusicLibrary.Core.Tests;

/// <summary>
/// Access to the shared ffmpeg-generated fixtures (tiny tone clips tagged Title=TestTitle,
/// Artist=TestArtist, Album=TestAlbum, Date=2021, Genre=Rock, TrackNumber=3), copied to temp
/// so write tests can mutate them without touching the originals.
/// </summary>
internal static class MediaFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "TestFiles");

    public static string Path_(string name) => Path.Combine(Dir, name);

    public static TempMedia Copy(string fixtureName)
    {
        var src = Path_(fixtureName);
        var ext = Path.GetExtension(fixtureName);
        var tmp = Path.Combine(Path.GetTempPath(), "mlc_" + Guid.NewGuid().ToString("N") + ext);
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
            foreach (var suffix in new[] { ".tmp", ".tmp~" })
                try { if (File.Exists(Path + suffix)) File.Delete(Path + suffix); } catch { }
        }
    }
}
