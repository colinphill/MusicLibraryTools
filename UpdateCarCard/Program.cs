using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using MusicFileUtilities;
using MusicLibraryTools;
using System.Xml.Serialization;
using System.Security.Cryptography;
using ConsoleTools;
using iTunes;

namespace UpdateCarCard
{

    static class Extensions
    {
        public static string LimitLength(this string val, int length)
        {
            int l = Math.Min(length, val.Length);
            return val.Substring(0, l).Trim(" \t".ToCharArray());
        }
    }

    public class Program
    {
        static string FixArtistPath(string item)
        {
            if (item.StartsWith("a ", StringComparison.CurrentCultureIgnoreCase))
                item = item.Substring(2);
            if (item.StartsWith("the ", StringComparison.CurrentCultureIgnoreCase))
                item = item.Substring(4);
            return item.FixPath();
        }

        static void DeleteEmptyFolders(string basedir)
        {
            foreach (string dir in Directory.GetDirectories(basedir))
                DeleteEmptyFolders(dir);
            if ((Directory.GetDirectories(basedir).Length == 0) && (Directory.GetFiles(basedir).Length == 0))
                Directory.Delete(basedir);
        }

        static void IndexDirectory(string basedir, Dictionary<string, bool> hits)
        {
            foreach (string dir in Directory.GetDirectories(basedir))
                IndexDirectory(dir, hits);
            foreach (string file in Directory.GetFiles(basedir, "*.m3u"))
                File.Delete(file);
            foreach (string file in Directory.GetFiles(basedir))
                hits.Add(file.ToLower(), false);
        }

        static int BALANCE_SIZE = 15; // 20;
        static int REBALANCE_SIZE = 25; // 30;
        static int MAX_DEPTH_DISPARITY = 0;
        static int BALANCE_BREAK = 20;

        public class BalancedPathNode : XmlCloneable<BalancedPathNode>
        {
            static readonly string[] reservednames_ = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2",
                "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            
            private BalancedPathNode parent_ = null;
            private List<BalancedPathNode> nodes_ = new List<BalancedPathNode>();
            private List<string> items_ = new List<string>();

            [XmlArray(ElementName = "Items")]
            [XmlArrayItem(ElementName = "Item")]
            public List<string> Items
            {
                get { return items_; }
                set { items_ = value; }
            }

            [XmlArray(ElementName = "Nodes")]
            [XmlArrayItem(ElementName = "Node")]
            public BalancedPathNode[] SerNodes
            {
                get { return nodes_.ToArray(); }
                set { Nodes = new List<BalancedPathNode>(value); }
            }


            [XmlIgnore]
            public List<BalancedPathNode> Nodes
            {
                get { return nodes_; }
                set {
                    nodes_ = value;
                    foreach (BalancedPathNode node in nodes_)
                        node.parent_ = this;
                }
            }

            private int MinDepth
            {
                get
                {
                    if (nodes_.Count == 0)
                        return 0;
                    return 1 + nodes_.Min(n => n.MinDepth);
                }
            }

            private int MaxDepth
            {
                get
                {
                    if (nodes_.Count == 0)
                        return 0;
                    return 1 + nodes_.Max(n => n.MaxDepth);
                }
            }

            public void AddItem(string item)
            {
                BalancedPathNode node = FindNode(item);
                if (!node.items_.Contains(item, StringComparer.CurrentCultureIgnoreCase))
                {
                    node.items_.Add(item);
                    node.items_.Sort(StringComparer.CurrentCultureIgnoreCase);
                }
            }

            public void RemoveItem(string item)
            {
                BalancedPathNode node = FindNode(item);
                if (node.items_.Contains(item))
                {
                    node.items_.Remove(item);
                    if (((nodes_.Count == 0) && (items_.Count == 0)) && (parent_ != null))
                        parent_.RemoveNode(this);
                }
            }

            private void RemoveNode(BalancedPathNode node)
            {
                nodes_.Remove(node);
                if (((nodes_.Count == 0) && (items_.Count == 0))&&(parent_ != null))
                    parent_.RemoveNode(this);
            }

            public bool HasItem(string item)
            {
                return FindNode(item).items_.Contains(item, StringComparer.CurrentCultureIgnoreCase);
            }

            static int StartMatchLength(string a, string b)
            {
                a = a.ToLower();
                b = b.ToLower();
                int complen = a.Length < b.Length ? a.Length : b.Length;
                for (int i = 0; i < complen; i++)
                    if (a[i] != b[i])
                        return i - 1;
                return complen;
            }

            public BalancedPathNode FindNode(string name)
            {
                for (int i = 0; i < nodes_.Count; i++)
                {
                    if ((nodes_[i].nodes_.Count == 0) && (nodes_[i].items_.Count == 0))
                        continue;
                    if ((string.Compare(name, nodes_[i].FirstName, true) < 0) && ((StartMatchLength(nodes_[i].FirstName, name) <= BALANCE_BREAK)))
                        return nodes_[i].FindNode(name); // Keep Diving
                    if ((string.Compare(name, nodes_[i].LastName, true) <= 0)||((StartMatchLength(nodes_[i].LastName, name) > BALANCE_BREAK)))
                        return nodes_[i].FindNode(name);
                }
                if (nodes_.Count == 0)
                    return this;
                return nodes_[nodes_.Count - 1];
            }

            private BalancedPathNode LeftNode
            {
                get
                {
                    if (parent_ == null)
                        return null;
                    int index = parent_.nodes_.IndexOf(this);
                    if (index > 0)
                        return parent_.nodes_[index - 1];
                    return parent_.LeftNode;
                }
            }

            private BalancedPathNode RightNode
            {
                get
                {
                    if (parent_ == null)
                        return null;
                    int index = parent_.nodes_.IndexOf(this);
                    if (index < parent_.nodes_.Count - 1)
                        return parent_.nodes_[index + 1];
                    return parent_.RightNode;
                }
            }

            private string FirstName
            {
                get
                {
                    if (items_.Count == 0)
                    {
                        if (nodes_.Count == 0)
                            return "(empty)";
                        return nodes_[0].FirstName;
                    }
                    return items_[0];
                }
            }

            private string LastName
            {
                get
                {
                    if (items_.Count == 0)
                    {
                        if (nodes_.Count == 0)
                            return "(empty)";
                        return nodes_[nodes_.Count - 1].LastName;
                    }
                    return items_[items_.Count - 1];
                }
            }

            public string Name
            {
                get
                {
                    BalancedPathNode leftnode = LeftNode;
                    BalancedPathNode rightnode = RightNode;
                    string leftname = FirstName;
                    if (leftnode != null)
                    {
                        string leftnodename = leftnode.LastName;
                        int length = 1;
                        while (leftnodename.StartsWith(leftname.Substring(0, length), StringComparison.CurrentCultureIgnoreCase) || reservednames_.Contains(leftname.Substring(0, length), StringComparer.CurrentCultureIgnoreCase))
                            length++;
                        leftname = leftname.Substring(0, length);
                    }

                    string rightname = LastName;
                    if (rightnode != null)
                    {
                        string rightnodename = rightnode.FirstName;
                        int length = 1;
                        while (rightname.StartsWith(rightnodename.Substring(0, length), StringComparison.CurrentCultureIgnoreCase) || reservednames_.Contains(rightnodename.Substring(0, length), StringComparer.CurrentCultureIgnoreCase))
                            length++;
                        if (length < rightname.Length)
                            rightname = rightname.Substring(0, length);
                    }

                    if (leftnode == null)
                    {
                        string templeftname;
                        int len = rightname.Length - 1;
                        if (len < leftname.Length)
                        {
                            do
                            {
                                templeftname = leftname.Substring(0, ++len);
                            }
                            while (templeftname.Equals(rightname, StringComparison.CurrentCultureIgnoreCase) || reservednames_.Contains(templeftname.Substring(0, len), StringComparer.CurrentCultureIgnoreCase));
                            leftname = templeftname;
                        }
                    }
                    if (rightnode == null)
                    {
                        string temprightname;
                        int len = leftname.Length - 1;
                        if (len < rightname.Length)
                        {
                            do
                            {
                                temprightname = rightname.Substring(0, ++len);
                            }
                            while (temprightname.Equals(leftname, StringComparison.CurrentCultureIgnoreCase) || reservednames_.Contains(temprightname.Substring(0, len), StringComparer.CurrentCultureIgnoreCase));
                            rightname = temprightname;
                        }
                    }

                    return (leftname + ".." + rightname).Trim();
                }
            }

            private string IPath
            {
                get
                {
                    if (parent_ == null)
                        return string.Empty;
                    return parent_.IPath + "\\" + Name;
                }
            }

            public string Path
            {
                get
                {
                    string path = IPath;
                    if (path.StartsWith("\\"))
                        return path.Substring(1);
                    return path;
                }
            }

            public BalancedPathNode(BalancedPathNode parent, IEnumerable<string> items)
            {
                parent_ = parent;
                items_ = new List<string>(items);
            }

            public BalancedPathNode()
            {

            }

            private IEnumerable<string> GetAllItems()
            {
                List<string> items = new List<string>();
                foreach (BalancedPathNode node in nodes_)
                    items.AddRange(node.GetAllItems());
                items.AddRange(items_);
                return items;
            }

            public IEnumerable<BalancedPathNode> GetAllNodes()
            {
                List<BalancedPathNode> nodes = new List<BalancedPathNode>();
                foreach (BalancedPathNode node in nodes_)
                    nodes.AddRange(node.GetAllNodes());
                nodes.Add(this);
                return nodes;
            }

            public void Rebalance(bool force = false)
            {
                if (force || (nodes_.Count > REBALANCE_SIZE))
                {
                    items_ = new List<string>(GetAllItems());
                    nodes_.Clear();
                }

                items_.Sort(StringComparer.CurrentCultureIgnoreCase);

                if (items_.Count > REBALANCE_SIZE)
                {
                    int sqrt = (int)Math.Sqrt(items_.Count);
                    int divisor = sqrt > BALANCE_SIZE ? BALANCE_SIZE : sqrt;
                    int modulus = items_.Count % divisor;
                    int last = -1;
                    for (int i = 1; i <= divisor; i++)
                    {
                        int first = last + 1;
                        last = first + items_.Count / divisor - 1;
                        if (modulus > 0)
                        {
                            last++;
                            modulus--;
                        }
                        while ((last < (items_.Count - 1)) && ((StartMatchLength(items_[last], items_[last + 1]) > BALANCE_BREAK) || (StartMatchLength(items_[first], items_[last]) > BALANCE_BREAK)))
                            last++;
                        nodes_.Add(new BalancedPathNode(this, items_.GetRange(first, last - first + 1)));
                    }
                    items_.Clear();
                }

                foreach (BalancedPathNode node in nodes_)
                    node.Rebalance();

                if ((MaxDepth - MinDepth) > MAX_DEPTH_DISPARITY)
                    Rebalance(true);
            }

            public class BalancedPathDelta
            {

            }

            public class AddPathDelta : BalancedPathDelta
            {
                public string Path { get; set; }
            }

            public class RemovePathDelta : BalancedPathDelta
            {
                public string Path { get; set; }
            }

            public class MovePathDelta : BalancedPathDelta
            {
                public string Item { get; set; }
                public string OldPath { get; set; }
                public string NewPath { get; set; }
            }

            public IEnumerable<BalancedPathDelta> ComputeDelta(BalancedPathNode other)
            {
                List<BalancedPathDelta> deltas = new List<BalancedPathDelta>();
                string[] items = GetAllItems().ToArray();
                string[] oitems = other.GetAllItems().ToArray();
                Dictionary<string, string> paths = items.ToDictionary(s => s, s => FindNode(s).Path);
                Dictionary<string, string> opaths = oitems.ToDictionary(s => s, s => other.FindNode(s).Path);
                string[] allpaths = GetAllNodes().Select(n => n.Path).Distinct().ToArray();
                string[] allopaths = other.GetAllNodes().Select(n => n.Path).Distinct().ToArray();

                string[] commonpaths = allpaths.Intersect(allopaths, StringComparer.CurrentCultureIgnoreCase).Distinct().ToArray();
                string[] removedpaths = allopaths.Where(s => !commonpaths.Contains(s, StringComparer.CurrentCultureIgnoreCase)).Distinct().ToArray();

                foreach (string p in removedpaths.OrderByDescending(s => s))
                    deltas.Add(new RemovePathDelta() { Path = p });
                string[] addedpaths = paths.Values.Where(s => !commonpaths.Contains(s, StringComparer.CurrentCultureIgnoreCase)).Distinct().ToArray();
                foreach (string p in addedpaths.OrderBy(s => s))
                    deltas.Add(new AddPathDelta() { Path = p });

                string[] commonitems = items.Intersect(oitems, StringComparer.CurrentCultureIgnoreCase).ToArray();
                string[] moves = commonitems.Where(s => !string.Equals(paths[s], opaths[s], StringComparison.CurrentCultureIgnoreCase)).ToArray();
                foreach (string m in moves)
                    deltas.Add(new MovePathDelta() { Item = m, OldPath = opaths[m], NewPath = paths[m] });

                return deltas;
            }

        }

        public class FileDatabase
        {
            public class Track
            {
                public int Index { get; set; }
                [XmlIgnore]
                public int Year { get; set; }
                public DateTime LastModifiedTime { get; set; }
                [XmlIgnore]
                public string Loc { get; set; }
                public string FileName { get; set; }
                public string Name { get; set; }
                [XmlIgnore]
                public string ContributingArtist { get; set; }
                [XmlIgnore]
                public string PersistentID { get; set; }
            }

            public class Album
            {
                public string Name { get; set; }
                [XmlArray("Tracks")]
                [XmlArrayItem("Track")]
                public List<Track> Tracks { get; set; } = new List<Track>();
                public Album(string name)
                {
                    Name = name;
                }
                public Album()
                {

                }
            }
            public class Artist
            {
                public string Name { get; set; }
                [XmlArray("Albums")]
                [XmlArrayItem("Album")]
                public List<Album> Albums { get; set; } = new List<Album>();
                public Artist(string name)
                {
                    Name = name;
                }
                public Artist()
                {

                }
                public Album FindAlbum(string name)
                {
                    Album def = new Album(name);
                    Album al = Albums.SingleOrDefault(a => a.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                    if (al == null)
                        Albums.Add(al = def);
                    return al;
                }
            }

            [XmlArray("Artists")]
            [XmlArrayItem("Artist")]
            public List<Artist> Artists { get; set; } = new List<Artist>();

            public Artist FindArtist(string name)
            {
                Artist def = new Artist(name);
                Artist ar = Artists.SingleOrDefault(a => a.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                if (ar == null)
                    Artists.Add(ar = def);
                return ar;
            }

            public class FileDatabaseDelta
            {
            }

            public class AddArtistDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
            }

            public class RemoveArtistDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
            }

            public class AddAlbumDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
                public string Album { get; set; }
            }

            public class RemoveAlbumDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
                public string Album { get; set; }
            }

            public class UpdateTrackDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
                public string Album { get; set; }
                public int Index { get; set; }
                public string FileName { get; set; }
                public string Loc { get; set; }
            }

            public class RemoveTrackDelta : FileDatabaseDelta
            {
                public string Artist { get; set; }
                public string Album { get; set; }
                public int Index { get; set; }
                public string FileName { get; set; }
            }

            public IEnumerable<FileDatabaseDelta> ComputeDelta(FileDatabase other)
            {
                List<FileDatabaseDelta> deltas = new List<FileDatabaseDelta>();

                string[] commonartists = other.Artists.Select(a => a.Name).Intersect(Artists.Select(a => a.Name)).ToArray();
                string[] removedartists = other.Artists.Select(a => a.Name).Where(n => !commonartists.Contains(n)).ToArray();
                string[] addedartists = Artists.Select(a => a.Name).Where(n => !commonartists.Contains(n)).ToArray();

                foreach (string artist in removedartists)
                {
                    foreach (Album album in other.Artists.Single(a => a.Name == artist).Albums)
                    {
                        foreach (Track track in album.Tracks)
                            deltas.Add(new RemoveTrackDelta() { Artist = artist, Album = album.Name, FileName = track.FileName, Index = track.Index });
                        deltas.Add(new RemoveAlbumDelta() { Artist = artist, Album = album.Name });
                    }
                    deltas.Add(new RemoveArtistDelta() { Artist = artist });
                }
                foreach (string artist in addedartists)
                {
                    foreach (Album album in Artists.Single(a => a.Name == artist).Albums)
                    {
                        foreach (Track track in album.Tracks)
                            deltas.Add(new UpdateTrackDelta() { Artist = artist, Album = album.Name, FileName = track.FileName, Index = track.Index, Loc = track.Loc });
                        deltas.Add(new AddAlbumDelta() { Artist = artist, Album = album.Name });
                    }
                    deltas.Add(new AddArtistDelta() { Artist = artist });
                }

                foreach (string name in commonartists)
                {
                    Artist artist = Artists.Single(a => a.Name == name);
                    Artist oartist = other.Artists.Single(a => a.Name == name);
                    string[] commonalbums = oartist.Albums.Select(a => a.Name).Intersect(artist.Albums.Select(a => a.Name)).ToArray();
                    string[] removedalbums = oartist.Albums.Select(a => a.Name).Where(n => !commonalbums.Contains(n)).ToArray();
                    string[] addedalbums = artist.Albums.Select(a => a.Name).Where(n => !commonalbums.Contains(n)).ToArray();

                    foreach (string album in removedalbums)
                    {
                        foreach (Track track in oartist.Albums.Single(a => a.Name == album).Tracks)
                            deltas.Add(new RemoveTrackDelta() { Artist = name, Album = album, FileName = track.FileName, Index = track.Index });
                        deltas.Add(new RemoveAlbumDelta() { Artist = name, Album = album });
                    }
                    foreach (string album in addedalbums)
                    {
                        foreach (Track track in artist.Albums.Single(a => a.Name == album).Tracks)
                            deltas.Add(new UpdateTrackDelta() { Artist = name, Album = album, FileName = track.FileName, Index = track.Index, Loc = track.Loc });
                        deltas.Add(new AddAlbumDelta() { Artist = name, Album = album });
                    }

                    foreach (string alname in commonalbums)
                    {
                        Album album = artist.Albums.Single(a => a.Name == alname);
                        Album oalbum = oartist.Albums.Single(a => a.Name == alname);

                        foreach (Track ot in oalbum.Tracks)
                        {
                            Track[] tracks = album.Tracks.Where(t => t.FileName == ot.FileName).ToArray();
                            if (tracks.Length == 1)
                            {
                                if (tracks[0].LastModifiedTime > ot.LastModifiedTime)
                                    deltas.Add(new UpdateTrackDelta() { Artist = name, Album = alname, Index = ot.Index, FileName = ot.FileName, Loc = tracks[0].Loc });
                            }
                            else
                                deltas.Add(new RemoveTrackDelta() { Artist = name, Album = alname, Index = ot.Index, FileName = ot.FileName });
                        }

                        foreach (Track tt in album.Tracks)
                        {
                            Track[] otracks = oalbum.Tracks.Where(t => t.FileName == tt.FileName).ToArray();
                            if (otracks.Length == 0)
                                deltas.Add(new UpdateTrackDelta() { Artist = name, Album = alname, Index = tt.Index, FileName = tt.FileName, Loc = tt.Loc });
                        }
                    }
                }

                return deltas;
            }
        }

        public class XmlCloneable<T>
        {
            private static XmlSerializer ser_ = new XmlSerializer(typeof(T));
            public void Serialize(Stream s)
            {
                ser_.Serialize(s, this);
            }
            public static T Deserialize(Stream s)
            {
                return (T)ser_.Deserialize(s);
            }
            public T Clone()
            {
                using (MemoryStream s = new MemoryStream())
                {
                    Serialize(s);
                    s.Seek(0, SeekOrigin.Begin);
                    return Deserialize(s);
                }
            }
        }

        public class ArtistMapElement
        {
            public string Key { get; set; }
            [XmlArray]
            [XmlArrayItem(ElementName="Item")]
            public List<string> Value { get; set; }
        }

        public class PlaylistHash
        {
            public string Name { get; set; }
            public string Hash { get; set; }
        }

        public class PlaylistSetElement
        {
            public string Key { get; set; }
            public List<PlaylistHash> Value { get; set; }
        }
        
        public class SyncHashSet
        {
            [XmlArray]
            public List<PlaylistHash> Playlists { get; set; } = new List<PlaylistHash>();

            [XmlArray]
            public List<PlaylistHash> Albums { get; set; } = new List<PlaylistHash>();

            [XmlIgnore]
            public Dictionary<string, List<PlaylistHash>> Artists = new Dictionary<string, List<PlaylistHash>>(StringComparer.CurrentCultureIgnoreCase);

            [XmlArray]
            [XmlArrayItem(ElementName = "PlaylistElement")]
            public PlaylistSetElement[] ArtistPlaylists
            {
                get
                {
                    return Artists.Select(m => new PlaylistSetElement() { Key = m.Key, Value = m.Value }).ToArray();
                }
                set
                {
                    Artists = value.ToDictionary(x => x.Key, x => x.Value, StringComparer.CurrentCultureIgnoreCase);
                }
            }

            [XmlIgnore]
            public Dictionary<string, List<PlaylistHash>> ContributingArtists = new Dictionary<string, List<PlaylistHash>>(StringComparer.CurrentCultureIgnoreCase);

            [XmlArray]
            [XmlArrayItem(ElementName = "PlaylistElement")]
            public PlaylistSetElement[] ContributingArtistPlaylists
            {
                get
                {
                    return ContributingArtists.Select(m => new PlaylistSetElement() { Key = m.Key, Value = m.Value }).ToArray();
                }
                set
                {
                    ContributingArtists = value.ToDictionary(x => x.Key, x => x.Value, StringComparer.CurrentCultureIgnoreCase);
                }
            }


        }


        public class SyncDatabase : XmlCloneable<SyncDatabase>
        {
            public BalancedPathNode ArtistStructure { get; set; } = new BalancedPathNode();
            public BalancedPathNode ContributingArtistStructure { get; set; } = new BalancedPathNode();
            public BalancedPathNode AlbumsStructure { get; set; } = new BalancedPathNode();
            public FileDatabase FileDatabase { get; set; } = new FileDatabase();

            public int BalanceSize { get; set; } = BALANCE_SIZE;
            public int RebalanceSize { get; set; } = REBALANCE_SIZE;
            public int BalanceBreak { get; set; } = BALANCE_BREAK;
            public int MaxDepthDisparity { get; set; } = MAX_DEPTH_DISPARITY;


            [XmlIgnore]
            public Dictionary<string, List<string>> ArtistMap { get; set; } = new Dictionary<string, List<string>>(StringComparer.CurrentCultureIgnoreCase);

            [XmlIgnore]
            public Dictionary<string, List<string>> ContributingArtistMap { get; set; } = new Dictionary<string, List<string>>(StringComparer.CurrentCultureIgnoreCase);

            [XmlArray]
            [XmlArrayItem(ElementName = "MapElement")]
            public ArtistMapElement[] ArtistMapElements
            {
                get
                {
                    return ArtistMap.Select(m => new ArtistMapElement() { Key = m.Key, Value = m.Value }).ToArray();
                }
                set
                {
                    ArtistMap = value.ToDictionary(x => x.Key, x => x.Value, StringComparer.CurrentCultureIgnoreCase);
                }
            }

            [XmlArray]
            [XmlArrayItem(ElementName = "MapElement")]
            public ArtistMapElement[] ContributingArtistMapElements
            {
                get
                {
                    return ContributingArtistMap.Select(m => new ArtistMapElement() { Key = m.Key, Value = m.Value }).ToArray();
                }
                set
                {
                    ContributingArtistMap = value.ToDictionary(x => x.Key, x => x.Value, StringComparer.CurrentCultureIgnoreCase);
                }
            }

            public SyncHashSet HashSet { get; set; } = new SyncHashSet();
        }

        static int MAX_PLAYLIST_COUNT = 500;

        static int EnumerateDirectory(Dictionary<string, DateTime> dict, DirectoryInfo dir, int count = -1)
        {
            int startcount = count;
            if (count == -1)
                count = 0;
            foreach (DirectoryInfo di in dir.EnumerateDirectories())
                count = EnumerateDirectory(dict, di, count);
            foreach (FileInfo file in dir.EnumerateFiles())
            {
                dict.Add(file.FullName, file.LastWriteTimeUtc);
                count++;
                if ((count % 1000) == 0)
                {
                    Console.Write(count + "\r");
                    Console.Out.Flush();
                }
            }
            if (startcount == -1)
                LogConsole.WriteLine(count.ToString());
            return count;
        }

        static string GetRelativePath(string filespec, string folder)
        {
            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        static void Main(string[] args)
        {
            SHA1 hash = SHA1.Create();
            LogConsole.SwitchFile("UpdateCarCard.log");

            if (args.Length == 0)
            {
                LogConsole.WriteLine("Usage: UpdateCarCard <destination> [rebalance] [walkman]");
                return;
            }

            bool walkmanmode = ((args.Length > 1) && (args.Skip(1).Where(a => a.Equals("walkman", StringComparison.CurrentCultureIgnoreCase)).Count() > 0));

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            SyncDatabase oldsyncdb = new SyncDatabase();

            string basedir = args[0];

            if (!basedir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basedir = basedir + Path.DirectorySeparatorChar;
            if (File.Exists(Path.Combine(basedir, "syncdb.xml")))
                using (FileStream s = File.OpenRead(Path.Combine(basedir, "syncdb.xml")))
                    oldsyncdb = SyncDatabase.Deserialize(s);

            string artistsdir = Path.Combine(args[0], "Artists");
            string albumsdir = Path.Combine(args[0], "Albums");
            string playlistsdir = walkmanmode ? args[0] : Path.Combine(args[0], "Playlists");
            string contributingartistsdir = Path.Combine(args[0], "Contributing Artists");

            Directory.CreateDirectory(artistsdir);
            if (!walkmanmode)
            {
                Directory.CreateDirectory(contributingartistsdir);
                Directory.CreateDirectory(albumsdir);
                Directory.CreateDirectory(playlistsdir);
            }

            SyncDatabase syncdb = new SyncDatabase();
            syncdb.ArtistStructure = oldsyncdb.ArtistStructure.Clone();
            syncdb.AlbumsStructure = oldsyncdb.AlbumsStructure.Clone();
            syncdb.ContributingArtistStructure = oldsyncdb.ContributingArtistStructure.Clone();

            Dictionary<string, DateTime> filetimes = new Dictionary<string, DateTime>(StringComparer.CurrentCultureIgnoreCase);

            LogConsole.WriteLine("Enumerating Current Files");
            EnumerateDirectory(filetimes, new DirectoryInfo(lib.LocalMusicFolder));

            LogConsole.WriteLine("Enumerating iTunes Library");
            KeyValuePair<int, iTunesTrack>[] library = lib.Tracks.Where(kv => (kv.Value.Type == "File") && (kv.Value.Kind.Contains("audio file") && (!kv.Value.Kind.ToLower().Contains("protected")))).ToArray();
            int libindex = 0;
            Regex dnre = new Regex(@"(.+)[ \t]+\(Disc (.+)\)", RegexOptions.IgnoreCase);
            foreach (var kv in library)
            {
                string loc = kv.Value.LocalLocation;
                DateTime dt;
                try
                {
                    dt = filetimes[loc];
                }
                catch
                {
                    // Outside Normal Folder
                    dt = File.GetLastWriteTimeUtc(loc);
                }
                string artist = (string.IsNullOrEmpty(kv.Value.AlbumArtist) ? kv.Value.Artist : kv.Value.AlbumArtist).LimitLength(32);
                string album = kv.Value.Album;
                Match m = dnre.Match(album);
                album = m.Success ? (m.Groups[1].Value.LimitLength(28) + " (Disc " + m.Groups[2].Value + ")") : album.LimitLength(32);
                string title = kv.Value.Title.LimitLength(32);
                int tracknumber = kv.Value.TrackNumber ?? 0;
                FileDatabase.Track trk = new FileDatabase.Track();
                trk.Index = tracknumber;
                trk.LastModifiedTime = dt;
                trk.Loc = loc;
                trk.FileName = Path.GetFileName(loc);
                trk.Name = title;
                trk.Year = kv.Value.Year ?? 0;
                trk.ContributingArtist = kv.Value.Artist.LimitLength(32);
                trk.PersistentID = kv.Value.PersistentID;
                syncdb.FileDatabase.FindArtist(artist).FindAlbum(album).Tracks.Add(trk);
                libindex++;
                if ((libindex % 1000) == 0)
                {
                    Console.Write(libindex + "\r");
                    Console.Out.Flush();
                }
            }
            LogConsole.WriteLine(libindex.ToString());

            LogConsole.WriteLine("Regenerating Database Maps");

            Dictionary<string, string> oldartistmap = new Dictionary<string, string>();
            foreach (KeyValuePair<string, List<string>> kv in oldsyncdb.ArtistMap)
                foreach (string v in kv.Value)
                    oldartistmap.Add(v, kv.Key);

            Dictionary<string, string> oldcontributingartistmap = new Dictionary<string, string>();

            if (!walkmanmode)
            {
                foreach (KeyValuePair<string, List<string>> kv in oldsyncdb.ContributingArtistMap)
                    foreach (string v in kv.Value)
                        oldcontributingartistmap[v] = kv.Key;
            }

            LogConsole.WriteLine("Mapping Artist Names");

            string[] artists = library.Select(kv => (string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.Artist : kv.Value.AlbumArtist).LimitLength(32)).Distinct().OrderBy(s => s).ToArray();
            Dictionary<string, string> artistmap = new Dictionary<string, string>();

            foreach (string artist in artists)
            {
                string mappedname = FixArtistPath(artist);
                if (syncdb.ArtistMap.ContainsKey(mappedname))
                    syncdb.ArtistMap[mappedname].Add(artist);
                else
                    syncdb.ArtistMap.Add(mappedname, new List<string>(new string[] { artist }));
                artistmap.Add(artist, mappedname);
            }

            foreach (string artist in syncdb.ArtistMap.Keys)
                syncdb.ArtistStructure.AddItem(artist);

            Dictionary<string, string> contributingartistmap = new Dictionary<string, string>();
            Tuple<string, string>[] newalbums = syncdb.FileDatabase.Artists.SelectMany(a => a.Albums.Select(al => new Tuple<string, string>(a.Name, al.Name))).Distinct().ToArray();

            if (!walkmanmode)
            {
                string[] contributingartists = library.Select(kv => kv.Value.Artist).Distinct().OrderBy(s => s).ToArray();
                LogConsole.WriteLine("Mapping Contributing Artist Names");
                foreach (string artist in contributingartists)
                {
                    string mappedname = FixArtistPath(artist).LimitLength(32);
                    if (syncdb.ContributingArtistMap.ContainsKey(mappedname))
                        syncdb.ContributingArtistMap[mappedname].Add(artist.LimitLength(32));
                    else
                        syncdb.ContributingArtistMap.Add(mappedname, new List<string>(new string[] { artist.LimitLength(32) }));
                    contributingartistmap[artist.LimitLength(32)] = mappedname;
                }

                foreach (string artist in syncdb.ContributingArtistMap.Keys)
                    syncdb.ContributingArtistStructure.AddItem(artist);

                foreach (Tuple<string, string> album in newalbums)
                {
                    string albname = album.Item2;
                    if (newalbums.Where(a => a.Item2 == albname).Count() > 1)
                        albname += " (" + artistmap[album.Item1] + ")";
                    albname = albname.FixPath() + ".m3u";
                    syncdb.AlbumsStructure.AddItem(albname);
                }
            }

            LogConsole.WriteLine("Computing File Database Deltas");
            FileDatabase.FileDatabaseDelta[] deltas = syncdb.FileDatabase.ComputeDelta(oldsyncdb.FileDatabase).ToArray();

            LogConsole.WriteLine("Removing Tracks");
            foreach (FileDatabase.RemoveTrackDelta delta in deltas.Where(d => d is FileDatabase.RemoveTrackDelta).Select(d => d as FileDatabase.RemoveTrackDelta))
            {
                string mappedname = oldartistmap[delta.Artist];
                string oldartistpath = Path.Combine(oldsyncdb.ArtistStructure.FindNode(mappedname).Path, mappedname);
                string filename = Path.Combine(artistsdir, oldartistpath, delta.Album.FixPath(), delta.FileName);
                LogConsole.WriteLine("Removing " + filename);
                if (File.Exists(filename))
                    File.Delete(filename);
                else
                    LogConsole.WriteLine("Warning, Missing " + filename);
            }

            if (!walkmanmode)
            {
                LogConsole.WriteLine("Removing Albums");
                foreach (FileDatabase.RemoveAlbumDelta delta in deltas.Where(d => d is FileDatabase.RemoveAlbumDelta).Select(d => d as FileDatabase.RemoveAlbumDelta))
                {
                    string mappedname = oldartistmap[delta.Artist];
                    string oldartistpath = Path.Combine(oldsyncdb.ArtistStructure.FindNode(mappedname).Path, mappedname);
                    string dirname = Path.Combine(artistsdir, oldartistpath, delta.Album.FixPath());
                    if (Directory.Exists(dirname))
                    {
                        if (!Directory.EnumerateFileSystemEntries(dirname).Any())
                        {
                            LogConsole.WriteLine("Removing " + dirname);
                            Directory.Delete(dirname);
                        }
                    }
                    else
                    {
                        LogConsole.WriteLine("Warning, Missing " + dirname);
                    }
                }

                LogConsole.WriteLine("Removing Artists");
                foreach (FileDatabase.RemoveArtistDelta delta in deltas.Where(d => d is FileDatabase.RemoveArtistDelta).Select(d => d as FileDatabase.RemoveArtistDelta))
                {
                    string mappedname = oldartistmap[delta.Artist];
                    string oldartistpath = Path.Combine(oldsyncdb.ArtistStructure.FindNode(mappedname).Path, mappedname);
                    string dirname = Path.Combine(artistsdir, oldartistpath);
                    if (!Directory.EnumerateDirectories(dirname).Any())
                        Directory.GetFiles(dirname, "*.m3u").ToList().ForEach(s => File.Delete(s));
                    if (!Directory.EnumerateFileSystemEntries(dirname).Any())
                    {
                        syncdb.ArtistStructure.RemoveItem(oldartistmap[delta.Artist]);
                        LogConsole.WriteLine("Removing " + dirname);
                        Directory.Delete(dirname);
                    }
                }

                string[] sharedcontarts = oldsyncdb.ContributingArtistMap.Keys.Intersect(syncdb.ContributingArtistMap.Keys, StringComparer.CurrentCultureIgnoreCase).Distinct().ToArray();
                string[] removedcontarts = oldsyncdb.ContributingArtistMap.Keys.Where(s => !sharedcontarts.Contains(s, StringComparer.CurrentCultureIgnoreCase)).Distinct().ToArray();

                LogConsole.WriteLine("Removing Contributing Artists");
                foreach (string artist in removedcontarts)
                {
                    string oldartistpath = Path.Combine(contributingartistsdir, oldsyncdb.ContributingArtistStructure.FindNode(artist).Path, artist);
                    LogConsole.WriteLine("Removing " + oldartistpath);
                    if (Directory.Exists(oldartistpath))
                        Directory.Delete(oldartistpath, true);
                    syncdb.ContributingArtistStructure.RemoveItem(artist);
                }

                Tuple<string, string>[] oldalbums = oldsyncdb.FileDatabase.Artists.SelectMany(a => a.Albums.Select(al => new Tuple<string, string>(a.Name, al.Name))).Distinct().ToArray();
                Tuple<string, string>[] sharedalbums = oldsyncdb.FileDatabase.Artists.SelectMany(a => a.Albums.Select(al => new Tuple<string, string>(a.Name, al.Name))).Intersect(
                    syncdb.FileDatabase.Artists.SelectMany(a => a.Albums.Select(al => new Tuple<string, string>(a.Name, al.Name)))).Distinct().ToArray();
                Tuple<string, string>[] removedalbums = oldsyncdb.FileDatabase.Artists.SelectMany(a => a.Albums.Select(al => new Tuple<string, string>(a.Name, al.Name))).Where(
                    s => !sharedalbums.Contains(s)).Distinct().ToArray();

                LogConsole.WriteLine("Removing Old Album Playlists");
                foreach (Tuple<string, string> album in removedalbums)
                {
                    string albname = album.Item2;
                    if (oldalbums.Where(a => a.Item2 == albname).Count() > 1)
                        albname += " (" + album.Item1 + ")";
                    albname = albname.FixPath() + ".m3u";
                    string oldalbumpath = Path.Combine(albumsdir, oldsyncdb.AlbumsStructure.FindNode(albname).Path, albname);
                    LogConsole.WriteLine("Removing " + oldalbumpath);
                    File.Delete(oldalbumpath);
                    syncdb.AlbumsStructure.RemoveItem(albname);
                }
            }

            LogConsole.WriteLine("Rebalancing Directories");

            bool forcerebalance = ((syncdb.BalanceSize != oldsyncdb.BalanceSize) || (syncdb.RebalanceSize != oldsyncdb.RebalanceSize) || 
                (syncdb.BalanceBreak != oldsyncdb.BalanceBreak) || (syncdb.MaxDepthDisparity != oldsyncdb.MaxDepthDisparity));

            if ((args.Length > 1)&&(args.Skip(1).Where(a => a.Equals("rebalance", StringComparison.CurrentCultureIgnoreCase)).Count() > 0))
                forcerebalance = true;
            
            syncdb.ArtistStructure.Rebalance(forcerebalance);

            if (!walkmanmode)
            {
                syncdb.ContributingArtistStructure.Rebalance(forcerebalance);
                syncdb.AlbumsStructure.Rebalance(forcerebalance);
            }

            LogConsole.WriteLine("Computing Artist Directory Structure Deltas");
            BalancedPathNode.BalancedPathDelta [] pdeltas = syncdb.ArtistStructure.ComputeDelta(oldsyncdb.ArtistStructure).ToArray();

            LogConsole.WriteLine("Adding Artist Directories");
            foreach (BalancedPathNode.AddPathDelta delta in pdeltas.Where(d => d is BalancedPathNode.AddPathDelta).Select(d => d as BalancedPathNode.AddPathDelta))
                Directory.CreateDirectory(Path.Combine(artistsdir, delta.Path));

            LogConsole.WriteLine("Moving Artist Directories");
            foreach (BalancedPathNode.MovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.MovePathDelta).Select(d => d as BalancedPathNode.MovePathDelta))
            {
                string oldpath = Path.Combine(artistsdir, delta.OldPath, delta.Item);
                string newpath = Path.Combine(artistsdir, delta.NewPath, delta.Item);
                if (Directory.Exists(oldpath) && !Directory.Exists(newpath))
                    Directory.Move(oldpath, newpath);
            }

            LogConsole.WriteLine("Removing Artist Directories");
            foreach (BalancedPathNode.RemovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.RemovePathDelta).Select(d => d as BalancedPathNode.RemovePathDelta))
                Directory.Delete(Path.Combine(artistsdir, delta.Path));

            if (!walkmanmode)
            {

                LogConsole.WriteLine("Computing Album Directory Structure Deltas");
                pdeltas = syncdb.AlbumsStructure.ComputeDelta(oldsyncdb.AlbumsStructure).ToArray();

                LogConsole.WriteLine("Adding Album Directories");
                foreach (BalancedPathNode.AddPathDelta delta in pdeltas.Where(d => d is BalancedPathNode.AddPathDelta).Select(d => d as BalancedPathNode.AddPathDelta))
                    Directory.CreateDirectory(Path.Combine(albumsdir, delta.Path));

                LogConsole.WriteLine("Moving Album Directories");
                foreach (BalancedPathNode.MovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.MovePathDelta).Select(d => d as BalancedPathNode.MovePathDelta))
                {
                    string oldpath = Path.Combine(albumsdir, delta.OldPath, delta.Item);
                    string newpath = Path.Combine(albumsdir, delta.NewPath, delta.Item);
                    File.Move(oldpath, newpath);
                }

                LogConsole.WriteLine("Removing Album Directories");
                foreach (BalancedPathNode.RemovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.RemovePathDelta).Select(d => d as BalancedPathNode.RemovePathDelta))
                    Directory.Delete(Path.Combine(albumsdir, delta.Path));

                LogConsole.WriteLine("Computing Contributing Artist Directory Structure Deltas");
                pdeltas = syncdb.ContributingArtistStructure.ComputeDelta(oldsyncdb.ContributingArtistStructure).ToArray();

                LogConsole.WriteLine("Adding Contributing Artist Directories");
                foreach (BalancedPathNode.AddPathDelta delta in pdeltas.Where(d => d is BalancedPathNode.AddPathDelta).Select(d => d as BalancedPathNode.AddPathDelta))
                    Directory.CreateDirectory(Path.Combine(contributingartistsdir, delta.Path));

                LogConsole.WriteLine("Moving Contributing Artist Directories");
                foreach (BalancedPathNode.MovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.MovePathDelta).Select(d => d as BalancedPathNode.MovePathDelta))
                {
                    string oldpath = Path.Combine(contributingartistsdir, delta.OldPath, delta.Item);
                    string newpath = Path.Combine(contributingartistsdir, delta.NewPath, delta.Item);
                    Directory.Move(oldpath, newpath);
                }

                LogConsole.WriteLine("Removing Contributing Artist Directories");
                foreach (BalancedPathNode.RemovePathDelta delta in pdeltas.Where(d => d is BalancedPathNode.RemovePathDelta).Select(d => d as BalancedPathNode.RemovePathDelta))
                    Directory.Delete(Path.Combine(contributingartistsdir, delta.Path));

            }

            LogConsole.WriteLine("Updating Tracks");
            foreach (FileDatabase.UpdateTrackDelta delta in deltas.Where(d => d is FileDatabase.UpdateTrackDelta).Select(d => d as FileDatabase.UpdateTrackDelta))
            {
                string mappedname = artistmap[delta.Artist];
                string artistpath = Path.Combine(syncdb.ArtistStructure.FindNode(mappedname).Path, mappedname);
                string albumpath = Path.Combine(artistsdir, artistpath, delta.Album.FixPath());
                string filename = Path.Combine(albumpath, delta.FileName);
                Directory.CreateDirectory(albumpath);
                LogConsole.WriteLine("Copy " + delta.Loc + " -> " + filename);
                File.Copy(delta.Loc, filename, true);
            }

            //using (FileStream fs = File.Create(Path.Combine(basedir, "syncdb.xml")))
            //    syncdb.Serialize(fs);

            // Generate Playlists

            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>[] alltracks = syncdb.FileDatabase.Artists.SelectMany(a => a.Albums.SelectMany(al => al.Tracks.Select(tr => new Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>(a, al, tr)))).ToArray();

            if (!walkmanmode)
            {
                LogConsole.WriteLine("Updating Artist/Album Playlists");
                foreach (string artist in syncdb.ArtistMap.Keys)
                {
                    using (MemoryStream allms = new MemoryStream(), allalbumsms = new MemoryStream(), allalbumsbyyearms = new MemoryStream())
                    {
                        using (StreamWriter allwriter = new StreamWriter(allms, Encoding.UTF8, 512, true),
                            allalbumswriter = new StreamWriter(allalbumsms, Encoding.UTF8, 512, true),
                            allalbumsbyyearwriter = new StreamWriter(allalbumsbyyearms, Encoding.UTF8, 512, true))
                        {
                            FileDatabase.Album[] albums = syncdb.FileDatabase.Artists.Where(a => artistmap[a.Name].Equals(artist, StringComparison.CurrentCultureIgnoreCase)).SelectMany(a => a.Albums).ToArray();

                            allwriter.WriteLine("#EXTM3U");
                            allalbumswriter.WriteLine("#EXTM3U");
                            allalbumsbyyearwriter.WriteLine("#EXTM3U");

                            foreach (FileDatabase.Album album in albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                            {
                                string albname = album.Name;
                                if (newalbums.Where(a => a.Item2 == albname).Count() > 1)
                                    albname += " (" + artist + ")";
                                albname = albname.FixPath() + ".m3u";

                                using (MemoryStream albumms = new MemoryStream())
                                {
                                    using (StreamWriter albumwriter = new StreamWriter(albumms, Encoding.UTF8, 5123, true))
                                    {
                                        foreach (FileDatabase.Track track in album.Tracks.OrderBy(t => t.Index).ThenBy(t => t.FileName))
                                        {
                                            albumwriter.WriteLine("#EXTM3U");
                                            allalbumswriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                            string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(artist).Path, artist, album.Name.FixPath(), track.FileName);
                                            allalbumswriter.WriteLine(Path.Combine(album.Name.FixPath(), track.FileName));
                                            albumwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                            albumwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(albumsdir, syncdb.AlbumsStructure.FindNode(albname).Path)));
                                        }
                                    }

                                    byte[] b = albumms.ToArray();
                                    PlaylistHash phash = new PlaylistHash() { Name = albname, Hash = Convert.ToBase64String(hash.ComputeHash(b)) };
                                    syncdb.HashSet.Albums.Add(phash);
                                    PlaylistHash ophash = oldsyncdb.HashSet.Albums.SingleOrDefault(a => a.Name == albname);
                                    if ((ophash == null) || (ophash.Hash != phash.Hash))
                                    {
                                        Directory.CreateDirectory(Path.Combine(albumsdir, syncdb.AlbumsStructure.FindNode(albname).Path));
                                        string filename = Path.Combine(albumsdir, syncdb.AlbumsStructure.FindNode(albname).Path, albname);
                                        LogConsole.WriteLine("Updating Playlist: " + filename);
                                        File.WriteAllBytes(filename, b);
                                    }
                                }
                            }

                            foreach (FileDatabase.Album album in albums.OrderBy(a => a.Tracks.Select(t => t.Year).Max()).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                            {
                                foreach (FileDatabase.Track track in album.Tracks.OrderBy(t => t.Index).ThenBy(t => t.FileName))
                                {
                                    allalbumsbyyearwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                    string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(artist).Path, artist, album.Name.FixPath(), track.FileName);
                                    allalbumsbyyearwriter.WriteLine(Path.Combine(album.Name.FixPath(), track.FileName));
                                }
                            }

                            foreach (Tuple<string, FileDatabase.Track> track in albums.SelectMany(a => a.Tracks.Select(t => new Tuple<string, FileDatabase.Track>(a.Name, t))).OrderBy(t => t.Item2.Name.ToLower()))
                            {
                                allwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Item2.Name.Replace("-", ""));
                                string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(artist).Path, artist, track.Item1.FixPath(), track.Item2.FileName);
                                allwriter.WriteLine(Path.Combine(track.Item1.FixPath(), track.Item2.FileName));
                            }

                        }

                        Dictionary<string, MemoryStream> pldict = new Dictionary<string, MemoryStream>() { { "All Tracks.m3u", allms }, { "All Albums.m3u", allalbumsms }, { "All Albums By Year.m3u", allalbumsbyyearms } };

                        foreach (KeyValuePair<string, MemoryStream> kv in pldict)
                        {
                            byte[] b = kv.Value.ToArray();
                            PlaylistHash phash = new PlaylistHash() { Name = kv.Key, Hash = Convert.ToBase64String(hash.ComputeHash(b)) };
                            if (syncdb.HashSet.Artists.ContainsKey(artist))
                                syncdb.HashSet.Artists[artist].Add(phash);
                            else
                                syncdb.HashSet.Artists.Add(artist, new List<PlaylistHash>(new PlaylistHash[] { phash }));
                            PlaylistHash ophash = oldsyncdb.HashSet.Artists.ContainsKey(artist) ? oldsyncdb.HashSet.Artists[artist].SingleOrDefault(a => a.Name == phash.Name) : null;
                            if ((ophash == null) || (ophash.Hash != phash.Hash))
                            {
                                string filename = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(artist).Path, artist, phash.Name);
                                LogConsole.WriteLine("Updating Playlist: " + filename);
                                File.WriteAllBytes(filename, b);
                            }
                        }

                    }
                }
                
                LogConsole.WriteLine("Updating Contributing Artist Playlists");

                foreach (string artist in syncdb.ContributingArtistMap.Keys)
                {
                    Directory.CreateDirectory(Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist));
                    using (MemoryStream allms = new MemoryStream(), allalbumsms = new MemoryStream(), allalbumsbyyearms = new MemoryStream())
                    {
                        using (StreamWriter allwriter = new StreamWriter(allms, Encoding.UTF8, 512, true),
                            allalbumswriter = new StreamWriter(allalbumsms, Encoding.UTF8, 512, true),
                            allalbumsbyyearwriter = new StreamWriter(allalbumsbyyearms, Encoding.UTF8, 512, true))
                        {
                            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>[] tracks = alltracks.Where(tr => contributingartistmap[tr.Item3.ContributingArtist].Equals(artist, StringComparison.CurrentCultureIgnoreCase)).ToArray();

                            allwriter.WriteLine("#EXTM3U");
                            allalbumswriter.WriteLine("#EXTM3U");
                            allalbumsbyyearwriter.WriteLine("#EXTM3U");

                            Tuple<string, FileDatabase.Album>[] albums = tracks.Select(trk => new Tuple<string, FileDatabase.Album>(trk.Item1.Name, trk.Item2)).Distinct().ToArray();

                            foreach (Tuple<string, FileDatabase.Album> album in albums.OrderBy(a => a.Item2.Name, StringComparer.CurrentCultureIgnoreCase))
                            {
                                string albname = album.Item2.Name;
                                if (albums.Count(al => al.Item2.Name.Equals(albname, StringComparison.CurrentCultureIgnoreCase)) > 1)
                                    albname = albname + " (" + album.Item1 + ")";
                                albname = albname.FixPath() + ".m3u";
                                string mappedname = artistmap[album.Item1];

                                using (MemoryStream albumms = new MemoryStream())
                                {
                                    using (StreamWriter albumwriter = new StreamWriter(albumms, Encoding.UTF8, 5123, true))
                                    {
                                        foreach (FileDatabase.Track track in tracks.Select(t => t.Item3).Intersect(album.Item2.Tracks).OrderBy(t => t.Index).ThenBy(t => t.FileName))
                                        {
                                            albumwriter.WriteLine("#EXTM3U");
                                            allalbumswriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                            string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(mappedname).Path, mappedname, album.Item2.Name.FixPath(), track.FileName);
                                            allalbumswriter.WriteLine(GetRelativePath(trackfile, Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist)));
                                            albumwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                            albumwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist)));
                                        }
                                    }

                                    byte[] b = albumms.ToArray();
                                    PlaylistHash phash = new PlaylistHash() { Name = albname, Hash = Convert.ToBase64String(hash.ComputeHash(b)) };
                                    if (syncdb.HashSet.ContributingArtists.ContainsKey(artist))
                                        syncdb.HashSet.ContributingArtists[artist].Add(phash);
                                    else
                                        syncdb.HashSet.ContributingArtists.Add(artist, new List<PlaylistHash>(new PlaylistHash[] { phash }));
                                    PlaylistHash ophash = oldsyncdb.HashSet.ContributingArtists.ContainsKey(artist) ? oldsyncdb.HashSet.ContributingArtists[artist].SingleOrDefault(a => a.Name == phash.Name) : null;
                                    if ((ophash == null) || (ophash.Hash != phash.Hash))
                                    {
                                        string filename = Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist, albname);
                                        LogConsole.WriteLine("Updating Playlist: " + filename);
                                        File.WriteAllBytes(filename, b);
                                    }
                                }
                            }

                            foreach (Tuple<string, FileDatabase.Album> album in albums.OrderBy(a => a.Item2.Tracks.Select(t => t.Year).Max()).ThenBy(a => a.Item2.Name, StringComparer.CurrentCultureIgnoreCase))
                            {
                                string mappedname = artistmap[album.Item1];
                                foreach (FileDatabase.Track track in tracks.Select(t => t.Item3).Intersect(album.Item2.Tracks).OrderBy(t => t.Index).ThenBy(t => t.FileName))
                                {
                                    allalbumsbyyearwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Name.Replace("-", ""));
                                    string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(mappedname).Path, mappedname, album.Item2.Name.FixPath(), track.FileName);
                                    allalbumsbyyearwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist)));
                                }
                            }

                            foreach (Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track> track in tracks.OrderBy(t => t.Item3.Name.ToLower()))
                            {
                                allwriter.WriteLine("#EXTINF:-1," + artist.Replace("-", "") + " - " + track.Item2.Name.Replace("-", ""));
                                string mappedname = artistmap[track.Item1.Name];
                                string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(mappedname).Path, mappedname, track.Item2.Name.FixPath(), track.Item3.FileName);
                                allwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist)));
                            }
                        }

                        Dictionary<string, MemoryStream> pldict = new Dictionary<string, MemoryStream>() { { "All Tracks.m3u", allms }, { "All Albums.m3u", allalbumsms }, { "All Albums By Year.m3u", allalbumsbyyearms } };

                        foreach (KeyValuePair<string, MemoryStream> kv in pldict)
                        {
                            byte[] b = kv.Value.ToArray();
                            PlaylistHash phash = new PlaylistHash() { Name = kv.Key, Hash = Convert.ToBase64String(hash.ComputeHash(b)) };
                            if (syncdb.HashSet.ContributingArtists.ContainsKey(artist))
                                syncdb.HashSet.ContributingArtists[artist].Add(phash);
                            else
                                syncdb.HashSet.ContributingArtists.Add(artist, new List<PlaylistHash>(new PlaylistHash[] { phash }));
                            PlaylistHash ophash = oldsyncdb.HashSet.ContributingArtists.ContainsKey(artist) ? oldsyncdb.HashSet.ContributingArtists[artist].SingleOrDefault(a => a.Name == phash.Name) : null;
                            if ((ophash == null) || (ophash.Hash != phash.Hash))
                            {
                                string filename = Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist, phash.Name);
                                LogConsole.WriteLine("Updating Playlist: " + filename);
                                File.WriteAllBytes(filename, b);
                            }
                        }
                    }

                    string[] desiredfiles = syncdb.HashSet.ContributingArtists[artist].Select(h => h.Name).ToArray();
                    string[] existingfiles = Directory.GetFiles(Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist)).Select(f => Path.GetFileName(f)).ToArray();
                    string[] diffs = existingfiles.Where(s => !desiredfiles.Contains(s, StringComparer.CurrentCultureIgnoreCase)).ToArray();

                    foreach (string diff in diffs)
                    {
                        LogConsole.WriteLine("Deleting File: " + artist + "::::" + Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist, diff));
                        File.Delete(Path.Combine(contributingartistsdir, syncdb.ContributingArtistStructure.FindNode(artist).Path, artist, diff));
                    }
                }
            }

            LogConsole.WriteLine("Updating User Playlists");
            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                int icount = 0;
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT) || ((pl.Title.ToLower() == "library")))
                    continue;

                string plname = pl.Title.FixPath() + ".m3u";

                using (MemoryStream plms = new MemoryStream())
                {
                    using (StreamWriter plwriter = new StreamWriter(plms, Encoding.UTF8, 5123, true))
                    {
                        plwriter.WriteLine("#EXTM3U");
                        foreach (int item in pl.Items)
                        {
                            iTunesTrack track = lib.Tracks[item];
                            if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                                track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                                continue;
                            plwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track> titem = alltracks.Single(t => t.Item3.PersistentID == track.PersistentID);

                            string mappedname = artistmap[titem.Item1.Name];
                            string trackfile = Path.Combine(artistsdir, syncdb.ArtistStructure.FindNode(mappedname).Path, mappedname, titem.Item2.Name.FixPath(), titem.Item3.FileName);
                            plwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(playlistsdir)));
                            icount++;
                        }
                    }

                    if (icount == 0)
                        continue;

                    byte[] b = plms.ToArray();
                    PlaylistHash phash = new PlaylistHash() { Name = plname, Hash = Convert.ToBase64String(hash.ComputeHash(b)) };
                    syncdb.HashSet.Playlists.Add(phash);
                    PlaylistHash ophash = oldsyncdb.HashSet.Playlists.SingleOrDefault(a => a.Name == phash.Name);
                    if ((ophash == null) || (ophash.Hash != phash.Hash))
                    {
                        string filename = Path.Combine(playlistsdir, plname);
                        LogConsole.WriteLine("Updating Playlist: " + filename);
                        File.WriteAllBytes(filename, b);
                    }

                }
            }

            // Remove Olds
            {
                string[] desiredfiles = syncdb.HashSet.Playlists.Select(h => h.Name).ToArray();
                string[] existingfiles = Directory.GetFiles(playlistsdir).Select(f => Path.GetFileName(f)).ToArray();
                string[] diffs = existingfiles.Where(s => !desiredfiles.Contains(s, StringComparer.CurrentCultureIgnoreCase)).ToArray();

                foreach (string diff in diffs)
                {
                    string filename = Path.Combine(playlistsdir, diff);
                    LogConsole.WriteLine("Deleting File: " + filename);
                    File.Delete(filename);
                }
            }

            LogConsole.WriteLine("Writing Synchronization Database");
            using (FileStream fs = File.Create(Path.Combine(basedir, "syncdb.xml")))
                syncdb.Serialize(fs);

            LogConsole.Close();


            return;
        }
    }
}
