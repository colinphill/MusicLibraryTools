using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace MusicFileUtilities
{
    /// <summary>
    /// Operations which the built-in parser and writer can perform for a media format.
    /// LibraryIndex is deliberately separate from metadata reading: the toolkit can open a few
    /// MPEG-4 variants which the legacy library scanner has never indexed automatically.
    /// </summary>
    [Flags]
    public enum MediaFormatCapabilities
    {
        None = 0,
        LibraryIndex = 1 << 0,
        ReadMetadata = 1 << 1,
        WriteMetadata = 1 << 2,
        ReadArtwork = 1 << 3,
        WriteArtwork = 1 << 4,
        Remux = 1 << 5,
        TranscodeSource = 1 << 6,
        TranscodeDestination = 1 << 7,
    }

    /// <summary>The parser family used for an extension.</summary>
    public enum MediaFormatFamily
    {
        Dsf = 0,
        Flac = 1,
        Mp3 = 2,
        Mp4 = 3,
        Ogg = 4,
        // Compatibility alias for callers compiled against the original registry.
        OggVorbis = Ogg,
        WavPack = 5,
    }

    /// <summary>Describes one extension and the operations supported for it.</summary>
    public sealed record MediaFormatDefinition(
        string Extension,
        string DisplayName,
        MediaFormatFamily Family,
        MediaFormatCapabilities Capabilities)
    {
        public bool IsLibraryIndexFormat => Supports(MediaFormatCapabilities.LibraryIndex);
        public bool CanReadMetadata => Supports(MediaFormatCapabilities.ReadMetadata);
        public bool CanWriteMetadata => Supports(MediaFormatCapabilities.WriteMetadata);
        public bool CanReadArtwork => Supports(MediaFormatCapabilities.ReadArtwork);
        public bool CanWriteArtwork => Supports(MediaFormatCapabilities.WriteArtwork);
        public bool CanRemux => Supports(MediaFormatCapabilities.Remux);
        public bool CanTranscodeFrom => Supports(MediaFormatCapabilities.TranscodeSource);
        public bool CanTranscodeTo => Supports(MediaFormatCapabilities.TranscodeDestination);

        public bool Supports(MediaFormatCapabilities capability) =>
            (Capabilities & capability) == capability;
    }

    /// <summary>
    /// Central source of truth for media extensions and parser/writer capabilities.
    /// Consumers should ask for the capability they need rather than maintaining extension lists.
    /// </summary>
    public interface IMediaFormatRegistry
    {
        IReadOnlyList<MediaFormatDefinition> Formats { get; }

        bool TryGetByExtension(string extension, out MediaFormatDefinition format);

        bool TryGetByExtension(ReadOnlySpan<char> extension, out MediaFormatDefinition format);

        bool TryGetForPath(string path, out MediaFormatDefinition format);

        bool SupportsExtension(string extension, MediaFormatCapabilities capability);

        bool SupportsExtension(ReadOnlySpan<char> extension, MediaFormatCapabilities capability);

        bool SupportsPath(string path, MediaFormatCapabilities capability);

        IReadOnlyList<string> GetExtensions(MediaFormatCapabilities capability);
    }

    /// <summary>
    /// Immutable media-format registry. <see cref="Default"/> preserves the format behavior used by
    /// the legacy scanner and editors while providing an injectable seam for future profiles.
    /// </summary>
    public sealed class MediaFormatRegistry : IMediaFormatRegistry
    {
        private readonly Dictionary<string, MediaFormatDefinition> _byExtension;
        private readonly Dictionary<string, MediaFormatDefinition>.AlternateLookup<ReadOnlySpan<char>>
            _byExtensionSpans;

        public static MediaFormatRegistry Default { get; } = new(CreateDefaults());

        public MediaFormatRegistry(IEnumerable<MediaFormatDefinition> formats)
        {
            ArgumentNullException.ThrowIfNull(formats);

            var normalized = new List<MediaFormatDefinition>();
            var byExtension = new Dictionary<string, MediaFormatDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (MediaFormatDefinition source in formats)
            {
                ArgumentNullException.ThrowIfNull(source);
                string extension = NormalizeExtension(source.Extension);
                if (string.IsNullOrWhiteSpace(source.DisplayName))
                    throw new ArgumentException($"A display name is required for '{extension}'.", nameof(formats));
                if (source.Supports(MediaFormatCapabilities.WriteMetadata) &&
                    !source.Supports(MediaFormatCapabilities.ReadMetadata))
                    throw new ArgumentException(
                        $"Format '{extension}' cannot write metadata without metadata-read support.", nameof(formats));
                if (source.Supports(MediaFormatCapabilities.WriteArtwork) &&
                    !source.Supports(MediaFormatCapabilities.ReadArtwork))
                    throw new ArgumentException(
                        $"Format '{extension}' cannot write artwork without artwork-read support.", nameof(formats));

                var definition = source with { Extension = extension };
                if (!byExtension.TryAdd(extension, definition))
                    throw new ArgumentException($"Duplicate media extension '{extension}'.", nameof(formats));
                normalized.Add(definition);
            }

            Formats = new ReadOnlyCollection<MediaFormatDefinition>(normalized);
            _byExtension = byExtension;
            _byExtensionSpans = _byExtension.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public IReadOnlyList<MediaFormatDefinition> Formats { get; }

        public bool TryGetByExtension(string extension, out MediaFormatDefinition format)
        {
            format = null;
            if (string.IsNullOrWhiteSpace(extension))
                return false;
            return _byExtension.TryGetValue(NormalizeExtension(extension), out format);
        }

        public bool TryGetByExtension(
            ReadOnlySpan<char> extension,
            out MediaFormatDefinition format)
        {
            format = null;
            extension = extension.Trim();
            if (extension.StartsWith("*.", StringComparison.Ordinal))
                extension = extension.Slice(1);
            if (extension.Length == 0)
                return false;
            if (extension[0] != '.')
                return TryGetByExtension(extension.ToString(), out format);
            return _byExtensionSpans.TryGetValue(extension, out format);
        }

        public bool TryGetForPath(string path, out MediaFormatDefinition format)
        {
            format = null;
            return !string.IsNullOrWhiteSpace(path) &&
                   TryGetByExtension(Path.GetExtension(path), out format);
        }

        public bool SupportsExtension(string extension, MediaFormatCapabilities capability) =>
            TryGetByExtension(extension, out MediaFormatDefinition format) && format.Supports(capability);

        public bool SupportsExtension(
            ReadOnlySpan<char> extension,
            MediaFormatCapabilities capability) =>
            TryGetByExtension(extension, out MediaFormatDefinition format) && format.Supports(capability);

        public bool SupportsPath(string path, MediaFormatCapabilities capability) =>
            TryGetForPath(path, out MediaFormatDefinition format) && format.Supports(capability);

        public IReadOnlyList<string> GetExtensions(MediaFormatCapabilities capability) =>
            Formats.Where(format => format.Supports(capability))
                .Select(format => format.Extension)
                .ToArray();

        private static string NormalizeExtension(string extension)
        {
            string value = extension.Trim();
            if (value.StartsWith("*.", StringComparison.Ordinal))
                value = value.Substring(1);
            else if (!value.StartsWith(".", StringComparison.Ordinal))
                value = "." + value;
            if (value.Length == 1 || value.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new ArgumentException($"Invalid media extension '{extension}'.", nameof(extension));
            return value.ToLowerInvariant();
        }

        private static IEnumerable<MediaFormatDefinition> CreateDefaults()
        {
            const MediaFormatCapabilities readableAndWritable =
                MediaFormatCapabilities.ReadMetadata |
                MediaFormatCapabilities.WriteMetadata |
                MediaFormatCapabilities.ReadArtwork |
                MediaFormatCapabilities.WriteArtwork;
            const MediaFormatCapabilities indexed =
                MediaFormatCapabilities.LibraryIndex | readableAndWritable |
                MediaFormatCapabilities.TranscodeSource;
            const MediaFormatCapabilities remux = MediaFormatCapabilities.Remux;

            yield return new(".dsf", "DSF", MediaFormatFamily.Dsf, indexed | remux);
            yield return new(".m4a", "MPEG-4 audio", MediaFormatFamily.Mp4,
                indexed | MediaFormatCapabilities.TranscodeDestination | remux);
            yield return new(".mp3", "MP3", MediaFormatFamily.Mp3, indexed | remux);
            yield return new(".flac", "FLAC", MediaFormatFamily.Flac,
                indexed | MediaFormatCapabilities.TranscodeDestination | remux);
            yield return new(".ogg", "Ogg Vorbis", MediaFormatFamily.Ogg,
                indexed | remux);
            yield return new(".opus", "Ogg Opus", MediaFormatFamily.Ogg,
                indexed | remux);
            yield return new(".spx", "Ogg Speex", MediaFormatFamily.Ogg,
                indexed | remux);
            yield return new(".wv", "WavPack", MediaFormatFamily.WavPack,
                indexed | MediaFormatCapabilities.TranscodeDestination | remux);

            // These variants have long been supported by direct metadata/artwork editing and by
            // iTunes reconciliation, but not by automatic library indexing. Preserve that boundary.
            yield return new(".mp4", "MPEG-4", MediaFormatFamily.Mp4,
                readableAndWritable | MediaFormatCapabilities.TranscodeSource | remux);
            yield return new(".m4p", "Protected MPEG-4 audio", MediaFormatFamily.Mp4, readableAndWritable);
            yield return new(".m4r", "MPEG-4 ringtone", MediaFormatFamily.Mp4,
                readableAndWritable | MediaFormatCapabilities.TranscodeSource | remux);
            yield return new(".m4b", "MPEG-4 audiobook", MediaFormatFamily.Mp4,
                readableAndWritable | MediaFormatCapabilities.TranscodeSource | remux);
            yield return new(".m4v", "MPEG-4 video", MediaFormatFamily.Mp4,
                readableAndWritable | MediaFormatCapabilities.TranscodeSource | remux);
        }
    }
}
