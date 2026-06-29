using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class ApeTagTests
    {
        private static Dictionary<TagFields, string> Known(APETag t)
        {
            var d = new Dictionary<TagFields, string>();
            foreach (var kv in t.GetKnownMetadata())
                d[kv.Key] = kv.Value;
            return d;
        }

        private static APETag RoundTrip(APETag tag)
        {
            byte[] bytes = tag.ToByteArray();
            var rt = new APETag();
            Assert.True(rt.ReadTag(new MemoryStream(bytes)));
            return rt;
        }

        [Fact]
        public void TextFieldsRoundTrip()
        {
            var tag = new APETag();
            tag.SetField(TagFields.Title, "Hello");
            tag.SetField(TagFields.Artist, "World");
            tag.SetField(TagFields.Album, "Greatest");

            var known = Known(RoundTrip(tag));
            Assert.Equal("Hello", known[TagFields.Title]);
            Assert.Equal("World", known[TagFields.Artist]);
            Assert.Equal("Greatest", known[TagFields.Album]);
        }

        [Fact]
        public void TrackAndTotalShareSingleKeyAsCombined()
        {
            var tag = new APETag();
            tag.SetField(TagFields.TrackNumber, "3");
            tag.SetField(TagFields.TotalTracks, "10");

            // Stored as a single "Track" item "3/10"
            Assert.Single(tag.TextItems, i => i.Key == "Track");
            Assert.Equal("3/10", tag.TextItems.First(i => i.Key == "Track").Value);

            var known = Known(RoundTrip(tag));
            Assert.Equal("3", known[TagFields.TrackNumber]);
            Assert.Equal("10", known[TagFields.TotalTracks]);
        }

        [Fact]
        public void RemoveFieldDeletesItem()
        {
            var tag = new APETag();
            tag.SetField(TagFields.Genre, "Rock");
            tag.RemoveField(TagFields.Genre);
            Assert.False(Known(tag).ContainsKey(TagFields.Genre));
        }

        [Fact]
        public void Utf8ValuesSurviveRoundTrip()
        {
            var tag = new APETag();
            tag.SetField(TagFields.Artist, "Sigur Rós");
            Assert.Equal("Sigur Rós", Known(RoundTrip(tag))[TagFields.Artist]);
        }

        [Fact]
        public void ReadTagReturnsFalseForTooShortStream()
        {
            var tag = new APETag();
            Assert.False(tag.ReadTag(new MemoryStream(new byte[10])));
        }
    }
}
