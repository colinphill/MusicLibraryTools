using System;
using System.Collections.Generic;
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
