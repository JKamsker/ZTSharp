using System.Buffers;
using System.Globalization;
using System.Net;
using ZTSharp.Transport;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Internal;

internal sealed class NodePeerService
{
    private readonly IStateStore _store;

    public NodePeerService(IStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task AddPeerAsync(
        ulong networkId,
        ulong peerNodeId,
        IPEndPoint endpoint,
        INodeTransport transport,
        CancellationToken cancellationToken)
    {
        if (transport is not OsUdpNodeTransport udpTransport)
        {
            throw new InvalidOperationException("Transport mode is not OS UDP.");
        }

        UdpEndpointNormalization.ValidateRemoteEndpoint(endpoint, nameof(endpoint));
        var normalized = UdpEndpointNormalization.Normalize(endpoint);

        cancellationToken.ThrowIfCancellationRequested();
        await udpTransport.AddPeerAsync(networkId, peerNodeId, normalized).ConfigureAwait(false);
        await PersistPeerAsync(networkId, peerNodeId, normalized, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverPeersAsync(ulong networkId, INodeTransport transport, CancellationToken cancellationToken)
    {
        if (transport is not OsUdpNodeTransport udpTransport)
        {
            return;
        }

        var prefix = BuildPeersNetworkPrefix(networkId);
        var keys = await _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParsePeerKey(prefix, key, out var peerNodeId))
            {
                continue;
            }

            var payload = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue || payload.Value.Length == 0)
            {
                continue;
            }

            if (!PeerEndpointCodec.TryDecode(payload.Value.Span, out var endpoint))
            {
                continue;
            }

            if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                continue;
            }

            await udpTransport.AddPeerAsync(networkId, peerNodeId, UdpEndpointNormalization.Normalize(endpoint)).ConfigureAwait(false);
        }
    }

    private async Task PersistPeerAsync(
        ulong networkId,
        ulong peerNodeId,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildPeerFileKey(networkId, peerNodeId);

        Span<byte> stackBuffer = stackalloc byte[PeerEndpointCodec.MaxEncodedLength];
        if (!PeerEndpointCodec.TryEncode(endpoint, stackBuffer, out var bytesWritten))
        {
            throw new InvalidOperationException("Failed to encode peer endpoint.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bytesWritten);
        try
        {
            stackBuffer.Slice(0, bytesWritten).CopyTo(buffer);
            await _store.WriteAsync(key, buffer.AsMemory(0, bytesWritten), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string BuildPeerFileKey(ulong networkId, ulong peerNodeId)
        => $"{NodeStoreKeys.PeersDirectoryPrefix}{networkId}/{peerNodeId}.peer";

    private static string BuildPeersNetworkPrefix(ulong networkId) => $"{NodeStoreKeys.PeersDirectoryPrefix}{networkId}/";

    private static bool TryParsePeerKey(ReadOnlySpan<char> prefix, string key, out ulong peerNodeId)
    {
        peerNodeId = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = key.AsSpan(prefix.Length);
        if (!suffix.EndsWith(".peer", StringComparison.Ordinal))
        {
            return false;
        }

        suffix = suffix[..^5];
        if (suffix.Length == 0 || suffix.Contains('/'))
        {
            return false;
        }

        return ulong.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out peerNodeId);
    }
}
