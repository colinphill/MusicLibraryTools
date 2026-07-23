using System.Collections.Immutable;
using System.Text.Json;
using MusicFileUtilities;

namespace MusicLibrary.Core.Models;

/// <summary>
/// A portable, ordered metadata value set copied between editing surfaces.
/// </summary>
public sealed record MetadataClipboardPayload(
    MetadataFieldKey Field,
    ImmutableArray<string> Values);

/// <summary>
/// Encodes tag-aware clipboard data while accepting ordinary line-delimited text on paste.
/// </summary>
public static class MetadataClipboardCodec
{
    public const int CurrentVersion = 1;
    public const string Header =
        "MusicLibraryManager.MetadataClipboard/1";

    private sealed record Envelope(
        int Version,
        string? KnownField,
        string? CustomName,
        string[]? Values);

    public static string Encode(MetadataClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Values.Any(value => value is null))
            throw new ArgumentException(
                "Metadata clipboard values cannot be null.",
                nameof(payload));

        var envelope = new Envelope(
            CurrentVersion,
            payload.Field.KnownField?.ToString(),
            payload.Field.CustomName,
            payload.Values.ToArray());
        return Header + "\n" + JsonSerializer.Serialize(envelope);
    }

    public static bool TryDecode(
        string? text,
        out MetadataClipboardPayload? payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(text) ||
            !text.StartsWith(Header, StringComparison.Ordinal))
            return false;

        string json = text.Length > Header.Length &&
                      text[Header.Length] == '\r'
            ? text[(Header.Length + 1)..].TrimStart('\n')
            : text[(Header.Length)..].TrimStart('\n');
        try
        {
            Envelope envelope =
                JsonSerializer.Deserialize<Envelope>(json) ??
                throw new InvalidDataException(
                    "The metadata clipboard payload is empty.");
            if (envelope.Version != CurrentVersion)
                throw new InvalidDataException(
                    $"Metadata clipboard version {envelope.Version} is not supported.");
            if (envelope.Values is null ||
                envelope.Values.Any(value => value is null))
                throw new InvalidDataException(
                    "The metadata clipboard payload has invalid values.");

            bool hasKnown = !string.IsNullOrWhiteSpace(
                envelope.KnownField);
            bool hasCustom = !string.IsNullOrWhiteSpace(
                envelope.CustomName);
            if (hasKnown == hasCustom)
                throw new InvalidDataException(
                    "The metadata clipboard payload must identify exactly one field.");

            MetadataFieldKey field;
            if (hasKnown)
            {
                if (!Enum.TryParse(
                        envelope.KnownField,
                        ignoreCase: false,
                        out TagFields known) ||
                    known == TagFields.NullField)
                    throw new InvalidDataException(
                        $"The metadata clipboard field '{envelope.KnownField}' is not recognized.");
                field = MetadataFieldKey.Known(known);
            }
            else
            {
                field = MetadataFieldKey.Custom(
                    envelope.CustomName!);
            }

            payload = new(
                field,
                envelope.Values.ToImmutableArray());
            return true;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "The metadata clipboard payload is malformed.",
                error);
        }
    }

    public static MetadataClipboardPayload DecodeOrPlainText(
        string text,
        MetadataFieldKey fallbackField)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fallbackField);
        if (TryDecode(text, out MetadataClipboardPayload? payload))
            return payload!;

        ImmutableArray<string> values = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Where(value => value.Length > 0)
            .ToImmutableArray();
        if (values.Length == 0)
            throw new InvalidDataException(
                "The clipboard contains no metadata values.");
        return new(fallbackField, values);
    }
}
