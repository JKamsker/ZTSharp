using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZTSharp.Internal;

internal sealed class NodeIdentityService
{
    private readonly IStateStore _store;
    private readonly NodeEventStream _events;

    public NodeIdentityService(IStateStore store, NodeEventStream events)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public async Task<Identity> EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var secret = await _store.ReadAsync(NodeStoreKeys.IdentitySecretKey, cancellationToken).ConfigureAwait(false);
        var publicKey = await _store.ReadAsync(NodeStoreKeys.IdentityPublicKey, cancellationToken).ConfigureAwait(false);

        if (secret.HasValue && secret.Value.Length == 32 && publicKey.HasValue && publicKey.Value.Length == 32)
        {
            return new Identity(
                new NodeId(ComputeNodeIdFromSecret(secret.Value.Span)),
                DateTimeOffset.UtcNow,
                publicKey.Value,
                secret.Value);
        }

        var createdSecret = RandomNumberGenerator.GetBytes(32);
        var createdPublicFull = SHA512.HashData(createdSecret.AsSpan(0, 32));
        var createdPublic = createdPublicFull.AsMemory(0, 32);
        var identity = new Identity(
            new NodeId(ComputeNodeIdFromSecret(createdSecret.AsSpan())),
            DateTimeOffset.UtcNow,
            createdPublic,
            createdSecret.AsMemory(0, 32));

        await _store.WriteAsync(NodeStoreKeys.IdentitySecretKey, identity.SecretKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(NodeStoreKeys.IdentityPublicKey, identity.PublicKey, cancellationToken).ConfigureAwait(false);
        await _store.WriteAsync(NodeStoreKeys.PlanetKey, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        // Secret-key file permissions are state-store dependent. FileStateStore performs best-effort hardening on Unix.
        _events.Publish(EventCode.IdentityInitialized, DateTimeOffset.UtcNow);
        return identity;
    }

    private static ulong ComputeNodeIdFromSecret(ReadOnlySpan<byte> secret)
    {
        var hash = SHA256.HashData(secret.Slice(0, 32));
        return BinaryPrimitives.ReadUInt64LittleEndian(hash) & NodeId.MaxValue;
    }
}

