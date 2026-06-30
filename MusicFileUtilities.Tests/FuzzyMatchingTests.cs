using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class FuzzyMatchingTests
    {
        [Theory]
        [InlineData("kitten", "sitting", 3)]
        [InlineData("", "abc", 3)]
        [InlineData("abc", "", 3)]
        [InlineData("flaw", "lawn", 2)]
        [InlineData("same", "same", 0)]
        public void EditDistanceMatchesKnownValues(string a, string b, int expected)
        {
            Assert.Equal(expected, a.EditDistance(b));
        }

        [Fact]
        public void EditDistanceIsSymmetric()
        {
            Assert.Equal("monday".EditDistance("tuesday"), "tuesday".EditDistance("monday"));
        }

        [Fact]
        public void FuzzyDistanceIsZeroForIdenticalIgnoringCase()
        {
            Assert.Equal(0.0, "Hello".FuzzyDistance("hello"), 5);
        }

        [Fact]
        public void FuzzyDistanceIsNormalizedBetweenZeroAndOne()
        {
            double d = "abc".FuzzyDistance("abd");
            Assert.InRange(d, 0.0, 1.0);
            Assert.Equal(1.0 / 3.0, d, 5);
        }

        [Fact]
        public void FuzzyDistanceOfTwoEmptyStringsIsZeroNotNaN()
        {
            double d = "".FuzzyDistance("");
            Assert.False(double.IsNaN(d));
            Assert.Equal(0.0, d, 5);
        }
    }
}
