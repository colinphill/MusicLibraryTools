using System;
using System.IO;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Behavioural coverage for MetadataExtensions.CleanEmptyMusicFolders. The method deletes
    // directories on disk, so these build a throwaway tree, run the prune, and assert what
    // survives. Files only need the right extension (no real media is parsed).
    public class CleanEmptyMusicFoldersTests
    {
        private sealed class TempTree : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "mlt_clean_" + Guid.NewGuid().ToString("N"));
            public TempTree() => Directory.CreateDirectory(Root);
            public string Dir(string rel) { var p = Path.Combine(Root, rel); Directory.CreateDirectory(p); return p; }
            public void File(string rel) { var p = Path.Combine(Root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)); System.IO.File.WriteAllBytes(p, Array.Empty<byte>()); }
            public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        }

        [Fact]
        public void KeepsMusicDirsAndPrunesEmptyOnes()
        {
            using var t = new TempTree();
            t.File("HasMusic/track.flac");
            t.Dir("HasMusic/EmptySub");          // empty dir under a kept dir -> removed
            t.Dir("EmptyTree/A/B");              // fully empty nested tree -> whole subtree removed
            t.File("OnlyText/readme.txt");        // non-music file, but deletenonmusic=false keeps it

            MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(t.Root));

            Assert.True(File.Exists(Path.Combine(t.Root, "HasMusic", "track.flac")));
            Assert.False(Directory.Exists(Path.Combine(t.Root, "HasMusic", "EmptySub")));
            Assert.False(Directory.Exists(Path.Combine(t.Root, "EmptyTree")));
            Assert.True(File.Exists(Path.Combine(t.Root, "OnlyText", "readme.txt")));
            Assert.True(Directory.Exists(t.Root));
        }

        [Fact]
        public void DeleteNonMusicRemovesNonMusicFilesAndTheirEmptiedDirs()
        {
            using var t = new TempTree();
            t.File("HasMusic/track.flac");
            t.File("HasMusic/cruft.txt");        // non-music alongside music: file removed, dir kept
            t.File("JunkOnly/notes.txt");         // non-music only: file removed, dir then pruned

            MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(t.Root), deletenonmusic: true);

            Assert.True(File.Exists(Path.Combine(t.Root, "HasMusic", "track.flac")));
            Assert.False(File.Exists(Path.Combine(t.Root, "HasMusic", "cruft.txt")));
            Assert.False(Directory.Exists(Path.Combine(t.Root, "JunkOnly")));
        }

        [Fact]
        public void ItlpPackagesArePreservedEvenWhenDeletingNonMusic()
        {
            using var t = new TempTree();
            t.File("Music/song.flac");
            t.File("Album.itlp/index.html");        // LP package web assets (non-music)
            t.File("Album.itlp/assets/style.css");

            // The enumerator does not recurse into .itlp, so the whole package must survive
            // intact rather than being pruned as an "empty" tree.
            MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(t.Root), deletenonmusic: true);

            Assert.True(File.Exists(Path.Combine(t.Root, "Music", "song.flac")));
            Assert.True(Directory.Exists(Path.Combine(t.Root, "Album.itlp")));
            Assert.True(File.Exists(Path.Combine(t.Root, "Album.itlp", "index.html")));
            Assert.True(File.Exists(Path.Combine(t.Root, "Album.itlp", "assets", "style.css")));
        }

        [Fact]
        public void KeepFolderImagesRetainsFolderImageDirs()
        {
            using var t = new TempTree();
            t.File("Art/folder.jpg");             // protected name -> dir kept
            t.File("Stray/cover.jpg");            // non-protected image -> deleted, dir pruned

            MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(t.Root), deletenonmusic: true, keepfolderimages: true);

            Assert.True(File.Exists(Path.Combine(t.Root, "Art", "folder.jpg")));
            Assert.False(Directory.Exists(Path.Combine(t.Root, "Stray")));
        }
    }
}
