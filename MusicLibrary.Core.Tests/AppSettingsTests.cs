using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void InvalidConfig_DoesNotReplaceOrRememberPreviousValidConfig()
    {
        using var temp = new TempDirectory();
        var good = Path.Combine(temp.Path, "good.xml");
        var bad = Path.Combine(temp.Path, "bad.xml");
        var state = Path.Combine(temp.Path, "settings.json");
        new EditableLibraryConfig
        {
            IndexTargets = [new IndexTargetEntry { Target = temp.Path }],
        }.Save(good);
        File.WriteAllText(bad, "<not-a-library />");

        var settings = new AppSettings(state);
        settings.LoadConfig(good);
        var before = settings.GetSnapshot();

        Assert.Throws<InvalidDataException>(() => settings.LoadConfig(bad));

        var after = settings.GetSnapshot();
        Assert.Equal(before.ConfigPath, after.ConfigPath);
        Assert.Same(before.Configuration, after.Configuration);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(good, new AppSettings(state).GetRememberedConfigPath());
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public void InvalidSet_IsRejectedDuringLoad(string set)
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "bad-set.xml");
        File.WriteAllText(config,
            $"<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<IndexTarget Set=\"{set}\">{System.Security.SecurityElement.Escape(temp.Path)}</IndexTarget>" +
            "</LibraryConfiguration>");

        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        Assert.Throws<InvalidDataException>(() => settings.LoadConfig(config));
        Assert.Null(settings.Configuration);
    }

    [Fact]
    public void InvalidIndexTargetOrganizeFlag_IsRejectedDuringLoad()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "bad-organize.xml");
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<IndexTarget Organize=\"sometimes\">{System.Security.SecurityElement.Escape(temp.Path)}</IndexTarget>" +
            "</LibraryConfiguration>");
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        var error = Assert.Throws<InvalidDataException>(() => settings.LoadConfig(config));

        Assert.Contains("Organize", error.Message);
        Assert.Null(settings.Configuration);
    }

    [Fact]
    public void InvalidIndexTargetItunesCanonicalNamingFlag_IsRejectedDuringLoad()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "bad-itunes-naming.xml");
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<IndexTarget ItunesCanonicalNaming=\"sometimes\">" +
            $"{System.Security.SecurityElement.Escape(temp.Path)}</IndexTarget>" +
            "</LibraryConfiguration>");
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        var error = Assert.Throws<InvalidDataException>(() => settings.LoadConfig(config));

        Assert.Contains("ItunesCanonicalNaming", error.Message);
        Assert.Null(settings.Configuration);
    }

    [Theory]
    [InlineData("OversizedByteThreshold", "0")]
    [InlineData("OversizedDimensionThreshold", "not-a-number")]
    public void InvalidArtworkHealthThreshold_IsRejectedDuringLoad(
        string attribute, string value)
    {
        using var temp = new TempDirectory();
        string config = Path.Combine(temp.Path, "bad-artwork-health.xml");
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<ArtworkHealthSettings {attribute}=\"{value}\" />" +
            "</LibraryConfiguration>");
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => settings.LoadConfig(config));

        Assert.Contains(attribute, error.Message);
        Assert.Null(settings.Configuration);
    }

    [Theory]
    [InlineData("<PlaylistTarget Type=\"m3u\">Z:\\Playlists</PlaylistTarget>")]
    [InlineData("<PlaylistTarget Set=\"1\">Z:\\Playlists</PlaylistTarget><PlaylistType>m3u</PlaylistType>")]
    [InlineData("<PlaylistTarget Type=\"unknown\" Set=\"1\">Z:\\Playlists</PlaylistTarget>")]
    [InlineData("<PlaylistTarget Type=\"m3u\" Set=\"1\">Z:\\Playlists</PlaylistTarget>")]
    [InlineData("<PlaylistType>m3u</PlaylistType>")]
    public void InvalidPlaylistTarget_IsRejectedDuringLoad(string playlistXml)
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "bad-playlist.xml");
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" + playlistXml +
            "</LibraryConfiguration>");

        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        Assert.Throws<InvalidDataException>(() => settings.LoadConfig(config));
        Assert.Null(settings.Configuration);
    }

    [Fact]
    public void MultipleIndexAndPlaylistSets_AreAcceptedDuringLoad()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "multi-set.xml");
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<IndexTarget Set=\"1,2\">{System.Security.SecurityElement.Escape(temp.Path)}</IndexTarget>" +
            "<PlaylistTarget Type=\"m3u\" Set=\"1,2\">Z:\\Playlists</PlaylistTarget>" +
            "</LibraryConfiguration>");

        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        settings.LoadConfig(config);

        Assert.Equal(["1", "2"], Assert.Single(settings.Configuration!.PlaylistTargets).Sets);
    }

    [Fact]
    public void PlaylistTargetWithConflictingOffsetsForOneRoot_IsRejectedDuringLoad()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "conflicting-offsets.xml");
        string root = System.Security.SecurityElement.Escape(temp.Path)!;
        File.WriteAllText(config,
            "<LibraryConfiguration><DatabaseFile>cache.db</DatabaseFile>" +
            $"<IndexTarget Path=\"{root}\"><Set Name=\"One\" Offset=\"/one\"/>" +
            "<Set Name=\"Two\" Offset=\"/two\"/></IndexTarget>" +
            "<PlaylistTarget Type=\"m3u\" Set=\"One,Two\">Z:\\Playlists</PlaylistTarget>" +
            "</LibraryConfiguration>");

        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));

        var error = Assert.Throws<InvalidDataException>(() => settings.LoadConfig(config));
        Assert.Contains("different offsets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtomicWriters_ReplaceFilesAndLeaveNoTemporarySiblings()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "library.xml");
        var statePath = Path.Combine(temp.Path, "settings.json");
        var config = new EditableLibraryConfig
        {
            DatabaseFile = "one.db",
            IndexTargets = [new IndexTargetEntry { Target = temp.Path }],
        };
        config.Save(configPath);
        config.DatabaseFile = "two.db";
        config.Save(configPath);

        var settings = new AppSettings(statePath);
        settings.LoadConfig(configPath);
        settings.SetPreference("test", "value");

        Assert.Equal("two.db", EditableLibraryConfig.Load(configPath).DatabaseFile);
        Assert.Equal("value", new AppSettings(statePath).GetPreference("test"));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".*.tmp"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mlsettings_" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
