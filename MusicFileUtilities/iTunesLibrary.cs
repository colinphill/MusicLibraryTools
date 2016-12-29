/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/iTunesLibrary.cs $
 * $Date: 2012-05-28 12:19:15 -0600 (Mon, 28 May 2012) $
 * $Revision: 4 $
 * $Author: Colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using System.Text;
using System.Linq;

namespace iTunes
{
    public class iTunesTrack
    {
        public iTunesTrack()
        {
        }

        public iTunesTrack(IEnumerable<XElement> keys)
        {
            foreach (XElement key in keys)
            {
                XElement next = key.NextNode as XElement;
                switch (key.Value)
                {
                    case "Name":
                        Title = next.Value;
                        break;

                    case "Persistent ID":
                        PersistentID = next.Value;
                        break;

                    case "Artist":
                        Artist = next.Value;
                        break;

                    case "Album":
                        Album = next.Value;
                        break;

                    case "Album Artist":
                        AlbumArtist = next.Value;
                        break;

                    case "Kind":
                        Kind = next.Value;
                        break;

                    case "Track Type":
                        Type = next.Value;
                        break;

                    case "Location":
                        Location = next.Value;
                        break;

                    case "Genre":
                        Genre = next.Value;
                        break;

                    case "Track Number":
                        TrackNumber = int.Parse(next.Value);
                        break;

                    case "Track Count":
                        TotalTracks = int.Parse(next.Value);
                        break;

                    case "Artwork Count":
                        ArtworkCount = int.Parse(next.Value);
                        break;

                    case "Total Time":
                        TotalTime = int.Parse(next.Value);
                        break;

                    case "Play Count":
                        PlayCount = int.Parse(next.Value);
                        break;

                    case "Year":
                        Year = int.Parse(next.Value);
                        break;

                    default:
                        break;
                }
            }
        }
          
        public override string ToString()
        {
            return Title;
        }

        public string PersistentID = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string AlbumArtist = "";
        public int? TrackNumber = null;
        public int? TotalTracks = null;
        public int? ArtworkCount = null;
        public int? Year = null;
        public string Kind = "";
        public string Genre = "";
        public string Type = "";
        public string Location = "";
        public int? TotalTime = null;
        public int? PlayCount = null;

        public string LocalLocation
        {
            get
            {
                return new Uri(Location).LocalPath.Replace(@"\\localhost\", "");
            }
        }
    }

    public class iTunesPlaylist
    {
        public iTunesPlaylist()
        {
        }

        public iTunesPlaylist(IEnumerable<XElement> keys, iTunesLibrary library)
        {

            foreach (XElement key in keys)
            {
                XElement next = key.NextNode as XElement;
                switch (key.Value)
                {
                    case "Name":
                        Title = next.Value;
                        break;

                    case "Playlist Persistent ID":
                        PersistentID = next.Value;
                        break;

                    case "Playlist Items":
                        IEnumerable<XElement> tracks = next.Descendants("integer");
                        foreach (XElement el in tracks)
                            Items.Add(int.Parse(el.Value));
                        break;

                    default:
                        break;
                }
            }

            if (Title.ToLower() == "most played")
            {
                Items = Items.OrderByDescending(i => library.Tracks[i].PlayCount).ToList();
            }
        }

        public string PersistentID = "";
        public string Title = "";
        public List<int> Items = new List<int>();

        public override string ToString()
        {
            return Title;
        }
    }

    public class iTunesLibrary
    {

        private Dictionary<int, iTunesTrack> _library = new Dictionary<int, iTunesTrack>();
        private Dictionary<int, iTunesPlaylist> _playlists = new Dictionary<int, iTunesPlaylist>();
        private string _persistentid = "";

        public Dictionary<int, iTunesTrack> Tracks
        {
            get
            {
                return _library;
            }
        }

        public Dictionary<int, iTunesPlaylist> Playlists
        {
            get
            {
                return _playlists;
            }
        }

        public string PersistentID
        {
            get
            {
                return _persistentid;
            }
        }

        public iTunesLibrary(string xmlfile)
        {
            Load(xmlfile);
        }

        public iTunesPlaylist FindPlaylist(string title)
        {
            try
            {
                return _playlists.Values.Where(pl => pl.Title.ToLower() == title.ToLower()).Single();
            }
            catch
            {
                return null;
            }
        }
            
        private void Load(string xmlfile)
        {
            XDocument doc = XDocument.Load(xmlfile);

            IEnumerable<XElement> dockeys = doc.Elements("plist").Elements("dict").Elements("key");

            _persistentid = dockeys.Where(key => key.Value == "Library Persistent ID").First().ElementsAfterSelf().First().Value;

            IEnumerable<XElement> tracks = dockeys.Where(el => el.Value == "Tracks").First().ElementsAfterSelf().First().Elements("dict");

            IEnumerable<XElement> playlists = dockeys.Where(el => el.Value == "Playlists").First().ElementsAfterSelf().First().Elements("dict");

            foreach (XElement el in tracks)
                LoadTrack(el);

            foreach (XElement el in playlists)
                LoadPlaylist(el);
        }

        private void LoadPlaylist(XElement el)
        {
            IEnumerable<XElement> keys = el.Elements("key");
            int id = int.Parse((keys.Where(kel => kel.Value == "Playlist ID").First().NextNode as XElement).Value);
            iTunesPlaylist playlist = new iTunesPlaylist(keys, this);
            _playlists.Add(id, playlist);
        }

        private void LoadTrack(XElement el)
        {
            IEnumerable<XElement> keys = el.Elements("key");
            int id = int.Parse((keys.Where(kel => kel.Value == "Track ID").First().NextNode as XElement).Value);
            iTunesTrack track = new iTunesTrack(keys);
            _library.Add(id, track);
        }


    }

}