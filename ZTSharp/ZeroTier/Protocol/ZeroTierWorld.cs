namespace ZTSharp.ZeroTier.Protocol;

internal sealed class ZeroTierWorld
{
    public const int C25519PublicKeyLength = 64;
    public const int C25519SignatureLength = 96;

    public ZeroTierWorld(
        ZeroTierWorldType type,
        ulong id,
        ulong timestamp,
        byte[] updatesMustBeSignedBy,
        byte[] signature,
        IReadOnlyList<ZeroTierWorldRoot> roots)
    {
        if (updatesMustBeSignedBy.Length != C25519PublicKeyLength)
        {
            throw new ArgumentException($"Public key must be {C25519PublicKeyLength} bytes.", nameof(updatesMustBeSignedBy));
        }

        if (signature.Length != C25519SignatureLength)
        {
            throw new ArgumentException($"Signature must be {C25519SignatureLength} bytes.", nameof(signature));
        }

        Type = type;
        Id = id;
        Timestamp = timestamp;
        UpdatesMustBeSignedBy = updatesMustBeSignedBy;
        Signature = signature;
        Roots = roots;
    }

    public ZeroTierWorldType Type { get; }

    public ulong Id { get; }

    public ulong Timestamp { get; }

    public byte[] UpdatesMustBeSignedBy { get; }

    public byte[] Signature { get; }

    public IReadOnlyList<ZeroTierWorldRoot> Roots { get; }
}

