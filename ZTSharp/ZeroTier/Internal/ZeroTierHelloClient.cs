using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierHelloOk(
    NodeId RootNodeId,
    IPEndPoint RootEndpoint,
    ulong HelloPacketId,
    ulong HelloTimestampEcho,
    byte RemoteProtocolVersion,
    byte RemoteMajorVersion,
    byte RemoteMinorVersion,
    ushort RemoteRevision,
    IPEndPoint? ExternalSurfaceAddress);

internal static class ZeroTierHelloClient
{
    internal const byte AdvertisedProtocolVersion = 12;
    internal const byte AdvertisedMajorVersion = 1;
    internal const byte AdvertisedMinorVersion = 12;
    internal const ushort AdvertisedRevision = 0;

    public static async Task<ZeroTierHelloOk> HelloRootsAsync(
        IZeroTierUdpTransport udp,
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var rootKeys = ZeroTierRootKeyDerivation.BuildRootKeys(localIdentity, planet);

        var helloTimestamp = (ulong)Environment.TickCount64;
        var pending = new Dictionary<ulong, NodeId>(capacity: planet.Roots.Count);

        foreach (var root in planet.Roots)
        {
            if (!rootKeys.TryGetValue(root.Identity.NodeId, out var key))
            {
                continue;
            }

            foreach (var endpoint in root.StableEndpoints)
            {
                var packet = ZeroTierHelloPacketBuilder.BuildPacket(
                    localIdentity,
                    destination: root.Identity.NodeId,
                    physicalDestination: endpoint,
                    planet,
                    helloTimestamp,
                    key,
                    advertisedProtocolVersion: AdvertisedProtocolVersion,
                    advertisedMajorVersion: AdvertisedMajorVersion,
                    advertisedMinorVersion: AdvertisedMinorVersion,
                    advertisedRevision: AdvertisedRevision,
                    out var packetId);

                try
                {
                    await udp.SendAsync(endpoint, packet, cancellationToken).ConfigureAwait(false);
                    pending[packetId] = root.Identity.NodeId;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // Some environments don't have IPv6 connectivity. Ignore send failures and wait for any
                    // reachable root to respond.
                }
            }
        }

        if (pending.Count == 0)
        {
            throw new InvalidOperationException("Failed to send HELLO to any root endpoints (no reachable network?).");
        }

        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "HELLO response", WaitForHelloOkAsync, cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<ZeroTierHelloOk> WaitForHelloOkAsync(CancellationToken token)
        {
            while (true)
            {
                var datagram = await udp.ReceiveAsync(token).ConfigureAwait(false);

                var packetBytes = datagram.Payload;
                if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var packet))
                {
                    continue;
                }

                if (!rootKeys.TryGetValue(packet.Header.Source, out var key))
                {
                    continue;
                }

                if (!ZeroTierHelloOkParser.TryParse(packetBytes, key, out var ok))
                {
                    continue;
                }

                if (!pending.TryGetValue(ok.InRePacketId, out var rootNodeId))
                {
                    continue;
                }

                if (rootNodeId != packet.Header.Source)
                {
                    continue;
                }

                return new ZeroTierHelloOk(
                    RootNodeId: rootNodeId,
                    RootEndpoint: datagram.RemoteEndPoint,
                    HelloPacketId: ok.InRePacketId,
                    HelloTimestampEcho: ok.TimestampEcho,
                    RemoteProtocolVersion: ok.RemoteProtocolVersion,
                    RemoteMajorVersion: ok.RemoteMajorVersion,
                    RemoteMinorVersion: ok.RemoteMinorVersion,
                    RemoteRevision: ok.RemoteRevision,
                    ExternalSurfaceAddress: ok.ExternalSurfaceAddress);
            }
        }
    }

    public static async Task<byte> HelloAsync(
        IZeroTierUdpTransport udp,
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        NodeId destination,
        IPEndPoint physicalDestination,
        byte[] sharedKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(physicalDestination);
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var helloTimestamp = (ulong)Environment.TickCount64;

        var packet = ZeroTierHelloPacketBuilder.BuildPacket(
            localIdentity,
            destination,
            physicalDestination,
            planet,
            helloTimestamp,
            sharedKey,
            advertisedProtocolVersion: AdvertisedProtocolVersion,
            advertisedMajorVersion: AdvertisedMajorVersion,
            advertisedMinorVersion: AdvertisedMinorVersion,
            advertisedRevision: AdvertisedRevision,
            out var packetId);

        await udp.SendAsync(physicalDestination, packet, cancellationToken).ConfigureAwait(false);

        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "HELLO response", WaitForHelloOkAsync, cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<byte> WaitForHelloOkAsync(CancellationToken token)
        {
            while (true)
            {
                var datagram = await udp.ReceiveAsync(token).ConfigureAwait(false);

                var packetBytes = datagram.Payload;
                if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
                {
                    continue;
                }

                if (decoded.Header.Source != destination)
                {
                    continue;
                }

                if (!ZeroTierHelloOkParser.TryParse(packetBytes, sharedKey, out var ok))
                {
                    continue;
                }

                if (ok.InRePacketId != packetId)
                {
                    continue;
                }

                return ok.RemoteProtocolVersion;
            }
        }
    }

    public static async Task<ZeroTierHelloOk> HelloRootsAsync(
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        try
        {
            return await HelloRootsAsync(udp, localIdentity, planet, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
    }

}
