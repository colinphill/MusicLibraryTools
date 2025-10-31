using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AndroidSync
{
    class Program
    {

        abstract class FileSystem
        {
            public abstract Task<FSDirectory> EnumerateDirectory(string path);
            public abstract Task RemoveFile(string path);
            public abstract Task RemoveDirectory(string path);
            public abstract Task CreateDirectory(string path);
            public abstract Task WriteFile(string path, Stream s, DateTime modified, ISyncProgress progress = null);
            public abstract Task<Stream> ReadFile(string path, ISyncProgress progress = null);
        }



        class AndroidFileSystem : FileSystem
        {
            ShellReceiver receiver_ = new ShellReceiver();

            public override async Task RemoveFile(string path)
            {
                await client_.ShellExecuteAsync("rm " + EscapeArgument(path), device_, receiver_);
            }

            public override async Task RemoveDirectory(string path)
            {
                await client_.ShellExecuteAsync("rm -rf " + EscapeArgument(path), device_, receiver_);
            }

            public override async Task CreateDirectory(string path)
            {
                await client_.ShellExecuteAsync("mkdir " + EscapeArgument(path), device_, receiver_);
            }

            public override async Task WriteFile(string path, Stream s, DateTime modified, ISyncProgress progress = null)
            {
                await client_.PushAsync(s, device_, path, 505, modified, progress);
            }

            public override async Task<Stream> ReadFile(string path, ISyncProgress progress)
            {
                string fn = Path.GetTempFileName();
                using (var fs = File.OpenWrite(fn))
                    await client_.PullAsync(fs, device_, path, progress);
                return new FileStream(fn, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }

            public AndroidFileSystem(AdbClient client, string device)
            {
                client_ = client;
                device_ = device;
            }

            private AdbClient client_;
            private string device_;

            public override async Task<FSDirectory> EnumerateDirectory(string path)
            {
                if (path.EndsWith("/"))
                    path = path.Remove(path.Length - 1);
                var dhash = new Dictionary<string, FSDirectory>();
                FSDirectory root = new FSDirectory(path, null, '/', path, dhash);
                dhash.Add(path, root);

                var receiver = new ShellReceiver();
                int exitcode = await client_.ShellExecuteAsync("TZ=UTC ls -l -A -R " + path, device_, receiver);
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
                        while (top.GetFullPath() != split.Dir)
                        {
                            dstack.Pop();
                            top = dstack.Peek();
                        }
                        FSDirectory fdir = new FSDirectory(split.File, top, '/', dir);
                        dhash.Add(dir, fdir);
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
                        FSFile file = new FSFile(name, top, size, date, '/', top.LocalPath + '/' + name);
                        top.Entries.Add(file);
                    }
                }
                return root;
            }
        }
        
        class LocalFileSystem : FileSystem
        {
            public override Task RemoveFile(string path)
            {
                return Task.Run(() => File.Delete(path));
            }

            public override Task RemoveDirectory(string path)
            {
                return Task.Run(() => Directory.Delete(path, true));
            }

            public override Task CreateDirectory(string path)
            {
                return Task.Run(() => Directory.CreateDirectory(path));
            }

            public override async Task WriteFile(string path, Stream s, DateTime modified, ISyncProgress progress = null)
            {
                using (var fs = File.OpenWrite(path))
                    await s.CopyToAsync(fs, 1 << 20);
                File.SetLastWriteTimeUtc(path, modified);
            }

            public override Task<Stream> ReadFile(string path, ISyncProgress progress)
            {
                return Task.Run(() => { return File.OpenRead(path) as Stream; });
            }

            public override async Task<FSDirectory> EnumerateDirectory(string path)
            {
                if (path.EndsWith("\\"))
                    path = path.Remove(path.Length - 1);
                var dhash = new Dictionary<string, FSDirectory>();
                FSDirectory root = new FSDirectory(path, null, '\\', Path.GetFullPath(path), dhash);
                await Task.Run(() =>
                {
                    var locals = (new DirectoryInfo(path)).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).OrderBy(f => f.FullName).ToArray();
                    dhash.Add(path, root);
                    foreach (var fi in locals)
                    {
                        string p = Path.GetDirectoryName(fi.FullName);
                        if (p.EndsWith("\\"))
                            p = p.Remove(p.Length - 1);
                        var top = dhash[p];
                        if (fi is DirectoryInfo)
                        {
                            FSDirectory dir = new FSDirectory(fi.Name, top, '\\', fi.FullName);
                            top.Entries.Add(dir);
                            dhash.Add(dir.LocalPath, dir);
                        }
                        else if (fi is FileInfo)
                        {
                            FSFile file = new FSFile(fi.Name, top, ((FileInfo)fi).Length, fi.LastWriteTimeUtc, '\\', fi.FullName);
                            top.Entries.Add(file);
                        }
                    }
                });
                return root;
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

        class FSFile
        {
            public FSFile(FSFile other, FSDirectory parent)
            {
                (name_, parent_, size_, modtime_, separator_, localpath_) = (other.name_, parent, other.size_, other.modtime_, other.separator_, other.localpath_);
            }
            public FSFile(string name, FSDirectory parent, long size, DateTime modtime, char separator, string localpath)
            {
                (name_, parent_, size_, modtime_, separator_, localpath_) = (name, parent, size, modtime, separator, localpath);
            }
            protected FSDirectory parent_;
            private string name_;
            public string Name => name_;
            private long size_;
            public long Size => size_;
            private DateTime modtime_;
            public DateTime Modified => modtime_;
            private char separator_;
            private string localpath_;
            public string LocalPath => localpath_;
            public char Separator => separator_;

            public string GetFullPath()
            {
                if (parent_ == null)
                    return name_;
                return parent_.GetFullPath() + separator_ + name_;
            }
        }

        class FSDirectory : FSFile
        {
            public FSDirectory(string name, FSDirectory parent, char separator, string localpath, Dictionary<string, FSDirectory> dhash = null) : base(name, parent, 0, DateTime.MinValue, separator, localpath)
            {
                dhash_ = dhash;
            }
            private List<FSFile> entries_ = new List<FSFile>();
            public List<FSFile> Entries => entries_;
            private Dictionary<string, FSDirectory> dhash_;
            public Dictionary<string, FSDirectory> DirectoryHash => dhash_ ?? parent_.DirectoryHash;

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
                    FSDirectory nd = new FSDirectory(e.Name, this, Separator, LocalPath + Separator + e.Name);
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
                     
           
        static (string Dir, string File) SplitPath(string path, char separator)
        {
            var paths = path.Split(new char[] { separator });
            return (string.Join(separator.ToString(), paths.Take(paths.Length - 1)), paths.Last());
        }

  
        class SyncProgress : ISyncProgress
        {
            private StringBuilder builder_ = new StringBuilder();
            private Stopwatch watch_ = new Stopwatch();
            private string lastprogress_ = string.Empty;
            private readonly string[] suffixes_ = { string.Empty, "k", "M", "G", "T", "P", "E" };

            public long Size
            {
                get;
                set;
            }

            public SyncProgress()
            {
            }

            public void Start()
            {
                watch_.Start();

            }

            public void End()
            {
                watch_.Stop();
                SetProgress(Size);
                watch_.Reset();
                Console.WriteLine();
            }

            public void SetProgress(long transferred)
            {
                builder_.Clear();
                int value = (int)(100L * transferred / Size);
                builder_.Append("\r[");
                for (int i = 1; i <= value / 2; i++)
                    builder_.Append("*");
                for (int i = value / 2 + 1; i <= 50; i++)
                    builder_.Append(" ");
                builder_.Append("] ");
                long ems = watch_.ElapsedMilliseconds;
                if (ems != 0)
                {
                    double rate = transferred * 1000 / ems;
                    string suffix = string.Empty;
                    foreach (string sfx in suffixes_)
                    {
                        suffix = sfx;
                        if (rate < 900.0)
                            break;
                        rate /= 1000.0;
                    }
                    builder_.Append("" + rate.ToString("0.00") + " " + suffix + "B/s        ");
                }
                string val = builder_.ToString();
                if (val != lastprogress_)
                {
                    Console.Write(lastprogress_ = val);
                    Console.Out.Flush();
                }
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

        static IEnumerable<FSFile> GetAllFiles(FSDirectory dir)
        {
            foreach (var e in dir.Entries)
            {
                if (e is FSDirectory de)
                {
                    foreach (var res in GetAllFiles(de))
                        yield return res;
                }
                else
                    yield return e;
            }
        }

        static bool CheckCollisions(FSDirectory dir)
        {
            bool collision = false;
            int ucount = dir.Entries.Select(de => de.Name).Distinct().Count();
            if (ucount != dir.Entries.Count)
            {
                Console.WriteLine($"Collision: {dir.GetFullPath()}");
                foreach (var e in dir.Entries)
                    Console.WriteLine(e.LocalPath);
                collision = true;
            }
            foreach (var e in dir.Entries.Where(de => de is FSDirectory))
                collision |= CheckCollisions(e as FSDirectory);
            return collision;
        }

        static readonly string[] remappaths_ = { "FLAC", "FLAC2", "HiResPCM", "HiResDSD", "HiResWV", "Lossy" };

        static FSDirectory RemapMusic(FSDirectory dir)
        {
            var dhash = new Dictionary<string, FSDirectory>();
            FSDirectory ndir = new FSDirectory(dir.Name, null, dir.Separator, dir.LocalPath);
            var allfiles = GetAllFiles(dir).ToArray();
            foreach (var f in allfiles)
            {
                string newpath = f.GetFullPath();
                foreach (var rp in remappaths_)
                    newpath = newpath.Replace($"{dir.Separator}{rp}{dir.Separator}", $"{dir.Separator}Files{dir.Separator}");

                var subpaths = newpath.Remove(0, dir.Name.Length + 1).Split($"{dir.Separator}", StringSplitOptions.RemoveEmptyEntries);
                subpaths = subpaths.Take(subpaths.Length - 1).ToArray();
                var d = ndir;
                foreach (var sp in subpaths)
                {
                    var tp = d.Name + d.Separator + sp;
                    if (dhash.ContainsKey(tp))
                        d = dhash[tp];
                    else
                    {
                        FSDirectory nd = new FSDirectory(sp, d, d.Separator, tp);
                        d.Entries.Add(nd);
                        d = nd;
                        dhash.Add(tp, d);
                    }
                }
                d.Entries.Add(f);
            }
            return ndir;
        }

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string lpath = args[0];
            string rpath = args[1];

            bool dry = args.Skip(2).Count(a => a.ToLower() == "dry") != 0;
            bool remap = args.Skip(2).Count(a => a.ToLower() == "remap") != 0;

            if (dry)
                Console.WriteLine("Test Mode");
                
            Console.WriteLine("Enumerating Paths");

            FileSystem localfs, remotefs;

            AdbClient client = new AdbClient();
            string device = null;

            if (lpath.StartsWith("adb:"))
            {
                localfs = new AndroidFileSystem(client, device);
                lpath = lpath.Remove(0, 4);
            }
            else
                localfs = new LocalFileSystem();

            if (rpath.StartsWith("adb:"))
            {
                remotefs = new AndroidFileSystem(client, device);
                rpath = rpath.Remove(0, 4);
            }
            else
                remotefs = new LocalFileSystem();

            var ltask = localfs.EnumerateDirectory(lpath);
            FSDirectory remote = await remotefs.EnumerateDirectory(rpath);
            FSDirectory local = await ltask;

            if (remap)
            {
                local = RemapMusic(local);
                Console.WriteLine("Checking Collisions");
                if (CheckCollisions(local))
                {
                    Console.WriteLine("Collisions Detected, Resolve Before Use");
                    return;
                }
            }

            Console.WriteLine("Computing Differences");

            var diffs = remote.Diff(local);
            SyncProgress progress = new SyncProgress();

            foreach (var diff in diffs)
            {
                var r = new ShellReceiver();
                var dest = diff.Dest;
                var source = diff.Source;
                if (dest is FSDirectory)
                {
                    if (diff.DiffType == FSDiffType.Addition)
                    {
                        Console.WriteLine("Create Directory: " + diff.Dest.GetFullPath());
                        if (!dry)
                            await remotefs.CreateDirectory(diff.Dest.GetFullPath());
                    }
                    else if (diff.DiffType == FSDiffType.Removal)
                    {
                        var fp = diff.Dest.GetFullPath();
                        if (fp.Contains("System Volume Information") || fp.Contains(".thumbnail") || fp.Contains("$RECYCLE"))
                            continue;
                        Console.WriteLine("Remove Directory: " + diff.Dest.GetFullPath());
                        if (!dry)
                            await remotefs.RemoveDirectory(diff.Dest.GetFullPath());
                    }
                    else
                        throw new Exception();
                }
                else
                {
                    if (diff.DiffType == FSDiffType.Addition)
                    {
                        Console.WriteLine("New File: " + diff.Dest.GetFullPath());
                        if (!dry)
                        {
                            progress.Size = dest.Size;
                            using (Stream s = await localfs.ReadFile(diff.Source.LocalPath))
                                await remotefs.WriteFile(diff.Dest.GetFullPath(), s, diff.Source.Modified, progress);
                        }
                    }
                    else if (diff.DiffType == FSDiffType.Change)
                    {
                        Console.WriteLine("Modify File: " + diff.Dest.GetFullPath());
                        if (!dry)
                        {
                            progress.Size = source.Size;
                            await remotefs.RemoveFile(diff.Dest.GetFullPath());
                            using (Stream s = File.OpenRead(diff.Source.LocalPath))
                                await remotefs.WriteFile(diff.Dest.GetFullPath(), s, diff.Source.Modified, progress);
                        }
                    }
                    else if (diff.DiffType == FSDiffType.Removal)
                    {
                        Console.WriteLine("Remove File: " + diff.Dest.GetFullPath());
                        if (!dry)
                            await remotefs.RemoveFile(diff.Dest.GetFullPath());
                    }
                    else
                        throw new Exception();
                }
            }

            Console.WriteLine("Done");
        }
    }
}
