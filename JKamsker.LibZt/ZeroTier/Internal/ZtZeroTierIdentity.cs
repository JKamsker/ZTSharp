using System.Globalization;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierIdentity
{
    public const int PublicKeyLength = 64;
    public const int PrivateKeyLength = 64;

    public ZtZeroTierIdentity(ZtNodeId nodeId, byte[] publicKey, byte[]? privateKey)
    {
        if (nodeId.Value == 0 || nodeId.Value > ZtNodeId.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        }

        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException($"Public key must be {PublicKeyLength} bytes.", nameof(publicKey));
        }

        if (privateKey is not null && privateKey.Length != PrivateKeyLength)
        {
            throw new ArgumentException($"Private key must be {PrivateKeyLength} bytes.", nameof(privateKey));
        }

        NodeId = nodeId;
        PublicKey = publicKey;
        PrivateKey = privateKey;
    }

    public ZtNodeId NodeId { get; }

    public byte[] PublicKey { get; }

    public byte[]? PrivateKey { get; }

    public bool HasPrivateKey => PrivateKey is not null;

    public bool IsReservedAddress()
    {
        var value = NodeId.Value;
        return value == 0 || (value >> 32) == 0xFF;
    }

    public bool LocallyValidate()
    {
        if (IsReservedAddress())
        {
            return false;
        }

        var digest = ZtZeroTierIdentityHashcash.ComputeMemoryHardHash(PublicKey);
        if (digest[0] >= ZtZeroTierIdentityHashcash.HashcashFirstByteLessThan)
        {
            return false;
        }

        Span<byte> addr = stackalloc byte[5];
        WriteAddressBytes(NodeId.Value, addr);
        return digest.AsSpan(59, 5).SequenceEqual(addr);
    }

    public override string ToString()
    {
        return ToIdentityString(includePrivate: HasPrivateKey);
    }

    public string ToIdentityString(bool includePrivate)
    {
        // Format matches ZeroTierOne Identity::toString:
        // <hex10 address>:0:<hex public key 64 bytes>[:<hex private key 64 bytes>]
        var addressText = NodeId.Value.ToString("x10", CultureInfo.InvariantCulture);
        var publicHex = ToLowerHex(PublicKey);
        if (!includePrivate || PrivateKey is null)
        {
            return $"{addressText}:0:{publicHex}";
        }

        var privateHex = ToLowerHex(PrivateKey);
        return $"{addressText}:0:{publicHex}:{privateHex}";
    }

    public static bool TryParse(string value, out ZtZeroTierIdentity identity)
    {
        identity = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':', StringSplitOptions.None);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (!ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) ||
            address == 0 ||
            address > ZtNodeId.MaxValue ||
            (address >> 32) == 0xFF)
        {
            return false;
        }

        if (!string.Equals(parts[1], "0", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (publicKey.Length != PublicKeyLength)
        {
            return false;
        }

        byte[]? privateKey = null;
        if (parts.Length == 4)
        {
            try
            {
                privateKey = Convert.FromHexString(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            if (privateKey.Length != PrivateKeyLength)
            {
                return false;
            }
        }

        identity = new ZtZeroTierIdentity(new ZtNodeId(address), publicKey, privateKey);
        return true;
    }

    private static string ToLowerHex(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length * 2];
        var b = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var byteValue = value[i];
            buffer[b++] = GetLowerHexNibble((byteValue >> 4) & 0xF);
            buffer[b++] = GetLowerHexNibble(byteValue & 0xF);
        }

        return new string(buffer);
    }

    private static char GetLowerHexNibble(int value)
    {
        return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
    }

    private static void WriteAddressBytes(ulong address, Span<byte> destination)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("Destination must be at least 5 bytes.", nameof(destination));
        }

        destination[0] = (byte)((address >> 32) & 0xFF);
        destination[1] = (byte)((address >> 24) & 0xFF);
        destination[2] = (byte)((address >> 16) & 0xFF);
        destination[3] = (byte)((address >> 8) & 0xFF);
        destination[4] = (byte)(address & 0xFF);
    }
}
