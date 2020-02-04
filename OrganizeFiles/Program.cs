using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using MusicFileUtilities;
using MusicLibraryTools;

namespace OrganizeFiles
{
    static class Extensions
    {
        public static string LimitLength(this string val, int length)
        {
            int l = Math.Min(length, val.Length);
            return val.Substring(0, l).Trim(" \t".ToCharArray());
        }

        public static string FixPath(this string item)
        {
            string fix = item;
            foreach (char c in Path.GetInvalidFileNameChars())
                fix = fix.Replace(c.ToString(), "_");
            foreach (char c in Path.GetInvalidPathChars())
                fix = fix.Replace(c.ToString(), "_");
            fix = fix.Replace('$', 's');
            fix = fix.Replace("\"", "");
            fix = fix.Trim();
            while (fix.EndsWith("."))
                fix = fix.Remove(fix.Length - 1);
            return fix;
        }
    
    }

    class Program
    {
        private static readonly string[] validexts_ = { ".dsf", ".m4a", ".mp3", ".flac", ".ogg" };
        static bool DeleteEmptyFolders(DirectoryInfo di)
        {
            bool empty = true;
            foreach (var subdi in di.GetDirectories())
            {
                if (!DeleteEmptyFolders(subdi))
                    empty = false;
            }

            var files = di.GetFiles();
            if (empty)
            {
                foreach (var file in files)
                {
                    if (validexts_.Contains(Path.GetExtension(file.Name).ToLower()))
                        empty = false;
                }
                if (empty)
                {
                    foreach (var file in files)
                        file.Delete();
                }
            }
            if ((empty)&&(di.GetFiles().Length == 0))
            {
                di.Delete();                
            }
            else
                empty = false;
            return empty;
        }

        static void Main(string[] args)
        {
            string basedir = args[0];
            const int LENGTH_LIMIT = 40;
            const int DISC_NUM_LENGTH_LIMIT = 32;
            MetadataCache cache = new MetadataCache();
            if (File.Exists(@"metadata.cache"))
                cache.Load(@"metadata.cache");
            cache.BeginBuildCache();
            cache.BuildCache(basedir);
            cache.EndBuildCache();
            cache.Save(@"metadata.cache");
            Regex dnre = new Regex(@"(.+)[ \t]+\(Disc (.+)\)", RegexOptions.IgnoreCase);
            int count = 0;
            foreach (var f in cache.FileCache)
            {
                count++;
                string art = (string.IsNullOrWhiteSpace(f.Value.AlbumArtist) ? f.Value.Artist : f.Value.AlbumArtist).LimitLength(LENGTH_LIMIT);
                string alb = f.Value.StrippedAlbum;
                string ttl = f.Value.Title.LimitLength(LENGTH_LIMIT);
                var m = dnre.Match(f.Value.Album);
                alb = m.Success ? (m.Groups[1].Value.LimitLength(DISC_NUM_LENGTH_LIMIT) + " (Disc " + m.Groups[2].Value + ")") : alb.LimitLength(LENGTH_LIMIT);
                art = art.FixPath();
                alb = alb.FixPath();
                ttl = ttl.FixPath();
                string tgt = Path.Combine(basedir, art, alb, f.Value.TrackNumber.ToString("D2") + " " + ttl + Path.GetExtension(f.Key));
                if (!f.Key.Equals(tgt, StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine(count.ToString() + ") " + f.Key + " -> " + tgt);
                    Directory.CreateDirectory(Path.GetDirectoryName(tgt));
                    File.Move(f.Key, tgt);
                }
            }
            DeleteEmptyFolders(new DirectoryInfo(basedir));
            Console.WriteLine();
        }
    }
}
