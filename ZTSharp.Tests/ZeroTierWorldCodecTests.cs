using System.Buffers.Binary;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierWorldCodecTests
{
    [Fact]
    public void CanDecodeEmbeddedDefaultPlanet()
    {
        var world = ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World);

        Assert.Equal(ZeroTierWorldType.Planet, world.Type);
        Assert.Equal(149604618UL, world.Id);
        Assert.InRange(world.Roots.Count, 1, 4);

        foreach (var root in world.Roots)
        {
            Assert.NotEqual(0UL, root.Identity.NodeId.Value);
            Assert.Equal(ZeroTierIdentity.PublicKeyLength, root.Identity.PublicKey.Length);
            Assert.NotEmpty(root.StableEndpoints);

            foreach (var endpoint in root.StableEndpoints)
            {
                Assert.Equal(9993, endpoint.Port);
                Assert.False(endpoint.Address.IsIPv4MappedToIPv6);
                Assert.False(endpoint.Address.Equals(System.Net.IPAddress.Any));
                Assert.False(endpoint.Address.Equals(System.Net.IPAddress.IPv6Any));
            }
        }
    }

    [Fact]
    public void CanLoadPlanetFromEmbeddedDefaultAndFile()
    {
        var options = new ZeroTierSocketOptions
        {
            StateRootPath = "unused",
            NetworkId = 1,
            PlanetSource = ZeroTierPlanetSource.EmbeddedDefault
        };

        var embedded = ZeroTierPlanetLoader.Load(options, CancellationToken.None);
        Assert.Equal(ZeroTierWorldType.Planet, embedded.Type);
        Assert.Equal(149604618UL, embedded.Id);

        var planetPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(planetPath, ZeroTierDefaultPlanet.World.ToArray());

            var fileOptions = new ZeroTierSocketOptions
            {
                StateRootPath = "unused",
                NetworkId = 1,
                PlanetSource = ZeroTierPlanetSource.FilePath,
                PlanetFilePath = planetPath
            };

            var fromFile = ZeroTierPlanetLoader.Load(fileOptions, CancellationToken.None);
            Assert.Equal(ZeroTierWorldType.Planet, fromFile.Type);
            Assert.Equal(149604618UL, fromFile.Id);
        }
        finally
        {
            File.Delete(planetPath);
        }
    }

    [Fact]
    public void EmbeddedDefault_PrefersPlanetFromLibztState_WhenPresent()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "zt-zero-tier-planet-test-" + Guid.NewGuid());
        var libztDir = Path.Combine(stateRoot, "libzt");
        var libztRootsPath = Path.Combine(libztDir, "roots");

        try
        {
            Directory.CreateDirectory(libztDir);

            var customPlanetId = 123456789UL;
            var bytes = ZeroTierDefaultPlanet.World.ToArray();
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1, 8), customPlanetId);
            File.WriteAllBytes(libztRootsPath, bytes);

            var options = new ZeroTierSocketOptions
            {
                StateRootPath = stateRoot,
                NetworkId = 1,
                PlanetSource = ZeroTierPlanetSource.EmbeddedDefault
            };

            var world = ZeroTierPlanetLoader.Load(options, CancellationToken.None);
            Assert.Equal(customPlanetId, world.Id);
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
