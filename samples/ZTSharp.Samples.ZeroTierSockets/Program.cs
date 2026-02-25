using ZTSharp.Samples.ZeroTierSockets.Commands;

namespace ZTSharp.Samples.ZeroTierSockets;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            SampleHelp.Print();
            return 0;
        }

        try
        {
            var command = args[0];
            var commandArgs = args.Skip(1).ToArray();

            switch (command)
            {
                case "server":
                    await TcpEchoServerCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "client":
                    await TcpEchoClientCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "udp-server":
                    await UdpServerCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "udp-client":
                    await UdpClientCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "http-get":
                    await HttpGetCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                default:
                    await Console.Error.WriteLineAsync($"Unknown command '{command}'.").ConfigureAwait(false);
                    SampleHelp.Print();
                    return 2;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}

