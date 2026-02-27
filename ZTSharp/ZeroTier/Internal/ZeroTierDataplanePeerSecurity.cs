using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplanePeerSecurity : IDisposable
{
    private const int HelloPayloadMinLength = 13 + (5 + 1 + ZeroTierIdentity.PublicKeyLength + 1);
    private const int MaxPeerKeyCacheEntries = 4096;
    private static readonly TimeSpan PeerKeyTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NegativePeerKeyTtl = TimeSpan.FromSeconds(30);

    private readonly IZeroTierUdpTransport _udp;
    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly NodeId _localNodeId;
    private readonly byte[] _localPrivateKey;

    private readonly ConcurrentDictionary<NodeId, PeerKeyCacheEntry> _peerKeys = new();
    private readonly ConcurrentDictionary<NodeId, Task<byte[]>> _inflightKeys = new();
    private readonly ConcurrentDictionary<NodeId, byte> _peerProtocolVersions = new();
    private readonly CancellationTokenSource _cts = new();
    private int _trimPeerKeysInProgress;

    public ZeroTierDataplanePeerSecurity(
        IZeroTierUdpTransport udp,
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

    internal void ObservePeerProtocolVersion(NodeId peerNodeId, byte peerProtocolVersion)
    {
        peerProtocolVersion = peerProtocolVersion <= ZeroTierHelloClient.AdvertisedProtocolVersion
            ? peerProtocolVersion
            : ZeroTierHelloClient.AdvertisedProtocolVersion;

        _peerProtocolVersions[peerNodeId] = peerProtocolVersion;
    }

    public bool TryGetPeerKey(NodeId peerNodeId, out byte[] key)
    {
        key = Array.Empty<byte>();

        var nowMs = Environment.TickCount64;
        if (!TryGetPeerKeyEntry(peerNodeId, nowMs, out var entry))
        {
            return false;
        }

        if (entry.Key is null)
        {
            return false;
        }

        key = entry.Key;
        return true;
    }

    public void EnsurePeerKeyAsync(NodeId peerNodeId)
    {
        var nowMs = Environment.TickCount64;
        if (TryGetPeerKeyEntry(peerNodeId, nowMs, out _))
        {
            return;
        }

        _ = _inflightKeys.GetOrAdd(peerNodeId, StartPeerKeyFetch);
    }

    public async Task<byte[]> GetPeerKeyAsync(NodeId peerNodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowMs = Environment.TickCount64;
        if (TryGetPeerKeyEntry(peerNodeId, nowMs, out var existing))
        {
            if (existing.Key is not null)
            {
                return existing.Key;
            }

            throw new InvalidOperationException($"Peer key for {peerNodeId} is unavailable (recent WHOIS failure).");
        }

        var task = _inflightKeys.GetOrAdd(peerNodeId, StartPeerKeyFetch);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask HandleHelloAsync(
        NodeId peerNodeId,
        ulong helloPacketId,
        byte[] packetBytes,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (packetBytes.Length > ZeroTierProtocolLimits.MaxPacketBytes)
        {
            return;
        }

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

        if (identity.NodeId != peerNodeId)
        {
            return;
        }

        var sharedKey = new byte[48];
        try
        {
            ZeroTierC25519.Agree(_localPrivateKey, identity.PublicKey, sharedKey);
        }
        catch (CryptographicException)
        {
            return;
        }

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, sharedKey))
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] Drop: failed to authenticate HELLO from {peerNodeId} via {remoteEndPoint}.");
            }

            return;
        }

        if (!identity.LocallyValidate())
        {
            return;
        }

        CachePeerKey(peerNodeId, sharedKey, nowMs: Environment.TickCount64);
        var peerProtocolVersion = payload[0];
        ObservePeerProtocolVersion(peerNodeId, peerProtocolVersion);

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
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private Task<byte[]> StartPeerKeyFetch(NodeId peerNodeId)
    {
        var task = FetchAndCachePeerKeyAsync(peerNodeId);
        _ = ObservePeerKeyFetchAsync(peerNodeId, task);
        return task;
    }

    private async Task<byte[]> FetchAndCachePeerKeyAsync(NodeId peerNodeId)
    {
        var nowMs = Environment.TickCount64;
        try
        {
            var identity = await _rootClient
                .WhoisAsync(peerNodeId, timeout: TimeSpan.FromSeconds(10), _cts.Token)
                .ConfigureAwait(false);

            var key = new byte[48];
            ZeroTierC25519.Agree(_localPrivateKey, identity.PublicKey, key);
            CachePeerKey(peerNodeId, key, nowMs);
            return key;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Background WHOIS failures must not take down the dataplane.
        catch (Exception)
#pragma warning restore CA1031
        {
            CacheNegativePeerKey(peerNodeId, nowMs);
            throw;
        }
    }

    private async Task ObservePeerKeyFetchAsync(NodeId peerNodeId, Task<byte[]> task)
    {
        try
        {
            _ = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Background WHOIS failures are handled via negative caching.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ZeroTierTrace.WriteLine($"[zerotier] WHOIS failed for {peerNodeId}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _inflightKeys.TryRemove(new KeyValuePair<NodeId, Task<byte[]>>(peerNodeId, task));
        }
    }

    private bool TryGetPeerKeyEntry(NodeId peerNodeId, long nowMs, out PeerKeyCacheEntry entry)
    {
        entry = default;
        if (!_peerKeys.TryGetValue(peerNodeId, out entry))
        {
            return false;
        }

        if (entry.ExpiresAtUnixMs <= nowMs)
        {
            _peerKeys.TryRemove(peerNodeId, out _);
            entry = default;
            return false;
        }

        return true;
    }

    private void CachePeerKey(NodeId peerNodeId, byte[] key, long nowMs)
    {
        _peerKeys[peerNodeId] = new PeerKeyCacheEntry(
            Key: key,
            ExpiresAtUnixMs: nowMs + (long)PeerKeyTtl.TotalMilliseconds);
        TrimPeerKeyCacheIfNeeded(nowMs);
    }

    private void CacheNegativePeerKey(NodeId peerNodeId, long nowMs)
    {
        var entry = new PeerKeyCacheEntry(
            Key: null,
            ExpiresAtUnixMs: nowMs + (long)NegativePeerKeyTtl.TotalMilliseconds);

        _peerKeys.AddOrUpdate(
            peerNodeId,
            _ => entry,
            (_, existing) =>
            {
                if (existing.Key is not null && existing.ExpiresAtUnixMs > nowMs)
                {
                    return existing;
                }

                return entry;
            });
        TrimPeerKeyCacheIfNeeded(nowMs);
    }

    private void TrimPeerKeyCacheIfNeeded(long nowMs)
    {
        if (_peerKeys.Count <= MaxPeerKeyCacheEntries)
        {
            return;
        }

        if (Interlocked.Exchange(ref _trimPeerKeysInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var (peerNodeId, entry) in _peerKeys)
            {
                if (entry.ExpiresAtUnixMs <= nowMs)
                {
                    _peerKeys.TryRemove(peerNodeId, out _);
                    _peerProtocolVersions.TryRemove(peerNodeId, out _);
                }
            }

            var count = _peerKeys.Count;
            if (count <= MaxPeerKeyCacheEntries)
            {
                return;
            }

            var toRemove = count - MaxPeerKeyCacheEntries;
            foreach (var peerNodeId in _peerKeys.Keys)
            {
                if (toRemove <= 0)
                {
                    break;
                }

                _peerKeys.TryRemove(peerNodeId, out _);
                _peerProtocolVersions.TryRemove(peerNodeId, out _);
                toRemove--;
            }
        }
        finally
        {
            Volatile.Write(ref _trimPeerKeysInProgress, 0);
        }
    }

    private readonly record struct PeerKeyCacheEntry(byte[]? Key, long ExpiresAtUnixMs);
}
