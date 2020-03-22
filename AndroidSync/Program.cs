using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;
//using SharpAdbClient;

namespace AndroidSync
{
    class Program
    {
        class FSFile
        {
            public FSFile(FSFile other, FSDirectory parent)
            {
                (name_, parent_, size_, modtime_) = (other.name_, parent, other.size_, other.modtime_);
            }
            public FSFile(string name, FSDirectory parent, long size, DateTime modtime)
            {
                (name_, parent_, size_, modtime_) = (name, parent, size, modtime);
            }
            private FSDirectory parent_;
            private string name_;
            public string Name => name_;
            private long size_;
            public long Size => size_;
            private DateTime modtime_;
            public DateTime Modified => modtime_;

            public string GetFullPath(char separator)
            {
                if (parent_ == null)
                    return name_;
                return parent_.GetFullPath(separator) + separator + name_;
            }
        }

        enum FSDiffType { Addition, Removal, Change };

        class FSDiff
        {
            public FSDiff(FSDiffType difftype, FSFile source, FSFile dest)
            {
                (difftype_, source_, dest_) = (difftype, source, dest);
            }
            private FSDiffType difftype_;
            public FSDiffType DiffType => difftype_;
            private FSFile source_;
            public FSFile Source => source_;
            private FSFile dest_;
            public FSFile Dest => dest_;
        }

        class FSDirectory : FSFile
        {
            public FSDirectory(string name, FSDirectory parent) : base(name, parent, 0, DateTime.MinValue)
            {
            }
            private List<FSFile> entries_ = new List<FSFile>();
            public List<FSFile> Entries => entries_;

            public IEnumerable<FSDiff> Diff(FSDirectory other)
            {
                var interval = TimeSpan.FromMinutes(65);
                var diffs = new List<FSDiff>();
                var mydirs = entries_.Select(e => e as FSDirectory).Where(e => e != null).ToArray();
                var myfiles = entries_.Where(e => !(e is FSDirectory)).ToArray();
                var theirdirs = other.entries_.Select(e => e as FSDirectory).Where(e => e != null).ToArray();
                var theirfiles = other.entries_.Where(e => !(e is FSDirectory)).ToArray();
                // Order
                // Remove Files
                // Add Directories
                // Traverse Subdirectories
                // Remove Directories
                // Add Files
                foreach (var e in myfiles.Where(f => theirfiles.Count(tf => tf.Name == f.Name) == 0))
                    diffs.Add(new FSDiff(FSDiffType.Removal, null, e));
                foreach (var e in theirdirs.Where(td => mydirs.Count(d => d.Name == td.Name) == 0))
                {
                    FSDirectory nd = new FSDirectory(e.Name, this);
                    entries_.Add(nd);
                    diffs.Add(new FSDiff(FSDiffType.Addition, null, nd));
                }
                mydirs = entries_.Select(e => e as FSDirectory).Where(e => e != null).ToArray();
                foreach (var e in mydirs)
                {
                    var td = theirdirs.SingleOrDefault(d => d.Name == e.Name);
                    if (td == null)
                        diffs.Add(new FSDiff(FSDiffType.Removal, null, e));
                    else
                        diffs.AddRange(e.Diff(td));
                }
                foreach (var e in theirfiles)
                {
                    var f = myfiles.SingleOrDefault(tf => tf.Name == e.Name);
                    if (f == null)
                    {
                        var mf = new FSFile(e, this);
                        diffs.Add(new FSDiff(FSDiffType.Addition, e, mf));
                    }
                    else if (((e.Modified - f.Modified) > interval) || (e.Size != f.Size))
                    {
                        diffs.Add(new FSDiff(FSDiffType.Change, e, f));
                    }
                }
                return diffs;
            }
        }

        private static readonly string escchars_ = "\\`~!#$&*()\t{[|;'\"<>? ";

        static string EscapeArgument(string arg)
        {
            string res = arg;
            foreach (var e in escchars_)
                res = res.Replace(e.ToString(), "\\" + e);
            return res;
        }

        /*static string UnescapeArgument(string arg)
        {
            StringBuilder res = new StringBuilder();
            for(int i=0;i<arg.Length;i++)
            {
                if (arg[i] == '\\')
                    i++;
                res.Append(arg[i]);
            }
            return res.ToString();
        }*/
                     
        static async Task<FSDirectory> BuildLocalStructure(string path)
        {
            FSDirectory root = new FSDirectory(path, null);
            await Task.Run(() =>
            {
                var locals = (new DirectoryInfo(path)).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).OrderBy(f => f.FullName).ToArray();
                var dhash = new Dictionary<string, FSDirectory>();
                dhash.Add(path, root);
                foreach (var fi in locals)
                {
                    string p = Path.GetDirectoryName(fi.FullName);
                    var top = dhash[p];
                    if (fi is DirectoryInfo)
                    {
                        FSDirectory dir = new FSDirectory(fi.Name, top);
                        top.Entries.Add(dir);
                        dhash.Add(dir.GetFullPath('\\'), dir);
                    }
                    else if (fi is FileInfo)
                    {
                        FSFile file = new FSFile(fi.Name, top, ((FileInfo)fi).Length, fi.LastWriteTimeUtc);
                        top.Entries.Add(file);
                    }
                }
            });
            return root;
        }

        class OutputReceiver //: IShellOutputReceiver
        {
            private List<string> lines_ = new List<string>();
            public IEnumerable<string> Lines => lines_;
            public bool ParsesErrors => false;

            public void AddOutput(string line)
            {
                lines_.Add(line);
            }

            public void Flush()
            {
            }
        }

        static (string Dir, string File) SplitPath(string path, char separator)
        {
            var paths = path.Split(new char[] { separator });
            return (string.Join(separator.ToString(), paths.Take(paths.Length - 1)), paths.Last());
        }

        static async Task<FSDirectory> BuildRemoteStructure(AdbClient client, string device, string path)
        {
            FSDirectory root = new FSDirectory(path, null);
            var dhash = new Dictionary<string, FSDirectory>();
            dhash.Add(path, root);

            var receiver = new ShellReceiver();
            int exitcode = await client.ShellExecuteAsync("TZ=UTC ls -l -A -R " + path, device, receiver);
            var res = receiver.StdoutLines;

            Stack<FSDirectory> dstack = new Stack<FSDirectory>();
            dstack.Push(root);
            FSDirectory top = root;
            foreach (var line in res)
            {
                if (line.Length == 0)
                    continue;
                if (line[0] == '/')
                {
                    string dir = line.Substring(0, line.Length - 1);
                    if (dir == root.Name)
                        continue;
                    var split = SplitPath(dir, '/');
                    while (top.GetFullPath('/') != split.Dir)
                    {
                        dstack.Pop();
                        top = dstack.Peek();
                    }
                    FSDirectory fdir = new FSDirectory(split.File, top);
                    top.Entries.Add(fdir);
                    dstack.Push(top = fdir);
                }
                else if (line[0] == '-')
                {
                    string[] cols = line.Split(" ".ToCharArray(), 8, StringSplitOptions.RemoveEmptyEntries);
                    (string attr, long count, string owner, string group, long size, DateTime date, string name) =
                        (cols[0], long.Parse(cols[1]), cols[2], cols[3], long.Parse(cols[4]), DateTime.SpecifyKind(
                            DateTime.ParseExact(cols[5] + " " + cols[6], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc),
                            cols[7]);
                    FSFile file = new FSFile(name, top, size, date);
                    top.Entries.Add(file);
                }
            }
            return root;
        }

        class SyncProgress : ISyncProgress
        {
            private FSFile file_;

            public SyncProgress(FSFile f)
            {
                file_ = f;
            }

            public void SetProgress(long transferred)
            {
                int value = (int)(100L * transferred / file_.Size);
                Console.Write("[");
                for (int i = 1; i <= value / 2; i++)
                    Console.Write("*");
                for (int i = value / 2 + 1; i <= 50; i++)
                    Console.Write(" ");
                Console.Write("]\r");
                Console.Out.Flush();
            }
        }

        public static string FindExePath(string exe)
        {
            exe = Environment.ExpandEnvironmentVariables(exe);
            if (!File.Exists(exe))
            {
                if (Path.GetDirectoryName(exe) == String.Empty)
                {
                    foreach (string test in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    {
                        string path = test.Trim();
                        if (!String.IsNullOrEmpty(path) && File.Exists(path = Path.Combine(path, exe)))
                            return Path.GetFullPath(path);
                    }
                }
                throw new FileNotFoundException(new FileNotFoundException().Message, exe);
            }
            return Path.GetFullPath(exe);
        }

        static async Task Main(string[] args)
        {
            string lpath = args[0];
            string rpath = args[1];

            bool dry = args.Skip(2).Count(a => a.ToLower() == "dry") != 0;
            bool touch = args.Skip(2).Count(a => a.ToLower() == "touch") != 0;

            if (dry)
                Console.WriteLine("Test Mode");
            if (touch)
                Console.WriteLine("Attempting Timestamp Updates");
                
            Console.WriteLine("Enumerating Local And Remote Paths");

            AdbClient client = new AdbClient();
            string device = null;

            var ltask = BuildLocalStructure(lpath);
            FSDirectory remote = await BuildRemoteStructure(client, device, rpath);
            FSDirectory local = await ltask;

            Console.WriteLine("Computing Differences");

            var diffs = remote.Diff(local);

            foreach (var diff in diffs)
            {
                var r = new ShellReceiver();
                var dest = diff.Dest;
                if (dest is FSDirectory)
                {
                    if (diff.DiffType == FSDiffType.Addition)
                    {
                        Console.WriteLine("Create Directory: " + diff.Dest.GetFullPath('/'));
                        if (!dry)
                            await client.ShellExecuteAsync("mkdir " + EscapeArgument(diff.Dest.GetFullPath('/')), null, r);
                    }
                    else if (diff.DiffType == FSDiffType.Removal)
                    {
                        Console.WriteLine("Remove Directory: " + diff.Dest.GetFullPath('/'));
                        if (!dry)
                            await client.ShellExecuteAsync("rm -rf " + EscapeArgument(diff.Dest.GetFullPath('/')), null, r);
                    }
                    else
                        throw new Exception();
                }
                else
                {
                    if (diff.DiffType == FSDiffType.Addition)
                    {
                        Console.WriteLine("New File: " + diff.Dest.GetFullPath('/'));
                        if (!dry)
                        {
                            using (Stream s = File.OpenRead(diff.Source.GetFullPath('\\')))
                                await client.PushAsync(s, device, diff.Dest.GetFullPath('/'), 505, diff.Source.Modified, new SyncProgress(dest));
                            Console.WriteLine();
                            if (touch)
                                await client.ShellExecuteAsync("touch -m -d" + diff.Source.Modified.ToString(" yyyyMMddHHmmZ ") + EscapeArgument(diff.Dest.GetFullPath('/')), device, r);
                        }
                    }
                    else if (diff.DiffType == FSDiffType.Change)
                    {
                        Console.WriteLine("Modify File: " + diff.Dest.GetFullPath('/'));
                        if (!dry)
                        {
                            await client.ShellExecuteAsync("rm " + EscapeArgument(diff.Dest.GetFullPath('/')), device, r);
                            using (Stream s = File.OpenRead(diff.Source.GetFullPath('\\')))
                                await client.PushAsync(s, device, diff.Dest.GetFullPath('/'), 505, diff.Source.Modified, new SyncProgress(dest));
                            Console.WriteLine();
                            if (touch)
                                await client.ShellExecuteAsync("touch -m -d" + diff.Source.Modified.ToString(" yyyy-MM-ddTHH:mm:00Z ") + EscapeArgument(diff.Dest.GetFullPath('/')), device, r);
                        }
                    }
                    else if (diff.DiffType == FSDiffType.Removal)
                    {
                        Console.WriteLine("Remove File: " + diff.Dest.GetFullPath('/'));
                        if (!dry)
                            await client.ShellExecuteAsync("rm " + EscapeArgument(diff.Dest.GetFullPath('/')), device, r);
                    }
                    else
                        throw new Exception();
                }
            }

            Console.WriteLine("Done");
        }
    }
}
