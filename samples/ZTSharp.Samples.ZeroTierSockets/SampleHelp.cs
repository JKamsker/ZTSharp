namespace ZTSharp.Samples.ZeroTierSockets;

internal static class SampleHelp
{
    public static void Print()
    {
        Console.WriteLine(
            """
            Usage:
              dotnet run -- server --network <nwid> [--state <path>] --port <port>
              dotnet run -- client --network <nwid> [--state <path>] --to <ip:port|url> --message <text>
              dotnet run -- udp-server --network <nwid> [--state <path>] --port <port>
              dotnet run -- udp-client --network <nwid> [--state <path>] --to <ip:port|url> --message <text>
              dotnet run -- http-get --network <nwid> [--state <path>] --url <url>

            Notes:
              - This sample uses the managed-only real ZeroTier stack (no OS adapter required).
              - For socket-like APIs, it uses ManagedSocket (compat wrapper over ZeroTierSocket).
            """);
    }
}

