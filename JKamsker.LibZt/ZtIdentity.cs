using System.Buffers.Binary;
namespace JKamsker.LibZt;

/// <summary>
/// In-memory representation of node identity material.
/// </summary>
public sealed record class ZtIdentity(
    ZtNodeId NodeId,
    DateTimeOffset CreatedUtc,
    byte[] PublicKey,
    byte[] SecretKey);

internal static class ZtIdentitySerializer
{
    private const int SecretLength = 32;
    private const int PublicLength = 32;

    public static byte[] Serialize(ZtIdentity identity)
    {
        var payload = new byte[1 + sizeof(ulong) + SecretLength + PublicLength];
        payload[0] = 1;
        var nodeIdBytes = BitConverter.GetBytes(identity.NodeId.Value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(nodeIdBytes);
        }

        nodeIdBytes.CopyTo(payload, 1);
        identity.SecretKey.AsSpan(0, SecretLength).CopyTo(payload.AsSpan(1 + sizeof(ulong), SecretLength));
        identity.PublicKey.AsSpan(0, PublicLength).CopyTo(payload.AsSpan(1 + sizeof(ulong) + SecretLength, PublicLength));
        return payload;
    }

    public static ZtIdentity? TryDeserialize(byte[] data)
    {
        if (data.Length != 1 + sizeof(ulong) + SecretLength + PublicLength || data[0] != 1)
        {
            return null;
        }

        var nodeId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1, sizeof(ulong)));
        var secret = data.AsSpan(1 + sizeof(ulong), SecretLength).ToArray();
        var publicKey = data.AsSpan(1 + sizeof(ulong) + SecretLength, PublicLength).ToArray();

        return new ZtIdentity(
            new ZtNodeId(nodeId),
            DateTimeOffset.UtcNow,
            publicKey,
            secret);
    }
}
