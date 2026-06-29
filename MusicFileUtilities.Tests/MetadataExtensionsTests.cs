using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class MetadataExtensionsTests
    {
        [Fact]
        public void LimitLengthTruncatesAndTrims()
        {
            Assert.Equal("Hello", "HelloWorld".LimitLength(5));
            Assert.Equal("Hello", "Hello World".LimitLength(6)); // trailing space trimmed
            Assert.Equal("Short", "Short".LimitLength(100));     // shorter than limit unchanged
        }

        [Fact]
        public void FormatDiscPreservesDiscSuffix()
        {
            Assert.Equal("Greatest Hits (Disc 2)", "Greatest Hits (Disc 2)".FormatDisc(50, 50));
        }

        [Fact]
        public void FormatDiscLimitsPlainAlbumName()
        {
            Assert.Equal("Greate", "Greatest Hits".FormatDisc(6, 50));
        }

        [Theory]
        [InlineData("AC/DC", "AC_DC")]      // invalid path char replaced
        [InlineData("a$b", "asb")]          // dollar mapped to s
        [InlineData("name...", "name")]     // trailing dots removed
        [InlineData("...name", "name")]     // leading dots removed
        public void FixPathSanitizes(string input, string expected)
        {
            Assert.Equal(expected, input.FixPath());
        }
    }
}
