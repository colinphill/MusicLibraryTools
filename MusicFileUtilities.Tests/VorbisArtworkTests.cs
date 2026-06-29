using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class VorbisArtworkTests
    {
        [Fact]
        public void ByteArrayRoundTripPreservesAllFields()
        {
            var art = new VorbisArtwork
            {
                PictureType = ID3v2Util.APICType.BackCover,
                MimeType = "image/jpeg",
                Description = "the back",
                Width = 500,
                Height = 400,
                Depth = 24,
                ColorsUsed = 0,
                Data = new byte[] { 9, 8, 7, 6, 5 }
            };

            var rt = new VorbisArtwork(art.ToByteArray());

            Assert.Equal(ID3v2Util.APICType.BackCover, rt.PictureType);
            Assert.Equal("image/jpeg", rt.MimeType);
            Assert.Equal("the back", rt.Description);
            Assert.Equal(500, rt.Width);
            Assert.Equal(400, rt.Height);
            Assert.Equal(24, rt.Depth);
            Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, rt.Data);
        }
    }
}
