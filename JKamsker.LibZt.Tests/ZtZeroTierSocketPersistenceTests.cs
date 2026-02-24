using JKamsker.LibZt.ZeroTier;
using JKamsker.LibZt.ZeroTier.Internal;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierSocketPersistenceTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public async Task CreateAsync_LoadsPersistedManagedIps()
    {
        Assert.True(ZtZeroTierIdentity.TryParse(KnownGoodIdentity, out var identity));

        var stateRoot = Path.Combine(Path.GetTempPath(), "zt-zero-tier-test-" + Guid.NewGuid());
        Directory.CreateDirectory(stateRoot);

        var networkId = 0x9ad07d01093a69e3UL;
        var statePath = Path.Combine(stateRoot, "zerotier");
        var networksDir = Path.Combine(statePath, "networks.d");

        try
        {
            Directory.CreateDirectory(networksDir);
            ZtZeroTierIdentityStore.Save(Path.Combine(statePath, "identity.bin"), identity);
            File.WriteAllLines(Path.Combine(networksDir, $"{networkId:x16}.ips.txt"), new[] { "10.121.15.5", "fd00::1" });

            await using var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
            {
                StateRootPath = stateRoot,
                NetworkId = networkId
            });

            Assert.Contains(System.Net.IPAddress.Parse("10.121.15.5"), socket.ManagedIps);
            Assert.Contains(System.Net.IPAddress.Parse("fd00::1"), socket.ManagedIps);
        }
        finally
        {
            try
            {
                Directory.Delete(stateRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task CreateAsync_ImportsLibztIdentity_WhenPresent()
    {
        Assert.True(ZtZeroTierIdentity.TryParse(KnownGoodIdentity, out var identity));

        var stateRoot = Path.Combine(Path.GetTempPath(), "zt-zero-tier-test-" + Guid.NewGuid());
        Directory.CreateDirectory(stateRoot);

        var networkId = 0x9ad07d01093a69e3UL;
        var libztDir = Path.Combine(stateRoot, "libzt");
        var importedIdentityPath = Path.Combine(stateRoot, "zerotier", "identity.bin");

        try
        {
            Directory.CreateDirectory(libztDir);
            File.WriteAllText(Path.Combine(libztDir, "identity.secret"), KnownGoodIdentity);

            await using var socket = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
            {
                StateRootPath = stateRoot,
                NetworkId = networkId
            });

            Assert.Equal(identity.NodeId, socket.NodeId);

            Assert.True(ZtZeroTierIdentityStore.TryLoad(importedIdentityPath, out var persisted));
            Assert.Equal(identity.NodeId, persisted.NodeId);
            Assert.NotNull(persisted.PrivateKey);
        }
        finally
        {
            try
            {
                Directory.Delete(stateRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
