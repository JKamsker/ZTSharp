using System.Net;
using ZTSharp;

namespace ZTSharp.Cli;

internal static class CliOutput
{
    public static void WriteNodeId(NodeId nodeId)
    {
        Console.WriteLine($"NodeId: {nodeId}");
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
