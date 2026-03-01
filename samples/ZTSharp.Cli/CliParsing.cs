using System.Globalization;
using System.Net;
using ZTSharp;
using ZTSharp.ZeroTier;

namespace ZTSharp.Cli;

internal static class CliParsing
{
    public static int ParseUShortPort(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        return port;
    }

    public static int ParseUShortPortAllowZero(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        return port;
    }

    public static long ParseNonNegativeLong(string value, string name)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        return parsed;
    }

    public static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        return parsed;
    }

    public static string NormalizeStack(string stack)
    {
        if (string.Equals(stack, "zerotier", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stack, "libzt", StringComparison.OrdinalIgnoreCase))
        {
            return "managed";
        }

        return stack;
    }

    public static string ReadOptionValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {name}.");
        }

        index++;
        return args[index];
    }

    public static ulong ParseNetworkId(string text)
    {
        var span = text.AsSpan().Trim();
        var hasHexPrefix = false;
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hasHexPrefix = true;
            span = span.Slice(2);
        }

        if (span.Length == 0)
        {
            throw new InvalidOperationException("Invalid --network value.");
        }

        var treatAsHex = hasHexPrefix || span.Length == 16 || ContainsHexLetters(span);
        if (treatAsHex)
        {
            if (!IsHex(span))
            {
                throw new InvalidOperationException("Invalid --network value.");
            }

            return ulong.Parse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return ulong.Parse(span, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public static (string Host, int Port) ParseHostPort(string value)
    {
        try
        {
            var uri = new Uri("http://" + value);
            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > ushort.MaxValue)
            {
                throw new InvalidOperationException("Invalid endpoint.");
            }

            return (uri.Host, uri.Port);
        }
        catch (UriFormatException)
        {
            throw new InvalidOperationException("Invalid endpoint format. Expected host:port.");
        }
    }

    public static IPEndPoint ParseIpEndpoint(string value)
    {
        var (host, port) = ParseHostPort(value);
        if (!IPAddress.TryParse(host, out var ip))
        {
            throw new InvalidOperationException("Invalid --advertise value (expected IP[:port]).");
        }

        return new IPEndPoint(ip, port);
    }

    public static (ulong NodeId, IPEndPoint Endpoint) ParsePeer(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == value.Length - 1)
        {
            throw new InvalidOperationException("Invalid --peer value (expected nodeId@ip:port).");
        }

        var nodeIdText = value.Substring(0, at);
        var endpointText = value.Substring(at + 1);

        var nodeId = ParseNodeId(nodeIdText);
        var endpoint = ParseIpEndpoint(endpointText);
        return (nodeId, endpoint);
    }

    public static ulong ParseNodeId(string text)
    {
        if (!NodeId.TryParse(text, out var nodeId))
        {
            throw new InvalidOperationException("Invalid nodeId.");
        }

        return nodeId.Value;
    }

    public static (IPAddress Address, ulong NodeId) ParseIpMapping(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Invalid --map-ip value.");
        }

        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || equals == value.Length - 1)
        {
            throw new InvalidOperationException("Invalid --map-ip value (expected ip=nodeId).");
        }

        var ipText = value.Substring(0, equals);
        var nodeIdText = value.Substring(equals + 1);

        if (!IPAddress.TryParse(ipText, out var ip))
        {
            throw new InvalidOperationException("Invalid --map-ip value (expected ip=nodeId).");
        }

        var nodeId = ParseNodeId(nodeIdText);
        return (ip, nodeId);
    }

    public static ZeroTierBondPolicy ParseBondPolicy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Invalid bond policy.");
        }

        var normalized = value.Trim();
        if (normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.Off;
        }

        if (normalized.Equals("active-backup", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("activebackup", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.ActiveBackup;
        }

        if (normalized.Equals("broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.Broadcast;
        }

        if (normalized.Equals("balance-rr", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("balance-roundrobin", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("balance-round-robin", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("roundrobin", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("rr", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.BalanceRoundRobin;
        }

        if (normalized.Equals("balance-xor", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("xor", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.BalanceXor;
        }

        if (normalized.Equals("balance-aware", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("aware", StringComparison.OrdinalIgnoreCase))
        {
            return ZeroTierBondPolicy.BalanceAware;
        }

        throw new InvalidOperationException("Invalid bond policy (expected off|active-backup|broadcast|balance-rr|balance-xor|balance-aware).");
    }

    public static IReadOnlyList<int> ParsePortList(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException($"Invalid {name}.");
        }

        var ports = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            ports[i] = ParseUShortPortAllowZero(parts[i], name);
        }

        return ports;
    }

    private static bool ContainsHexLetters(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= '0' and <= '9')
            {
                continue;
            }

            if (c is >= 'a' and <= 'f')
            {
                continue;
            }

            if (c is >= 'A' and <= 'F')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
