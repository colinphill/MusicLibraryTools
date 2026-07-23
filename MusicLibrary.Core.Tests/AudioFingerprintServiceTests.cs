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

            AudioFingerprint result = await service.GenerateAsync(media.Path);

            Assert.Equal("custom-fpcalc", runner.Executable);
            Assert.Equal(Path.GetFullPath(media.Path), runner.Path);
            Assert.Equal("fingerprint", result.Fingerprint);
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    private sealed class RecordingFpcalcRunner : IFpcalcRunner
    {
        public string? Executable { get; private set; }
        public string? Path { get; private set; }

        public Task<AudioFingerprint> GenerateAsync(
            string executable,
            string path,
            CancellationToken ct = default)
        {
            Executable = executable;
            Path = path;
            return Task.FromResult(new AudioFingerprint(
                path, "fingerprint", TimeSpan.FromSeconds(1), 1));
        }
    }
}
