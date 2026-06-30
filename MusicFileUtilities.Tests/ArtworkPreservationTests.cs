using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Verifies embedded cover art is not lost across the write path. Pairs with the in-memory
    // APE cover-art test; this one exercises a real FLAC container end to end.
    public class ArtworkPreservationTests
    {
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20, 0x30, 0x40 };

        [Fact]
        public void FlacEmbeddedArtworkSurvivesTagEditAndSave()
        {
            using var withArt = MediaFixtures.Copy("sample.flac");

            // Output must keep a .flac extension so MediaFile.GetFile dispatches correctly.
            string artPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mlt_art_" + System.Guid.NewGuid().ToString("N") + ".flac");

            // Phase 1: add a picture and write it out (separate output path -> full rewrite,
            // which is the path that emits PICTURE blocks).
            {
                var flac = (FLACFile)MediaFile.GetFile(withArt.Path);
                flac.Artworks.Add(new VorbisArtwork
                {
                    PictureType = ID3v2Util.APICType.FrontCover,
                    MimeType = "image/jpeg",
                    Description = "",
                    Depth = 24,
                    Data = Jpeg
                });
                flac.SaveTags(artPath);
            }

            try
            {
                // The freshly written file really has the artwork.
                var img = Assert.Single(MediaFile.GetFile(artPath).Tags.First().GetImageMetadata());
                Assert.Equal(Jpeg, img.Data);

                // Phase 2: edit a text tag and save in place; the cover must survive.
                var mf = MediaFile.GetFile(artPath);
                ((IMetadataWriter)mf).SetField(TagFields.Title, "Edited With Art");
                mf.SaveTags();

                var reopened = MediaFile.GetFile(artPath);
                Assert.Equal("Edited With Art", reopened.Tags.First()
                    .GetKnownMetadata().First(kv => kv.Key == TagFields.Title).Value);
                var img2 = Assert.Single(reopened.Tags.First().GetImageMetadata());
                Assert.Equal(Jpeg, img2.Data);
            }
            finally
            {
                if (System.IO.File.Exists(artPath)) System.IO.File.Delete(artPath);
                if (System.IO.File.Exists(artPath + ".tmp~")) System.IO.File.Delete(artPath + ".tmp~");
            }
        }

        [Fact]
        public void FlacAddingArtworkInPlaceIsPersisted()
        {
            // The fixture has no art and small tags, so a plain SaveTags() is an in-place
            // VORBIS_COMMENT-rewrite candidate. Adding a picture must still force a full
            // rewrite so the new PICTURE block is actually written (regression).
            using var tmp = MediaFixtures.Copy("sample.flac");

            var flac = (FLACFile)MediaFile.GetFile(tmp.Path);
            Assert.Empty(flac.GetImageMetadata());
            flac.Artworks.Add(new VorbisArtwork
            {
                PictureType = ID3v2Util.APICType.FrontCover,
                MimeType = "image/jpeg",
                Description = "",
                Depth = 24,
                Data = Jpeg
            });
            flac.SaveTags();

            var img = Assert.Single(MediaFile.GetFile(tmp.Path).Tags.First().GetImageMetadata());
            Assert.Equal(Jpeg, img.Data);
        }
    }
}
