using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.IO;
using iTunes.Binary;
using MusicFileUtilities;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;

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

        static void CommitPlaylist(
            ItlDocument document,
            string playlistName,
            IReadOnlyList<ulong> items,
            string? templateName)
        {
            ItlRecord[] existing = [.. document.FindPlaylists(playlistName, StringComparison.OrdinalIgnoreCase)];

            if (existing.Length > 1)
                throw new InvalidOperationException($"More than one writable playlist is named '{playlistName}'; no playlist was changed.");

            int[] trackIds = [.. items.Select(persistentId =>
            {
                ItlRecord? track = document.FindTrackByPersistentId(persistentId);
                return track is null
                    ? throw new InvalidOperationException(
                        $"Planned track {persistentId:X16} is no longer present in the library.")
                    : ItlDocument.TrackIdOf(track);
            })];

            ItlRecord playlist;
            if (existing.Length == 1)
            {
                playlist = existing[0];
                if (ItlDocument.IsMasterPlaylist(playlist) || ItlDocument.SmartPlaylistOf(playlist) is not null)
                    throw new InvalidOperationException(
                        $"Playlist '{playlistName}' is system-owned or smart and cannot be replaced as a manual playlist.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(templateName))
                    throw new InvalidOperationException(
                        $"Playlist '{playlistName}' does not exist. Specify --template <manual-playlist> " +
                        "so its native header layout can be cloned safely.");
                ItlRecord[] templates = [.. document.FindPlaylists(templateName, StringComparison.OrdinalIgnoreCase)];
                if (templates.Length != 1 || ItlDocument.IsMasterPlaylist(templates[0]) ||
                    ItlDocument.SmartPlaylistOf(templates[0]) is not null)
                    throw new InvalidOperationException(
                        $"Template '{templateName}' must name exactly one existing manual non-master playlist.");
                playlist = document.AddPlaylist(playlistName, templates[0]);
            }

            document.ReplacePlaylistEntries(playlist, trackIds);
        }

        static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"BackSyncPlaylists: {exception.Message}");
                return 1;
            }
        }

        static int Run(string[] args)
        {
            bool apply = false;
            string? specifiedLibrary = null;
            string? templateName = null;
            var operandList = new List<string>();
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--apply", StringComparison.OrdinalIgnoreCase))
                    apply = true;
                else if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= args.Length) throw new ArgumentException("--library requires a path.");
                    specifiedLibrary = args[index];
                }
                else if (args[index].Equals("--template", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= args.Length) throw new ArgumentException("--template requires a playlist name.");
                    templateName = args[index];
                }
                else
                    operandList.Add(args[index]);
            }
            string[] operands = [.. operandList];
            if (operands.Length == 0)
            {
                Console.WriteLine("Usage: BackSyncPlaylists [playlist] [remap:<src>=><dest>] ... " +
                                  "[--library <file.itl>] [--template <manual-playlist>] [--apply]");
                Console.WriteLine("The default mode only reports matches. Pass --apply to edit the ITL directly.");
                return 0;
            }

            string iTunesLibraryFile = ItlFileEditor.ResolveLibraryPath(specifiedLibrary);
            if (apply)
                ItlFileEditor.EnsureItunesIsClosed();
            ItlEnvelope envelope = ItlEnvelope.Load(iTunesLibraryFile);
            ItlLibrary lib = ItlLibrary.Parse(envelope);
            ItlDocument? document = apply ? ItlDocument.Parse(envelope) : null;
            int changedPlaylists = 0;
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

                    var plannedItems = new List<ulong>();
                    bool validPlan = true;
                    foreach (string plfile in plfiles)
                    {
                        try
                        {
                            Console.WriteLine(plfile);

                            var mp = MediaFile.GetFile(plfile, readOnly: true).Tags.First();

                            string[] sourceArtists = new[] { mp.Artist, mp.AlbumArtist }
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            ItlTrack[] tracks = [.. lib.Tracks.Where(track =>
                                string.Equals(track.Title, mp.Title, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(track.Album, mp.Album, StringComparison.OrdinalIgnoreCase) &&
                                sourceArtists.Any(artist =>
                                    string.Equals(track.Artist, artist, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(track.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase)) &&
                                ((track.TrackNumber == mp.TrackNumber) || (mp.TrackNumber == 0)) &&
                                ((track.DiscNumber == mp.DiscNumber) || (mp.DiscNumber is null or 0)))];
                            if (tracks.Length != 1)
                            {
                                validPlan = false;
                                Console.WriteLine($"Expected one library match but found {tracks.Length}.");
                            }
                            else
                            {
                                Console.WriteLine(tracks[0].Title);
                                plannedItems.Add(tracks[0].PersistentId);
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
                            CommitPlaylist(document!, plname, plannedItems, templateName);
                            changedPlaylists++;
                            Console.WriteLine($"Prepared playlist '{plname}' with {plannedItems.Count} items.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Playlist '{plname}' was not changed: {ex.Message}");
                        }
                    }

                }
            }

            if (apply && changedPlaylists > 0)
            {
                ItlFileEditor.SaveValidated(document!, iTunesLibraryFile);
                Console.WriteLine($"Saved {changedPlaylists} playlist update(s) to '{iTunesLibraryFile}'.");
                Console.WriteLine($"The previous file is retained as '{iTunesLibraryFile}.bak'.");
            }
            return 0;
        }
    }
}
