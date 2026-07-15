using System.Xml.Serialization;

namespace MusicLibrary.Core.Services;

public sealed record CarCardBalanceSettings(
    int BalanceSize = 15,
    int RebalanceSize = 25,
    int MaxDepthDisparity = 0,
    int BalanceBreak = 20);

public sealed class CarBalancedPathNode
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
        "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
        "LPT7", "LPT8", "LPT9",
    ];
    private CarBalancedPathNode? _parent;
    private List<CarBalancedPathNode> _nodes = [];

    [XmlArray("Items"), XmlArrayItem("Item")]
    public List<string> Items { get; set; } = [];

    [XmlArray("Nodes"), XmlArrayItem("Node")]
    public CarBalancedPathNode[] SerializedNodes
    {
        get => [.. _nodes];
        set => Nodes = [.. value];
    }

    [XmlIgnore]
    public List<CarBalancedPathNode> Nodes
    {
        get => _nodes;
        set
        {
            _nodes = value;
            foreach (CarBalancedPathNode node in _nodes) node._parent = this;
        }
    }

    [XmlIgnore]
    public string Path
    {
        get
        {
            var parts = new Stack<string>();
            for (CarBalancedPathNode? current = this; current?._parent is not null;
                 current = current._parent)
                parts.Push(current.Name);
            return string.Join(System.IO.Path.DirectorySeparatorChar, parts);
        }
    }

    [XmlIgnore]
    public string Name
    {
        get
        {
            CarBalancedPathNode? left = LeftNode;
            CarBalancedPathNode? right = RightNode;
            string leftName = FirstName;
            if (left is not null)
            {
                string adjacent = left.LastName;
                int length = 1;
                while (length < leftName.Length &&
                       (adjacent.StartsWith(leftName[..length], StringComparison.CurrentCultureIgnoreCase) ||
                        ReservedNames.Contains(leftName[..length], StringComparer.CurrentCultureIgnoreCase)))
                    length++;
                leftName = leftName[..length];
            }
            string rightName = LastName;
            if (right is not null)
            {
                string adjacent = right.FirstName;
                int length = 1;
                while (length < adjacent.Length && length < rightName.Length &&
                       (rightName.StartsWith(adjacent[..length], StringComparison.CurrentCultureIgnoreCase) ||
                        ReservedNames.Contains(adjacent[..length], StringComparer.CurrentCultureIgnoreCase)))
                    length++;
                if (length < rightName.Length) rightName = rightName[..length];
            }
            if (left is null) leftName = ExtendBoundary(leftName, rightName);
            if (right is null) rightName = ExtendBoundary(rightName, leftName);
            return (leftName + ".." + rightName).Trim('\0', '\t', ' ', '.');
        }
    }

    public CarBalancedPathNode() { }

    private CarBalancedPathNode(CarBalancedPathNode parent, IEnumerable<string> items)
    {
        _parent = parent;
        Items = [.. items];
    }

    public CarBalancedPathNode DeepClone()
    {
        var clone = new CarBalancedPathNode { Items = [.. Items] };
        clone.Nodes = Nodes.Select(node => node.DeepClone()).ToList();
        return clone;
    }

    public void AddItem(string item, CarCardBalanceSettings settings)
    {
        CarBalancedPathNode node = FindNode(item, settings);
        if (!node.Items.Contains(item, StringComparer.CurrentCultureIgnoreCase))
        {
            node.Items.Add(item);
            node.Items.Sort(StringComparer.CurrentCultureIgnoreCase);
        }
    }

    public void RemoveItem(string item, CarCardBalanceSettings settings)
    {
        CarBalancedPathNode node = FindNode(item, settings);
        string? existing = node.Items.FirstOrDefault(candidate =>
            candidate.Equals(item, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null) return;
        node.Items.Remove(existing);
        if (node.Items.Count == 0 && node.Nodes.Count == 0 && node._parent is not null)
            node._parent.RemoveNode(node);
    }

    public CarBalancedPathNode FindNode(string name, CarCardBalanceSettings settings)
    {
        foreach (CarBalancedPathNode node in Nodes)
        {
            if (node.Nodes.Count == 0 && node.Items.Count == 0) continue;
            if (string.Compare(name, node.FirstName, true) < 0 &&
                StartMatchLength(node.FirstName, name) <= settings.BalanceBreak)
                return node.FindNode(name, settings);
            if (string.Compare(name, node.LastName, true) <= 0 ||
                StartMatchLength(node.LastName, name) > settings.BalanceBreak)
                return node.FindNode(name, settings);
        }
        return Nodes.Count == 0 ? this : Nodes[^1];
    }

    public void Rebalance(CarCardBalanceSettings settings, bool force = false, bool ignoreBreak = false)
    {
        if (force || Nodes.Count > settings.RebalanceSize)
        {
            Items = [.. GetAllItems()];
            Nodes.Clear();
        }
        Items.Sort(StringComparer.CurrentCultureIgnoreCase);
        int partitionedItemCount = Items.Count;
        if (Items.Count > settings.RebalanceSize)
        {
            int sqrt = (int)Math.Sqrt(Items.Count);
            int divisor = Math.Min(sqrt, settings.BalanceSize);
            int modulus = Items.Count % divisor;
            int last = -1, breakDelta = 0;
            for (int index = 1; index <= divisor && last < Items.Count - 1; index++)
            {
                int first = last + 1;
                last = first - breakDelta + Items.Count / divisor - 1;
                if (modulus-- > 0) last++;
                int leftBreak = 0, rightBreak = 0;
                while (!ignoreBreak && last + leftBreak > first && last + rightBreak < Items.Count - 1 &&
                       (StartMatchLength(Items[last + leftBreak], Items[last + leftBreak + 1]) > settings.BalanceBreak ||
                        StartMatchLength(Items[first], Items[last + leftBreak]) > settings.BalanceBreak))
                    leftBreak--;
                while (!ignoreBreak && last + rightBreak < Items.Count - 1 &&
                       (StartMatchLength(Items[last + rightBreak], Items[last + rightBreak + 1]) > settings.BalanceBreak ||
                        StartMatchLength(Items[first], Items[last + rightBreak]) > settings.BalanceBreak))
                    rightBreak++;
                breakDelta = -leftBreak < rightBreak ? leftBreak : rightBreak;
                last += breakDelta;
                Nodes.Add(new(this, Items.GetRange(first, last - first + 1)));
            }
            Items.Clear();
        }
        // A break-preserving partition can put every item back into one child when all names
        // share a long prefix. Descending into that unchanged child never makes progress.
        if (!ignoreBreak && partitionedItemCount > settings.RebalanceSize && Nodes.Count == 1 &&
            Nodes[0].GetAllItems().Count() == partitionedItemCount)
        {
            Rebalance(settings, force: true, ignoreBreak: true);
            return;
        }
        foreach (CarBalancedPathNode node in Nodes)
            node.Rebalance(settings, ignoreBreak: ignoreBreak);
        // A forced, break-agnostic rebuild is the terminal balancing attempt. Repeating the
        // identical partition cannot improve its depth and made the legacy implementation loop.
        if (!ignoreBreak && MaxDepth - MinDepth > settings.MaxDepthDisparity)
            Rebalance(settings, force: true, ignoreBreak: force || ignoreBreak);
    }

    public IEnumerable<string> GetAllItems() => Nodes.SelectMany(node => node.GetAllItems()).Concat(Items);

    private void RemoveNode(CarBalancedPathNode node)
    {
        Nodes.Remove(node);
        if (Nodes.Count == 0 && Items.Count == 0 && _parent is not null)
            _parent.RemoveNode(this);
    }

    private int MinDepth => Nodes.Count == 0 ? 0 : 1 + Nodes.Min(node => node.MinDepth);
    private int MaxDepth => Nodes.Count == 0 ? 0 : 1 + Nodes.Max(node => node.MaxDepth);
    private string FirstName => Items.Count > 0 ? Items[0] : Nodes.Count > 0 ? Nodes[0].FirstName : "(empty)";
    private string LastName => Items.Count > 0 ? Items[^1] : Nodes.Count > 0 ? Nodes[^1].LastName : "(empty)";

    private CarBalancedPathNode? LeftNode
    {
        get
        {
            if (_parent is null) return null;
            int index = _parent.Nodes.IndexOf(this);
            return index > 0 ? _parent.Nodes[index - 1] : _parent.LeftNode;
        }
    }

    private CarBalancedPathNode? RightNode
    {
        get
        {
            if (_parent is null) return null;
            int index = _parent.Nodes.IndexOf(this);
            return index < _parent.Nodes.Count - 1 ? _parent.Nodes[index + 1] : _parent.RightNode;
        }
    }

    private static int StartMatchLength(string first, string second)
    {
        int length = Math.Min(first.Length, second.Length);
        for (int index = 0; index < length; index++)
            if (char.ToLower(first[index]) != char.ToLower(second[index])) return index - 1;
        return length;
    }

    private static string ExtendBoundary(string value, string other)
    {
        int length = Math.Min(value.Length, Math.Max(1, other.Length));
        while (length < value.Length)
        {
            string candidate = value[..length];
            if (!candidate.Equals(other, StringComparison.CurrentCultureIgnoreCase) &&
                !ReservedNames.Contains(candidate, StringComparer.CurrentCultureIgnoreCase))
                return candidate;
            length++;
        }
        return value;
    }
}

[XmlRoot("SyncDatabase")]
public sealed class CarSyncDatabase
{
    public CarBalancedPathNode ArtistStructure { get; set; } = new();
    public CarBalancedPathNode ContributingArtistStructure { get; set; } = new();
    public CarBalancedPathNode AlbumsStructure { get; set; } = new();
    public CarFileDatabase FileDatabase { get; set; } = new();
    public int BalanceSize { get; set; } = 15;
    public int RebalanceSize { get; set; } = 25;
    public int BalanceBreak { get; set; } = 20;
    public int MaxDepthDisparity { get; set; }

    [XmlIgnore] public Dictionary<string, List<string>> ArtistMap { get; set; } =
        new(StringComparer.CurrentCultureIgnoreCase);
    [XmlIgnore] public Dictionary<string, List<string>> ContributingArtistMap { get; set; } =
        new(StringComparer.CurrentCultureIgnoreCase);

    [XmlArray, XmlArrayItem("MapElement")]
    public CarArtistMapElement[] ArtistMapElements
    {
        get => ArtistMap.Select(pair => new CarArtistMapElement(pair.Key, pair.Value)).ToArray();
        set => ArtistMap = value.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.CurrentCultureIgnoreCase);
    }

    [XmlArray, XmlArrayItem("MapElement")]
    public CarArtistMapElement[] ContributingArtistMapElements
    {
        get => ContributingArtistMap.Select(pair => new CarArtistMapElement(pair.Key, pair.Value)).ToArray();
        set => ContributingArtistMap = value.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.CurrentCultureIgnoreCase);
    }

    public CarSyncHashSet HashSet { get; set; } = new();
}

public sealed class CarFileDatabase
{
    [XmlArray("Artists"), XmlArrayItem("Artist")]
    public List<CarArtist> Artists { get; set; } = [];

    public CarArtist FindArtist(string name)
    {
        CarArtist? artist = Artists.SingleOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (artist is null) Artists.Add(artist = new() { Name = name });
        return artist;
    }
}

public sealed class CarArtist
{
    public string Name { get; set; } = "";
    [XmlArray("Albums"), XmlArrayItem("Album")]
    public List<CarAlbum> Albums { get; set; } = [];
    public CarAlbum FindAlbum(string name)
    {
        CarAlbum? album = Albums.SingleOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (album is null) Albums.Add(album = new() { Name = name });
        return album;
    }
}

public sealed class CarAlbum
{
    public string Name { get; set; } = "";
    [XmlArray("Tracks"), XmlArrayItem("Track")]
    public List<CarTrack> Tracks { get; set; } = [];
}

public sealed class CarTrack
{
    public int Index { get; set; }
    public int DiscNumber { get; set; }
    [XmlIgnore] public int Year { get; set; }
    public DateTime LastModifiedTime { get; set; }
    [XmlIgnore] public string Loc { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Name { get; set; } = "";
    [XmlIgnore] public string ContributingArtist { get; set; } = "";
    [XmlIgnore] public string PersistentID { get; set; } = "";
}

public sealed class CarTrackComparer : IEqualityComparer<CarTrack>
{
    public bool Equals(CarTrack? left, CarTrack? right) => left is not null && right is not null &&
        left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
        left.Index == right.Index && left.DiscNumber == right.DiscNumber &&
        left.FileName.Equals(right.FileName, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode(CarTrack value) => HashCode.Combine(value.Index, value.DiscNumber,
        StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
        StringComparer.OrdinalIgnoreCase.GetHashCode(value.FileName));
}

public sealed class CarArtistMapElement
{
    public string Key { get; set; } = "";
    [XmlArray, XmlArrayItem("Item")]
    public List<string> Value { get; set; } = [];
    public CarArtistMapElement() { }
    public CarArtistMapElement(string key, List<string> value) => (Key, Value) = (key, value);
}

public sealed class CarPlaylistHash
{
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class CarPlaylistSetElement
{
    public string Key { get; set; } = "";
    public List<CarPlaylistHash> Value { get; set; } = [];
}

public sealed class CarSyncHashSet
{
    [XmlArray] public List<CarPlaylistHash> Playlists { get; set; } = [];
    [XmlArray] public List<CarPlaylistHash> Albums { get; set; } = [];
    [XmlIgnore] public Dictionary<string, List<CarPlaylistHash>> Artists { get; set; } =
        new(StringComparer.CurrentCultureIgnoreCase);
    [XmlIgnore] public Dictionary<string, List<CarPlaylistHash>> ContributingArtists { get; set; } =
        new(StringComparer.CurrentCultureIgnoreCase);

    [XmlArray, XmlArrayItem("PlaylistElement")]
    public CarPlaylistSetElement[] ArtistPlaylists
    {
        get => Artists.Select(pair => new CarPlaylistSetElement { Key = pair.Key, Value = pair.Value }).ToArray();
        set => Artists = value.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.CurrentCultureIgnoreCase);
    }

    [XmlArray, XmlArrayItem("PlaylistElement")]
    public CarPlaylistSetElement[] ContributingArtistPlaylists
    {
        get => ContributingArtists.Select(pair => new CarPlaylistSetElement { Key = pair.Key, Value = pair.Value }).ToArray();
        set => ContributingArtists = value.ToDictionary(item => item.Key, item => item.Value,
            StringComparer.CurrentCultureIgnoreCase);
    }
}
