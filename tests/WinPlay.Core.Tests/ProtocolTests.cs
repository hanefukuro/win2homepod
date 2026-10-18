using WinPlay.Core.Discovery;
using WinPlay.Core.Plist;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void SetupPlistRoundTripsNestedStreamAndBinaryKey()
    {
        var input = new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L, ["ct"] = 2L, ["spf"] = 352L,
                    ["audioMode"] = "default", ["isMedia"] = true,
                    ["shk"] = new byte[] { 0, 1, 127, 255 },
                },
            },
        };
        var output = BinaryPlist.ReadDictionary(BinaryPlist.Write(input));
        var streams = Assert.IsType<List<object?>>(output["streams"]);
        var stream = Assert.IsType<Dictionary<string, object?>>(Assert.Single(streams));
        Assert.Equal(96L, stream["type"]);
        Assert.Equal(2L, stream["ct"]);
        Assert.Equal(352L, stream["spf"]);
        Assert.Equal("default", stream["audioMode"]);
        Assert.Equal(true, stream["isMedia"]);
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, Assert.IsType<byte[]>(stream["shk"]));
    }

    [Theory]
    [InlineData("0x200,0x200", 0x20000000200UL)]
    [InlineData("0x80000000,0x80000000", 0x8000000080000000UL)]
    [InlineData("0x200,0x80000000", 0x8000000000000200UL)]
    [InlineData("0x80000000,0x1", 0x0000000180000000UL)]
    [InlineData("512", 512UL)]
    [InlineData("0x200", 512UL)]
    [InlineData(null, 0UL)]
    [InlineData("garbage", 0UL)]
    public void FeatureWordsKeepTheirOrderAndUnknownBits(string? txt, ulong expected) =>
        Assert.Equal(expected, AirPlayFeaturesExtensions.ParseFeatures(txt));

    [Fact]
    public void AlacVerbatimFramePreservesSignedStereoSamples()
    {
        short[] samples = Enumerable.Range(0, 704).Select(i => (short)(i % 2 == 0 ? short.MinValue + i : short.MaxValue - i)).ToArray();
        byte[] frame = AlacFramer.WrapPcmFrame(samples);
        int position = 0;
        uint Bits(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++, position++)
                value = (value << 1) | (uint)((frame[position / 8] >> (7 - position % 8)) & 1);
            return value;
        }
        Assert.Equal(1412, frame.Length);
        Assert.Equal(1U, Bits(3));
        Assert.Equal(0U, Bits(19));
        Assert.Equal(1U, Bits(1));
        foreach (short sample in samples) Assert.Equal(unchecked((ushort)sample), Bits(16));
        Assert.Equal(7U, Bits(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(352)]
    [InlineData(703)]
    [InlineData(705)]
    public void UnsupportedAudioFrameSizesFailRatherThanSilentlyChangingPacketization(int samples) =>
        Assert.Throws<ArgumentException>(() => AlacFramer.WrapPcmFrame(new short[samples]));
}
