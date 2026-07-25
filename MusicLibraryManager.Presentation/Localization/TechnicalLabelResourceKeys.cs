using MusicFileUtilities;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Maps semantic technical values to one shared localized label. The value
/// identities remain invariant; only their presentation uses these resources.
/// </summary>
public static class TechnicalLabelResourceKeys
{
    public static string? For<T>(T value) =>
        value switch
        {
            ReportFormat.Csv => "Technical.Format.Csv",
            ReportFormat.Html => "Technical.Format.Html",
            ReportFormat.Rtf => "Technical.Format.Rtf",
            ReportEncoding.Utf8 => "Technical.Encoding.Utf8",
            ReportEncoding.Utf8WithBom =>
                "Technical.Encoding.Utf8WithBom",
            ReportEncoding.Utf16LittleEndian =>
                "Technical.Encoding.Utf16Le",
            PlaylistWorkspaceEncoding.Utf8 =>
                "Technical.Encoding.Utf8",
            PlaylistWorkspaceEncoding.Utf8WithBom =>
                "Technical.Encoding.Utf8WithBom",
            PlaylistWorkspaceEncoding.Utf16LittleEndian =>
                "Technical.Encoding.Utf16Le",
            PlaylistLineEnding.CrLf =>
                "Technical.LineEnding.CrLf",
            PlaylistLineEnding.Lf =>
                "Technical.LineEnding.Lf",
            ID3v2Version.V22 => "Technical.Id3Version.V22",
            ID3v2Version.V23 => "Technical.Id3Version.V23",
            ID3v2Version.V24 => "Technical.Id3Version.V24",
            ID3TextEncodingPolicy.Latin1 =>
                "Technical.Encoding.Latin1",
            ID3TextEncodingPolicy.Utf16 =>
                "Technical.Encoding.Utf16",
            ID3TextEncodingPolicy.Utf8 =>
                "Technical.Encoding.Utf8",
            _ => null,
        };

    public static string? ForSettingsChoice(
        string group,
        string value) =>
        (group, value) switch
        {
            ("PlaylistType", "m3u") =>
                "Technical.PlaylistFormat.M3u",
            ("PlaylistType", "m3u8") =>
                "Technical.PlaylistFormat.M3u8",
            ("PlaylistType", "wpl") =>
                "Technical.PlaylistFormat.Wpl",
            ("PlaylistSourceType", "m3u") =>
                "Technical.PlaylistFormat.M3uFamily",
            ("PlaylistEncoding", "utf-8") =>
                "Technical.Encoding.Utf8",
            ("PlaylistEncoding", "utf-16") =>
                "Technical.Encoding.Utf16Le",
            ("PlaylistEncoding", "utf-16be") =>
                "Technical.Encoding.Utf16Be",
            ("PlaylistEncoding", "ascii") =>
                "Technical.Encoding.Ascii",
            ("PlaylistLineEnding", "crlf") =>
                "Technical.LineEnding.CrLf",
            ("PlaylistLineEnding", "lf") =>
                "Technical.LineEnding.Lf",
            _ => null,
        };
}
