using System.Xml.Linq;

namespace MusicLibrary.Core.Services;

/// <summary>One &lt;IndexTarget&gt; row: a scan root plus its optional Offset/Set/Filter attributes.</summary>
public sealed class IndexTargetEntry
{
    public string Target { get; set; } = "";
    public string? Offset { get; set; }
    public int Set { get; set; }
    public string? Filter { get; set; }
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
    public string? PlaylistTarget { get; set; }
    public string? PlaylistType { get; set; }
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
            PlaylistTarget = (string?)root.Element("PlaylistTarget"),
            PlaylistType = (string?)root.Element("PlaylistType"),
        };

        if (int.TryParse((string?)root.Element("LengthLimit"), out var ll)) config.LengthLimit = ll;
        if (int.TryParse((string?)root.Element("DiscNumLengthLimit"), out var dl)) config.DiscNumLengthLimit = dl;

        foreach (var e in root.Elements("IndexTarget"))
        {
            config.IndexTargets.Add(new IndexTargetEntry
            {
                Target = e.Value,
                Offset = (string?)e.Attribute("Offset"),
                Set = int.TryParse((string?)e.Attribute("Set"), out var s) ? s : 0,
                Filter = (string?)e.Attribute("Filter"),
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
            if (t.Set != 0) e.SetAttributeValue("Set", t.Set);
            if (!string.IsNullOrEmpty(t.Filter)) e.SetAttributeValue("Filter", t.Filter);
            root.Add(e);
        }

        if (!string.IsNullOrWhiteSpace(SyncTarget)) root.Add(new XElement("SyncTarget", SyncTarget));
        if (!string.IsNullOrWhiteSpace(PlaylistTarget)) root.Add(new XElement("PlaylistTarget", PlaylistTarget));
        if (!string.IsNullOrWhiteSpace(PlaylistType)) root.Add(new XElement("PlaylistType", PlaylistType));

        root.Add(new XElement("LengthLimit", LengthLimit));
        root.Add(new XElement("DiscNumLengthLimit", DiscNumLengthLimit));

        foreach (var e in _passthrough)
            root.Add(e);

        var document = new XDocument(root);
        AtomicFile.Write(path, document.Save);
    }
}
