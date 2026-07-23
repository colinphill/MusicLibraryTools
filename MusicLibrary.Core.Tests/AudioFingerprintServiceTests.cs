using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AudioFingerprintServiceTests
{
    [Fact]
    public void FpcalcOutput_ParsesCompressedFingerprintAndWholeFileDuration()
    {
        string path = Path.Combine(Path.GetTempPath(), "track.flac");

        AudioFingerprint result = FpcalcRunner.ParseOutput(
            path,
            """{"duration": 123.51, "fingerprint": "AQAD-test"}""");

        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal("AQAD-test", result.Fingerprint);
        Assert.Equal(TimeSpan.FromSeconds(123.51), result.Duration);
        Assert.Equal(124, result.LookupDurationSeconds);
        Assert.Equal("Chromaprint", result.Algorithm);
    }

    [Theory]
    [InlineData("""{"duration": 0, "fingerprint": "AQAD-test"}""")]
    [InlineData("""{"duration": 12.3, "fingerprint": ""}""")]
    [InlineData("""{"duration": 12.3, "fingerprint": [1, 2]}""")]
    [InlineData("not-json")]
    public void FpcalcOutput_RejectsUnusableResults(string output)
    {
        Assert.Throws<InvalidDataException>(() =>
            FpcalcRunner.ParseOutput("track.flac", output));
    }

    [Fact]
    public async Task Service_UsesPersonalExecutablePreferenceAndFullPath()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            settings.SetPreference(
                AudioFingerprintService.ExecutablePreferenceKey,
                "custom-fpcalc");
            var runner = new RecordingFpcalcRunner();
            var service = new AudioFingerprintService(runner, settings);
            var progress = new RecordingProgress();

            AudioFingerprint result =
                await service.GenerateAsync(media.Path, progress);

            Assert.Equal("custom-fpcalc", runner.Executable);
            Assert.Equal(Path.GetFullPath(media.Path), runner.Path);
            Assert.Equal("fingerprint-1", result.Fingerprint);
            Assert.Equal([0, 1], progress.Items.Select(item => item.Completed));
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    [InlineData("sample.dsf")]
    public async Task PayloadIdentity_IgnoresNativeMetadataChanges(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);

        string before = await identities.ComputeAsync(media.Path);
        IMediaFile file = MediaFile.GetFile(
            media.Path, readOnly: false, readArtwork: true);
        IMetadataWriter? writer = file as IMetadataWriter ??
            file.Tags.OfType<IMetadataWriter>().FirstOrDefault();
        if (writer is not null)
            writer.SetField(TagFields.Title, "Payload identity title");
        else
            Assert.IsAssignableFrom<VorbisComments>(file.Tags.First())
                .SetField(TagFields.Title, "Payload identity title");
        file.SaveTags();
        string after = await identities.ComputeAsync(media.Path);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PayloadIdentity_ChangesWhenFlacAudioPayloadChanges()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);
        string before = await identities.ComputeAsync(media.Path);

        await using (var stream = new FileStream(
                         media.Path, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(new byte[] { 0x5a });

        string after = await identities.ComputeAsync(media.Path);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Service_ReusesFingerprintAfterMetadataOnlyEdit()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-fingerprint-cache-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "settings.json");
        string databasePath = Path.Combine(root, "cache.db");
        try
        {
            Directory.CreateDirectory(root);
            var runner = new RecordingFpcalcRunner();
            var service = new AudioFingerprintService(
                runner,
                new AppSettings(statePath),
                new AudioPayloadIdentityService(MediaFormatRegistry.Default),
                new AudioFingerprintCache(databasePath));

            AudioFingerprint first =
                await service.GenerateAsync(media.Path);
            IMediaFile file = MediaFile.GetFile(
                media.Path, readOnly: false, readArtwork: true);
            IMetadataWriter writer = file as IMetadataWriter ??
                file.Tags.OfType<IMetadataWriter>().First();
            writer.SetField(TagFields.Title, "Cache-safe title edit");
            file.SaveTags();
            var restarted = new AudioFingerprintService(
                runner,
                new AppSettings(statePath),
                new AudioPayloadIdentityService(MediaFormatRegistry.Default),
                new AudioFingerprintCache(databasePath));
            AudioFingerprint second =
                await restarted.GenerateAsync(media.Path);

            Assert.Equal(1, runner.CallCount);
            Assert.Equal(first.Fingerprint, second.Fingerprint);

            await using (var stream = new FileStream(
                             media.Path, FileMode.Append, FileAccess.Write))
                await stream.WriteAsync(new byte[] { 0x33 });
            await restarted.GenerateAsync(media.Path);

            Assert.Equal(2, runner.CallCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class RecordingFpcalcRunner : IFpcalcRunner
    {
        public string? Executable { get; private set; }
        public string? Path { get; private set; }
        public int CallCount { get; private set; }

        public Task<AudioFingerprint> GenerateAsync(
            string executable,
            string path,
            CancellationToken ct = default)
        {
            Executable = executable;
            Path = path;
            CallCount++;
            return Task.FromResult(new AudioFingerprint(
                path,
                $"fingerprint-{CallCount}",
                TimeSpan.FromSeconds(1),
                1));
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
