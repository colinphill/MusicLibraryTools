/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/PlayProject/Program.cs $
 * $Date: 2012-05-28 12:19:15 -0600 (Mon, 28 May 2012) $
 * $Revision: 4 $
 * $Author: Colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Security.Cryptography;

using iTunes;

namespace PlayProject
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Loading iTunes Library XML...");
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            string test = "Garbage\0Garbage";
            byte[] array = Encoding.ASCII.GetBytes(test);
            MD5 hash = MD5.Create();
            hash.TransformFinalBlock(array, 0, array.Length);

            ulong sum = 0, xor = 0;
            foreach (iTunesTrack track in lib.Tracks.Values)
            {
                ulong pid = ulong.Parse(track.PersistentID, System.Globalization.NumberStyles.HexNumber);
                sum = unchecked(sum + pid);
                xor = xor ^ pid;
            }

            Console.WriteLine();

        }
    }
}
