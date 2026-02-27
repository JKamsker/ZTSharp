using System.Linq;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierInlineComTests
{
    [Fact]
    public void GetInlineCom_ReturnsCom_WhenLengthMatches()
    {
        var com = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        var dict = BuildDictionaryWithCom(com);

        var inlineCom = ZeroTierInlineCom.GetInlineCom(dict);

        Assert.True(inlineCom.SequenceEqual(com));
    }

    [Fact]
    public void GetInlineCom_TruncatesCom_WhenTrailingBytesArePresent()
    {
        var com = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        var dict = BuildDictionaryWithCom(com.Concat(new byte[] { 0x99, 0x88, 0x77 }).ToArray());

        var inlineCom = ZeroTierInlineCom.GetInlineCom(dict);

        Assert.Equal(com.Length, inlineCom.Length);
        Assert.True(inlineCom.SequenceEqual(com));
    }

    private static byte[] BuildDictionaryWithCom(byte[] comValueBytes)
    {
        var prefix = new byte[] { (byte)'C', (byte)'=' };
        var newline = new byte[] { (byte)'\n' };
        return prefix
            .Concat(comValueBytes)
            .Concat(newline)
            .ToArray();
    }
}
