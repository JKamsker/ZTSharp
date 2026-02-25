using System.Buffers;
using System.Globalization;
using System.Text;

namespace ZTSharp.Cli;

internal static class HttpUtilities
{
    public static async Task<string?> ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int maxBytes = 64 * 1024;
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream
                    .ReadAsync(rented.AsMemory(total, maxBytes - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return total == 0 ? null : Encoding.ASCII.GetString(rented, 0, total);
                }

                total += read;
                var end = IndexOfHttpHeaderTerminator(rented.AsSpan(0, total));
                if (end >= 0)
                {
                    return Encoding.ASCII.GetString(rented, 0, end + 4);
                }
            }

            return Encoding.ASCII.GetString(rented, 0, total);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static async Task WriteHttpOkAsync(Stream stream, long bodyBytes, CancellationToken cancellationToken)
    {
        var body = bodyBytes > 0
            ? ReadOnlyMemory<byte>.Empty
            : Encoding.UTF8.GetBytes("ok\n");

        var contentLength = bodyBytes > 0 ? bodyBytes : body.Length;
        var headerText =
            "HTTP/1.1 200 OK\r\n" +
            (bodyBytes > 0
                ? "Content-Type: application/octet-stream\r\n"
                : "Content-Type: text/plain; charset=utf-8\r\n") +
            $"Content-Length: {contentLength.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        var header = Encoding.ASCII.GetBytes(headerText);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        if (bodyBytes > 0)
        {
            const int chunkSize = 16 * 1024;
            var chunk = new byte[chunkSize];
            chunk.AsSpan().Fill((byte)'a');

            var remaining = bodyBytes;
            while (remaining > 0)
            {
                var toWrite = (int)Math.Min(chunk.Length, remaining);
                await stream.WriteAsync(chunk.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
                remaining -= toWrite;
            }
        }
        else
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int IndexOfHttpHeaderTerminator(ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i <= buffer.Length - 4; i++)
        {
            if (buffer[i] == (byte)'\r' &&
                buffer[i + 1] == (byte)'\n' &&
                buffer[i + 2] == (byte)'\r' &&
                buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }
}

