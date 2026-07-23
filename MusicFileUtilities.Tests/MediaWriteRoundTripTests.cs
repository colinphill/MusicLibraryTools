using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Exercises the IMetadataWriter / Save() paths on real containers: open a copy of a
    // fixture, mutate tags, persist, then reopen with the library and verify.
    public class MediaWriteRoundTripTests
    {
        // Every writable format. (.ogg is writable via VorbisComments.SetField + SaveTags,
        // even though OggVorbisFile doesn't implement IMetadataWriter directly.)
        public static IEnumerable<object[]> WritableFiles => new[]
        {
            new object[] { "sample.flac" },
            new object[] { "sample.mp3" },
            new object[] { "sample.ogg" },
            new object[] { "sample_alac.m4a" },
            new object[] { "sample_aac.m4a" },
            new object[] { "sample.wv" },
            new object[] { "sample.ape" },
            new object[] { "sample.mpc" },
            new object[] { "sample.tta" },
            new object[] { "sample.tak" },
            new object[] { "sample.ofr" },
            new object[] { "sample.ofs" },
            new object[] { "sample.off" },
        };

        private static Action<TagFields, string> Setter(IMediaFile mf) =>
            mf switch
            {
                IMetadataWriter w => w.SetField,
                VorbisComments vc => vc.SetField,
                _ => throw new InvalidOperationException("not writable")
            };

        private static Action<TagFields> Remover(IMediaFile mf) =>
            mf switch
            {
                IMetadataWriter w => w.RemoveField,
                VorbisComments vc => vc.RemoveField,
                _ => throw new InvalidOperationException("not writable")
            };

        private static Dictionary<TagFields, string> Read(string path)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var t in MediaFile.GetFile(path).Tags)
                foreach (var kv in t.GetKnownMetadata())
                    d[kv.Key] = kv.Value;
            return d;
        }

        [Theory]
        [MemberData(nameof(WritableFiles))]
        public void StandardAndUserFieldsPersistAcrossSave(string file)
        {
            using var tmp = MediaFixtures.Copy(file);

            var mf = MediaFile.GetFile(tmp.Path);
            var set = Setter(mf);
            set(TagFields.Title, "Rewritten Title");
            set(TagFields.Artist, "New Artist");
            // MusicBrainz Artist Id is a user-defined field (ID3 TXXX / MP4 freeform / Vorbis+APE key).
            // This is the end-to-end regression for the UserStringFrame.Encode bug on ID3.
            set(TagFields.MusicBrainz_ArtistID, "11111111-2222-3333-4444-555555555555");
            mf.SaveTags();

            var tags = Read(tmp.Path);
            Assert.Equal("Rewritten Title", tags[TagFields.Title]);
            Assert.Equal("New Artist", tags[TagFields.Artist]);
            Assert.Equal("11111111-2222-3333-4444-555555555555", tags[TagFields.MusicBrainz_ArtistID]);

            // Untouched baseline tag survives.
            Assert.Equal("TestAlbum", tags[TagFields.Album]);

            // Audio stream is intact and still parses.
            Assert.Equal(44100u, MediaFile.GetFile(tmp.Path).Codecs.First().Samplerate);
        }

        [Theory]
        [MemberData(nameof(WritableFiles))]
        public void TrackAndTotalPersistAcrossSave(string file)
        {
            using var tmp = MediaFixtures.Copy(file);

            var mf = MediaFile.GetFile(tmp.Path);
            var set = Setter(mf);
            set(TagFields.TrackNumber, "7");
            set(TagFields.TotalTracks, "11");   // regression for the APE TotalTracks guard bug
            mf.SaveTags();

            var tags = Read(tmp.Path);
            Assert.Equal("7", tags[TagFields.TrackNumber]);
            Assert.Equal("11", tags[TagFields.TotalTracks]);
        }

        [Theory]
        [MemberData(nameof(WritableFiles))]
        public void RemoveFieldPersistsAcrossSave(string file)
        {
            using var tmp = MediaFixtures.Copy(file);

            // Genre=Rock is present in every fixture.
            Assert.Equal("Rock", Read(tmp.Path)[TagFields.Genre]);

            var mf = MediaFile.GetFile(tmp.Path);
            Remover(mf)(TagFields.Genre);
            mf.SaveTags();

            Assert.False(Read(tmp.Path).ContainsKey(TagFields.Genre));
        }

        // Reassembles the logical packet stream of an ogg file from its page lacing values.
        private static List<byte[]> ReadOggPackets(string path)
        {
            var packets = new List<byte[]>();
            var current = new List<byte>();
            using var fs = System.IO.File.OpenRead(path);
            while (fs.Position < fs.Length)
            {
                byte[] hdr = new byte[27];
                fs.ReadExactly(hdr);
                Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(hdr, 0, 4));
                byte[] segs = new byte[hdr[26]];
                fs.ReadExactly(segs);
                foreach (byte sg in segs)
                {
                    byte[] chunk = new byte[sg];
                    fs.ReadExactly(chunk);
                    current.AddRange(chunk);
                    if (sg < 255)
                    {
                        packets.Add(current.ToArray());
                        current.Clear();
                    }
                }
            }
            return packets;
        }

        private static bool IsVorbisHeaderPacket(byte[] p, byte type) =>
            p.Length >= 7 && p[0] == type &&
            p[1] == 'v' && p[2] == 'o' && p[3] == 'r' && p[4] == 'b' && p[5] == 'i' && p[6] == 's';

        // The vorbis setup header (packet type 5) shares a page with the comment packet.
        // SaveTags used to replace that whole page, silently dropping the setup header and
        // leaving the file undecodable by real players (our own parser never noticed because
        // it only reads packet types 1 and 3).
        [Fact]
        public void OggSavePreservesSetupHeaderPacket()
        {
            using var tmp = MediaFixtures.Copy("sample.ogg");
            byte[] setupBefore = ReadOggPackets(tmp.Path).Single(p => IsVorbisHeaderPacket(p, 5));
            long lengthBefore = new FileInfo(tmp.Path).Length;

            var mf = (OggVorbisFile)MediaFile.GetFile(tmp.Path);
            Setter(mf)(TagFields.Title, "New Title");
            mf.SaveTags();

            Assert.True(mf.LastSaveWasInPlace);
            Assert.Equal(lengthBefore, new FileInfo(tmp.Path).Length);
            var packets = ReadOggPackets(tmp.Path);
            Assert.True(IsVorbisHeaderPacket(packets[0], 1)); // identification header
            byte[] setupAfter = packets.Single(p => IsVorbisHeaderPacket(p, 5));
            Assert.Equal(setupBefore, setupAfter);
            Assert.Equal("New Title", Read(tmp.Path)[TagFields.Title]);
        }

        // Same, but with the old comment packet spanning multiple pages so the setup header
        // sits at the tail of a continuation page when the tag is rewritten smaller.
        [Fact]
        public void OggMultiPageCommentShrinkPreservesSetupHeader()
        {
            using var tmp = MediaFixtures.Copy("sample.ogg");
            byte[] setupBefore = ReadOggPackets(tmp.Path).Single(p => IsVorbisHeaderPacket(p, 5));

            var mf = MediaFile.GetFile(tmp.Path);
            Setter(mf)(TagFields.Lyrics, new string('x', 300000));
            mf.SaveTags();
            Assert.Equal(setupBefore, ReadOggPackets(tmp.Path).Single(p => IsVorbisHeaderPacket(p, 5)));
            Assert.Equal(300000, Read(tmp.Path)[TagFields.Lyrics].Length);

            var mf2 = MediaFile.GetFile(tmp.Path);
            Setter(mf2)(TagFields.Lyrics, "short");
            mf2.SaveTags();

            Assert.Equal(setupBefore, ReadOggPackets(tmp.Path).Single(p => IsVorbisHeaderPacket(p, 5)));
            var tags = Read(tmp.Path);
            Assert.Equal("short", tags[TagFields.Lyrics]);
            Assert.Equal("TestArtist", tags[TagFields.Artist]);
            Assert.Equal(44100u, MediaFile.GetFile(tmp.Path).Codecs.First().Samplerate);
        }

        [Fact]
        public void OggSaveDoesNotRenumberAChainedLogicalStream()
        {
            byte[] stream = File.ReadAllBytes(MediaFixtures.Path_("sample.ogg"));
            string path = Path.Combine(Path.GetTempPath(), $"mlt_{Guid.NewGuid():N}.ogg");
            File.WriteAllBytes(path, stream.Concat(stream).ToArray());
            using var tmp = new MediaFixtures.TempMedia(path);

            var ogg = MediaFile.GetFile(path);
            Setter(ogg)(TagFields.Lyrics, new string('x', 300000)); // force a page-count delta
            ogg.SaveTags();

            byte[] rewritten = File.ReadAllBytes(path);
            int secondBos = FindNthOggBos(rewritten, 2);
            Assert.True(secondBos > 0);
            Assert.Equal(stream, rewritten[secondBos..]);
        }

        private static int FindNthOggBos(byte[] bytes, int occurrence)
        {
            int pos = 0, found = 0;
            while (pos + 27 <= bytes.Length)
            {
                Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(bytes, pos, 4));
                if ((bytes[pos + 5] & 2) != 0 && ++found == occurrence)
                    return pos;
                int segments = bytes[pos + 26];
                Assert.True(pos + 27 + segments <= bytes.Length);
                int dataLength = 0;
                for (int i = 0; i < segments; i++)
                    dataLength += bytes[pos + 27 + i];
                pos += 27 + segments + dataLength;
            }
            return -1;
        }

        [Theory]
        [MemberData(nameof(WritableFiles))]
        public void SaveToSeparateOutputPathLeavesOriginalUntouched(string file)
        {
            using var src = MediaFixtures.Copy(file);
            using var dst = MediaFixtures.Copy(file); // placeholder path; will be overwritten

            var mf = MediaFile.GetFile(src.Path);
            Setter(mf)(TagFields.Title, "Branched Copy");
            mf.SaveTags(dst.Path);

            Assert.Equal("Branched Copy", Read(dst.Path)[TagFields.Title]);
            // Original copy still has the baseline title.
            Assert.Equal("TestTitle", Read(src.Path)[TagFields.Title]);
        }
    }
}
