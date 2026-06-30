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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using MusicLibraryTools;

// GenerateAssemblyInfo is disabled for this project, so the MSBuild <InternalsVisibleTo>
// item is ignored; declare it here so the test project can reach the internal byte helpers.
[assembly: InternalsVisibleTo("MusicFileUtilities.Tests")]

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
            res = (res << 32) | UInt32AtLE(b, offset);
            return res;
        }

        public static long Int64AtLE(byte[] b, int offset)
        {
            return unchecked((long)UInt64AtLE(b, offset));
        }

        public static ulong UInt64AtBE(byte[] b, int offset)
        {
            ulong res = UInt32AtBE(b, offset);
            res = (res << 32) | UInt32AtBE(b, offset + 4);
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
            int max = Math.Max(x.Length, y.Length);
            if (max == 0)
                return 0.0; // two empty strings are identical; avoid 0/0 -> NaN
            double dist = x.ToLower().EditDistance(y.ToLower());
            return dist / max;
        }

    }

    public static class MetadataExtensions
    {
        public static readonly HashSet<string> ValidExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dsf", ".m4a", ".mp3", ".flac", ".ogg", ".wv" };

        public static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ValidExtensionSpans = ValidExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

        public static readonly Regex DiscNumRegex = new Regex(@"(.+)[ \t]+\(Disc (.+)\)", RegexOptions.IgnoreCase);

        // Union of invalid filename + path chars, built once for FixPath's single-pass sanitize.
        private static readonly HashSet<char> InvalidPathChars = BuildInvalidPathChars();

        private static HashSet<char> BuildInvalidPathChars()
        {
            var set = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (char c in Path.GetInvalidPathChars())
                set.Add(c);
            return set;
        }

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
            // Single pass, preserving the original rules: invalid path/filename chars -> '_',
            // '$' -> 's', and a '"' that isn't already an invalid char is dropped. Then trim
            // surrounding whitespace and leading/trailing dots.
            var sb = new StringBuilder(item.Length);
            foreach (char c in item)
            {
                if (c == '$')
                    sb.Append('s');
                else if (InvalidPathChars.Contains(c))
                    sb.Append('_');
                else if (c == '"')
                    continue; // only reached where '"' isn't an invalid char (non-Windows)
                else
                    sb.Append(c);
            }

            string fix = sb.ToString().Trim();
            int start = 0, end = fix.Length;
            while (end > start && fix[end - 1] == '.') end--;
            while (start < end && fix[start] == '.') start++;
            return fix.Substring(start, end - start);
        }

        public static void CleanEmptyMusicFolders(DirectoryInfo dirinfo, bool deletenonmusic = false, bool keepfolderimages = false)
        {
            var hitpaths = new ConcurrentDictionary<string, bool>();

            // MusicFileEnumerator streams the tree, classifies files as music/other via the
            // allocation-free span extension lookup, and prunes .itlp packages during recursion.
            // Parallel.ForEach synchronizes MoveNext on the single-use enumerator, so concurrent
            // consumption (and the file deletes below) is safe.
            Parallel.ForEach(new MusicFileEnumerator(dirinfo.FullName), (entry) =>
            {
                if (entry.FileType == MFEType.Directory)
                {
                    // .itlp packages are not recursed into, so their contents are never seen and the
                    // package would otherwise look empty and get pruned. Mark it kept to leave the
                    // package (and everything under it) intact.
                    if (Path.GetFileName(entry.Name).Contains(".itlp", StringComparison.OrdinalIgnoreCase))
                        hitpaths[entry.Name] = true;
                    else
                        hitpaths.TryAdd(entry.Name, false);
                    return;
                }

                // A file: keep its folder for any music file, or for any file at all when we are
                // not deleting non-music; otherwise delete it (optionally sparing folder images).
                if (entry.FileType == MFEType.MusicFile || !deletenonmusic)
                    hitpaths[Path.GetDirectoryName(entry.Name)] = true;
                else if (keepfolderimages && Path.GetFileNameWithoutExtension(entry.Name).Equals("folder", StringComparison.InvariantCultureIgnoreCase))
                    hitpaths[Path.GetDirectoryName(entry.Name)] = true;
                else
                    File.Delete(entry.Name);
            });

            var hitkeys = hitpaths.Where(kv => kv.Value).Select(kv => kv.Key);
            var hitdict = new Dictionary<string, bool>(hitpaths);
            foreach (var key in hitkeys)
            {
                var path = Path.GetDirectoryName(key);
                // Stop as soon as we reach an ancestor already marked kept: its chain to the root
                // was propagated by an earlier walk, so re-walking is redundant (turns the overall
                // pass from O(hits x depth) into ~O(dirs)). The null check also prevents walking
                // past the root if a casing/separator mismatch means dirinfo.FullName is never hit,
                // which would otherwise index hitdict[null].
                while (path != null && path != dirinfo.FullName)
                {
                    if (hitdict.TryGetValue(path, out var kept) && kept)
                        break;
                    hitdict[path] = true;
                    path = Path.GetDirectoryName(path);
                }
            }

            // A missed (false) directory has no music anywhere beneath it, so its descendants are
            // all missed too. Delete only the top-most missed dirs and let the recursive delete
            // remove each subtree in one call (and skip the O(n log n) ordering the old per-dir
            // delete relied on).
            var missed = new HashSet<string>(hitdict.Where(kv => !kv.Value).Select(kv => kv.Key));
            foreach (var path in missed)
                if (!missed.Contains(Path.GetDirectoryName(path)))
                    Directory.Delete(path, true);
        }

   }

}