namespace ZTSharp.Tests;

public sealed class StateStoreKeyNormalizationTests
{
    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("conin$")]
    [InlineData("conout$")]
    [InlineData("clock$")]
    [InlineData("nul")]
    [InlineData("lpt1")]
    [InlineData("peers.d/con/peer")]
    [InlineData("bad<name")]
    [InlineData("bad\"name")]
    [InlineData("bad|name")]
    [InlineData("bad*name")]
    [InlineData("bad\u0001name")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    public void NormalizeKey_RejectsReservedNames_AndInvalidSegments(string key)
    {
        _ = Assert.Throws<ArgumentException>(() => StateStoreKeyNormalization.NormalizeKey(key));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("con.txt")]
    [InlineData("bad<name")]
    [InlineData("bad\u0001name")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    public void NormalizePrefix_RejectsReservedNames_AndInvalidSegments(string prefix)
    {
        _ = Assert.Throws<ArgumentException>(() => StateStorePrefixNormalization.NormalizeForList(prefix));
    }
}
