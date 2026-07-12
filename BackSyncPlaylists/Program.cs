using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.IO;
using iTunesLib;
using iTunes;
using MusicFileUtilities;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using System.Globalization;

namespace BackSyncPlaylists
{
    class Program
    {

        static bool TryParseRemap(string argument, out (string oldval, string newval) remap)
        {
            remap = default;
            const string prefix = "remap:";
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string value = argument.Substring(prefix.Length);
            int separator = value.IndexOf("=>", StringComparison.Ordinal);
            int separatorLength = 2;

            if (separator < 0)
            {
                separatorLength = 1;
                int start = value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
                    (value[2] == '\\' || value[2] == '/') ? 2 : 0;

                // Prefer a delimiter whose right side clearly starts a rooted path. This
                // avoids treating either drive-letter colon in D:\\Music:E:\\Music as
                // the src/dest separator.
                for (int i = start; i < value.Length; i++)
                {
                    if (value[i] != ':')
                        continue;
                    string candidate = value.Substring(i + 1);
                    bool rootedDrive = candidate.Length >= 3 && char.IsLetter(candidate[0]) &&
                        candidate[1] == ':' && (candidate[2] == '\\' || candidate[2] == '/');
                    bool rootedOther = candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                        candidate.StartsWith("/", StringComparison.Ordinal);
                    if (rootedDrive || rootedOther)
                    {
                        separator = i;
                        break;
                    }
                }

                if (separator < 0)
                    separator = value.IndexOf(':', start);
            }

            if (separator <= 0 || separator + separatorLength >= value.Length)
                throw new ArgumentException($"Invalid remap '{argument}'. Use remap:<source>=><destination>.");

            remap = (value.Substring(0, separator), value.Substring(separator + separatorLength));
            return true;
        }

        static (int row, int col) ParseReference(string reference) 
        {
            int row = 0;
            int col = 0;
            foreach (char c in reference) 
            {
                if (char.IsLetter(c))
                    col = col * 26 + (int)c - (int)'A' + 1;
                else
                    row = row * 10 + (int)c - (int)'0';
            }

            return (row-1, col-1);
        }

        static void CommitPlaylist(iTunesApp app, string playlistName, IReadOnlyList<(int High, int Low)> items)
        {
            var existing = new List<(IITSource Source, IITUserPlaylist Playlist)>();
            foreach (IITSource source in app.Sources)
                foreach (IITPlaylist playlist in source.Playlists)
                    if (playlist.Kind == ITPlaylistKind.ITPlaylistKindUser &&
                        playlist.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase) &&
                        playlist is IITUserPlaylist user && !user.Smart &&
                        user.SpecialKind == ITUserPlaylistSpecialKind.ITUserPlaylistSpecialKindNone)
                        existing.Add((source, user));

            if (existing.Count > 1)
                throw new InvalidOperationException($"More than one writable playlist is named '{playlistName}'; no playlist was changed.");

            IITSource targetSource = existing.Count == 1 ? existing[0].Source : app.LibrarySource;
            string token = Guid.NewGuid().ToString("N");
            string temporaryName = "BackSync staging " + token;
            object sourceObject = targetSource;
            IITUserPlaylist replacement = (IITUserPlaylist)app.CreatePlaylistInSource(temporaryName, ref sourceObject);
            try
            {
                if (existing.Count == 1)
                {
                    object parent = existing[0].Playlist.get_Parent();
                    replacement.set_Parent(ref parent);
                    replacement.Shuffle = existing[0].Playlist.Shuffle;
                    replacement.SongRepeat = existing[0].Playlist.SongRepeat;
                    replacement.Shared = existing[0].Playlist.Shared;
                }

                foreach (var item in items)
                {
                    IITTrack track = app.LibraryPlaylist.Tracks.get_ItemByPersistentID(item.High, item.Low);
                    if (track == null)
                        throw new InvalidOperationException("A planned persistent ID is no longer present in the live library.");
                    replacement.AddTrack(track);
                }
                if (replacement.Tracks.Count != items.Count)
                    throw new InvalidOperationException("The staged playlist item count did not match the plan.");

                if (existing.Count == 0)
                {
                    replacement.Name = playlistName;
                    return;
                }

                IITUserPlaylist original = existing[0].Playlist;
                string backupName = "BackSync backup " + token;
                original.Name = backupName;
                try
                {
                    replacement.Name = playlistName;
                }
                catch
                {
                    original.Name = playlistName;
                    throw;
                }

                try
                {
                    original.Delete();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Playlist was replaced, but backup '{backupName}' could not be removed: {ex.Message}");
                }
            }
            catch
            {
                try { replacement.Delete(); } catch { }
                throw;
            }
        }

        static void Main(string[] args)
        {
            bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            string[] operands = args.Where(a => !a.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (operands.Length == 0)
            {
                Console.WriteLine("Usage: BackSyncPlaylists [playlist] [remap:<src>=><dest>] ... [--apply]");
                Console.WriteLine("The default mode only reports matches. Pass --apply to create playlists.");
                return;
            }

            iTunesApp app = apply ? new iTunesApp() : null;
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);
            var remaps = new List<(string oldval, string newval)>();
            var playlistOperands = new List<string>();

            foreach (var arg in operands)
            {
                if (TryParseRemap(arg, out var parsedRemap))
                    remaps.Add(parsedRemap);
                else
                    playlistOperands.Add(arg);
            }

            foreach (var arg in playlistOperands)
            {
                var dir = Path.GetDirectoryName(arg);
                if (string.IsNullOrEmpty(dir))
                    dir = Directory.GetCurrentDirectory();
                var files = Directory.GetFiles(dir, Path.GetFileName(arg));
                foreach (var file in files)
                {
                    string plname = Path.GetFileNameWithoutExtension(file);
                    string[] plfiles = new string[0];
                    if (Path.GetExtension(file).Equals(".wpl", StringComparison.InvariantCultureIgnoreCase))
                    {
                        XDocument doc = XDocument.Load(file);
                        plname = doc.Descendants("title").Single().Value;
                        plfiles = doc.Descendants("media").Select(n => n.Attribute("src").Value).ToArray();
                    }
                    if (Path.GetExtension(file).Equals(".m3u", StringComparison.InvariantCultureIgnoreCase))
                        plfiles = File.ReadAllLines(file).Where(s => !s.StartsWith("#")).ToArray();
                    if (Path.GetExtension(file).Equals(".xlsx", StringComparison.InvariantCultureIgnoreCase))
                    {
                        try
                        {
                            using (var d = SpreadsheetDocument.Open(file, false))
                            {
                                List<((int row, int col) reference, string value)> cells = new();
                                var sst = d.WorkbookPart.SharedStringTablePart.SharedStringTable;
                                var sheet = d.WorkbookPart.Workbook.Sheets.Cast<Sheet>().Where(s => s.Name == "Tracks").First();
                                var wsp = d.WorkbookPart.WorksheetParts.Where(w => d.WorkbookPart.GetIdOfPart(w) == sheet.Id).First();
                                var sd = wsp.Worksheet.Elements<SheetData>().First();
                                foreach (var r in sd.Elements<Row>())
                                {
                                    foreach (var c in r.Elements<Cell>())
                                    {
                                        string val = c.CellValue.InnerText;
                                        if (c.DataType == CellValues.SharedString)
                                            val = sst.ElementAt(int.Parse(val)).InnerText;
                                        cells.Add((ParseReference(c.CellReference), val));
                                    }
                                }
                                int rows = cells.Max(c => c.reference.row)+1;
                                int cols = cells.Max(c => c.reference.col)+1;
                                string[,] mat = new string[rows, cols];
                                cells.ForEach(c => mat[c.reference.row, c.reference.col] = c.value);
                                int pathcol = 0;
                                for (;pathcol<cols;pathcol++)
                                    if (mat[0, pathcol].Equals("path", StringComparison.InvariantCultureIgnoreCase))
                                        break;
                                var paths = new List<string>();
                                for (int row = 1; row < rows; row++)
                                    paths.Add(mat[row, pathcol]);
                                plfiles = paths.ToArray();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Unable to read spreadsheet '{file}': {ex.Message}");
                        }
                    }
                    if (plfiles.Length == 0)
                        continue;

                    foreach (var rm in remaps)
                        plfiles = plfiles.Select(f => f.Replace(rm.oldval, rm.newval, StringComparison.InvariantCultureIgnoreCase)).ToArray();
                    plfiles = plfiles.Select(f => Path.IsPathFullyQualified(f) ? f : Path.Combine(dir, f)).ToArray();

                    var plannedItems = new List<(int High, int Low)>();
                    bool validPlan = true;
                    foreach (string plfile in plfiles)
                    {
                        try
                        {
                            Console.WriteLine(plfile);

                            var mp = MediaFile.GetFile(plfile).Tags.First();

                            string[] sourceArtists = new[] { mp.Artist, mp.AlbumArtist }
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            KeyValuePair<int, iTunesTrack>[] tracks = lib.Tracks.Where(kv =>
                                string.Equals(kv.Value.Title, mp.Title, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(kv.Value.Album, mp.Album, StringComparison.OrdinalIgnoreCase) &&
                                sourceArtists.Any(artist =>
                                    string.Equals(kv.Value.Artist, artist, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(kv.Value.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase)) &&
                                ((kv.Value.TrackNumber == mp.TrackNumber) || (mp.TrackNumber == 0)) &&
                                ((kv.Value.DiscNumber == mp.DiscNumber) || (mp.DiscNumber is null or 0))).ToArray();
                            if (tracks.Length != 1)
                            {
                                validPlan = false;
                                Console.WriteLine($"Expected one library match but found {tracks.Length}.");
                            }
                            else
                            {
                                Console.WriteLine(tracks[0].Value.Title);
                                // Persistent IDs are unsigned 32-bit halves. Parse them as
                                // uint and preserve their bit patterns for the signed COM API.
                                int highpid = unchecked((int)uint.Parse(tracks[0].Value.PersistentID.Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                int lowpid = unchecked((int)uint.Parse(tracks[0].Value.PersistentID.Substring(8), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                plannedItems.Add((highpid, lowpid));
                            }

                            Console.WriteLine();
                        }
                        catch (Exception ex)
                        {
                            validPlan = false;
                            Console.WriteLine($"Unable to plan '{plfile}': {ex.Message}");
                        }

                    }

                    if (!validPlan || plannedItems.Count != plfiles.Length)
                    {
                        Console.WriteLine($"Skipping playlist '{plname}' because its complete item list could not be resolved.");
                        continue;
                    }

                    if (!apply)
                        Console.WriteLine($"Would create or replace playlist '{plname}' with {plannedItems.Count} items.");
                    else
                    {
                        try
                        {
                            CommitPlaylist(app, plname, plannedItems);
                            Console.WriteLine($"Updated playlist '{plname}' with {plannedItems.Count} items.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Playlist '{plname}' was not changed: {ex.Message}");
                        }
                    }

                }
            }
        }
    }
}
