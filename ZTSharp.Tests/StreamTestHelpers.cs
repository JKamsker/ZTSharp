namespace ZTSharp.Tests;

internal static class StreamTestHelpers
{
    public static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int length,
        CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(readTotal, length - readTotal),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return readTotal;
            }

            readTotal += read;
        }

        return readTotal;
    }
}

