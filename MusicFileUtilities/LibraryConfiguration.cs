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

        static LibraryConfiguration()
        {
            try
            {
                XDocument doc = XDocument.Load("LibraryConfiguration.xml");
                _syncsource = doc.Element("LibraryConfiguration").Element("SyncSource").Value;
                _synctarget = doc.Element("LibraryConfiguration").Element("SyncTarget").Value;
                _indextarget = doc.Element("LibraryConfiguration").Element("IndexTarget").Value;
                _wpltarget = doc.Element("LibraryConfiguration").Element("WPLTarget").Value;
                _m3utarget = doc.Element("LibraryConfiguration").Element("M3UTarget").Value;
                _trashtarget = doc.Element("LibraryConfiguration").Element("TrashTarget").Value;
                _wploffset = doc.Element("LibraryConfiguration").Element("WPLOffset").Value;
                _m3uoffset = doc.Element("LibraryConfiguration").Element("M3UOffset").Value;
                _valid = true;
            }
            catch
            {
            }
        }

        private static string _syncsource = "";
        private static string _synctarget = "";
        private static string _indextarget = "";
        private static string _wpltarget = "";
        private static string _m3utarget = "";
        private static string _trashtarget = "";
        private static string _wploffset = "";
        private static string _m3uoffset = "";
        private static bool _valid = false;

        public static string CrossSyncTargetLibraryPath
        {
            get
            {
                return _synctarget;
            }
        }

        public static string CrossSyncSourceLibraryPath
        {
            get
            {
                return _syncsource;
            }
        }
        
        public static string CrossSyncTargetMusicFolder
        {
            get
            {
                return _indextarget;
            }
        }

        public static string WPLTargetFolder
        {
            get
            {
                return _wpltarget;
            }
        }

        public static string M3UTargetFolder
        {
            get
            {
                return _m3utarget;
            }
        }

        public static string WPLOffset
        {
            get
            {
                return _wploffset;
            }
        }

        public static string M3UOffset
        {
            get
            {
                return _m3uoffset;
            }
        }

        public static string TrashTargetFolder
        {
            get
            {
                return _trashtarget;
            }
        }

        public static bool Valid
        {
            get
            {
                return _valid;
            }
        }

    }
}
