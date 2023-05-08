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
using System.Text.RegularExpressions;

namespace BackSyncPlaylists
{
    class Program
    {

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

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: BackSyncPlaylists <playlist> [playlist] [playlist...]");
                return;
            }

            iTunesApp app = new iTunesApp();
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);
            var remap = new Regex("^remap:(.+):(.+)$", RegexOptions.IgnoreCase);
            var remaps = new List<(string oldval, string newval)>();

            foreach (var arg in args)
            {
                var m = remap.Match(arg);
                if (m.Success)
                {
                    remaps.Add((m.Groups[1].Value, m.Groups[2].Value));
                    continue;
                }
                var dir = Path.GetDirectoryName(arg);
                var files = Directory.GetFiles(dir, Path.GetFileName(arg));
                foreach (var file in files)
                {
                    string plname = Path.GetFileNameWithoutExtension(file);
                    string[] plfiles = new string[0];
                    if (Path.GetExtension(file).Equals(".wpl", StringComparison.InvariantCultureIgnoreCase))
                    {
                        XDocument doc = XDocument.Load(file);
                        plname = doc.Descendants("title").Single().Value;
                        plfiles = doc.Descendants("media").Select(n => Path.Combine(dir, n.Attribute("src").Value)).ToArray();
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
                        catch
                        { }
                    }
                    if (plfiles.Length == 0)
                        continue;

                    foreach (var rm in remaps)
                        plfiles = plfiles.Select(f => f.Replace(rm.oldval, rm.newval, StringComparison.InvariantCultureIgnoreCase)).ToArray();

                    IITPlaylist playlist = app.CreatePlaylist(plname);
                    IITUserPlaylist libplaylist = playlist as IITUserPlaylist;

                    foreach (string plfile in plfiles)
                    {
                        try
                        {
                            Console.WriteLine(plfile);

                            var mp = MediaFile.GetFile(plfile).Tags.First();

                            KeyValuePair<int, iTunesTrack>[] tracks = lib.Tracks.Where(kv => (kv.Value.Title.ToLower() == mp.Title.ToLower()) && (kv.Value.Album.ToLower() == mp.Album.ToLower()) && ((kv.Value.Artist.ToLower() == mp.Artist.ToLower()) || (kv.Value.Artist.ToLower() == mp.AlbumArtist.ToLower())) && ((kv.Value.TrackNumber == mp.TrackNumber) || (mp.TrackNumber == 0))).ToArray();
                            if (tracks.Length != 1)
                            {
                                Console.WriteLine();
                            }
                            else
                            {
                                int highpid = int.Parse(tracks[0].Value.PersistentID.Substring(0, 8), System.Globalization.NumberStyles.HexNumber);
                                int lowpid = int.Parse(tracks[0].Value.PersistentID.Substring(8), System.Globalization.NumberStyles.HexNumber);
                                IITTrack track = app.LibraryPlaylist.Tracks.get_ItemByPersistentID(highpid, lowpid);
                                if (track != null)
                                {
                                    Console.WriteLine(track.Name);
                                    libplaylist.AddTrack(track);
                                }
                                else
                                    Console.WriteLine();
                                //libplaylist.AddFile(tracks[0].Value.LocalLocation);
                            }

                            Console.WriteLine();
                        }
                        catch
                        {

                        }

                    }

                }
            }
        }
    }
}
