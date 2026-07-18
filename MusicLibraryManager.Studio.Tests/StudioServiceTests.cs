using MusicLibrary.Core.Services;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Studio.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Studio.Tests;

public sealed class StudioServiceTests
{
    [Fact]
    public void Grid_layout_round_trips_under_studio_specific_preference()
    {
        var settings = new FakeSettings();
        var service = new StudioGridStateService(settings);
        var snapshot = new StudioGridSnapshot(
            [new LibraryColumnState("title", 310, 1, true), new LibraryColumnState("codec", 90, 0, false)],
            new LibrarySortState("codec", true));

        service.Save(snapshot);
        StudioGridSnapshot loaded = Assert.IsType<StudioGridSnapshot>(service.Load());

        Assert.Contains("manager.studio.library.grid.v1", settings.Preferences.Keys);
        Assert.Equal(310, loaded.Columns[0].Width);
        Assert.True(loaded.Sort!.Descending);
    }

    [Fact]
    public void Split_width_round_trips_under_a_named_studio_preference()
    {
        var settings = new FakeSettings();
        var service = new StudioSplitStateService(settings);

        service.Save("library", 742);

        Assert.Equal(742, service.Load("library"));
        Assert.Contains("manager.studio.split.library.v1", settings.Preferences.Keys);
    }

    [Fact]
    public void Window_state_round_trips_and_ignores_malformed_data()
    {
        var settings = new FakeSettings();
        var service = new StudioWindowStateService(settings);
        service.Save(new WindowStateSnapshot(1, 12, 24, 1440, 900, true));

        Assert.Equal(1440, service.Load()!.Width);
        Assert.Contains("manager.window.studio.v1", settings.Preferences.Keys);
        settings.Preferences["manager.window.studio.v1"] = "not-json";
        Assert.Null(service.Load());
    }

    [Theory]
    [InlineData("png", ".png")]
    [InlineData("*.xml", ".xml")]
    [InlineData(".db", ".db")]
    public void Native_file_adapter_normalizes_extensions(string input, string expected) =>
        Assert.Equal(expected, StudioFilePickerService.NormalizeExtension(input));

    [Fact]
    public async Task Thumbnail_service_creates_a_mime_qualified_data_url()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
        string result = Assert.IsType<string>(await new StudioThumbnailService().CreateImageSourceAsync(
            png, cancellationToken: TestContext.Current.CancellationToken));
        Assert.StartsWith("data:image/png;base64,", result);
    }

    [Fact]
    public void Theme_and_drop_services_publish_normalized_state()
    {
        var theme = new StudioThemeService();
        int changes = 0;
        theme.Changed += () => changes++;
        theme.Apply("Dark");
        theme.Apply("unexpected");
        Assert.Equal("System", theme.Current);
        Assert.Equal(2, changes);

        var drops = new StudioDropService();
        string? observed = null;
        drops.SourceDropped += path => observed = path;
        drops.SetDroppedSource(@"C:\Incoming");
        Assert.Equal(@"C:\Incoming", observed);
    }

    [Fact]
    public async Task Dialog_host_completes_confirmation_and_cancellation_flows()
    {
        var dialogs = new StudioDialogService(new UnusedMedia(), new UnusedWriter());
        Task<bool> accepted = dialogs.ConfirmAsync("Apply", "Apply the preview?", "Apply");
        Assert.IsType<StudioConfirmRequest>(dialogs.Current);
        dialogs.Complete(true);
        Assert.True(await accepted);
        Assert.Null(dialogs.Current);

        Task<bool> cancelled = dialogs.ConfirmAsync("Purge", "Permanently purge?", "Purge");
        dialogs.Complete(false);
        Assert.False(await cancelled);
        Assert.Null(dialogs.Current);
    }

    private sealed class FakeSettings : IAppSettings
    {
        public Dictionary<string, string> Preferences { get; } = [];
        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() => new(null, null, 0);
        public void LoadConfig(string path) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs() { }
        public string? GetPreference(string key) => Preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value) { if (value is null) Preferences.Remove(key); else Preferences[key] = value; }
    }

    private sealed class UnusedMedia : IMediaFileService
    {
        public Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<MediaFileModel>> LoadAsync(string path, bool includeArtwork, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedWriter : ITagWriteService
    {
        public Task<BatchWriteResult> ApplyAsync(IReadOnlyList<string> paths, IReadOnlyList<TagEdit> edits,
            IProgress<int>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
