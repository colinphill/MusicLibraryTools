/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/MetaTools.cs $
 * $Date: 2014-09-25 06:50:52 -0600 (Thu, 25 Sep 2014) $
 * $Revision: 17 $
 * $Author: colin $
 * 
 */

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MusicFileUtilities
{

    internal class Tools
    {

        public static ushort UInt16AtLE(byte[] b, int offset)
        {
            ushort res = b[offset + 1];
            res = (ushort)((res << 8) | b[offset]);
            return res;
        }

        public static short Int16AtLE(byte[] b, int offset)
        {
            return unchecked((short)UInt16AtLE(b, offset));
        }

        public static ushort UInt16AtBE(byte[] b, int offset)
        {
            ushort res = b[offset];
            res = (ushort)((res << 8) | b[offset + 1]);
            return res;
        }

        public static short Int16AtBE(byte[] b, int offset)
        {
            return unchecked((short)UInt16AtBE(b, offset));
        }

        public static uint UInt32AtLE(byte[] b, int offset)
        {
            uint res = b[offset + 3];
            res = (res << 8) | b[offset + 2];
            res = (res << 8) | b[offset + 1];
            res = (res << 8) | b[offset];
            return res;
        }

        public static int Int32AtLE(byte[] b, int offset)
        {
            return unchecked((int)UInt32AtLE(b, offset));
        }

        public static uint UInt32AtBE(byte[] b, int offset)
        {
            uint res = b[offset];
            res = (res << 8) | b[offset + 1];
            res = (res << 8) | b[offset + 2];
            res = (res << 8) | b[offset + 3];
            return res;
        }

        public static int Int32AtBE(byte[] b, int offset)
        {
            return unchecked((int)UInt32AtBE(b, offset));
        }

        public static ulong UInt64AtLE(byte[] b, int offset)
        {
            ulong res = UInt32AtLE(b, offset + 4);
            res = (res << 8) | UInt32AtLE(b, offset);
            return res;
        }

        public static long Int64AtLE(byte[] b, int offset)
        {
            return unchecked((long)UInt64AtLE(b, offset));
        }

        public static ulong UInt64AtBE(byte[] b, int offset)
        {
            ulong res = UInt32AtBE(b, offset);
            res = (res << 8) | UInt32AtBE(b, offset + 4);
            return res;
        }

        public static long Int64AtBE(byte[] b, int offset)
        {
            return unchecked((long)UInt64AtBE(b, offset));
        }

        public static byte[] ToLE(uint u)
        {
            byte[] b = new byte[4];
            b[0] = (byte)(u & 0xff);
            b[1] = (byte)((u >> 8) & 0xff);
            b[2] = (byte)((u >> 16) & 0xff);
            b[3] = (byte)((u >> 24) & 0xff);
            return b;
        }

        public static byte[] ToLE(int i)
        {
            return ToLE((uint)i);
        }

        public static byte[] ToBE(uint u)
        {
            byte[] b = new byte[4];
            b[3] = (byte)(u & 0xff);
            b[2] = (byte)((u >> 8) & 0xff);
            b[1] = (byte)((u >> 16) & 0xff);
            b[0] = (byte)((u >> 24) & 0xff);
            return b;
        }

        public static byte[] ToBE(int i)
        {
            return ToBE((uint)i);
        }

    }

    public static class FuzzyMatching
    {
        /// <SUMMARY>Computes the Levenshtein Edit Distance between two enumerables.</SUMMARY>
        /// <TYPEPARAM name="T">The type of the items in the enumerables.</TYPEPARAM>
        /// <PARAM name="x">The first enumerable.</PARAM>
        /// <PARAM name="y">The second enumerable.</PARAM>
        /// <RETURNS>The edit distance.</RETURNS>
        public static int EditDistance<T>(this IEnumerable<T> x, IEnumerable<T> y)
            where T : IEquatable<T>
        {
            // Validate parameters
            if (x == null) throw new ArgumentNullException("x");
            if (y == null) throw new ArgumentNullException("y");

            // Convert the parameters into IList instances
            // in order to obtain indexing capabilities
            IList<T> first = x as IList<T> ?? new List<T>(x);
            IList<T> second = y as IList<T> ?? new List<T>(y);

            // Get the length of both.  If either is 0, return
            // the length of the other, since that number of insertions
            // would be required.
            int n = first.Count, m = second.Count;
            if (n == 0) return m;
            if (m == 0) return n;

            // Rather than maintain an entire matrix (which would require O(n*m) space),
            // just store the current row and the next row, each of which has a length m+1,
            // so just O(m) space. Initialize the current row.
            int curRow = 0, nextRow = 1;
            int[][] rows = new int[][] { new int[m + 1], new int[m + 1] };
            for (int j = 0; j <= m; ++j) rows[curRow][j] = j;

            // For each virtual row (since we only have physical storage for two)
            for (int i = 1; i <= n; ++i)
            {
                // Fill in the values in the row
                rows[nextRow][0] = i;
                for (int j = 1; j <= m; ++j)
                {
                    int dist1 = rows[curRow][j] + 1;
                    int dist2 = rows[nextRow][j - 1] + 1;
                    int dist3 = rows[curRow][j - 1] +
                        (first[i - 1].Equals(second[j - 1]) ? 0 : 1);

                    rows[nextRow][j] = Math.Min(dist1, Math.Min(dist2, dist3));
                }

                // Swap the current and next rows
                if (curRow == 0)
                {
                    curRow = 1;
                    nextRow = 0;
                }
                else
                {
                    curRow = 0;
                    nextRow = 1;
                }
            }

            // Return the computed edit distance
            return rows[curRow][m];
        }

        public static double FuzzyDistance(this string x, string y)
        {
            double dist = x.ToLower().EditDistance(y.ToLower());
            return dist / Math.Max(x.Length, y.Length);
        }

    }

    public static class MetadataExtensions
    {
        public static readonly HashSet<string> ValidExtensions = new HashSet<string>() { ".dsf", ".m4a", ".mp3", ".flac", ".ogg" };

        public static readonly Regex DiscNumRegex = new Regex(@"(.+)[ \t]+\(Disc (.+)\)", RegexOptions.IgnoreCase);

        public static string LimitLength(this string val, int length)
        {
            int l = Math.Min(length, val.Length);
            return val.Substring(0, l).Trim(" \t".ToCharArray());
        }

        public static string FormatDisc(this string alb, int length, int discnumlength)
        {
            var m = DiscNumRegex.Match(alb);
            return m.Success ? (m.Groups[1].Value.LimitLength(discnumlength) + " (Disc " + m.Groups[2].Value + ")") : alb.LimitLength(length);
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
            while (fix.StartsWith("."))
                fix = fix.Remove(0, 1);
            return fix;
        }

#if false
        public static bool CleanEmptyMusicFolders(DirectoryInfo di, bool deletenonmusic = false)
        {
            bool empty = true;
            foreach (var subdi in di.GetDirectories())
            {
                if (!CleanEmptyMusicFolders(subdi, deletenonmusic))
                    empty = false;
            }

            var files = di.GetFiles();
            if (empty)
            {
                foreach (var file in files)
                {
                    if (ValidExtensions.Contains(Path.GetExtension(file.Name).ToLower()))
                        empty = false;
                    else if (deletenonmusic)
                        file.Delete();
                }
            }
            if ((empty) && (di.GetFiles().Length == 0))
            {
                di.Delete();
            }
            else
                empty = false;
            return empty;
        }
#endif
#if false
        public static void CleanEmptyMusicFolders(DirectoryInfo di, bool deletenonmusic = false)
        {
            var results = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories);
            var hitpaths = results.Where(r => r is DirectoryInfo).ToDictionary(r => r.FullName, r => false);

            foreach (var fi in results.Where(fi => fi is FileInfo))
            {
                var kept = false;
                if (ValidExtensions.Contains(Path.GetExtension(fi.FullName).ToLower()))
                    kept = true;
                else if (deletenonmusic)
                    fi.Delete();
                else
                    kept = true;
                if (kept)
                    hitpaths[Path.GetDirectoryName(fi.FullName)] = true;
            }

            foreach (var path in hitpaths.Where(kv => kv.Value).Select(kv => kv.Key).ToArray())
            {
                var tpath = Path.GetDirectoryName(path);
                while (hitpaths.ContainsKey(tpath))
                {
                    hitpaths[tpath] = true;
                    tpath = Path.GetDirectoryName(tpath);
                }
            }

            foreach (var path in hitpaths.Where(kv => !kv.Value).Select(kv => kv.Key).OrderByDescending(p => p))
                Directory.Delete(path);
        }
#endif

        public static void CleanEmptyMusicFolders(DirectoryInfo dirinfo, bool deletenonmusic = false)
        {
            var results = dirinfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories);
            var hitpaths = new ConcurrentDictionary<string, bool>();

            Parallel.ForEach(results, (fsi) =>
            {
                if (fsi is FileInfo fi)
                {
                    if (ValidExtensions.Contains(fi.Extension.ToLower()) || (!deletenonmusic))
                        hitpaths[Path.GetDirectoryName(fi.FullName)] = true;
                    else if (deletenonmusic)
                        fi.Delete();
                }
                if (fsi is DirectoryInfo di)
                    hitpaths.TryAdd(di.FullName, false);
            });

            var hitkeys = hitpaths.Where(kv => kv.Value).Select(kv => kv.Key);
            var hitdict = new Dictionary<string, bool>(hitpaths);
            foreach (var key in hitkeys)
            {
                var path = Path.GetDirectoryName(key);
                while (path != dirinfo.FullName)
                {
                    hitdict[path] = true;
                    path = Path.GetDirectoryName(path);
                }
            }

            var missedpaths = hitdict.Where(kv => !kv.Value).Select(kv => kv.Key).OrderByDescending(p => p);

            foreach (var path in missedpaths)
                Directory.Delete(path);
        }

   }

}