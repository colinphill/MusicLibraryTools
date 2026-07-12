using System.Xml.Linq;
using MusicLibrary.Core.Services;

namespace MusicLibrary.Core.Models;

public sealed record IngestMusicConfiguration
{
    public required string FfmpegPath { get; init; }
    public required string AacDestination { get; init; }
    public required string CdDestination { get; init; }
    public required string PairedCdDestination { get; init; }
    public required string HighResolutionDestination { get; init; }
    public int LengthLimit { get; init; } = 255;
    public int DiscNumLengthLimit { get; init; } = 255;
    public string AacEncoder { get; init; } = "libfdk_aac";
    public int AacBitrateKbps { get; init; } = 256;

    public static IngestMusicConfiguration Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var root = XDocument.Load(fullPath).Element("IngestMusicConfiguration")
            ?? throw new InvalidDataException("Missing <IngestMusicConfiguration> root element.");
        string Required(string name)
        {
            string? value = (string?)root.Element(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Missing or empty <{name}>.");
            return value.Trim();
        }
        int Positive(string name, int fallback)
        {
            string? value = (string?)root.Element(name);
            if (value is null) return fallback;
            if (!int.TryParse(value, out int parsed) || parsed <= 0)
                throw new InvalidDataException($"<{name}> must be a positive integer.");
            return parsed;
        }
        string ResolveRoot(string value) => Path.GetFullPath(value, Path.GetDirectoryName(fullPath)!);

        return new IngestMusicConfiguration
        {
            FfmpegPath = Required("FfmpegPath"),
            AacDestination = ResolveRoot(Required("AacDestination")),
            CdDestination = ResolveRoot(Required("CdDestination")),
            PairedCdDestination = ResolveRoot(Required("PairedCdDestination")),
            HighResolutionDestination = ResolveRoot(Required("HighResolutionDestination")),
            LengthLimit = Positive("LengthLimit", 255),
            DiscNumLengthLimit = Positive("DiscNumLengthLimit", 255),
            AacEncoder = ((string?)root.Element("AacEncoder") ?? "libfdk_aac").Trim(),
            AacBitrateKbps = Positive("AacBitrateKbps", 256),
        };
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(AacDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(CdDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(PairedCdDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(HighResolutionDestination);
        ArgumentException.ThrowIfNullOrWhiteSpace(AacEncoder);
        if (LengthLimit <= 0 || DiscNumLengthLimit <= 0 || AacBitrateKbps <= 0)
            throw new InvalidDataException("Length limits and AAC bitrate must be positive integers.");

        var document = new XDocument(
            new XElement("IngestMusicConfiguration",
                new XElement("FfmpegPath", FfmpegPath),
                new XElement("AacDestination", AacDestination),
                new XElement("CdDestination", CdDestination),
                new XElement("PairedCdDestination", PairedCdDestination),
                new XElement("HighResolutionDestination", HighResolutionDestination),
                new XElement("LengthLimit", LengthLimit),
                new XElement("DiscNumLengthLimit", DiscNumLengthLimit),
                new XElement("AacEncoder", AacEncoder),
                new XElement("AacBitrateKbps", AacBitrateKbps)));
        AtomicFile.Write(path, document.Save);
    }
}
