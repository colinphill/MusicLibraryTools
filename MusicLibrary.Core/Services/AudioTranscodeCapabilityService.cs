using System.Collections.Immutable;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IAudioTranscodeCapabilityService
{
    Task<AudioTranscodeCapabilitySnapshot> GetAsync(
        bool forceRefresh = false,
        CancellationToken ct = default);

    void Invalidate();
}

public sealed class AudioTranscodeCapabilityService :
    IAudioTranscodeCapabilityService,
    IDisposable
{
    private readonly IAppSettings _settings;
    private readonly IManagedProcessRunner _processes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioTranscodeCapabilitySnapshot? _cached;
    private string? _cacheKey;

    public AudioTranscodeCapabilityService(
        IAppSettings settings,
        IManagedProcessRunner processes)
    {
        _settings = settings;
        _processes = processes;
        _settings.ConfigurationChanged +=
            OnConfigurationChanged;
    }

    public async Task<AudioTranscodeCapabilitySnapshot> GetAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        AppConfigurationSnapshot configuration =
            _settings.GetSnapshot();
        string ffmpeg =
            configuration.Configuration?.FfmpegPath ??
            "ffmpeg";
        string wavpack =
            configuration.Configuration?.WavpackPath ??
            "wavpack";
        string monkeysAudio =
            configuration.Configuration?
                .MonkeysAudioPath ??
            "MAC";
        string optimFrogDirectory =
            _settings.GetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey) ??
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "optimfrog");
        string key = CacheKey(
            configuration.Version,
            ffmpeg,
            wavpack,
            monkeysAudio,
            optimFrogDirectory);
        if (!forceRefresh &&
            _cached is not null &&
            string.Equals(
                key,
                _cacheKey,
                StringComparison.Ordinal))
            return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh &&
                _cached is not null &&
                string.Equals(
                    key,
                    _cacheKey,
                    StringComparison.Ordinal))
                return _cached;

            Task<AudioToolProbeResult> ffmpegProbe =
                ProbeFfmpegAsync(ffmpeg, ct);
            Task<AudioToolProbeResult> wavpackProbe =
                ProbeWavPackAsync(wavpack, ct);
            Task<AudioToolProbeResult> optimFrogProbe =
                ProbeOptimFrogAsync(
                    optimFrogDirectory,
                    ct);
            Task<AudioToolProbeResult> monkeysAudioProbe =
                ProbeMonkeysAudioAsync(
                    monkeysAudio,
                    ct);
            AudioToolProbeResult[] probes =
                await Task.WhenAll(
                        ffmpegProbe,
                        wavpackProbe,
                        optimFrogProbe,
                        monkeysAudioProbe)
                    .ConfigureAwait(false);
            BuildCatalog(
                probes,
                out ImmutableArray<
                    AudioTranscodeFormatDescriptor> formats,
                out ImmutableArray<
                    AudioEncoderDescriptor> encoders);
            _cached = new(
                [.. probes],
                formats,
                encoders,
                DateTimeOffset.UtcNow,
                configuration.Version);
            _cacheKey = key;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _cached = null;
        _cacheKey = null;
    }

    public void Dispose()
    {
        _settings.ConfigurationChanged -=
            OnConfigurationChanged;
        _gate.Dispose();
    }

    private void OnConfigurationChanged(
        object? sender,
        EventArgs e) =>
        Invalidate();

    private async Task<AudioToolProbeResult> ProbeFfmpegAsync(
        string executable,
        CancellationToken ct)
    {
        try
        {
            ManagedProcessResult version =
                await RunRequiredAsync(
                    executable,
                    ["-version"],
                    ct).ConfigureAwait(false);
            Task<ManagedProcessResult> encoders =
                RunRequiredAsync(
                    executable,
                    ["-hide_banner", "-encoders"],
                    ct);
            Task<ManagedProcessResult> decoders =
                RunRequiredAsync(
                    executable,
                    ["-hide_banner", "-decoders"],
                    ct);
            Task<ManagedProcessResult> muxers =
                RunRequiredAsync(
                    executable,
                    ["-hide_banner", "-muxers"],
                    ct);
            Task<ManagedProcessResult> demuxers =
                RunRequiredAsync(
                    executable,
                    ["-hide_banner", "-demuxers"],
                    ct);
            await Task.WhenAll(
                    encoders,
                    decoders,
                    muxers,
                    demuxers)
                .ConfigureAwait(false);
            return new(
                AudioTranscodeToolKind.Ffmpeg,
                AudioToolProbeState.Ready,
                executable,
                ResolveExecutable(executable),
                FirstNonblankLine(
                    version.StandardOutput,
                    version.StandardError),
                ParseToolTable(
                    encoders.Result.StandardOutput +
                    Environment.NewLine +
                    encoders.Result.StandardError,
                    requiredFlag: 'A'),
                ParseToolTable(
                    decoders.Result.StandardOutput +
                    Environment.NewLine +
                    decoders.Result.StandardError,
                    requiredFlag: 'A'),
                ParseToolTable(
                    muxers.Result.StandardOutput +
                    Environment.NewLine +
                    muxers.Result.StandardError,
                    requiredFlag: 'E'),
                ParseToolTable(
                    demuxers.Result.StandardOutput +
                    Environment.NewLine +
                    demuxers.Result.StandardError,
                    requiredFlag: 'D'));
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AudioToolProbeResult.Unavailable(
                AudioTranscodeToolKind.Ffmpeg,
                executable,
                "Transcode.Tool.FfmpegUnavailable",
                error.Message);
        }
    }

    private async Task<AudioToolProbeResult> ProbeWavPackAsync(
        string executable,
        CancellationToken ct)
    {
        try
        {
            ManagedProcessResult version =
                await RunRequiredAsync(
                    executable,
                    ["--version"],
                    ct).ConfigureAwait(false);
            ManagedProcessResult help =
                await _processes.RunAsync(
                    executable,
                    ["--help"],
                    ct: ct).ConfigureAwait(false);
            string combined =
                version.StandardOutput +
                version.StandardError +
                help.StandardOutput +
                help.StandardError;
            var capabilities =
                ImmutableHashSet.CreateBuilder<string>(
                    StringComparer.Ordinal);
            capabilities.Add("wavpack");
            if (combined.Contains(
                    "--import-id3",
                    StringComparison.OrdinalIgnoreCase))
                capabilities.Add("import-id3");
            if (combined.Contains(
                    "correction",
                    StringComparison.OrdinalIgnoreCase) ||
                combined.Contains(
                    "-c",
                    StringComparison.Ordinal))
                capabilities.Add("correction");
            if (combined.Contains(
                    "DSD",
                    StringComparison.OrdinalIgnoreCase))
                capabilities.Add("dsd");
            return new(
                AudioTranscodeToolKind.WavPack,
                AudioToolProbeState.Ready,
                executable,
                ResolveExecutable(executable),
                FirstNonblankLine(
                    version.StandardOutput,
                    version.StandardError),
                capabilities.ToImmutable(),
                capabilities.ToImmutable(),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    "wv"),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    "wav",
                    "dsf"));
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AudioToolProbeResult.Unavailable(
                AudioTranscodeToolKind.WavPack,
                executable,
                "Transcode.Tool.WavPackUnavailable",
                error.Message);
        }
    }

    private async Task<AudioToolProbeResult> ProbeOptimFrogAsync(
        string directory,
        CancellationToken ct)
    {
        try
        {
            var ready =
                ImmutableHashSet.CreateBuilder<string>(
                    StringComparer.Ordinal);
            string? version = null;
            foreach (string name in new[]
                     {
                         "ofr",
                         "ofs",
                         "off",
                     })
            {
                string executable = Path.Combine(
                    directory,
                    OperatingSystem.IsWindows()
                        ? name + ".exe"
                        : name);
                if (!File.Exists(executable))
                    continue;
                ManagedProcessResult result =
                    await RunRequiredAsync(
                        executable,
                        ["--help"],
                        ct).ConfigureAwait(false);
                string text =
                    result.StandardOutput +
                    result.StandardError;
                if (text.Contains(
                        "OptimFROG",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ready.Add(name);
                    version ??= FirstNonblankLine(text);
                }
            }
            if (ready.Count == 0)
                throw new FileNotFoundException(
                    "No OptimFROG command-line tools were found.",
                    directory);
            return new(
                AudioTranscodeToolKind.OptimFrog,
                AudioToolProbeState.Ready,
                directory,
                Path.GetFullPath(directory),
                version,
                ready.ToImmutable(),
                ready.ToImmutable(),
                ready.ToImmutable(),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    "wav",
                    "raw"));
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AudioToolProbeResult.Unavailable(
                AudioTranscodeToolKind.OptimFrog,
                directory,
                "Transcode.Tool.OptimFrogUnavailable",
                error.Message);
        }
    }

    private async Task<AudioToolProbeResult>
        ProbeMonkeysAudioAsync(
            string executable,
            CancellationToken ct)
    {
        try
        {
            ManagedProcessResult help =
                await _processes.RunAsync(
                    executable,
                    [],
                    ct: ct).ConfigureAwait(false);
            string combined =
                (help.StandardOutput +
                 help.StandardError)
                .Replace(
                    "\0",
                    "",
                    StringComparison.Ordinal);
            if (!combined.Contains(
                    "Monkey's Audio",
                    StringComparison.OrdinalIgnoreCase) ||
                !combined.Contains(
                    "-c2000",
                    StringComparison.Ordinal) ||
                !combined.Contains(
                    "Verify",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The configured executable did not " +
                    "report the expected MAC command-line " +
                    "interface.");
            var capabilities =
                ImmutableHashSet.CreateBuilder<string>(
                    StringComparer.Ordinal);
            capabilities.Add("mac");
            capabilities.Add("verify");
            if (combined.Contains(
                    "-threads=#",
                    StringComparison.OrdinalIgnoreCase))
                capabilities.Add("threads");
            return new(
                AudioTranscodeToolKind.MonkeysAudio,
                AudioToolProbeState.Ready,
                executable,
                ResolveExecutable(executable),
                FirstNonblankLine(combined),
                capabilities.ToImmutable(),
                capabilities.ToImmutable(),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    "ape"),
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    "wav"));
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AudioToolProbeResult.Unavailable(
                AudioTranscodeToolKind.MonkeysAudio,
                executable,
                "Transcode.Tool.MonkeysAudioUnavailable",
                error.Message);
        }
    }

    private async Task<ManagedProcessResult> RunRequiredAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        ManagedProcessResult result =
            await _processes.RunAsync(
                executable,
                arguments,
                ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{executable}' exited with code " +
                $"{result.ExitCode}: " +
                $"{Diagnostic(result)}");
        return result;
    }

    internal static ImmutableHashSet<string> ParseToolTable(
        string text,
        char requiredFlag)
    {
        var values =
            ImmutableHashSet.CreateBuilder<string>(
                StringComparer.Ordinal);
        foreach (string line in text.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            string[] fields = trimmed.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 ||
                fields[0].Length > 8 ||
                !fields[0].Contains(requiredFlag) ||
                fields[0].Any(character =>
                    character is not (
                        '.' or
                        'A' or
                        'V' or
                        'S' or
                        'F' or
                        'X' or
                        'D' or
                        'E')))
                continue;
            foreach (string value in fields[1].Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
                values.Add(value);
        }
        return values.ToImmutable();
    }

    private static void BuildCatalog(
        IReadOnlyList<AudioToolProbeResult> probes,
        out ImmutableArray<AudioTranscodeFormatDescriptor> formats,
        out ImmutableArray<AudioEncoderDescriptor> encoders)
    {
        AudioToolProbeResult? ffmpeg = probes.FirstOrDefault(
            item =>
                item.Tool ==
                AudioTranscodeToolKind.Ffmpeg &&
                item.State == AudioToolProbeState.Ready);
        AudioToolProbeResult? wavpack = probes.FirstOrDefault(
            item =>
                item.Tool ==
                AudioTranscodeToolKind.WavPack &&
                item.State == AudioToolProbeState.Ready);
        AudioToolProbeResult? optimFrog = probes.FirstOrDefault(
            item =>
                item.Tool ==
                AudioTranscodeToolKind.OptimFrog &&
                item.State == AudioToolProbeState.Ready);
        AudioToolProbeResult? monkeysAudio =
            probes.FirstOrDefault(item =>
                item.Tool ==
                    AudioTranscodeToolKind.MonkeysAudio &&
                item.State ==
                    AudioToolProbeState.Ready);
        var encoderRows =
            new List<AudioEncoderDescriptor>();
        var formatRows =
            new List<AudioTranscodeFormatDescriptor>();

        if (ffmpeg is not null)
        {
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.Flac,
                "flac",
                "flac",
                ".flac",
                true,
                ["flac"],
                [Lossless()],
                [16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.AlacM4a,
                "alac",
                "ipod|mp4",
                ".m4a",
                true,
                ["alac", "alac_at"],
                [Lossless()],
                [16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.AacM4a,
                "aac",
                "ipod|mp4",
                ".m4a",
                false,
                ["libfdk_aac", "aac", "aac_at", "aac_mf"],
                [Cbr(), Abr(), Vbr()],
                []);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.AacAdts,
                "aac",
                "adts",
                ".aac",
                false,
                ["libfdk_aac", "aac", "aac_at", "aac_mf"],
                [Cbr(), Abr(), Vbr()],
                []);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.Mp3,
                "mp3",
                "mp3",
                ".mp3",
                false,
                ["libmp3lame", "libshine", "mp3_mf"],
                [Cbr(32, 320), Abr(32, 320), Vbr(0, 9, false)],
                []);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.OpusOgg,
                "opus",
                "opus|ogg",
                ".opus",
                false,
                ["libopus"],
                [
                    Cbr(6, 510),
                    Abr(6, 510),
                    new(
                        AudioTranscodeRateMode
                            .ConstrainedVariableBitrate,
                        6,
                        510),
                ],
                []);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.VorbisOgg,
                "vorbis",
                "ogg",
                ".ogg",
                false,
                ["libvorbis"],
                [Abr(32, 500), Vbr(-1, 10)],
                []);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.WavPack,
                "wavpack",
                "wv",
                ".wv",
                true,
                ["wavpack"],
                [Lossless()],
                [8, 16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.PcmWave,
                "pcm",
                "wav",
                ".wav",
                true,
                ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le"],
                [Lossless()],
                [16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.PcmRf64,
                "pcm",
                "wav",
                ".rf64",
                true,
                ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le"],
                [Lossless()],
                [16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.PcmAiff,
                "pcm",
                "aiff",
                ".aiff",
                true,
                ["pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_f32be"],
                [Lossless()],
                [16, 24, 32]);
            AddFfmpeg(
                ffmpeg,
                encoderRows,
                formatRows,
                AudioTranscodeFormatIds.TrueAudio,
                "tta",
                "tta",
                ".tta",
                true,
                ["tta"],
                [Lossless()],
                [8, 16, 24, 32]);
        }

        if (wavpack is not null)
        {
            string id = AudioTranscodeEncoderIds.WavPackCli;
            encoderRows.Add(new(
                id,
                AudioTranscodeToolKind.WavPack,
                "wavpack",
                AudioEncoderThreadingMode.SingleThreaded,
                [
                    Lossless(),
                    new(
                        AudioTranscodeRateMode.HybridBitrate,
                        200,
                        960),
                    new(
                        AudioTranscodeRateMode.HybridQuality,
                        MinimumQuality: 2,
                        MaximumQuality: 6),
                ],
                [],
                [8, 16, 24, 32],
                SupportsCorrectionFile:
                    wavpack.Encoders.Contains("correction"),
                SupportsDsd:
                    wavpack.Encoders.Contains("dsd")));
            AddOrMergeFormat(
                formatRows,
                new(
                    AudioTranscodeFormatIds.WavPack,
                    "wavpack",
                    "wv",
                    ".wv",
                    true,
                    [id],
                    SupportsDsd:
                        wavpack.Encoders.Contains("dsd")));
        }

        if (optimFrog is not null)
        {
            AddOptimFrog(
                optimFrog,
                encoderRows,
                formatRows,
                "ofr",
                AudioTranscodeEncoderIds.OptimFrogOfr,
                AudioTranscodeFormatIds.OptimFrog,
                ".ofr",
                true,
                false);
            AddOptimFrog(
                optimFrog,
                encoderRows,
                formatRows,
                "ofs",
                AudioTranscodeEncoderIds.OptimFrogOfs,
                AudioTranscodeFormatIds.OptimFrogDualStream,
                ".ofs",
                false,
                false);
            AddOptimFrog(
                optimFrog,
                encoderRows,
                formatRows,
                "off",
                AudioTranscodeEncoderIds.OptimFrogOff,
                AudioTranscodeFormatIds.OptimFrogFloat,
                ".ofr",
                true,
                true);
        }

        if (monkeysAudio is not null)
        {
            string id =
                AudioTranscodeEncoderIds
                    .MonkeysAudioMac;
            encoderRows.Add(new(
                id,
                AudioTranscodeToolKind.MonkeysAudio,
                "MAC",
                monkeysAudio.Encoders.Contains(
                    "threads")
                    ? AudioEncoderThreadingMode
                        .ThreadCountControllable
                    : AudioEncoderThreadingMode
                        .SingleThreaded,
                [Lossless()],
                [],
                [8, 16, 24, 32]));
            AddOrMergeFormat(
                formatRows,
                new(
                    AudioTranscodeFormatIds
                        .MonkeysAudio,
                    "ape",
                    "ape",
                    ".ape",
                    true,
                    [id]));
        }

        encoders =
        [
            .. encoderRows
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];
        formats =
        [
            .. formatRows
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];
    }

    private static void AddFfmpeg(
        AudioToolProbeResult ffmpeg,
        List<AudioEncoderDescriptor> encoders,
        List<AudioTranscodeFormatDescriptor> formats,
        string formatId,
        string codec,
        string muxers,
        string extension,
        bool lossless,
        IReadOnlyList<string> candidates,
        ImmutableArray<AudioRateControlDescriptor> rateControls,
        ImmutableArray<int> bitDepths)
    {
        if (!muxers.Split('|').Any(ffmpeg.Muxers.Contains))
            return;
        var ids = new List<string>();
        foreach (string candidate in candidates.Where(
                     ffmpeg.Encoders.Contains))
        {
            string id = AudioTranscodeEncoderIds.Ffmpeg(candidate);
            ids.Add(id);
            encoders.Add(new(
                id,
                AudioTranscodeToolKind.Ffmpeg,
                candidate,
                AudioEncoderThreadingMode.ThreadCountControllable,
                rateControls,
                [],
                bitDepths));
        }
        if (ids.Count == 0)
            return;
        AddOrMergeFormat(
            formats,
            new(
                formatId,
                codec,
                muxers.Split('|')[0],
                extension,
                lossless,
                [.. ids]));
    }

    private static void AddOptimFrog(
        AudioToolProbeResult probe,
        List<AudioEncoderDescriptor> encoders,
        List<AudioTranscodeFormatDescriptor> formats,
        string executable,
        string encoderId,
        string formatId,
        string extension,
        bool lossless,
        bool requiresFloat)
    {
        if (!probe.Encoders.Contains(executable))
            return;
        encoders.Add(new(
            encoderId,
            AudioTranscodeToolKind.OptimFrog,
            executable,
            AudioEncoderThreadingMode.SingleThreaded,
            executable == "ofs"
                ? [
                    new(
                        AudioTranscodeRateMode.AverageBitrate,
                        300,
                        1000),
                    new(
                        AudioTranscodeRateMode.VariableQuality,
                        MinimumQuality: 0,
                        MaximumQuality: 6),
                ]
                : [Lossless()],
            [],
            requiresFloat ? [32] : [8, 16, 24, 32],
            SupportsCorrectionFile:
                executable == "ofs"));
        AddOrMergeFormat(
            formats,
            new(
                formatId,
                executable,
                executable,
                extension,
                lossless,
                [encoderId],
                RequiresFloatInput: requiresFloat));
    }

    private static void AddOrMergeFormat(
        List<AudioTranscodeFormatDescriptor> formats,
        AudioTranscodeFormatDescriptor value)
    {
        int index = formats.FindIndex(item =>
            item.Id.Equals(value.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            formats.Add(value);
            return;
        }
        AudioTranscodeFormatDescriptor existing = formats[index];
        formats[index] = existing with
        {
            EncoderIds =
            [
                .. existing.EncoderIds
                    .Concat(value.EncoderIds)
                    .Distinct(StringComparer.Ordinal),
            ],
            SupportsDsd =
                existing.SupportsDsd ||
                value.SupportsDsd,
        };
    }

    private static AudioRateControlDescriptor Lossless() =>
        new(AudioTranscodeRateMode.Lossless);

    private static AudioRateControlDescriptor Cbr(
        int minimum = 16,
        int maximum = 1_536) =>
        new(
            AudioTranscodeRateMode.ConstantBitrate,
            minimum,
            maximum);

    private static AudioRateControlDescriptor Abr(
        int minimum = 16,
        int maximum = 1_536) =>
        new(
            AudioTranscodeRateMode.AverageBitrate,
            minimum,
            maximum);

    private static AudioRateControlDescriptor Vbr(
        double minimum = 0,
        double maximum = 10,
        bool higherIsBetter = true) =>
        new(
            AudioTranscodeRateMode.VariableQuality,
            MinimumQuality: minimum,
            MaximumQuality: maximum,
            HigherQualityValueIsBetter: higherIsBetter);

    private static string CacheKey(
        long version,
        params string[] values) =>
        string.Join(
            "|",
            [
                version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Environment.OSVersion.Platform.ToString(),
                System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture.ToString(),
                .. values.Select(FileIdentity),
            ]);

    private static string FileIdentity(string value)
    {
        try
        {
            string fullPath = Path.GetFullPath(value);
            var info = new FileInfo(fullPath);
            return info.Exists
                ? $"{fullPath}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : value;
        }
        catch
        {
            return value;
        }
    }

    private static string? ResolveExecutable(string value)
    {
        try
        {
            if (Path.IsPathRooted(value) ||
                value.Contains(Path.DirectorySeparatorChar) ||
                value.Contains(Path.AltDirectorySeparatorChar))
                return Path.GetFullPath(value);
        }
        catch
        {
        }
        return value;
    }

    private static string? FirstNonblankLine(
        params string[] values) =>
        values.SelectMany(value => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
            .FirstOrDefault();

    private static string Diagnostic(
        ManagedProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
}
