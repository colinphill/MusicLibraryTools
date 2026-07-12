using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using MusicFileUtilities;

namespace SortDownloads
{
    class Program
    {
        static string GetFileName(string desired)
        {
            if (!File.Exists(desired))
                return desired;
            string dir = Path.GetDirectoryName(desired);
            string file = Path.GetFileNameWithoutExtension(desired);
            string ext = Path.GetExtension(desired);
            for(int i=1; ;i++)
            {
                string fn = Path.Combine(dir, file) + i.ToString() + ext;
                if (!File.Exists(fn))
                    return fn;
            }
        }

        static string ComputeHash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        static bool PathsOverlap(string first, string second)
        {
            string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsHighResolution(ICodecProvider codec) =>
            codec.Samplerate > 48000 || codec.BitsPerSample > 16 || codec.Channels > 2;

        static void Move(string source, string destination, bool apply, string description)
        {
            string actualDestination = apply ? GetFileName(destination) : destination;
            Console.WriteLine($"{description}: {source} -> {actualDestination}");
            if (apply)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(actualDestination));
                File.Move(source, actualDestination);
            }
        }

        static void Main(string[] args)
        {
            bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            string[] operands = args.Where(a => !a.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (operands.Length != 4)
            {
                Console.WriteLine("Usage: SortDownloads <source> <standard-destination> <paired-standard-destination> <high-resolution-destination> [--apply]");
                return;
            }

            string basedir = Path.GetFullPath(operands[0]);
            string standardDirectory = Path.GetFullPath(operands[1]);
            string pairedStandardDirectory = Path.GetFullPath(operands[2]);
            string highResolutionDirectory = Path.GetFullPath(operands[3]);
            if (!Directory.Exists(basedir))
            {
                Console.WriteLine("Source directory does not exist: " + basedir);
                return;
            }
            string[] destinations = { standardDirectory, pairedStandardDirectory, highResolutionDirectory };
            if (destinations.Any(destination => PathsOverlap(basedir, destination)) ||
                destinations.SelectMany((first, index) => destinations.Skip(index + 1).Select(second => (first, second)))
                    .Any(pair => PathsOverlap(pair.first, pair.second)))
            {
                Console.WriteLine("Refusing to continue because source/destination directories overlap.");
                return;
            }

            if (!apply)
                Console.WriteLine("Dry-run mode; pass --apply to move files. No files are ever deleted.");
            else
            {
                foreach (string destination in destinations)
                    Directory.CreateDirectory(destination);
            }

            string quarantineRoot = basedir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                ".SortDownloads-quarantine" + Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");

            var di = new DirectoryInfo(basedir);
            var files = di.GetFileSystemInfos("*.*", SearchOption.AllDirectories);
            var buckets = new Dictionary<(int Disc, int Track, string Artist, string Album, string Title), List<(string Path, ICodecProvider Codec)>>();

            foreach (var file in files.Where(f => !f.Attributes.HasFlag(FileAttributes.Directory)))
            {
                try
                {
                    var filename = file.FullName;
                    var mediafile = MediaFile.GetFile(filename);
                    var tag = mediafile.Tags.First();
                    var codec = mediafile.Codecs.First();
                    if (codec.CodecType == CodecType.Lossy)
                    {
                        Console.WriteLine($"Skipping Lossy File: {filename}");
                        continue;
                    }
                    string artist = string.IsNullOrWhiteSpace(tag.AlbumArtist) ? tag.Artist : tag.AlbumArtist;
                    var key = (
                        tag.DiscNumber ?? 0,
                        tag.TrackNumber ?? 0,
                        (artist ?? "").Trim().ToUpperInvariant(),
                        (tag.Album ?? "").Trim().ToUpperInvariant(),
                        (tag.Title ?? "").Trim().ToUpperInvariant());
                    if (!buckets.ContainsKey(key))
                        buckets.Add(key, new List<(string, ICodecProvider)>());
                    buckets[key].Add((filename, codec));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Skipping unreadable file {file.FullName}: {ex.Message}");
                }
            }

            foreach (var bucket in buckets)
            {
                var candidates = bucket.Value
                    .Select(item => (item.Path, item.Codec, Hash: ComputeHash(item.Path)))
                    .ToArray();
                var standard = candidates.Where(item => !IsHighResolution(item.Codec))
                    .OrderBy(item => item.Codec.Samplerate)
                    .ThenBy(item => item.Codec.BitsPerSample)
                    .ThenBy(item => item.Codec.Channels)
                    .LastOrDefault();
                var highResolution = candidates.Where(item => IsHighResolution(item.Codec))
                    .OrderBy(item => item.Codec.Samplerate)
                    .ThenBy(item => item.Codec.BitsPerSample)
                    .ThenBy(item => item.Codec.Channels)
                    .LastOrDefault();

                var retained = new List<(string Path, ICodecProvider Codec, string Hash)>();
                if (standard.Path != null)
                {
                    retained.Add(standard);
                    string destination = highResolution.Path == null ? standardDirectory : pairedStandardDirectory;
                    Move(standard.Path, Path.Combine(destination, Path.GetFileName(standard.Path)), apply, "Standard lossless");
                }
                if (highResolution.Path != null)
                {
                    retained.Add(highResolution);
                    Move(highResolution.Path, Path.Combine(highResolutionDirectory, Path.GetFileName(highResolution.Path)), apply, "High resolution");
                }

                var retainedPaths = new HashSet<string>(retained.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
                var retainedHashes = new HashSet<string>(retained.Select(item => item.Hash), StringComparer.Ordinal);
                foreach (var extra in candidates.Where(item => !retainedPaths.Contains(item.Path)))
                {
                    string relative = Path.GetRelativePath(basedir, extra.Path);
                    string reason = retainedHashes.Contains(extra.Hash)
                        ? "Verified byte-identical duplicate (quarantine)"
                        : "Alternate resolution or metadata collision (quarantine)";
                    Move(extra.Path, Path.Combine(quarantineRoot, relative), apply, reason);
                }
            }

            if (apply)
                MetadataExtensions.CleanEmptyMusicFolders(di, false);

            Console.WriteLine();

        }
    }
}
