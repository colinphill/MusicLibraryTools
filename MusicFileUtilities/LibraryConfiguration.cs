/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/LibraryConfiguration.cs $
 * $Date: 2013-01-06 06:51:18 -0700 (Sun, 06 Jan 2013) $
 * $Revision: 14 $
 * $Author: Colin $
 * 
 */

using MusicFileUtilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MusicLibraryTools
{

    /// <summary>
    /// One configured index root. A root can participate in any number of logical comparison sets;
    /// the empty collection means it is indexed but is not a member of a logical set.
    /// </summary>
    public sealed record LibraryIndexLocation(
        string Target,
        string Offset,
        IReadOnlyList<int> Sets,
        string Filter);

    /// <summary>
    /// One playlist export destination. Every destination must select at least one logical scan
    /// set so an export can never accidentally use the entire indexed library.
    /// </summary>
    public sealed record LibraryPlaylistTarget(
        string Target,
        string Type,
        IReadOnlyList<int> Sets);

    public enum MFEType { Directory, MusicFile, Other }

    public class MusicFileEnumerator : FileSystemEnumerator<(string Name, DateTime Modified, long Size, MFEType FileType)>, IEnumerable<(string Name, DateTime Modified, long Size, MFEType FileType)>
    {
        private readonly bool _skipItlpPackages;

        // The 64KB buffer sizes each directory-query round-trip; the default is small enough
        // that large folders take several round-trips per directory on a network share.
        // recurse:false enumerates just the immediate children (used to split a scan root
        // into per-subtree units).
        public MusicFileEnumerator(string directory, bool recurse = true, bool skipItlpPackages = true)
            : base(directory, new EnumerationOptions { RecurseSubdirectories = recurse, BufferSize = 64 * 1024 })
        {
            _skipItlpPackages = skipItlpPackages;
        }

        public IEnumerator<(string Name, DateTime Modified, long Size, MFEType FileType)> GetEnumerator()
        {
            return this;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            return true;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            return !_skipItlpPackages || !entry.FileName.Contains(".itlp", StringComparison.OrdinalIgnoreCase);
        }

        protected override (string Name, DateTime Modified, long Size, MFEType FileType) TransformEntry(ref FileSystemEntry entry)
        {
            return (entry.ToFullPath(), entry.LastWriteTimeUtc.UtcDateTime, entry.Length, entry.IsDirectory ? MFEType.Directory : (MetadataExtensions.ValidExtensionSpans.Contains(Path.GetExtension(entry.FileName)) ? MFEType.MusicFile : MFEType.Other));
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public class LibraryConfiguration
    {
        private XElement root_;

        public LibraryConfiguration(string filename)
        {
            root_ = XDocument.Load(filename).Element("LibraryConfiguration");
        }
        
        public string CrossSyncTargetLibraryPath => root_.Element("SyncTarget").Value;

        public IEnumerable<LibraryIndexLocation> IndexLocations => root_.Elements("IndexTarget").Select(e =>
            new LibraryIndexLocation(
                e.Value,
                e.Attributes("Offset").FirstOrDefault()?.Value,
                ParseScanSets(e.Attributes("Set").FirstOrDefault()?.Value),
                e.Attributes("Filter").FirstOrDefault()?.Value));

        /// <summary>Parse a comma, semicolon, or whitespace separated logical-set list.</summary>
        public static IReadOnlyList<int> ParseScanSets(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var sets = value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int set) && set >= 0
                    ? set
                    : throw new InvalidDataException($"Invalid scan-set value '{part}'."))
                .Distinct()
                .OrderBy(set => set)
                .ToArray();
            return sets;
        }

        public IReadOnlyList<LibraryPlaylistTarget> PlaylistTargets
        {
            get
            {
                if (root_.Element("PlaylistType") is not null)
                    throw new InvalidDataException(
                        "<PlaylistType> is obsolete; add a Type attribute to each <PlaylistTarget>.");
                return root_.Elements("PlaylistTarget").Select(ParsePlaylistTarget).ToArray();
            }
        }

        private static LibraryPlaylistTarget ParsePlaylistTarget(XElement element)
        {
            string target = element.Value?.Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("<PlaylistTarget> cannot be empty.");

            string type = ((string)element.Attribute("Type"))?.Trim().ToLowerInvariant();
            if (type is not ("m3u" or "wpl"))
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' must have a Type attribute of 'm3u' or 'wpl'.");

            var sets = ParseScanSets((string)element.Attribute("Set"));
            if (sets.Count == 0)
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' must select at least one scan set with its Set attribute.");

            return new LibraryPlaylistTarget(target, type, sets);
        }

        [Obsolete("Use PlaylistTargets; playlist export configurations may contain multiple targets.")]
        public string PlaylistTargetFolder => PlaylistTargets.First().Target;

        [Obsolete("Use PlaylistTargets; Type is now an attribute of each PlaylistTarget.")]
        public string PlaylistType => PlaylistTargets.First().Type;

        public string DatabaseFile
        {
            get
            {
                try
                {
                    return root_.Element("DatabaseFile").Value;
                }
                catch
                {
                    return "cache.db";
                }
            }
        }

        public string [] this[string key] => root_.Elements(key).Select(e => e.Value).ToArray();

        public int LengthLimit => int.Parse(root_.Element("LengthLimit").Value);

        public int DiscNumLengthLimit => int.Parse(root_.Element("DiscNumLengthLimit").Value);
 
    }
}
