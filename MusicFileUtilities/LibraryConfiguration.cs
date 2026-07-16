/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/LibraryConfiguration.cs $
 * $Date: 2013-01-06 06:51:18 -0700 (Sun, 06 Jan 2013) $
 * $Revision: 14 $
 * $Author: Colin $
 * 
 */

#nullable enable

using MusicFileUtilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicLibraryTools
{

    /// <summary>
    /// One configured index root. A root can participate in any number of logical comparison sets;
    /// the empty collection means it is indexed but is not a member of a logical set.
    /// </summary>
    public sealed record LibraryIndexSetMembership(string Name, string? Offset);

    public sealed record LibraryIndexLocation(
        string Target,
        string? DefaultOffset,
        IReadOnlyList<LibraryIndexSetMembership> Memberships,
        string? Filter,
        bool Organize = true)
    {
        public IReadOnlyList<string> Sets { get; } =
            Memberships.Select(membership => membership.Name).ToArray();

        public string? OffsetFor(string setName)
        {
            LibraryIndexSetMembership? membership = Memberships.FirstOrDefault(candidate =>
                LibraryConfiguration.ScanSetComparer.Equals(candidate.Name, setName));
            return membership is null ? null : membership.Offset ?? DefaultOffset;
        }
    }

    /// <summary>
    /// One playlist export destination. Every destination must select at least one logical scan
    /// set so an export can never accidentally use the entire indexed library.
    /// </summary>
    public sealed record LibraryPlaylistTarget(
        string Target,
        string Type,
        IReadOnlyList<string> Sets);

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
        private static readonly Regex ScanSetNamePattern = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);
        public static readonly StringComparer ScanSetComparer = StringComparer.OrdinalIgnoreCase;
        private readonly XElement root_;
        private readonly string configurationDirectory_;

        public LibraryConfiguration(string filename)
        {
            string fullPath = Path.GetFullPath(filename);
            configurationDirectory_ = Path.GetDirectoryName(fullPath)!;
            root_ = XDocument.Load(fullPath).Element("LibraryConfiguration")
                ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");
        }
        
        public string CrossSyncTargetLibraryPath => root_.Element("SyncTarget")?.Value
            ?? throw new InvalidDataException("Missing <SyncTarget> element.");

        public IEnumerable<LibraryIndexLocation> IndexLocations =>
            root_.Elements("IndexTarget").Select(ParseIndexLocation);

        private static LibraryIndexLocation ParseIndexLocation(XElement element)
        {
            string? structuredPath = ((string?)element.Attribute("Path"))?.Trim();
            bool structured = structuredPath is not null || element.Elements("Set").Any();
            string target = structured ? structuredPath ?? "" : element.Value.Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("<IndexTarget> must specify a non-empty Path.");

            string? defaultOffset = CleanOptional((string?)element.Attribute("Offset"));
            IReadOnlyList<LibraryIndexSetMembership> memberships;
            if (structured)
            {
                if (element.Attribute("Set") is not null)
                    throw new InvalidDataException(
                        $"IndexTarget '{target}' cannot combine the legacy Set attribute with child Set elements.");
                var seen = new HashSet<string>(ScanSetComparer);
                memberships = element.Elements("Set").Select(child =>
                {
                    string name = ParseScanSetName((string?)child.Attribute("Name"));
                    if (!seen.Add(name))
                        throw new InvalidDataException(
                            $"IndexTarget '{target}' contains duplicate scan set '{name}'.");
                    return new LibraryIndexSetMembership(name,
                        CleanOptional((string?)child.Attribute("Offset")));
                }).ToArray();
            }
            else
            {
                memberships = ParseScanSets((string?)element.Attribute("Set"))
                    .Select(name => new LibraryIndexSetMembership(name, null)).ToArray();
            }
            return new(target, defaultOffset, memberships,
                CleanOptional((string?)element.Attribute("Filter")),
                ParseOptionalBoolean(element, "Organize", defaultValue: true));
        }

        /// <summary>Parse a comma, semicolon, or whitespace separated logical-set list.</summary>
        public static IReadOnlyList<string> ParseScanSets(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var sets = value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseScanSetName)
                .Distinct(ScanSetComparer)
                .OrderBy(set => set, ScanSetComparer)
                .ToArray();
            return sets;
        }

        public static string ParseScanSetName(string? value)
        {
            string name = value?.Trim() ?? "";
            if (!ScanSetNamePattern.IsMatch(name))
                throw new InvalidDataException(
                    $"Invalid scan-set name '{name}'. Set names may contain ASCII letters and digits only.");
            return name;
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
            string target = element.Value.Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("<PlaylistTarget> cannot be empty.");

            string? type = ((string?)element.Attribute("Type"))?.Trim().ToLowerInvariant();
            if (type is not ("m3u" or "wpl"))
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' must have a Type attribute of 'm3u' or 'wpl'.");

            var sets = ParseScanSets((string?)element.Attribute("Set"));
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
                    return root_.Element("DatabaseFile")!.Value;
                }
                catch
                {
                    return "cache.db";
                }
            }
        }

        public string? ItunesLibraryPath => ResolveOptionalPath((string?)root_.Element("ItunesLibrary"));

        public string FfmpegPath
        {
            get
            {
                string? value = CleanOptional((string?)root_.Element("FfmpegPath"));
                if (value is null) return "ffmpeg";
                return Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) ||
                       value.Contains(Path.AltDirectorySeparatorChar)
                    ? Path.GetFullPath(value, configurationDirectory_)
                    : value;
            }
        }

        public string [] this[string key] => root_.Elements(key).Select(e => e.Value).ToArray();

        public int LengthLimit => int.Parse(root_.Element("LengthLimit")!.Value);

        public int DiscNumLengthLimit => int.Parse(root_.Element("DiscNumLengthLimit")!.Value);

        private string? ResolveOptionalPath(string? value)
        {
            value = CleanOptional(value);
            return value is null ? null : Path.GetFullPath(value, configurationDirectory_);
        }

        private static string? CleanOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static bool ParseOptionalBoolean(
            XElement element, string attributeName, bool defaultValue)
        {
            string? value = CleanOptional((string?)element.Attribute(attributeName));
            if (value is null)
                return defaultValue;
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element.Name.LocalName}> must be true or false.");
        }
 
    }
}
