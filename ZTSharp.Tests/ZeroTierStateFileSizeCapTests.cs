using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierStateFileSizeCapTests
{
    [Fact]
    public void LoadManagedIps_LargeFile_IsRejected()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-state-ips-");
        var statePath = Path.Combine(stateRoot, "zerotier");
        var networksDir = Path.Combine(statePath, "networks.d");
        Directory.CreateDirectory(networksDir);

        const ulong networkId = 1;
        var path = Path.Combine(networksDir, $"{networkId:x16}.ips.txt");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(10L * 1024 * 1024);
        }

        var ips = ZeroTierSocketStatePersistence.LoadManagedIps(statePath, networkId);
        Assert.Empty(ips);
    }

    [Fact]
    public void TryLoadLibztIdentity_LargeFile_IsRejected()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-state-identity-secret-");
        var libztDir = Path.Combine(stateRoot, "libzt");
        Directory.CreateDirectory(libztDir);

        var path = Path.Combine(libztDir, "identity.secret");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(1024L * 1024);
        }

        Assert.False(ZeroTierSocketIdentityMigration.TryLoadLibztIdentity(stateRoot, out _));
    }

    [Fact]
    public void LoadNetworkConfigDictionary_LargeFile_IsRejected()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-state-netconf-");
        var statePath = Path.Combine(stateRoot, "zerotier");
        var networksDir = Path.Combine(statePath, "networks.d");
        Directory.CreateDirectory(networksDir);

        const ulong networkId = 1;
        var path = Path.Combine(networksDir, $"{networkId:x16}.netconf.dict");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(2L * 1024 * 1024);
        }

        Assert.Null(ZeroTierSocketStatePersistence.LoadNetworkConfigDictionary(statePath, networkId));
    }

    [Fact]
    public void IdentityStore_TryLoad_LargeFile_IsRejected()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-state-identity-store-");
        Directory.CreateDirectory(stateRoot);

        var path = Path.Combine(stateRoot, "identity.secret");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(1024L * 1024);
        }

        Assert.False(ZeroTierIdentityStore.TryLoad(path, out _));
    }
}
