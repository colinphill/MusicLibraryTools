/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/LogConsole.cs $
 * $Date: 2014-09-27 18:13:09 -0600 (Sat, 27 Sep 2014) $
 * $Revision: 22 $
 * $Author: colin $
 * 
 */

using System;
using System.IO;
using System.Text;

namespace ConsoleTools
{

    public enum LogVerbosity : int { Any = 0, Informational = 10, Chatty = 100, Verbose = 200, Max = int.MaxValue }

    public class LogConsole
    {
        private static StreamWriter _w = null;
        private static string _filename = "Console.log";
        private static bool encoded_ = false;

        public static LogVerbosity ConsoleVerbosity
        {
            get;
            set;
        }

        public static LogVerbosity FileVerbosity
        {
            get;
            set;
        }

        public static void End()
        {
            _w.Close();
        }

        private static void CheckOpen()
        {
            if (_w == null)
                _w = new StreamWriter(_filename, false, Encoding.UTF8);
        }

        public static void Write(string s)
        {
            Write(LogVerbosity.Any, s);
        }

        public static void WriteLine(string s)
        {
            WriteLine(LogVerbosity.Any, s);
        }

        private static void CheckEncoded()
        {
            if (!encoded_)
            {
                Console.OutputEncoding = Encoding.UTF8;
                encoded_ = true;
            }
        }

        public static void Write(LogVerbosity level, string s)
        {
            CheckEncoded();
            if (FileVerbosity >= level)
            {
                CheckOpen();
                _w.Write(s);
            }
            if (ConsoleVerbosity >= level)
                Console.Write(s);
        }

        public static void WriteLine(LogVerbosity level, string s)
        {
            CheckEncoded();
            if (FileVerbosity >= level)
            {
                CheckOpen();
                _w.WriteLine(s);
            }
            if (ConsoleVerbosity >= level)
                Console.WriteLine(s);
        }

        public static void WriteLine()
        {
            CheckEncoded();
            CheckOpen();
            _w.WriteLine();
            Console.WriteLine();
        }

        public static void SwitchFile(string newfile)
        {
            if (_w != null)
                _w.Close();
            _w = new StreamWriter(_filename = newfile, false, Encoding.UTF8);
        }

        public static void Close()
        {
            _w.Close();
            _w = null;
        }

   
    }


}