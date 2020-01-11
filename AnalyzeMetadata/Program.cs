using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicFileUtilities;
using MusicLibraryTools;
using System.IO;

namespace AnalyzeMetadata
{
    class Program
    {
        static void Main(string[] args)
        {
            FLACFile flac = new FLACFile(@"Z:\iTunes\HiRes\Multi\Downloads\85498-YAR88171DSD-yuko-mabuchi-plays-miles-davis-multi256\FLAC\1_All-Blues_multi256.flac");
            RootAtom aac = new RootAtom();
            aac.ReadFile(@"c:\temp\test.m4a");
            RootAtom alac = new RootAtom();
            alac.ReadFile(@"c:\temp\testalac.m4a");
            RootAtom hr = new RootAtom();
            hr.ReadFile(@"c:\temp\testhr.m4a");
            MetadataCache cache = new MetadataCache();
            if (File.Exists(@"metadata.cache"))
                cache.Load(@"metadata.cache");
            cache.BeginBuildCache();
            cache.BuildCache(@"z:\itunes\hires", false);
            cache.BuildCache(@"z:\itunes\lossless\music", false);
            cache.BuildCache(@"z:\itunes\lossless\purchased sync", false);
            cache.EndBuildCache();
            cache.Save(@"metadata.cache");
            Console.WriteLine();

        }
    }
}
