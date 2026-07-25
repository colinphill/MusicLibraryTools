using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Bridges persisted ingest recipes to the shared transcode engine without
/// duplicating executable, codec, or container selection in the ingest path.
/// </summary>
public static class IngestTranscodeSettingsResolver
{
    public static bool UsesSharedEngine(LibraryIngestRecipe recipe) =>
        recipe.Action == LibraryIngestAction.Transcode &&
        !string.IsNullOrWhiteSpace(recipe.TranscodeFormatId);

    public static AudioTranscodeSettings? Resolve(LibraryIngestRecipe recipe)
    {
        if (!UsesSharedEngine(recipe))
            return null;

        if (!Enum.TryParse(
                recipe.TranscodeRateMode,
                ignoreCase: true,
                out AudioTranscodeRateMode rateMode))
            rateMode = DefaultRateMode(recipe.TranscodeFormatId!);

        return new(
            recipe.TranscodeFormatId!,
            string.IsNullOrWhiteSpace(recipe.TranscodeEncoderId)
                ? AudioTranscodeEncoderIds.Automatic
                : recipe.TranscodeEncoderId!,
            rateMode,
            recipe.BitrateKbps,
            recipe.TranscodeQuality,
            recipe.SampleRateHz,
            recipe.BitsPerSample,
            recipe.TranscodeCompressionEffort,
            recipe.TranscodeCreateCorrectionFile);
    }

    public static string? OutputExtension(LibraryIngestRecipe recipe) =>
        ResolveFormat(recipe.TranscodeFormatId)?.Extension ??
        NormalizeOptionalExtension(recipe.OutputExtension);

    public static string? OutputCodec(LibraryIngestRecipe recipe) =>
        ResolveFormat(recipe.TranscodeFormatId)?.Codec ??
        recipe.Codec;

    public static AudioTranscodeFormatDescriptor? ResolveFormat(
        string? formatId) =>
        formatId switch
        {
            AudioTranscodeFormatIds.Flac => Format(formatId, "flac", "flac", ".flac", true),
            AudioTranscodeFormatIds.AlacM4a => Format(formatId, "alac", "ipod", ".m4a", true),
            AudioTranscodeFormatIds.AacM4a => Format(formatId, "aac", "ipod", ".m4a", false),
            AudioTranscodeFormatIds.AacAdts => Format(formatId, "aac", "adts", ".aac", false),
            AudioTranscodeFormatIds.Mp3 => Format(formatId, "mp3", "mp3", ".mp3", false),
            AudioTranscodeFormatIds.OpusOgg => Format(formatId, "opus", "ogg", ".ogg", false),
            AudioTranscodeFormatIds.VorbisOgg => Format(formatId, "vorbis", "ogg", ".ogg", false),
            AudioTranscodeFormatIds.WavPack => Format(formatId, "wavpack", "wv", ".wv", true),
            AudioTranscodeFormatIds.PcmWave => Format(formatId, "pcm", "wav", ".wav", true),
            AudioTranscodeFormatIds.PcmRf64 => Format(formatId, "pcm", "rf64", ".rf64", true),
            AudioTranscodeFormatIds.PcmAiff => Format(formatId, "pcm", "aiff", ".aiff", true),
            AudioTranscodeFormatIds.TrueAudio => Format(formatId, "tta", "tta", ".tta", true),
            AudioTranscodeFormatIds.OptimFrog => Format(formatId, "optimfrog", "ofr", ".ofr", true),
            AudioTranscodeFormatIds.OptimFrogDualStream => Format(formatId, "optimfrog", "ofs", ".ofs", true),
            AudioTranscodeFormatIds.OptimFrogFloat => Format(formatId, "optimfrog", "off", ".ofr", true),
            AudioTranscodeFormatIds.MonkeysAudio => Format(formatId, "ape", "ape", ".ape", true),
            _ => null,
        };

    public static AudioEncoderDescriptor? ResolveEncoder(
        AudioTranscodeCapabilitySnapshot snapshot,
        AudioTranscodeFormatDescriptor format,
        string encoderId)
    {
        string? resolved = encoderId.Equals(
                AudioTranscodeEncoderIds.Automatic,
                StringComparison.Ordinal)
            ? format.EncoderIds.FirstOrDefault()
            : format.EncoderIds.Contains(encoderId, StringComparer.Ordinal)
                ? encoderId
                : null;
        return resolved is null ? null : snapshot.FindEncoder(resolved);
    }

    public static bool TryResolveCapability(
        AudioTranscodeCapabilitySnapshot snapshot,
        AudioTranscodeSettings settings,
        out AudioTranscodeFormatDescriptor? format,
        out AudioEncoderDescriptor? encoder,
        out string? error)
    {
        format = snapshot.FindFormat(settings.FormatId);
        encoder = format is null
            ? null
            : ResolveEncoder(
                snapshot,
                format,
                settings.EncoderId);
        if (format is null)
        {
            error = $"Transcode format '{settings.FormatId}' is unavailable.";
            return false;
        }
        if (encoder is null)
        {
            error = $"Transcode encoder '{settings.EncoderId}' is unavailable.";
            return false;
        }

        AudioRateControlDescriptor? rate =
            encoder.RateControls.FirstOrDefault(control =>
                control.Mode == settings.RateMode);
        if (rate is null)
        {
            error =
                $"Rate mode '{settings.RateMode}' is unavailable for encoder '{encoder.Id}'.";
            return false;
        }
        if (settings.BitrateKbps is { } bitrate &&
            (rate.MinimumBitrateKbps is { } minimumBitrate &&
             bitrate < minimumBitrate ||
             rate.MaximumBitrateKbps is { } maximumBitrate &&
             bitrate > maximumBitrate))
        {
            error =
                $"Bitrate {bitrate} kbps is outside the selected encoder's range.";
            return false;
        }
        if (settings.Quality is { } quality &&
            (rate.MinimumQuality is { } minimumQuality &&
             quality < minimumQuality ||
             rate.MaximumQuality is { } maximumQuality &&
             quality > maximumQuality))
        {
            error =
                $"Quality {quality} is outside the selected encoder's range.";
            return false;
        }
        if (settings.BitsPerSample is { } bits &&
            !encoder.SupportedBitDepths.IsDefaultOrEmpty &&
            !encoder.SupportedBitDepths.Contains(bits))
        {
            error =
                $"Bit depth {bits} is unavailable for encoder '{encoder.Id}'.";
            return false;
        }
        if (settings.CreateCorrectionFile &&
            !encoder.SupportsCorrectionFile)
        {
            error =
                $"Encoder '{encoder.Id}' cannot create a correction file.";
            return false;
        }

        error = null;
        return true;
    }

    private static AudioTranscodeRateMode DefaultRateMode(string formatId) =>
        formatId is AudioTranscodeFormatIds.AacM4a or
            AudioTranscodeFormatIds.AacAdts or
            AudioTranscodeFormatIds.Mp3 or
            AudioTranscodeFormatIds.OpusOgg or
            AudioTranscodeFormatIds.VorbisOgg
            ? AudioTranscodeRateMode.VariableQuality
            : AudioTranscodeRateMode.Lossless;

    private static AudioTranscodeFormatDescriptor Format(
        string id,
        string codec,
        string container,
        string extension,
        bool lossless) =>
        new(
            id,
            codec,
            container,
            extension,
            lossless,
            []);

    private static string? NormalizeOptionalExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;
        string value = extension.Trim();
        return value.StartsWith('.') ? value.ToLowerInvariant() : "." + value.ToLowerInvariant();
    }
}
