using System.Collections.Generic;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // ID3v2Tag is concrete and can be exercised in-memory (SetField builds frames,
    // GetKnownMetadata reads them back) without needing a real MP3 on disk.
    public class Id3TagTests
    {
        private static Dictionary<TagFields, string> Known(ID3v2Tag t)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var kv in t.GetKnownMetadata())
                d[kv.Key] = kv.Value;
            return d;
        }

        [Fact]
        public void SimpleTextFrameRoundTrips()
        {
            var tag = new ID3v2Tag();
            tag.SetField(TagFields.Title, "My Title");
            tag.SetField(TagFields.Artist, "My Artist");

            var known = Known(tag);
            Assert.Equal("My Title", known[TagFields.Title]);
            Assert.Equal("My Artist", known[TagFields.Artist]);
        }

        [Fact]
        public void TrackAndTotalCombineIntoSingleFrame()
        {
            var tag = new ID3v2Tag();
            tag.SetField(TagFields.TrackNumber, "4");
            tag.SetField(TagFields.TotalTracks, "9");

            Assert.Single(tag.Frames.OfType<TextFrame>(), f => f.FrameID == "TRCK");
            Assert.Equal("4/9", tag.Frames.OfType<TextFrame>().First(f => f.FrameID == "TRCK").Text);

            var known = Known(tag);
            Assert.Equal("4", known[TagFields.TrackNumber]);
            Assert.Equal("9", known[TagFields.TotalTracks]);
        }

        // Regression test for the UserStringFrame.Encode bug: creating a fresh TXXX
        // frame previously threw IndexOutOfRangeException and corrupted the encoding byte.
        [Fact]
        public void UserDefinedTxxxFieldRoundTrips()
        {
            var tag = new ID3v2Tag();
            tag.SetField(TagFields.MusicBrainz_ArtistID, "abc-123");
            tag.SetField(TagFields.ReplayGain_Track_Gain, "-6.5 dB");

            var known = Known(tag);
            Assert.Equal("abc-123", known[TagFields.MusicBrainz_ArtistID]);
            Assert.Equal("-6.5 dB", known[TagFields.ReplayGain_Track_Gain]);
        }

        [Fact]
        public void SettingSameFieldTwiceDoesNotDuplicateFrames()
        {
            var tag = new ID3v2Tag();
            tag.SetField(TagFields.Album, "First");
            tag.SetField(TagFields.Album, "Second");

            Assert.Single(tag.Frames.OfType<TextFrame>(), f => f.FrameID == "TALB");
            Assert.Equal("Second", Known(tag)[TagFields.Album]);
        }

        [Fact]
        public void RemoveFieldDeletesFrame()
        {
            var tag = new ID3v2Tag();
            tag.SetField(TagFields.Title, "Temp");
            tag.RemoveField(TagFields.Title);
            Assert.False(Known(tag).ContainsKey(TagFields.Title));
        }

        [Fact]
        public void UnsupportedFieldThrows()
        {
            var tag = new ID3v2Tag();
            // Performer has no ID3 mapping in ActionMappingsv23v24
            Assert.Throws<System.ArgumentException>(() => tag.SetField(TagFields.Performer, "x"));
        }
    }
}
