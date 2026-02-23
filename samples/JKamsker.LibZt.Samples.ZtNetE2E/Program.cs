using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JKamsker.LibZt;
using JKamsker.LibZt.Sockets;

var networkName = $"libzt-dotnet-sample-{Guid.NewGuid():N}";
var timeout = TimeSpan.FromSeconds(30);

var auth = await RunZtNetCommandAsync("auth test", timeout);
if (auth.ExitCode != 0)
{
    Console.Error.WriteLine("ztnet auth test failed:");
    Console.Error.WriteLine(auth.StandardError);
    return;
}

var create = await RunZtNetCommandAsync($"--quiet --output json network create --name {networkName}", timeout);
if (create.ExitCode != 0)
{
    Console.Error.WriteLine("ztnet network create failed:");
    Console.Error.WriteLine(create.StandardError);
    return;
}

var networkIdText = ParseNetworkIdFromJson(create.StandardOutput);
if (string.IsNullOrWhiteSpace(networkIdText))
{
    Console.Error.WriteLine("Could not parse nwid from ztnet output:");
    Console.Error.WriteLine(create.StandardOutput);
    return;
}

var networkId = ulong.Parse(networkIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
Console.WriteLine($"Created network: {networkIdText}");

try
{
    await using var node1 = new ZtNode(new ZtNodeOptions
    {
        StateRootPath = Path.Combine(Path.GetTempPath(), "zt-sample-node-" + Guid.NewGuid()),
        StateStore = new MemoryZtStateStore(),
        TransportMode = ZtTransportMode.OsUdp
    });

    await using var node2 = new ZtNode(new ZtNodeOptions
    {
        StateRootPath = Path.Combine(Path.GetTempPath(), "zt-sample-node-" + Guid.NewGuid()),
        StateStore = new MemoryZtStateStore(),
        TransportMode = ZtTransportMode.OsUdp
    });

    await node1.StartAsync();
    await node2.StartAsync();

    await node1.JoinNetworkAsync(networkId);
    await node2.JoinNetworkAsync(networkId);

    var node1Identity = await node1.GetIdentityAsync();
    var node2Identity = await node2.GetIdentityAsync();

    var node1IdText = node1Identity.NodeId.Value.ToString("x10", CultureInfo.InvariantCulture);
    var node2IdText = node2Identity.NodeId.Value.ToString("x10", CultureInfo.InvariantCulture);
    Console.WriteLine($"Node1: {node1IdText}");
    Console.WriteLine($"Node2: {node2IdText}");

    var node1Endpoint = node1.LocalTransportEndpoint;
    var node2Endpoint = node2.LocalTransportEndpoint;
    if (node1Endpoint is null || node2Endpoint is null)
    {
        throw new InvalidOperationException("OS UDP transport did not expose local endpoints.");
    }

    await node1.AddPeerAsync(networkId, node2Identity.NodeId.Value, node2Endpoint);
    await node2.AddPeerAsync(networkId, node1Identity.NodeId.Value, node1Endpoint);

    _ = await RunZtNetCommandAsync($"--yes --quiet network member add {networkIdText} {node1IdText}", timeout);
    _ = await RunZtNetCommandAsync($"--yes --quiet network member add {networkIdText} {node2IdText}", timeout);
    _ = await RunZtNetCommandAsync($"--yes --quiet network member authorize {networkIdText} {node1IdText}", timeout);
    _ = await RunZtNetCommandAsync($"--yes --quiet network member authorize {networkIdText} {node2IdText}", timeout);

    await using var udp1 = new ZtUdpClient(node1, networkId, 10001);
    await using var udp2 = new ZtUdpClient(node2, networkId, 10002);

    await udp1.ConnectAsync(node2Identity.NodeId.Value, 10002);
    await udp2.ConnectAsync(node1Identity.NodeId.Value, 10001);

    var ping = Encoding.UTF8.GetBytes("ping");
    var pong = Encoding.UTF8.GetBytes("pong");

    Console.WriteLine("Sending ping...");
    var receivePing = udp2.ReceiveAsync();
    await udp1.SendAsync(ping);
    var datagramPing = await receivePing.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(datagramPing.Payload.Span)}");

    Console.WriteLine("Sending pong...");
    var receivePong = udp1.ReceiveAsync();
    await udp2.SendAsync(pong);
    var datagramPong = await receivePong.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(datagramPong.Payload.Span)}");

    Console.WriteLine("E2E OK");
}
finally
{
    _ = await RunZtNetCommandAsync($"--yes --quiet network delete {networkIdText}", timeout);
}

static string? ParseNetworkIdFromJson(string json)
{
    using var document = JsonDocument.Parse(json);
    if (!document.RootElement.TryGetProperty("nwid", out var nwid))
    {
        return null;
    }

    var value = nwid.GetString();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static async Task<CommandResult> RunZtNetCommandAsync(string arguments, TimeSpan timeout)
{
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo
    {
        FileName = "ztnet",
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    process.Start();

    using var cts = new CancellationTokenSource(timeout);
    var readOutTask = process.StandardOutput.ReadToEndAsync();
    var readErrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
    await Task.WhenAll(readOutTask, readErrTask).ConfigureAwait(false);

    var output = await readOutTask.ConfigureAwait(false);
    var error = await readErrTask.ConfigureAwait(false);
    return new CommandResult(process.ExitCode, output, error);
}

internal readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError);
