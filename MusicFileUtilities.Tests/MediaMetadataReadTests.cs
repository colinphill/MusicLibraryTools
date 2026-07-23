using System.Collections.Generic;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Cross-validates the readers against tags written by a reference encoder (ffmpeg).
    public class MediaMetadataReadTests
    {
        private static Dictionary<TagFields, string> Read(string file)
        {
            var mf = MediaFile.GetFile(MediaFixtures.Path_(file));
            var d = new Dictionary<TagFields, string>();
            foreach (var t in mf.Tags)
                foreach (var kv in t.GetKnownMetadata())
                    d[kv.Key] = kv.Value;
            return d;
        }

        [Theory]
        [InlineData("sample.flac")]
        [InlineData("sample.mp3")]
        [InlineData("sample.ogg")]
        [InlineData("sample_alac.m4a")]
        [InlineData("sample_aac.m4a")]
        [InlineData("sample.wv")]
        [InlineData("sample.ape")]
        [InlineData("sample.mpc")]
        [InlineData("sample.tta")]
        [InlineData("sample.tak")]
        [InlineData("sample.ofr")]
        [InlineData("sample.ofs")]
        [InlineData("sample.off")]
        public void BaselineTagsAreReadFromEveryFormat(string file)
        {
            var tags = Read(file);
            Assert.Equal("TestTitle", tags[TagFields.Title]);
            Assert.Equal("TestArtist", tags[TagFields.Artist]);
            Assert.Equal("TestAlbum", tags[TagFields.Album]);
            Assert.Equal("Rock", tags[TagFields.Genre]);
            Assert.Equal("3", tags[TagFields.TrackNumber]);
        }

        [Theory]
        [InlineData("sample.flac")]
        [InlineData("sample.mp3")]
        [InlineData("sample.ogg")]
        [InlineData("sample_alac.m4a")]
        public void ReleaseDateIsRead(string file)
        {
            // ffmpeg's wavpack muxer doesn't emit a Year tag, so .wv is excluded here.
            Assert.StartsWith("2021", Read(file)[TagFields.Date]);
        }

        [Fact]
        public void TagTypeReflectsContainer()
        {
            Assert.Equal("Vorbis", MediaFile.GetFile(MediaFixtures.Path_("sample.flac")).Tags.First().TagType);
            Assert.StartsWith("ID3v2", MediaFile.GetFile(MediaFixtures.Path_("sample.mp3")).Tags.First().TagType);
            Assert.Equal("MP4", MediaFile.GetFile(MediaFixtures.Path_("sample_alac.m4a")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.wv")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.ape")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.mpc")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.tta")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.tak")).Tags.First().TagType);
            Assert.Equal("APE", MediaFile.GetFile(MediaFixtures.Path_("sample.ofr")).Tags.First().TagType);
        }

        [Fact]
        public void StandardFieldsExposedViaTagBaseProperties()
        {
            var tag = MediaFile.GetFile(MediaFixtures.Path_("sample.flac")).Tags.First();
            Assert.Equal("TestTitle", tag.Title);
            Assert.Equal("TestArtist", tag.Artist);
            Assert.Equal("TestAlbum", tag.Album);
            Assert.Equal(3, tag.TrackNumber);
            // AlbumArtist represents only an explicit nonblank tag. Grouping callers use their
            // own effective Artist fallback when this value is empty.
            Assert.Equal(string.Empty, tag.AlbumArtist);
            Assert.False(tag.HasAlbumArtist);
        }
    }
}
