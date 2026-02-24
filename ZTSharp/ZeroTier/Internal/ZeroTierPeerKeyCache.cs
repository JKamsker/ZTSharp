using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerKeyCache : IDisposable
{
    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly ZeroTierIdentity _localIdentity;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<NodeId, ZeroTierIdentity> _identities = new();
    private readonly Dictionary<NodeId, byte[]> _keys = new();

    public ZeroTierPeerKeyCache(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZeroTierIdentity localIdentity)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(localIdentity);

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _localIdentity = localIdentity;
    }

    public async Task<byte[]> GetSharedKeyAsync(
        NodeId peer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_keys.TryGetValue(peer, out var existing))
        {
            return existing;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.TryGetValue(peer, out existing))
            {
                return existing;
            }

            if (!_identities.TryGetValue(peer, out var identity))
            {
                identity = await ZeroTierWhoisClient
                    .WhoisAsync(
                        _udp,
                        _rootNodeId,
                        _rootEndpoint,
                        _rootKey,
                        _localIdentity.NodeId,
                        peer,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                _identities[peer] = identity;
            }

            var key = new byte[48];
            ZeroTierC25519.Agree(_localIdentity.PrivateKey!, identity.PublicKey, key);
            _keys[peer] = key;
            return key;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
