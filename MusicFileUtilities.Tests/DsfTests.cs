using System.Collections.Generic;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // The .dsf fixture is a hand-crafted DSD64 container with no metadata chunk, so these
    // tests cover both the codec/format parsing and the DSF-specific tag write path
    // (append ID3v2 tag at the end + patch the DSD header pointers).
    public class DsfTests
    {
        private static Dictionary<TagFields, string> Read(string path)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var t in MediaFile.GetFile(path).Tags)
                foreach (var kv in t.GetKnownMetadata())
                    d[kv.Key] = kv.Value;
            return d;
        }

        [Fact]
        public void CodecPropertiesAreParsed()
        {
            var c = MediaFile.GetFile(MediaFixtures.Path_("sample.dsf")).Codecs.First();
            Assert.Equal("DSD", c.CodecName);
            Assert.Equal(CodecType.Lossless, c.CodecType);
            Assert.Equal(2u, c.Channels);
            Assert.Equal(2822400u, c.Samplerate);   // DSD64
            Assert.Equal(1u, c.BitsPerSample);
            Assert.Equal(1u * 2822400u * 2u, c.AverageBitrate);
        }

        [Fact]
        public void TagsCanBeWrittenToAPreviouslyUntaggedDsf()
        {
            using var tmp = MediaFixtures.Copy("sample.dsf");

            // Fixture starts with no metadata chunk.
            Assert.Empty(Read(tmp.Path));

            var mf = MediaFile.GetFile(tmp.Path);
            var w = Assert.IsAssignableFrom<IMetadataWriter>(mf);
            w.SetField(TagFields.Title, "DSD Title");
            w.SetField(TagFields.Artist, "DSD Artist");
            w.SetField(TagFields.TrackNumber, "5");
            w.SetField(TagFields.TotalTracks, "9");
            w.SetField(TagFields.MusicBrainz_ArtistID, "aaaa-bbbb");
            mf.SaveTags();

            var tags = Read(tmp.Path);
            Assert.Equal("DSD Title", tags[TagFields.Title]);
            Assert.Equal("DSD Artist", tags[TagFields.Artist]);
            Assert.Equal("5", tags[TagFields.TrackNumber]);
            Assert.Equal("9", tags[TagFields.TotalTracks]);
            Assert.Equal("aaaa-bbbb", tags[TagFields.MusicBrainz_ArtistID]);

            // Audio/codec still parses after the rewrite.
            Assert.Equal(2822400u, MediaFile.GetFile(tmp.Path).Codecs.First().Samplerate);
        }

        [Fact]
        public void TagsSurviveASecondEditCycle()
        {
            using var tmp = MediaFixtures.Copy("sample.dsf");

            var mf1 = MediaFile.GetFile(tmp.Path);
            ((IMetadataWriter)mf1).SetField(TagFields.Title, "First");
            mf1.SaveTags();

            var mf2 = MediaFile.GetFile(tmp.Path);
            ((IMetadataWriter)mf2).SetField(TagFields.Album, "Second Album");
            mf2.SaveTags();

            var tags = Read(tmp.Path);
            Assert.Equal("First", tags[TagFields.Title]);
            Assert.Equal("Second Album", tags[TagFields.Album]);
        }

        [Fact]
        public void SaveToSeparatePathProducesAReadableDsf()
        {
            using var src = MediaFixtures.Copy("sample.dsf");
            using var dst = MediaFixtures.Copy("sample.dsf");

            var mf = MediaFile.GetFile(src.Path);
            ((IMetadataWriter)mf).SetField(TagFields.Title, "Branched");
            mf.SaveTags(dst.Path);

            Assert.Equal("Branched", Read(dst.Path)[TagFields.Title]);
            Assert.Empty(Read(src.Path)); // original copy untouched
        }
    }
}
