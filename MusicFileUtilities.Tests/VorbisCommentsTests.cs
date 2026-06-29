using System.Collections.Generic;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class VorbisCommentsTests
    {
        private static Dictionary<TagFields, string> Known(VorbisComments vc)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var kv in vc.GetKnownMetadata())
                d[kv.Key] = kv.Value; // last wins; fine for these tests
            return d;
        }

        [Fact]
        public void SetFieldThenReadBack()
        {
            var vc = new VorbisComments { Vendor = "test" };
            vc.SetField(TagFields.Title, "Song");
            vc.SetField(TagFields.Artist, "Band");

            var known = Known(vc);
            Assert.Equal("Song", known[TagFields.Title]);
            Assert.Equal("Band", known[TagFields.Artist]);
        }

        [Fact]
        public void SetFieldOverwritesRatherThanDuplicates()
        {
            var vc = new VorbisComments { Vendor = "test" };
            vc.SetField(TagFields.Album, "First");
            vc.SetField(TagFields.Album, "Second");

            Assert.Single(vc.Comments, c => c.Key == "ALBUM");
            Assert.Equal("Second", Known(vc)[TagFields.Album]);
        }

        [Fact]
        public void RemoveFieldDeletesComment()
        {
            var vc = new VorbisComments { Vendor = "test" };
            vc.SetField(TagFields.Genre, "Jazz");
            vc.RemoveField(TagFields.Genre);
            Assert.False(Known(vc).ContainsKey(TagFields.Genre));
        }

        [Fact]
        public void TrackNumberWithSlashSplitsIntoTrackAndTotal()
        {
            var vc = new VorbisComments { Vendor = "test" };
            vc["TRACKNUMBER"] = "3/12";
            var known = Known(vc);
            Assert.Equal("3", known[TagFields.TrackNumber]);
            Assert.Equal("12", known[TagFields.TotalTracks]);
        }

        [Fact]
        public void IndexerThrowsForMissingKey()
        {
            var vc = new VorbisComments { Vendor = "test" };
            Assert.Throws<KeyNotFoundException>(() => _ = vc["NOPE"]);
        }

        [Fact]
        public void ByteArrayRoundTripPreservesComments()
        {
            var vc = new VorbisComments { Vendor = "reference libVorbis" };
            vc.SetField(TagFields.Title, "Round Trip");
            vc.SetField(TagFields.Artist, "Tester");
            vc.SetField(TagFields.Album, "Album X");

            byte[] bytes = vc.ToByteArray(false);
            var rt = new VorbisComments(bytes);

            Assert.Equal("reference libVorbis", rt.Vendor);
            var known = Known(rt);
            Assert.Equal("Round Trip", known[TagFields.Title]);
            Assert.Equal("Tester", known[TagFields.Artist]);
            Assert.Equal("Album X", known[TagFields.Album]);
        }

        [Fact]
        public void Utf8CommentValuesSurviveRoundTrip()
        {
            var vc = new VorbisComments { Vendor = "v" };
            vc.SetField(TagFields.Artist, "Björk");
            var rt = new VorbisComments(vc.ToByteArray(false));
            Assert.Equal("Björk", Known(rt)[TagFields.Artist]);
        }

        // Regression test for the METADATA_BLOCK_PICTURE key typo:
        // embedded artwork must survive a ToByteArray(includeart:true) -> parse round trip.
        [Fact]
        public void EmbeddedArtworkRoundTripsThroughComment()
        {
            var vc = new VorbisComments { Vendor = "v" };
            vc.Artworks.Add(new VorbisArtwork
            {
                PictureType = ID3v2Util.APICType.FrontCover,
                MimeType = "image/png",
                Description = "cover",
                Width = 1,
                Height = 1,
                Depth = 24,
                ColorsUsed = 0,
                Data = new byte[] { 1, 2, 3, 4 }
            });

            byte[] bytes = vc.ToByteArray(true);
            var rt = new VorbisComments(bytes);

            Assert.Single(rt.Artworks);
            Assert.Equal("image/png", rt.Artworks[0].MimeType);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, rt.Artworks[0].Data);
            // The picture must not leak into the text comments.
            Assert.DoesNotContain(rt.Comments, c => c.Key.Contains("PICTURE") || c.Key.Contains("BLOCK"));
        }
    }
}
