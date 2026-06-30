using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests
{
    // Direct coverage of the byte-order primitives in MetaTools (Tools), which every
    // hand-rolled parser depends on. Accessible via InternalsVisibleTo.
    public class ByteOrderTests
    {
        private static readonly byte[] Bytes = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

        [Fact]
        public void UInt16ReadsBothEndians()
        {
            Assert.Equal((ushort)0x2211, Tools.UInt16AtLE(Bytes, 0));
            Assert.Equal((ushort)0x1122, Tools.UInt16AtBE(Bytes, 0));
        }

        [Fact]
        public void UInt32ReadsBothEndians()
        {
            Assert.Equal(0x44332211u, Tools.UInt32AtLE(Bytes, 0));
            Assert.Equal(0x11223344u, Tools.UInt32AtBE(Bytes, 0));
        }

        [Fact]
        public void UInt64ReadsBothEndians()
        {
            Assert.Equal(0x8877665544332211ul, Tools.UInt64AtLE(Bytes, 0));
            Assert.Equal(0x1122334455667788ul, Tools.UInt64AtBE(Bytes, 0));
        }

        [Fact]
        public void SignedHelpersReinterpretHighBit()
        {
            byte[] neg = { 0xFF, 0xFF, 0xFF, 0xFF };
            Assert.Equal(-1, Tools.Int32AtLE(neg, 0));
            Assert.Equal((short)-1, Tools.Int16AtBE(neg, 0));
        }

        [Fact]
        public void ToLeRoundTrips()
        {
            byte[] b = Tools.ToLE(0x44332211u);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, b);
            Assert.Equal(0x44332211u, Tools.UInt32AtLE(b, 0));
        }

        [Fact]
        public void ToBeRoundTrips()
        {
            byte[] b = Tools.ToBE(0x11223344u);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, b);
            Assert.Equal(0x11223344u, Tools.UInt32AtBE(b, 0));
        }
    }
}
