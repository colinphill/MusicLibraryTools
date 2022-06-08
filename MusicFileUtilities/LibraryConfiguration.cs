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
