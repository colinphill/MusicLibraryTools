/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/FindNonLossless/Program.cs $
 * $Date: 2014-09-25 06:50:52 -0600 (Thu, 25 Sep 2014) $
 * $Revision: 17 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using MusicFileUtilities;
using ConsoleTools;

namespace FindNonLossless
{
    class Program
    {

        static void CheckM4AFile(string filename)
        {
            RootAtom root = new RootAtom(filename);
            Atom atom = root.FindPath("moov.trak.mdia.minf.stbl.stsd.alac");
            if (atom == null)
                LogConsole.WriteLine("Not ALAC: " + filename);
        }

        static void ScanDirectory(string dir)
        {
            static string[] Files(string path, string pattern)
            {
                try { return Directory.GetFiles(path, pattern); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    LogConsole.WriteLine($"Unable to enumerate {path}: {ex.Message}");
                    return Array.Empty<string>();
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                LogConsole.WriteLine($"Unable to scan {dir}: {ex.Message}");
                return;
            }

            foreach (string s in directories)
            {
                if (s.Contains("purchased sync", StringComparison.OrdinalIgnoreCase))
                    continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(s); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    LogConsole.WriteLine($"Unable to inspect {s}: {ex.Message}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    LogConsole.WriteLine("Skipping reparse point: " + s);
                    continue;
                }
                ScanDirectory(s);
            }
            foreach (string f in Files(dir, "*.m4a"))
            {
                try { CheckM4AFile(f); }
                catch (Exception ex) { LogConsole.WriteLine($"Unable to inspect {f}: {ex.Message}"); }
            }
            // Directory.GetFiles takes a single pattern, not a ';'-separated list, so each
            // non-ALAC extension must be enumerated on its own.
            foreach (string ext in new[] { "*.mp3", "*.ogg", "*.flac", "*.wav", "*.aiff" })
                foreach (string f in Files(dir, ext))
                    LogConsole.WriteLine("Not ALAC: " + f);
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("FindNonLossless.log");
            if (args.Length != 1)
            {
                LogConsole.WriteLine("Usage: FindNonLossless <path>");
                return;
            }
            string dir = args[0];
            if (!Directory.Exists(dir))
            {
                LogConsole.WriteLine("Directory does not exist: " + dir);
                LogConsole.Close();
                return;
            }
            ScanDirectory(dir);
            LogConsole.Close();
        }
    }
}
