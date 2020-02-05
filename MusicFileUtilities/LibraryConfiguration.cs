/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/LibraryConfiguration.cs $
 * $Date: 2013-01-06 06:51:18 -0700 (Sun, 06 Jan 2013) $
 * $Revision: 14 $
 * $Author: Colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MusicLibraryTools
{
    public class LibraryConfiguration
    {
        private XElement root_;

        public LibraryConfiguration(string filename)
        {
            root_ = XDocument.Load(filename).Element("LibraryConfiguration");
        }
        
        public string CrossSyncTargetLibraryPath => root_.Element("SyncTarget").Value;

        public string CrossSyncSourceLibraryPath => root_.Element("SyncSource").Value;

        public IEnumerable<(string Target, string Offset)> IndexLocations => root_.Elements("IndexTarget").Select(e => (e.Element("Target").Value, e.Element("Offset").Value));

        public string PlaylistTargetFolder => root_.Element("PlaylistTarget").Value;

        public string PlaylistType => root_.Element("PlaylistType").Value;

        public string TrashTargetFolder => root_.Element("TrashTarget").Value;

        public string ReferenceConfig => root_.Element("ReferenceConfig").Value;

        public string [] this[string key] => root_.Elements(key).Select(e => e.Value).ToArray();

        public int LengthLimit => int.Parse(root_.Element("LengthLimit").Value);

        public int DiscNumLengthLimit => int.Parse(root_.Element("DiscNumLengthLimit").Value);
 
    }
}
