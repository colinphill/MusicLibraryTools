using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using MusicFileUtilities;
using MusicLibraryTools;
using ConsoleTools;
using MetadataCaching;

namespace OrganizeFiles
{
    class Program
    {

 
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: OrganizeFiles <libraryconfiguration.xml> [--apply]");
                return;
            }

            bool apply = args.Skip(1).Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            if (!apply)
                LogConsole.WriteLine("Dry run: pass --apply to move files.");

            LibraryConfiguration config = new LibraryConfiguration(args[0]);
            bool deletenonmusic = config["DeleteNonMusic"].Count() != 0;
            bool keepfolderimages = config["KeepFolderImages"].Count() != 0;
            int LENGTH_LIMIT = config.LengthLimit;
            int DISC_NUM_LENGTH_LIMIT = config.DiscNumLengthLimit;

            LogConsole.WriteLine("Indexing...");

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(config.DatabaseFile); // TBD Dispose
            db.IndexFiles(config.IndexLocations.Select(l => new ScanRootDefinition(l.Target, l.Sets)));

            var planned = new List<(string Source, string Destination)>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string basedir in config.IndexLocations.Select(il => il.Target))
            {
                LogConsole.WriteLine("Indexing: " + basedir);

                var cache = db.BuildCache(new string[] { basedir });

                int count = 0;
                foreach (var f in cache.FileCache)
                {
                    count++;
                    string tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + Path.GetExtension(f.Key)).Normalize();
                    if (!f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase) || !f.Key.IsNormalized())
                    {
                        int index = 2;
                        while ((File.Exists(tgt) && !f.Key.Equals(tgt, StringComparison.OrdinalIgnoreCase)) || claimed.Contains(tgt))
                        {
                            tgt = Path.Combine(basedir, f.Value.FormatPath(LENGTH_LIMIT, DISC_NUM_LENGTH_LIMIT) + $"_{index++}" + Path.GetExtension(f.Key)).Normalize();
                            if (f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase) && f.Key.IsNormalized())
                                break; 
                        }
                        if (f.Key.Equals(tgt, StringComparison.OrdinalIgnoreCase) && f.Key.IsNormalized())
                            continue;

                        claimed.Add(tgt);
                        planned.Add((f.Key, tgt));
                        LogConsole.WriteLine(count.ToString() + ") " + f.Key + " -> " + tgt);
                    }
                }
                Console.WriteLine();
            }

            if (!apply)
            {
                LogConsole.WriteLine($"Planned moves: {planned.Count}");
                return;
            }

            var completed = new List<(string Source, string Destination)>();
            try
            {
                foreach (var move in planned)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                    File.Move(move.Source, move.Destination);
                    completed.Add(move);
                }
            }
            catch
            {
                LogConsole.WriteLine("Move failed; attempting to roll back completed moves.");
                foreach (var move in completed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(move.Destination) && !File.Exists(move.Source))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!);
                            File.Move(move.Destination, move.Source);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogConsole.WriteLine($"Rollback failed: {move.Destination} -> {move.Source}: {ex.Message}");
                    }
                }
                throw;
            }

            foreach (string basedir in config.IndexLocations.Select(il => il.Target))
                MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(basedir), deletenonmusic, keepfolderimages);
        }
    }
}
