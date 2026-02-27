namespace ZTSharp.Tests;

public sealed class StateStoreKeyNormalizationSecurityTests
{
    [Theory]
    [InlineData("file:stream")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("C:Windows\\System32")]
    [InlineData("\\\\?\\C:\\Windows\\System32")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("/absolute/path")]
    [InlineData("\\absolute\\path")]
    public void NormalizeKey_Rejects_Rooted_Or_Ads_Like_Keys(string key)
    {
        _ = Assert.Throws<ArgumentException>(() => StateStoreKeyNormalization.NormalizeKey(key));
    }
}
