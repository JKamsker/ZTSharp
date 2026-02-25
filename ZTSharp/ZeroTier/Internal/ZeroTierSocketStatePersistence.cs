using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketStatePersistence
{
    public static IPAddress[] LoadManagedIps(string statePath, ulong networkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentOutOfRangeException.ThrowIfZero(networkId);

        var networksDir = Path.Combine(statePath, "networks.d");
        var path = Path.Combine(networksDir, $"{networkId:x16}.ips.txt");
        if (!File.Exists(path))
        {
            return Array.Empty<IPAddress>();
        }

        try
        {
            var lines = File.ReadAllLines(path);
            var ips = new List<IPAddress>(lines.Length);
            foreach (var line in lines)
            {
                if (IPAddress.TryParse(line.Trim(), out var ip))
                {
                    ips.Add(ip);
                }
            }

            return ips.ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<IPAddress>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    public static byte[]? LoadNetworkConfigDictionary(string statePath, ulong networkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentOutOfRangeException.ThrowIfZero(networkId);

        var networksDir = Path.Combine(statePath, "networks.d");
        var path = Path.Combine(networksDir, $"{networkId:x16}.netconf.dict");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void PersistNetworkState(
        string statePath,
        ulong networkId,
        byte[] dictionaryBytes,
        IReadOnlyList<IPAddress> managedIps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentOutOfRangeException.ThrowIfZero(networkId);
        ArgumentNullException.ThrowIfNull(dictionaryBytes);
        ArgumentNullException.ThrowIfNull(managedIps);

        var networksDir = Path.Combine(statePath, "networks.d");
        Directory.CreateDirectory(networksDir);

        var dictPath = Path.Combine(networksDir, $"{networkId:x16}.netconf.dict");
        File.WriteAllBytes(dictPath, dictionaryBytes);

        var ipsPath = Path.Combine(networksDir, $"{networkId:x16}.ips.txt");
        File.WriteAllLines(ipsPath, managedIps.Select(ip => ip.ToString()));
    }
}

