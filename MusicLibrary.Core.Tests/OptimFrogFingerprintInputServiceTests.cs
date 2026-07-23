using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OptimFrogFingerprintInputServiceTests
{
    [Theory]
    [InlineData("sample.ofr", "ofr")]
    [InlineData("sample.ofs", "ofs")]
    [InlineData("sample.off", "off")]
    public async Task OptimFrogInput_DecodesToOwnedTemporaryWave(
        string fixture,
        string toolName)
    {
        using var media = MediaFixtures.Copy(fixture);
        using var environment = new ToolEnvironment();
        var runner = new RecordingOptimFrogRunner();
        var service = new OptimFrogFingerprintInputService(
            runner,
            environment.Settings);

        string decoded;
        await using (PreparedFingerprintInput input =
                     await service.PrepareAsync(media.Path))
        {
            decoded = input.DecoderPath;
            Assert.True(input.IsTemporary);
            Assert.Equal(
                Path.GetFullPath(media.Path),
                input.OriginalPath);
            Assert.Equal(".wav", Path.GetExtension(decoded));
            Assert.True(File.Exists(decoded));
            Assert.Equal(
                toolName +
                (OperatingSystem.IsWindows() ? ".exe" : ""),
                Path.GetFileName(runner.Executable));
            Assert.Equal(
                Path.GetFullPath(media.Path),
                runner.SourcePath);
        }

        Assert.False(File.Exists(decoded));
    }

    [Fact]
    public async Task OtherFormats_PassThroughWithoutDecoder()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        using var environment = new ToolEnvironment();
        var runner = new RecordingOptimFrogRunner();
        var service = new OptimFrogFingerprintInputService(
            runner,
            environment.Settings);

        await using PreparedFingerprintInput input =
            await service.PrepareAsync(media.Path);

        Assert.False(input.IsTemporary);
        Assert.Equal(
            Path.GetFullPath(media.Path),
            input.DecoderPath);
        Assert.Null(runner.Executable);
    }

    [Fact]
    public async Task FingerprintService_RestoresOriginalPathAndCleansDecode()
    {
        using var media = MediaFixtures.Copy("sample.ofr");
        using var environment = new ToolEnvironment();
        var decoder = new RecordingOptimFrogRunner();
        var inputs = new OptimFrogFingerprintInputService(
            decoder,
            environment.Settings);
        var fpcalc = new RecordingFpcalcRunner();
        var service = new AudioFingerprintService(
            fpcalc,
            environment.Settings,
            executableResolver:
                new FpcalcExecutableResolver(
                    environment.Directory),
            inputService: inputs);

        AudioFingerprint result =
            await service.GenerateAsync(media.Path);

        Assert.Equal(
            Path.GetFullPath(media.Path),
            result.Path);
        Assert.Equal(".wav", Path.GetExtension(fpcalc.Path));
        Assert.False(File.Exists(fpcalc.Path));
    }

    [Fact]
    public async Task MissingDecoder_ExplainsRequiredConfiguration()
    {
        using var media = MediaFixtures.Copy("sample.ofr");
        using var environment = new ToolEnvironment(
            createTools: false);
        var service = new OptimFrogFingerprintInputService(
            new RecordingOptimFrogRunner(),
            environment.Settings);

        FileNotFoundException error =
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.PrepareAsync(media.Path));

        Assert.Contains("fpcalc cannot decode OptimFROG", error.Message);
        Assert.Contains("ofr", error.FileName!);
    }

    [Fact]
    public async Task DecodeCancellation_CleansOwnedTemporaryDirectory()
    {
        using var media = MediaFixtures.Copy("sample.ofr");
        using var environment = new ToolEnvironment();
        var runner = new RecordingOptimFrogRunner
        {
            Cancel = true,
        };
        var service = new OptimFrogFingerprintInputService(
            runner,
            environment.Settings);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.PrepareAsync(media.Path));

        Assert.NotNull(runner.OutputPath);
        Assert.False(
            Directory.Exists(
                Path.GetDirectoryName(runner.OutputPath)!));
    }

    private sealed class RecordingOptimFrogRunner :
        IOptimFrogRunner
    {
        public string? Executable { get; private set; }
        public string? SourcePath { get; private set; }
        public string? OutputPath { get; private set; }
        public bool Cancel { get; init; }

        public Task DecodeAsync(
            string executable,
            string sourcePath,
            string outputPath,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Executable = executable;
            SourcePath = sourcePath;
            OutputPath = outputPath;
            if (Cancel)
                throw new OperationCanceledException(ct);
            File.WriteAllBytes(outputPath, [1, 2, 3]);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFpcalcRunner :
        IFpcalcRunner
    {
        public string Path { get; private set; } = "";

        public Task<AudioFingerprint> GenerateAsync(
            string executable,
            string path,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Path = path;
            Assert.True(File.Exists(path));
            return Task.FromResult(new AudioFingerprint(
                path,
                "fingerprint",
                TimeSpan.FromSeconds(12),
                12));
        }
    }

    private sealed class ToolEnvironment : IDisposable
    {
        private readonly string _settingsPath;

        public ToolEnvironment(bool createTools = true)
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                $"optimfrog-tools-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            _settingsPath = Path.Combine(
                Directory,
                "settings.json");
            Settings = new AppSettings(_settingsPath);
            string tools = Path.Combine(Directory, "tools");
            if (createTools)
            {
                System.IO.Directory.CreateDirectory(tools);
                foreach (string name in
                         new[] { "ofr", "ofs", "off" })
                    File.WriteAllText(
                        Path.Combine(
                            tools,
                            name +
                            (OperatingSystem.IsWindows()
                                ? ".exe"
                                : "")),
                        "");
            }
            Settings.SetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey,
                tools);
            Settings.SetPreference(
                AudioFingerprintService.ExecutablePreferenceKey,
                "test-fpcalc");
        }

        public string Directory { get; }
        public AppSettings Settings { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(
                    Directory,
                    recursive: true);
            }
            catch
            {
            }
        }
    }
}
