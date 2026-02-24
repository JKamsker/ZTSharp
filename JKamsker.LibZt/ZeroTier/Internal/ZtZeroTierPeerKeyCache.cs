using System.Net;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierPeerKeyCache : IDisposable
{
    private readonly ZtZeroTierUdpTransport _udp;
    private readonly ZtNodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly ZtZeroTierIdentity _localIdentity;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<ZtNodeId, ZtZeroTierIdentity> _identities = new();
    private readonly Dictionary<ZtNodeId, byte[]> _keys = new();

    public ZtZeroTierPeerKeyCache(
        ZtZeroTierUdpTransport udp,
        ZtNodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZtZeroTierIdentity localIdentity)
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
        ZtNodeId peer,
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
                identity = await ZtZeroTierWhoisClient
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
            ZtZeroTierC25519.Agree(_localIdentity.PrivateKey!, identity.PublicKey, key);
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
