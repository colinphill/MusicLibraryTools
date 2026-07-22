using System.Text;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class PlaylistSourceTests
{
    [Fact]
    public async Task ReadsRelativeAbsoluteAndExtendedEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m3u-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string absolute = Path.Combine(root, "absolute.flac");
            string playlist = Path.Combine(root, "Favorites.m3u8");
            await File.WriteAllTextAsync(playlist,
                "#EXTM3U\n#EXTINF:123,Artist - Relative\nMusic/relative.flac\n" +
                absolute + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var source = new M3uPlaylistSource();

            IReadOnlyList<PlaylistDocument> result = await source.LoadAsync(new(playlist));

            PlaylistDocument document = Assert.Single(result);
            Assert.Equal("Favorites", document.Name);
            Assert.Collection(document.Tracks,
                relative =>
                {
                    Assert.Equal(Path.GetFullPath(Path.Combine(root, "Music", "relative.flac")),
                        relative.Path);
                    Assert.Equal("Artist - Relative", relative.DisplayText);
                    Assert.Equal(123, relative.DurationSeconds);
                },
                item => Assert.Equal(Path.GetFullPath(absolute), item.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySourceHonorsRecursiveOption()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m3u-source-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "root.m3u"), "root.flac\n");
            await File.WriteAllTextAsync(Path.Combine(nested, "nested.m3u8"), "nested.flac\n");
            var source = new M3uPlaylistSource();

            Assert.Single(await source.LoadAsync(new(root)));
            Assert.Equal(2, (await source.LoadAsync(new(root, Recursive: true))).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
