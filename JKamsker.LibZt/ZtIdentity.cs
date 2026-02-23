using System.Buffers.Binary;
namespace JKamsker.LibZt;

/// <summary>
/// In-memory representation of node identity material.
/// </summary>
public sealed record class ZtIdentity(
    ZtNodeId NodeId,
    DateTimeOffset CreatedUtc,
    ReadOnlyMemory<byte> PublicKey,
    ReadOnlyMemory<byte> SecretKey);

internal static class ZtIdentitySerializer
{
    private const int SecretLength = 32;
    private const int PublicLength = 32;

    public static ReadOnlyMemory<byte> Serialize(ZtIdentity identity)
    {
        var payload = new byte[1 + sizeof(ulong) + SecretLength + PublicLength];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1), identity.NodeId.Value);
        identity.SecretKey.Span.Slice(0, SecretLength).CopyTo(payload.AsSpan(1 + sizeof(ulong), SecretLength));
        identity.PublicKey.Span.Slice(0, PublicLength).CopyTo(payload.AsSpan(1 + sizeof(ulong) + SecretLength, PublicLength));
        return payload;
    }

    public static ZtIdentity? TryDeserialize(ReadOnlyMemory<byte> data)
    {
        if (data.Length != 1 + sizeof(ulong) + SecretLength + PublicLength || data.Span[0] != 1)
        {
            return null;
        }

        var nodeId = BinaryPrimitives.ReadUInt64LittleEndian(data.Span.Slice(1, sizeof(ulong)));
        var secret = data.Slice(1 + sizeof(ulong), SecretLength);
        var publicKey = data.Slice(1 + sizeof(ulong) + SecretLength, PublicLength);

        return new ZtIdentity(
            new ZtNodeId(nodeId),
            DateTimeOffset.UtcNow,
            publicKey,
            secret);
    }
}
