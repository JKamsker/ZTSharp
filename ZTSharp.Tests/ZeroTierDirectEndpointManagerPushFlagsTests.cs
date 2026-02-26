using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDirectEndpointManagerPushFlagsTests
{
    [Fact]
    public async Task PushDirectPaths_ForgetFlag_RemovesEndpoint()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var receiver = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var relay = new IPEndPoint(IPAddress.Loopback, 9999);
        var peerNodeId = new NodeId(0x1111111111);
        var manager = new ZeroTierDirectEndpointManager(udp, relay, peerNodeId);

        var endpoint = TestUdpEndpoints.ToLoopback(receiver.LocalEndpoint);
        await manager.HandlePushDirectPathsFromRemoteAsync(BuildPushDirectPathsPayload(endpoint, flags: 0), CancellationToken.None);

        _ = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(manager.Endpoints, ep => ep.Equals(endpoint));

        await manager.HandlePushDirectPathsFromRemoteAsync(
            BuildPushDirectPathsPayload(endpoint, flags: ZtPushDirectPathsFlagForgetPath),
            CancellationToken.None);

        Assert.DoesNotContain(manager.Endpoints, ep => ep.Equals(endpoint));
    }

    private const byte ZtPushDirectPathsFlagForgetPath = 0x01;

    private static byte[] BuildPushDirectPathsPayload(IPEndPoint endpoint, byte flags)
    {
        if (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "Test helper supports IPv4 only.");
        }

        var addressBytes = endpoint.Address.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "Invalid IPv4 address bytes.");
        }

        var payload = new byte[2 + 1 + 2 + 1 + 1 + 6];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), 1);

        var ptr = 2;
        span[ptr++] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(ptr, 2), 0);
        ptr += 2;

        span[ptr++] = 4;
        span[ptr++] = 6;

        addressBytes.CopyTo(span.Slice(ptr, 4));
        ptr += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(ptr, 2), (ushort)endpoint.Port);

        return payload;
    }
}

