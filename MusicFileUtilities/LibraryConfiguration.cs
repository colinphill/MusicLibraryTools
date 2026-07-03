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

    public enum MFEType { Directory, MusicFile, Other }

    public class MusicFileEnumerator : FileSystemEnumerator<(string Name, DateTime Modified, long Size, MFEType FileType)>, IEnumerable<(string Name, DateTime Modified, long Size, MFEType FileType)>
    {
        // The 64KB buffer sizes each directory-query round-trip; the default is small enough
        // that large folders take several round-trips per directory on a network share.
        // recurse:false enumerates just the immediate children (used to split a scan root
        // into per-subtree units).
        public MusicFileEnumerator(string directory, bool recurse = true) : base(directory, new EnumerationOptions { RecurseSubdirectories = recurse, BufferSize = 64 * 1024 }) { }

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
            return (!entry.FileName.Contains(".itlp", StringComparison.OrdinalIgnoreCase));
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

        public IEnumerable<(string Target, string Offset, int Set, string Filter)> IndexLocations => root_.Elements("IndexTarget").Select(e => (e.Value, e.Attributes("Offset").FirstOrDefault()?.Value, 
            int.Parse(e.Attributes("Set").Select(a => a.Value).DefaultIfEmpty("0").FirstOrDefault()),
            e.Attributes("Filter").Select(a => a.Value).DefaultIfEmpty(null).FirstOrDefault()));

        public string PlaylistTargetFolder => root_.Element("PlaylistTarget").Value;

        public string PlaylistType => root_.Element("PlaylistType").Value;

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
