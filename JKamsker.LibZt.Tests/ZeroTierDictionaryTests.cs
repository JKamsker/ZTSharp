using System.Text;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierDictionaryTests
{
    [Fact]
    public void TryGet_ReturnsPlainStringBytes()
    {
        var dict = Encoding.ASCII.GetBytes("k=abc\r\n");
        Assert.True(ZeroTierDictionary.TryGet(dict, "k", out var value));
        Assert.Equal("abc", Encoding.ASCII.GetString(value));
    }

    [Fact]
    public void TryGet_UnescapesBinaryNull()
    {
        var dict = Encoding.ASCII.GetBytes("I=\\0A\r\n");
        Assert.True(ZeroTierDictionary.TryGet(dict, "I", out var value));
        Assert.Equal(new byte[] { 0, (byte)'A' }, value);
    }

    [Fact]
    public void TryGet_UnescapesEquals()
    {
        var dict = Encoding.ASCII.GetBytes("k=a\\eb\r\n");
        Assert.True(ZeroTierDictionary.TryGet(dict, "k", out var value));
        Assert.Equal("a=b", Encoding.ASCII.GetString(value));
    }
}
