using System.Globalization;
using System.Net;

namespace ZTSharp.Samples.ZeroTierSockets;

internal static class SampleParsing
{
    public static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {option}.");
        }

        return args[++index];
    }

    public static IPEndPoint ParseToEndpoint(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var url))
        {
            if (string.IsNullOrWhiteSpace(url.Host) || url.Port <= 0)
            {
                throw new InvalidOperationException("Invalid --to value.");
            }

            if (!IPAddress.TryParse(url.Host, out var hostIp))
            {
                throw new InvalidOperationException("Invalid --to value (host must be an IP literal).");
            }

            return new IPEndPoint(hostIp, url.Port);
        }

        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var ip) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Invalid --to value (expected ip:port or url).");
        }

        return new IPEndPoint(ip, port);
    }

    public static ulong ParseNetworkId(string text)
    {
        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span.Slice(2);
        }

        if (span.Length == 0)
        {
            throw new InvalidOperationException("Invalid --network value.");
        }

        return ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

