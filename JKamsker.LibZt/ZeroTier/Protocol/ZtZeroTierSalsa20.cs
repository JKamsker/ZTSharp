using System.Buffers.Binary;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierSalsa20
{
    private const int KeyLength = 32;
    private const int IvLength = 8;
    private const int BlockLength = 64;

    public static void GenerateKeyStream12(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> destination)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"Key must be {KeyLength} bytes.", nameof(key));
        }

        if (iv.Length != IvLength)
        {
            throw new ArgumentException($"IV must be {IvLength} bytes.", nameof(iv));
        }

        Span<uint> state = stackalloc uint[16];
        InitState(state, key, iv);

        Span<uint> x = stackalloc uint[16];
        Span<byte> block = stackalloc byte[BlockLength];

        var remaining = destination;
        while (!remaining.IsEmpty)
        {
            GenerateBlock12(state, x, block);

            var toCopy = Math.Min(block.Length, remaining.Length);
            block.Slice(0, toCopy).CopyTo(remaining);
            remaining = remaining.Slice(toCopy);
        }
    }

    private static void InitState(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        // Matches ZeroTierOne/node/Salsa20.cpp (non-SSE init):
        // constants = "expand 32-byte k"
        state[0] = 0x61707865;
        state[1] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(0, 4));
        state[2] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4, 4));
        state[3] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(8, 4));
        state[4] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12, 4));
        state[5] = 0x3320646e;
        state[6] = BinaryPrimitives.ReadUInt32LittleEndian(iv.Slice(0, 4));
        state[7] = BinaryPrimitives.ReadUInt32LittleEndian(iv.Slice(4, 4));
        state[8] = 0;
        state[9] = 0;
        state[10] = 0x79622d32;
        state[11] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16, 4));
        state[12] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20, 4));
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24, 4));
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28, 4));
        state[15] = 0x6b206574;
    }

    private static void GenerateBlock12(Span<uint> state, Span<uint> x, Span<byte> output)
    {
        state.CopyTo(x);

        // Salsa20/12 = 6 double-rounds.
        for (var i = 0; i < 6; i++)
        {
            // Column round
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 5, 9, 13, 1);
            QuarterRound(x, 10, 14, 2, 6);
            QuarterRound(x, 15, 3, 7, 11);

            // Row round
            QuarterRound(x, 0, 1, 2, 3);
            QuarterRound(x, 5, 6, 7, 4);
            QuarterRound(x, 10, 11, 8, 9);
            QuarterRound(x, 15, 12, 13, 14);
        }

        for (var i = 0; i < 16; i++)
        {
            x[i] = unchecked(x[i] + state[i]);
        }

        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), x[i]);
        }

        // Increment 64-bit block counter in state[8..9]
        state[8] = unchecked(state[8] + 1);
        if (state[8] == 0)
        {
            state[9] = unchecked(state[9] + 1);
        }
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= RotateLeft(unchecked(x[a] + x[d]), 7);
        x[c] ^= RotateLeft(unchecked(x[b] + x[a]), 9);
        x[d] ^= RotateLeft(unchecked(x[c] + x[b]), 13);
        x[a] ^= RotateLeft(unchecked(x[d] + x[c]), 18);
    }

    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }
}

