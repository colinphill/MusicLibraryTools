using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using MusicFileUtilities;
using MusicLibraryTools;


namespace PlayProject
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 1 || !Directory.Exists(args[0]))
            {
                Console.Error.WriteLine("Usage: PlayProject <music-root>");
                return;
            }

            int count = 0;
            MusicFileEnumerator mfe = new MusicFileEnumerator(args[0]);
            foreach (var entry in mfe)
            {
                if (entry.FileType != MFEType.MusicFile)
                {
                    Console.WriteLine(entry.Name);
                    count++;
                }
            }
            
            Console.WriteLine(count);

        }
    }
}
