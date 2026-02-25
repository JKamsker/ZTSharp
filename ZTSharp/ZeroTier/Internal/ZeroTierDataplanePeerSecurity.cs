using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplanePeerSecurity : IDisposable
{
    private const int HelloPayloadMinLength = 13 + (5 + 1 + ZeroTierIdentity.PublicKeyLength + 1);

    private readonly ZeroTierUdpTransport _udp;
    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly NodeId _localNodeId;
    private readonly byte[] _localPrivateKey;

    private readonly ConcurrentDictionary<NodeId, byte[]> _peerKeys = new();
    private readonly ConcurrentDictionary<NodeId, byte> _peerProtocolVersions = new();
    private readonly SemaphoreSlim _peerKeyLock = new(1, 1);

    public ZeroTierDataplanePeerSecurity(
        ZeroTierUdpTransport udp,
        ZeroTierDataplaneRootClient rootClient,
        ZeroTierIdentity localIdentity)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootClient);
        ArgumentNullException.ThrowIfNull(localIdentity);

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        _udp = udp;
        _rootClient = rootClient;
        _localNodeId = localIdentity.NodeId;
        _localPrivateKey = localIdentity.PrivateKey;
    }

    public byte GetPeerProtocolVersionOrDefault(NodeId peerNodeId)
        => _peerProtocolVersions.TryGetValue(peerNodeId, out var version) ? version : (byte)0;

    public async Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_peerKeys.TryGetValue(peerNodeId, out var existing))
        {
            return existing;
        }

        await _peerKeyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_peerKeys.TryGetValue(peerNodeId, out existing))
            {
                return existing;
            }

            var identity = await _rootClient.WhoisAsync(peerNodeId, timeout: TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var key = new byte[48];
            ZeroTierC25519.Agree(_localPrivateKey, identity.PublicKey, key);
            _peerKeys[peerNodeId] = key;
            return key;
        }
        finally
        {
            _peerKeyLock.Release();
        }
    }

    public async ValueTask HandleHelloAsync(
        NodeId peerNodeId,
        ulong helloPacketId,
        byte[] packetBytes,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (packetBytes.Length < ZeroTierPacketHeader.Length + HelloPayloadMinLength)
        {
            return;
        }

        var payload = packetBytes.AsSpan(ZeroTierPacketHeader.IndexPayload);
        if (payload.Length < HelloPayloadMinLength)
        {
            return;
        }

        var helloTimestamp = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(5, 8));

        ZeroTierIdentity identity;
        try
        {
            identity = ZeroTierIdentityCodec.Deserialize(payload.Slice(13), out _);
        }
        catch (FormatException)
        {
            return;
        }

        if (identity.NodeId != peerNodeId || !identity.LocallyValidate())
        {
            return;
        }

        var sharedKey = new byte[48];
        ZeroTierC25519.Agree(_localPrivateKey, identity.PublicKey, sharedKey);

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, sharedKey))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Drop: failed to authenticate HELLO from {peerNodeId} via {remoteEndPoint}.");
            }

            return;
        }

        _peerKeys[peerNodeId] = sharedKey;
        var peerProtocolVersion = payload[0];
        _peerProtocolVersions[peerNodeId] = peerProtocolVersion;

        var okPacket = ZeroTierHelloOkPacketBuilder.BuildPacket(
            packetId: ZeroTierPacketIdGenerator.GeneratePacketId(),
            destination: peerNodeId,
            source: _localNodeId,
            inRePacketId: helloPacketId,
            helloTimestampEcho: helloTimestamp,
            externalSurfaceAddress: remoteEndPoint,
            sharedKey: ZeroTierPacketCrypto.SelectOutboundKey(sharedKey, peerProtocolVersion));

        try
        {
            await _udp.SendAsync(remoteEndPoint, okPacket, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _peerKeyLock.Dispose();
    }
}
