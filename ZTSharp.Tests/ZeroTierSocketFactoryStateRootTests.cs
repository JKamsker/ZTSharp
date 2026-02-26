using ZTSharp.ZeroTier;

namespace ZTSharp.Tests;

public sealed class ZeroTierSocketFactoryStateRootTests
{
    [Fact]
    public async Task CreateAsync_Normalizes_StateRootPath_ToFullPath()
    {
        var absoluteStateRoot = TestTempPaths.CreateGuidSuffixed("zt-state-root-rel-");
        var relativeStateRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), absoluteStateRoot);

        var options = new ZeroTierSocketOptions
        {
            StateRootPath = relativeStateRoot,
            NetworkId = 1
        };

        await using var socket = await ZeroTierSocket.CreateAsync(options, CancellationToken.None);

        var storedOptions = GetPrivateField<ZeroTierSocketOptions>(socket, "_options");
        Assert.Equal(Path.GetFullPath(relativeStateRoot), storedOptions.StateRootPath);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }
}

