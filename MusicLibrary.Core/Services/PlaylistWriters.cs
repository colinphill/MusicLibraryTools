using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MusicLibrary.Core.Services;

public enum PlaylistPathStyle
{
    AsProvided,
    Absolute,
    Relative,
}

public enum PlaylistLineEnding
{
    Platform,
    CrLf,
    Lf,
}

public sealed record PlaylistWriterTrack(
    string Path,
    int? DurationSeconds = null,
    string? DisplayText = null);

public sealed record PlaylistWriterRequest(
    string Format,
    string PlaylistName,
    string TargetDirectory,
    IReadOnlyList<PlaylistWriterTrack> Tracks);

public sealed record PlaylistWriterOutput(
    string DestinationPath,
    ImmutableArray<byte> Content,
    int TrackCount);

/// <summary>
/// Controls playlist serialization independently of playlist discovery and track mapping.
/// </summary>
public sealed record PlaylistWriterOptions
{
    public PlaylistPathStyle PathStyle { get; init; } = PlaylistPathStyle.AsProvided;
    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public bool EmitByteOrderMark { get; init; } = true;
    public PlaylistLineEnding LineEnding { get; init; } = PlaylistLineEnding.Platform;
    public bool IncludeExtendedInfo { get; init; } = true;
    public Func<string, string> FileNameTransform { get; init; } = static name => name;
    public int MaxTrackCount { get; init; } = int.MaxValue;
}

public sealed class PlaylistTrackLimitExceededException : InvalidOperationException
{
    public PlaylistTrackLimitExceededException(int trackCount, int maximumTrackCount)
        : base($"Playlist contains {trackCount} tracks; the configured maximum is {maximumTrackCount}.")
    {
        TrackCount = trackCount;
        MaximumTrackCount = maximumTrackCount;
    }

    public int TrackCount { get; }
    public int MaximumTrackCount { get; }
}

/// <summary>Serializes one playlist format without writing to the filesystem.</summary>
public interface IPlaylistWriter
{
    bool CanWrite(string format);

    PlaylistWriterOutput Write(
        PlaylistWriterRequest request,
        PlaylistWriterOptions? options = null);
}

/// <summary>Serializes extended or plain M3U and M3U8 playlists.</summary>
public sealed class M3uPlaylistWriter : IPlaylistWriter
{
    public bool CanWrite(string format) =>
        format.Equals("m3u", StringComparison.OrdinalIgnoreCase) ||
        format.Equals("m3u8", StringComparison.OrdinalIgnoreCase);

    public PlaylistWriterOutput Write(
        PlaylistWriterRequest request,
        PlaylistWriterOptions? options = null)
    {
        PlaylistWriterUtilities.Validate(request, options ??= new());
        if (!CanWrite(request.Format))
            throw new NotSupportedException($"'{request.Format}' is not an M3U playlist format.");

        string newLine = PlaylistWriterUtilities.GetNewLine(options.LineEnding);
        var text = new StringBuilder();
        if (options.IncludeExtendedInfo)
            text.Append("#EXTM3U").Append(newLine);

        foreach (PlaylistWriterTrack track in request.Tracks)
        {
            if (options.IncludeExtendedInfo)
            {
                int duration = track.DurationSeconds is null or 0
                    ? -1
                    : track.DurationSeconds.Value;
                text.Append("#EXTINF:")
                    .Append(duration.ToString(CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(track.DisplayText ?? string.Empty)
                    .Append(newLine);
            }
            text.Append(PlaylistWriterUtilities.ResolvePath(
                    track.Path, request.TargetDirectory, options.PathStyle))
                .Append(newLine);
        }

        string extension = request.Format.Equals("m3u8", StringComparison.OrdinalIgnoreCase)
            ? ".m3u8"
            : ".m3u";
        return PlaylistWriterUtilities.CreateOutput(request, options, extension, text.ToString());
    }
}

/// <summary>Serializes Windows Media Player WPL playlists.</summary>
public sealed class WplPlaylistWriter : IPlaylistWriter
{
    public bool CanWrite(string format) =>
        format.Equals("wpl", StringComparison.OrdinalIgnoreCase);

    public PlaylistWriterOutput Write(
        PlaylistWriterRequest request,
        PlaylistWriterOptions? options = null)
    {
        PlaylistWriterUtilities.Validate(request, options ??= new());
        if (!CanWrite(request.Format))
            throw new NotSupportedException($"'{request.Format}' is not a WPL playlist format.");

        var sequence = new XElement("seq",
            request.Tracks.Select(track => new XElement("media", new XAttribute("src",
                PlaylistWriterUtilities.ResolvePath(
                    track.Path, request.TargetDirectory, options.PathStyle)))));
        var document = new XDocument(
            new XProcessingInstruction("wpl", "version=\"1.0\""),
            new XElement("smil",
                new XElement("head",
                    new XElement("meta", new XAttribute("name", "Generator"),
                        new XAttribute("content", "CrossSyncPlaylists")),
                    new XElement("meta", new XAttribute("name", "ItemCount"),
                        new XAttribute("content", request.Tracks.Count.ToString(
                            CultureInfo.InvariantCulture))),
                    new XElement("title", request.PlaylistName)),
                new XElement("body", sequence)));

        var text = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            CloseOutput = false,
            NewLineChars = PlaylistWriterUtilities.GetNewLine(options.LineEnding),
        };
        using (var stringWriter = new StringWriter(text, CultureInfo.InvariantCulture))
        using (XmlWriter writer = XmlWriter.Create(stringWriter, settings))
            document.Save(writer);

        return PlaylistWriterUtilities.CreateOutput(request, options, ".wpl", text.ToString());
    }
}

internal static class PlaylistWriterUtilities
{
    public static void Validate(PlaylistWriterRequest request, PlaylistWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Format);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlaylistName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetDirectory);
        ArgumentNullException.ThrowIfNull(request.Tracks);
        ArgumentNullException.ThrowIfNull(options.Encoding);
        ArgumentNullException.ThrowIfNull(options.FileNameTransform);
        if (options.MaxTrackCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                "The maximum playlist track count cannot be negative.");
        if (request.Tracks.Count > options.MaxTrackCount)
            throw new PlaylistTrackLimitExceededException(
                request.Tracks.Count, options.MaxTrackCount);
        foreach (PlaylistWriterTrack track in request.Tracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            ArgumentException.ThrowIfNullOrWhiteSpace(track.Path);
        }
    }

    public static string GetNewLine(PlaylistLineEnding lineEnding) => lineEnding switch
    {
        PlaylistLineEnding.Platform => Environment.NewLine,
        PlaylistLineEnding.CrLf => "\r\n",
        PlaylistLineEnding.Lf => "\n",
        _ => throw new ArgumentOutOfRangeException(nameof(lineEnding)),
    };

    public static string ResolvePath(
        string path,
        string targetDirectory,
        PlaylistPathStyle pathStyle)
    {
        if (pathStyle == PlaylistPathStyle.AsProvided)
            return path;

        string target = Path.GetFullPath(targetDirectory);
        string absolute = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, target);
        return pathStyle switch
        {
            PlaylistPathStyle.Absolute => absolute,
            PlaylistPathStyle.Relative => Path.GetRelativePath(target, absolute),
            _ => throw new ArgumentOutOfRangeException(nameof(pathStyle)),
        };
    }

    public static PlaylistWriterOutput CreateOutput(
        PlaylistWriterRequest request,
        PlaylistWriterOptions options,
        string extension,
        string text)
    {
        string fileName = options.FileNameTransform(request.PlaylistName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("The playlist filename transformation returned an empty name.");
        string destination = Path.Combine(request.TargetDirectory, fileName + extension);
        return new(destination, Encode(text, options.Encoding, options.EmitByteOrderMark),
            request.Tracks.Count);
    }

    private static ImmutableArray<byte> Encode(string text, Encoding encoding, bool emitPreamble)
    {
        byte[] content = encoding.GetBytes(text);
        byte[] preamble = emitPreamble ? GetPreamble(encoding) : [];
        if (preamble.Length == 0)
            return content.ToImmutableArray();

        var bytes = ImmutableArray.CreateBuilder<byte>(preamble.Length + content.Length);
        bytes.AddRange(preamble);
        bytes.AddRange(content);
        return bytes.MoveToImmutable();
    }

    private static byte[] GetPreamble(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        _ => encoding.GetPreamble(),
    };
}
