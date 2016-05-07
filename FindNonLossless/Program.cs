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
            foreach (string s in Directory.GetDirectories(dir))
            {
                if (s.ToLower().Contains("purchased sync"))
                    continue;
                ScanDirectory(s);
            }
            foreach (string f in Directory.GetFiles(dir, "*.m4a"))
                CheckM4AFile(f);
            foreach (string f in Directory.GetFiles(dir, "*.mp3;*.ogg;*.flac;*.wav;*.aiff"))
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
            ScanDirectory(dir);
            LogConsole.Close();
        }
    }
}
