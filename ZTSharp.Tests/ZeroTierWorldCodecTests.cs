using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
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
    public void EmbeddedDefault_IgnoresInvalidStatePlanet_WhenPresent()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-zero-tier-planet-test-");
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
            Assert.Equal(ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World).Id, world.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(stateRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void EmbeddedDefault_IgnoresOversizedStatePlanet_WhenPresent()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-zero-tier-planet-test-");
        var libztDir = Path.Combine(stateRoot, "libzt");
        var libztRootsPath = Path.Combine(libztDir, "roots");

        try
        {
            Directory.CreateDirectory(libztDir);
            File.WriteAllBytes(libztRootsPath, new byte[ZeroTierProtocolLimits.MaxWorldBytes + 1]);

            var options = new ZeroTierSocketOptions
            {
                StateRootPath = stateRoot,
                NetworkId = 1,
                PlanetSource = ZeroTierPlanetSource.EmbeddedDefault
            };

            var world = ZeroTierPlanetLoader.Load(options, CancellationToken.None);
            Assert.Equal(ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World).Id, world.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(stateRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Decode_TruncatesRoots_AndStableEndpoints_ForForwardCompatibility()
    {
        var roots = new List<(ZeroTierIdentity Identity, IPEndPoint[] Endpoints)>();

        for (var i = 0; i < 50; i++)
        {
            var identity = CreateIdentity(0x0100000000UL + (ulong)i);
            var endpoints = new List<IPEndPoint>();

            var endpointCount = i == 0 ? 100 : 1;
            for (var j = 0; j < endpointCount; j++)
            {
                endpoints.Add(new IPEndPoint(IPAddress.Parse($"10.0.{i % 255}.{(j % 250) + 1}"), 9993));
            }

            roots.Add((identity, endpoints.ToArray()));
        }

        var bytes = EncodePlanet(roots);
        var decoded = ZeroTierWorldCodec.Decode(bytes);

        Assert.Equal(ZeroTierWorldType.Planet, decoded.Type);
        Assert.Equal(32, decoded.Roots.Count);
        Assert.Equal(64, decoded.Roots[0].StableEndpoints.Count);
    }

    private static ZeroTierIdentity CreateIdentity(ulong nodeId)
    {
        var publicKey = new byte[ZeroTierIdentity.PublicKeyLength];
        BinaryPrimitives.WriteUInt64BigEndian(publicKey.AsSpan(0, 8), nodeId);
        return new ZeroTierIdentity(new NodeId(nodeId), publicKey, privateKey: null);
    }

    private static byte[] EncodePlanet(List<(ZeroTierIdentity Identity, IPEndPoint[] Endpoints)> roots)
    {
        var bytes = new List<byte>(1024);

        bytes.Add(1); // Planet

        WriteUInt64(bytes, 1);
        WriteUInt64(bytes, 1);

        bytes.AddRange(new byte[ZeroTierWorld.C25519PublicKeyLength]);
        bytes.AddRange(new byte[ZeroTierWorld.C25519SignatureLength]);

        bytes.Add(checked((byte)roots.Count));

        foreach (var (identity, endpoints) in roots)
        {
            bytes.AddRange(ZeroTierIdentityCodec.Serialize(identity, includePrivate: false));
            bytes.Add(checked((byte)endpoints.Length));
            foreach (var endpoint in endpoints)
            {
                if (endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new InvalidOperationException("Test helper supports IPv4 only.");
                }

                bytes.Add(0x04);
                bytes.AddRange(endpoint.Address.GetAddressBytes());
                WriteUInt16(bytes, (ushort)endpoint.Port);
            }
        }

        Assert.True(bytes.Count <= ZeroTierProtocolLimits.MaxWorldBytes);
        return bytes.ToArray();
    }

    private static void WriteUInt16(List<byte> destination, ushort value)
    {
        Span<byte> temp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(temp, value);
        destination.AddRange(temp.ToArray());
    }

    private static void WriteUInt64(List<byte> destination, ulong value)
    {
        Span<byte> temp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(temp, value);
        destination.AddRange(temp.ToArray());
    }
}
