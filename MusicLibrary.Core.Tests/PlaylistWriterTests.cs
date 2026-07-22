using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class PlaylistWriterTests
{
    [Fact]
    public void CoreServicesRegisterBothBuiltInWriters()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        IPlaylistWriter[] writers = provider.GetServices<IPlaylistWriter>().ToArray();

        Assert.Contains(writers, writer => writer is M3uPlaylistWriter);
        Assert.Contains(writers, writer => writer is WplPlaylistWriter);
    }

    [Fact]
    public void M3u8HonorsRelativePathsUtf8BomLineEndingsExtendedInfoAndFileNameTransform()
    {
        using var workspace = new TempDirectory();
        string target = Directory.CreateDirectory(Path.Combine(workspace.Path, "playlists")).FullName;
        string media = Directory.CreateDirectory(Path.Combine(workspace.Path, "media")).FullName;
        string track = Path.Combine(media, "track.flac");
        var writer = new M3uPlaylistWriter();
        var options = new PlaylistWriterOptions
        {
            PathStyle = PlaylistPathStyle.Relative,
            Encoding = Encoding.UTF8,
            EmitByteOrderMark = false,
            LineEnding = PlaylistLineEnding.Lf,
            IncludeExtendedInfo = true,
            FileNameTransform = static name => name.Replace(' ', '_'),
            MaxTrackCount = 1,
        };

        PlaylistWriterOutput output = writer.Write(new("m3u8", "Road Mix", target,
            [new(track, 61, "Artist - Title")]), options);

        Assert.Equal(Path.Combine(target, "Road_Mix.m3u8"), output.DestinationPath);
        Assert.Equal(1, output.TrackCount);
        Assert.False(output.Content.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal("#EXTM3U\n#EXTINF:61,Artist - Title\n" +
                     Path.GetRelativePath(target, track) + "\n",
            Encoding.UTF8.GetString(output.Content.AsSpan()));
    }

    [Fact]
    public void M3uCanWritePlainAbsolutePlaylistWithSelectedEncodingAndBom()
    {
        using var workspace = new TempDirectory();
        string target = Directory.CreateDirectory(Path.Combine(workspace.Path, "playlists")).FullName;
        string track = Path.Combine(workspace.Path, "media", "track.flac");
        var writer = new M3uPlaylistWriter();
        var options = new PlaylistWriterOptions
        {
            PathStyle = PlaylistPathStyle.Absolute,
            Encoding = Encoding.Unicode,
            EmitByteOrderMark = true,
            LineEnding = PlaylistLineEnding.CrLf,
            IncludeExtendedInfo = false,
        };

        PlaylistWriterOutput output = writer.Write(new("m3u", "Plain", target,
            [new(track)]), options);

        Assert.True(output.Content.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));
        Assert.Equal(Path.GetFullPath(track) + "\r\n",
            Encoding.Unicode.GetString(output.Content.AsSpan()[Encoding.Unicode.GetPreamble().Length..]));
    }

    [Fact]
    public void WplHonorsRelativePathsLineEndingsEncodingAndFileNameTransform()
    {
        using var workspace = new TempDirectory();
        string target = Directory.CreateDirectory(Path.Combine(workspace.Path, "playlists")).FullName;
        string track = Path.Combine(workspace.Path, "media", "track.flac");
        var writer = new WplPlaylistWriter();
        var options = new PlaylistWriterOptions
        {
            PathStyle = PlaylistPathStyle.Relative,
            Encoding = Encoding.UTF8,
            EmitByteOrderMark = false,
            LineEnding = PlaylistLineEnding.Lf,
            FileNameTransform = static name => name.ToUpperInvariant(),
        };

        PlaylistWriterOutput output = writer.Write(new("wpl", "Favorites", target,
            [new(track)]), options);

        Assert.Equal(Path.Combine(target, "FAVORITES.wpl"), output.DestinationPath);
        string text = Encoding.UTF8.GetString(output.Content.AsSpan());
        Assert.DoesNotContain('\r', text);
        XDocument document = XDocument.Parse(text);
        Assert.Equal("Favorites", document.Root!.Element("head")!.Element("title")!.Value);
        Assert.Equal("1", document.Root.Element("head")!.Elements("meta")
            .Single(element => (string?)element.Attribute("name") == "ItemCount")
            .Attribute("content")!.Value);
        Assert.Equal(Path.GetRelativePath(target, track),
            document.Root.Element("body")!.Element("seq")!.Element("media")!
                .Attribute("src")!.Value);
    }

    [Theory]
    [InlineData("m3u")]
    [InlineData("m3u8")]
    [InlineData("wpl")]
    public void WritersEnforceMaximumTrackCount(string format)
    {
        IPlaylistWriter writer = format == "wpl"
            ? new WplPlaylistWriter()
            : new M3uPlaylistWriter();
        var options = new PlaylistWriterOptions { MaxTrackCount = 1 };
        var request = new PlaylistWriterRequest(format, "Too Large", Environment.CurrentDirectory,
            [new("one.flac"), new("two.flac")]);

        PlaylistTrackLimitExceededException exception = Assert.Throws<
            PlaylistTrackLimitExceededException>(() => writer.Write(request, options));

        Assert.Equal(2, exception.TrackCount);
        Assert.Equal(1, exception.MaximumTrackCount);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mlplaylist_writer_" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
