using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierIpv4Link : IUserSpaceIpLink
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly ZeroTierIpv4LinkSender _sender;
    private readonly ZeroTierIpv4LinkReceiver _receiver;
    private bool _disposed;

    public ZeroTierIpv4Link(
        ZeroTierUdpTransport udp,
        IPEndPoint relayEndpoint,
        NodeId rootNodeId,
        byte[] rootKey,
        NodeId localNodeId,
        NodeId remoteNodeId,
        ulong networkId,
        IPAddress localManagedIp,
        byte[] inlineCom,
        byte[] sharedKey,
        byte remoteProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localManagedIp);
        ArgumentNullException.ThrowIfNull(inlineCom);
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (localManagedIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIp), "Local managed IP must be an IPv4 address.");
        }

        _udp = udp;

        var localManagedIpV4 = localManagedIp.GetAddressBytes();
        var remoteMac = ZeroTierMac.FromAddress(remoteNodeId, networkId);
        var localMac = ZeroTierMac.FromAddress(localNodeId, networkId);
        var directEndpoints = new ZeroTierDirectEndpointManager(udp, relayEndpoint, remoteNodeId);

        _sender = new ZeroTierIpv4LinkSender(
            udp,
            relayEndpoint,
            directEndpoints,
            localNodeId,
            remoteNodeId,
            networkId,
            inlineCom,
            to: remoteMac,
            from: localMac,
            sharedKey,
            remoteProtocolVersion);

        _receiver = new ZeroTierIpv4LinkReceiver(
            udp,
            rootNodeId,
            localNodeId,
            remoteNodeId,
            networkId,
            rootKey,
            sharedKey,
            localMac,
            remoteMac,
            localManagedIpV4,
            directEndpoints,
            _sender);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sender.SendIpv4Async(ipPacket, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _udp.DisposeAsync().ConfigureAwait(false);
    }
}
