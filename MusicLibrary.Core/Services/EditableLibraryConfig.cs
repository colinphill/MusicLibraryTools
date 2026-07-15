using System.Xml.Linq;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>One &lt;IndexTarget&gt; row: a scan root plus its optional Offset/Set/Filter attributes.</summary>
public sealed class IndexTargetEntry
{
    public string Target { get; set; } = "";
    public string? Offset { get; set; }
    public List<int> Sets { get; set; } = [];
    public string? Filter { get; set; }
}

/// <summary>One repeatable playlist export destination.</summary>
public sealed class PlaylistTargetEntry
{
    public string Target { get; set; } = "";
    public string Type { get; set; } = "m3u";
    public List<int> Sets { get; set; } = [];
}

/// <summary>
/// A read/write view of the LibraryConfiguration XML (the read-only <c>LibraryConfiguration</c> in
/// MusicFileUtilities can't create or edit files). Round-trips the elements the GUI edits and
/// preserves any unknown top-level elements that were already in the file.
/// </summary>
public sealed class EditableLibraryConfig
{
    public string DatabaseFile { get; set; } = "cache.db";
    public int LengthLimit { get; set; } = 255;
    public int DiscNumLengthLimit { get; set; } = 255;
    public string? SyncTarget { get; set; }
    public List<PlaylistTargetEntry> PlaylistTargets { get; set; } = [];
    public List<IndexTargetEntry> IndexTargets { get; set; } = [];

    // Top-level elements we don't model explicitly, kept so a save doesn't drop them.
    private readonly List<XElement> _passthrough = [];

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "DatabaseFile", "LengthLimit", "DiscNumLengthLimit",
        "SyncTarget", "PlaylistTarget", "PlaylistType", "IndexTarget",
    };

    public static EditableLibraryConfig Load(string path)
    {
        var root = XDocument.Load(path).Element("LibraryConfiguration")
            ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");

        var config = new EditableLibraryConfig
        {
            DatabaseFile = (string?)root.Element("DatabaseFile") ?? "cache.db",
            SyncTarget = (string?)root.Element("SyncTarget"),
        };

        if (int.TryParse((string?)root.Element("LengthLimit"), out var ll)) config.LengthLimit = ll;
        if (int.TryParse((string?)root.Element("DiscNumLengthLimit"), out var dl)) config.DiscNumLengthLimit = dl;

        foreach (var e in root.Elements("IndexTarget"))
        {
            config.IndexTargets.Add(new IndexTargetEntry
            {
                Target = e.Value,
                Offset = (string?)e.Attribute("Offset"),
                Sets = [.. LibraryConfiguration.ParseScanSets((string?)e.Attribute("Set"))],
                Filter = (string?)e.Attribute("Filter"),
            });
        }

        // Preserve the old standalone PlaylistType value in the editor so a legacy row is easy to
        // migrate. Saving always writes Type and Set attributes on each PlaylistTarget.
        string? legacyPlaylistType = (string?)root.Element("PlaylistType");
        foreach (var e in root.Elements("PlaylistTarget"))
        {
            config.PlaylistTargets.Add(new PlaylistTargetEntry
            {
                Target = e.Value,
                Type = (string?)e.Attribute("Type") ?? legacyPlaylistType ?? "m3u",
                Sets = [.. LibraryConfiguration.ParseScanSets((string?)e.Attribute("Set"))],
            });
        }

        foreach (var e in root.Elements())
            if (!Known.Contains(e.Name.LocalName))
                config._passthrough.Add(new XElement(e));

        return config;
    }

    public void Save(string path)
    {
        var root = new XElement("LibraryConfiguration");

        root.Add(new XElement("DatabaseFile", DatabaseFile));

        foreach (var t in IndexTargets)
        {
            if (string.IsNullOrWhiteSpace(t.Target))
                continue;
            var e = new XElement("IndexTarget", t.Target);
            if (!string.IsNullOrEmpty(t.Offset)) e.SetAttributeValue("Offset", t.Offset);
            if (t.Sets.Count > 0) e.SetAttributeValue("Set", string.Join(",", t.Sets.Distinct().Order()));
            if (!string.IsNullOrEmpty(t.Filter)) e.SetAttributeValue("Filter", t.Filter);
            root.Add(e);
        }

        if (!string.IsNullOrWhiteSpace(SyncTarget)) root.Add(new XElement("SyncTarget", SyncTarget));
        var configuredSets = IndexTargets.SelectMany(indexTarget => indexTarget.Sets).ToHashSet();
        foreach (var target in PlaylistTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Target))
                continue;
            string type = target.Type?.Trim().ToLowerInvariant() ?? "";
            if (type is not ("m3u" or "wpl"))
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' must have a type of 'm3u' or 'wpl'.");
            if (target.Sets.Count == 0)
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' must select at least one scan set.");
            if (target.Sets.Any(set => set < 0))
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' contains a negative scan set.");
            int[] unknownSets = target.Sets.Where(set => !configuredSets.Contains(set)).ToArray();
            if (unknownSets.Length > 0)
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' references scan set(s) with no IndexTarget: " +
                    string.Join(",", unknownSets));

            root.Add(new XElement("PlaylistTarget",
                new XAttribute("Type", type),
                new XAttribute("Set", string.Join(",", target.Sets.Distinct().Order())),
                target.Target));
        }

        root.Add(new XElement("LengthLimit", LengthLimit));
        root.Add(new XElement("DiscNumLengthLimit", DiscNumLengthLimit));

        foreach (var e in _passthrough)
            root.Add(e);

        var document = new XDocument(root);
        AtomicFile.Write(path, document.Save);
    }
}
