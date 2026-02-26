using ZTSharp.Cli.Commands;

namespace ZTSharp.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            CliHelp.Print();
            return 0;
        }

        var trace = bool.TryParse(Environment.GetEnvironmentVariable("LIBZT_CLI_TRACE"), out var parsedTrace) && parsedTrace;

        try
        {
            var command = args[0];
            var commandArgs = args.Skip(1).ToArray();

            switch (command)
            {
                case "join":
                    await JoinCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "listen":
                    await ListenCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "udp-listen":
                    await UdpListenCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "udp-send":
                    await UdpSendCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "expose":
                    await ExposeCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                case "call":
                    await CallCommand.RunAsync(commandArgs).ConfigureAwait(false);
                    return 0;
                default:
                    await Console.Error.WriteLineAsync($"Unknown command '{command}'.").ConfigureAwait(false);
                    CliHelp.Print();
                    return 2;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await Console.Error.WriteLineAsync(trace ? ex.ToString() : ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
