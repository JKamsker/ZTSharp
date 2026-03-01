using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class SecretFilePermissionTests
{
    [UnixFact]
    public void ZeroTierIdentityStore_Save_SetsUnixMode600_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("UnixFact should have skipped this test on Windows.");
        }

        var root = TestTempPaths.CreateGuidSuffixed("zt-secret-perms-");
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, "identity.bin");
        var identity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);

        ZeroTierIdentityStore.Save(path, identity);
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        ZeroTierIdentityStore.Save(path, identity);
        var mode2 = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode2);
    }
}

