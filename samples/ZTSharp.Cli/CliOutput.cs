using System.Globalization;
using System.Net;
using ZTSharp;

namespace ZTSharp.Cli;

internal static class CliOutput
{
    public static string FormatNodeIdHost(NodeId nodeId) => nodeId.Value.ToString("x10", CultureInfo.InvariantCulture);

    public static void WriteNodeId(NodeId nodeId)
    {
        Console.WriteLine($"NodeId: {nodeId} ({FormatNodeIdHost(nodeId)})");
    }

    public static void WriteManagedIps(IReadOnlyList<IPAddress> managedIps)
    {
        if (managedIps.Count == 0)
        {
            return;
        }

        Console.WriteLine("Managed IPs:");
        foreach (var ip in managedIps)
        {
            Console.WriteLine($"  {ip}");
        }
    }
}
