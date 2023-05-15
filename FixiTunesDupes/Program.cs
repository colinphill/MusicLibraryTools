#define REWRITEPLAYLISTS

using System;
using iTunesLib;
using System.Collections.Generic;
using System.IO;

namespace FixiTunesDupes
{

    class Program
    {

        static void Main()
        {

            iTunesApp app = new iTunesApp();

            var hitset = new HashSet<(int HighID, int LowID)>();
            var playlists = new List<(string Name, List<(int HighID, int LowID)> Items)>();

            foreach (IITSource source in app.Sources)
            {
                foreach (IITPlaylist playlist in source.Playlists)
                {
                    if ((playlist.Kind == ITPlaylistKind.ITPlaylistKindUser)&&(playlist.Tracks.Count <= 250)&&(playlist.Tracks.Count > 0))
                    {
                        var idlist = new List<(int HighID, int LowId)>();
                        foreach (IITTrack track in playlist.Tracks)
                        {
                            if (track.Kind == ITTrackKind.ITTrackKindFile)
                            {
                                IITFileOrCDTrack filetrack = (IITFileOrCDTrack)track;
                                if (filetrack.VideoKind == ITVideoKind.ITVideoKindNone)
                                {
                                    int highid, lowid;
                                    app.GetITObjectPersistentIDs(filetrack, out highid, out lowid);
                                    if (!hitset.Contains((highid, lowid)))
                                        hitset.Add((highid, lowid));
                                    idlist.Add((highid, lowid));
                                }
                            }
                        }
                        if (idlist.Count > 0)
                            playlists.Add((playlist.Name, idlist));
                    }
                }
            }

            IITPlaylist lib = app.LibraryPlaylist;

            var trackmap = new Dictionary<(int Track, string Artist, string Album), List<(int HighID, int LowID, DateTime DateAdded, string Path)>>();

            foreach (IITTrack track in lib.Tracks)
            {
                if (track.Kind == ITTrackKind.ITTrackKindFile)
                {
                    IITFileOrCDTrack filetrack = (IITFileOrCDTrack)track;
                    if (filetrack.VideoKind == ITVideoKind.ITVideoKindNone)
                    {
                        int highid, lowid;
                        app.GetITObjectPersistentIDs(filetrack, out highid, out lowid);
                        var key = (Track: filetrack.TrackNumber, Artist: filetrack.Artist, Album: filetrack.Album);
                        if (!trackmap.ContainsKey(key))
                            trackmap.Add(key, new List<(int HighID, int LowID, DateTime DateAdded, string Path)>());
                        trackmap[key].Add((highid, lowid, filetrack.DateAdded, filetrack.Location));
                    }
                    
                }
            }

            var rewriteplaylists = new HashSet<string>();

            foreach (var kv in trackmap.Where(kv => kv.Value.Count > 1))
            {
                Console.WriteLine(kv.Key);
                bool first = true;
                int refhi = 0, reflo = 0;
                foreach (var track in kv.Value.OrderByDescending(t => t.DateAdded))
                {
                    if (first)
                    {
                        refhi = track.HighID;
                        reflo = track.LowID;
                        first = false;
                    }
                    else
                    {
                        IITTrack itrack = lib.Tracks.ItemByPersistentID[track.HighID, track.LowID];
                        if (hitset.Contains((track.HighID, track.LowID)))
                        {
                            Console.WriteLine("Hit --> " + track.Path);
                            foreach (var playlist in playlists)
                            {
                                int index = playlist.Items.IndexOf((track.HighID, track.LowID));
                                if (index > -1)
                                    if (!rewriteplaylists.Contains(playlist.Name))
                                        rewriteplaylists.Add(playlist.Name);
                                while (index > -1)
                                {
                                    Console.WriteLine($"Replace Index: {index} In Playlist: {playlist.Name}");
                                    playlist.Items[index] = (refhi, reflo);
                                    index = playlist.Items.IndexOf((track.HighID, track.LowID));
                                }
                            }
#if REWRITEPLAYLISTS
                            Console.WriteLine("Remove --> " + track.Path);
                            itrack.Delete();
#endif
                        }
                        else
                        {
                            Console.WriteLine("Remove --> " + track.Path);
                            itrack.Delete();
                        }
                    }
                }
            }

#if REWRITEPLAYLISTS
            foreach (var playlist in rewriteplaylists)
            {
                var items = playlists.First(p => p.Name == playlist).Items;
                Console.WriteLine("Rewriting Playlist: " + playlist);

                foreach (IITSource source in app.Sources)
                {
                    IITPlaylist epl = source.Playlists.ItemByName[playlist];
                    if (epl != null)
                        epl.Delete();
                }

                IITUserPlaylist pl = (IITUserPlaylist)app.CreatePlaylist(playlist);
                foreach (var item in items)
                {
                    IITTrack track = lib.Tracks.ItemByPersistentID[item.HighID, item.LowID];
                    pl.AddTrack(track);
                }

            }
#endif

            Console.WriteLine();


        }

    }

}