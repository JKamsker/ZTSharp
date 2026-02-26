namespace ZTSharp.Cli;

internal static class CliHelp
{
    public static void Print()
    {
        Console.WriteLine(
            """
            Usage:
              libzt join --network <nwid> [options]
              libzt listen <localPort> --network <nwid> [options]
              libzt udp-listen <localPort> --network <nwid> [options]
              libzt udp-send --network <nwid> --to <ip:port> --data <text> [options]
              libzt expose <localPort> --network <nwid> [options]
              libzt call --network <nwid> --url <url> [options]

            Options:
              --listen <port>             Listen port (default: <localPort>)
              --body-bytes <bytes>       For 'listen': response body size (default: small 'ok\n')
              --to <host:port>            Forward target (default: 127.0.0.1:<localPort>) or UDP target (udp-send)
              --data <text>               UDP payload (udp-send)
              --state <path>              State directory (default: temp folder)
              --stack <managed|overlay>   Node stack (default: managed; 'zerotier' and 'libzt' are aliases for 'managed')
              --transport <osudp|inmem>   Transport mode (default: osudp)
              --udp-port <port>           OS UDP listen port (osudp only, default: 0)
              --advertise <ip[:port]>     Advertised UDP endpoint for peers (osudp only)
              --peer <nodeId@ip:port>     Add an OS UDP peer (repeatable)
              --http <overlay|os>         HTTP mode for 'call' (default: overlay)
              --map-ip <ip=nodeId>        Map IP to node id for overlay HTTP (repeatable)
              --once                      For 'join': initialize and exit
            """);
    }
}

