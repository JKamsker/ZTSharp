using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using JKamsker.LibZt.Sockets;

namespace JKamsker.LibZt.Tests;

public class ExternalZtNetTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    [E2eFact]
    public async Task Ztnet_NetworkCreate_And_Get_E2E()
    {
        var authCheck = await RunZtNetCommandAsync("auth test", TimeSpan.FromSeconds(20));
        Assert.Equal(0, authCheck.ExitCode);

        var createNetworkName = $"libzt-dotnet-e2e-{Guid.NewGuid():N}";
        var createResult = await RunZtNetCommandAsync($"network create --name {createNetworkName}");
        Assert.Equal(0, createResult.ExitCode);

        var networkId = ParseNetworkId(createResult.StandardOutput);
        Assert.NotNull(networkId);

        var getResult = await RunZtNetCommandAsync($"network get {networkId}");
        Assert.Equal(0, getResult.ExitCode);
        Assert.Contains($"nwid: {networkId}", getResult.StandardOutput);
    }

    [E2eFact("LIBZT_E2E_NETWORK_ID")]
    public async Task Ztnet_JoinActualNetwork_RequiresConfiguredEndpointE2E()
    {
        var networkId = Environment.GetEnvironmentVariable("LIBZT_E2E_NETWORK_ID");
        Assert.False(string.IsNullOrWhiteSpace(networkId));

        var getResult = await RunZtNetCommandAsync($"network get {networkId}");
        Assert.Equal(0, getResult.ExitCode);
        Assert.Contains($"nwid: {networkId}", getResult.StandardOutput);
    }

    [E2eFact]
    public async Task Ztnet_NetworkCreate_SpawnTwoClients_And_Communicate_E2E()
    {
        var authCheck = await RunZtNetCommandAsync("auth test", TimeSpan.FromSeconds(20));
        Assert.Equal(0, authCheck.ExitCode);

        var createNetworkName = $"libzt-dotnet-e2e-{Guid.NewGuid():N}";
        var createResult = await RunZtNetCommandAsync($"--quiet --output json network create --name {createNetworkName}");
        Assert.Equal(0, createResult.ExitCode);

        var networkIdText = ParseNetworkIdFromJson(createResult.StandardOutput);
        Assert.False(string.IsNullOrWhiteSpace(networkIdText));
        var networkId = ulong.Parse(networkIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        try
        {
            var node1Store = new MemoryZtStateStore();
            var node2Store = new MemoryZtStateStore();

            await using var node1 = new ZtNode(new ZtNodeOptions
            {
                StateRootPath = Path.Combine(Path.GetTempPath(), "zt-e2e-node-" + Guid.NewGuid()),
                StateStore = node1Store,
                TransportMode = ZtTransportMode.OsUdp
            });

            await using var node2 = new ZtNode(new ZtNodeOptions
            {
                StateRootPath = Path.Combine(Path.GetTempPath(), "zt-e2e-node-" + Guid.NewGuid()),
                StateStore = node2Store,
                TransportMode = ZtTransportMode.OsUdp
            });

            await node1.StartAsync();
            await node2.StartAsync();

            await node1.JoinNetworkAsync(networkId);
            await node2.JoinNetworkAsync(networkId);

            var node1Identity = await node1.GetIdentityAsync();
            var node2Identity = await node2.GetIdentityAsync();
            var node1Id = node1Identity.NodeId.Value.ToString("x10", CultureInfo.InvariantCulture);
            var node2Id = node2Identity.NodeId.Value.ToString("x10", CultureInfo.InvariantCulture);

            var node1Endpoint = node1.LocalTransportEndpoint;
            var node2Endpoint = node2.LocalTransportEndpoint;
            Assert.NotNull(node1Endpoint);
            Assert.NotNull(node2Endpoint);

            // Ensure both nodes know each other's transport endpoint before attempting application traffic.
            await node1.AddPeerAsync(networkId, node2Identity.NodeId.Value, node2Endpoint);
            await node2.AddPeerAsync(networkId, node1Identity.NodeId.Value, node1Endpoint);

            // Register + authorize members using ztnet (session auth).
            var add1 = await RunZtNetCommandAsync($"--yes --quiet network member add {networkIdText} {node1Id}");
            Assert.Equal(0, add1.ExitCode);
            var add2 = await RunZtNetCommandAsync($"--yes --quiet network member add {networkIdText} {node2Id}");
            Assert.Equal(0, add2.ExitCode);

            var auth1 = await RunZtNetCommandAsync($"--yes --quiet network member authorize {networkIdText} {node1Id}");
            Assert.Equal(0, auth1.ExitCode);
            var auth2 = await RunZtNetCommandAsync($"--yes --quiet network member authorize {networkIdText} {node2Id}");
            Assert.Equal(0, auth2.ExitCode);

            await using var udp1 = new ZtUdpClient(node1, networkId, 10001);
            await using var udp2 = new ZtUdpClient(node2, networkId, 10002);

            await udp1.ConnectAsync(node2Identity.NodeId.Value, 10002);
            await udp2.ConnectAsync(node1Identity.NodeId.Value, 10001);

            var ping = Encoding.UTF8.GetBytes("ping");
            var pong = Encoding.UTF8.GetBytes("pong");

            var receivePing = udp2.ReceiveAsync();
            await udp1.SendAsync(ping);
            var datagramPing = await receivePing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(datagramPing.Payload.Span.SequenceEqual(ping));

            var receivePong = udp1.ReceiveAsync();
            await udp2.SendAsync(pong);
            var datagramPong = await receivePong.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(datagramPong.Payload.Span.SequenceEqual(pong));
        }
        finally
        {
            var deleteResult = await RunZtNetCommandAsync($"--yes --quiet network delete {networkIdText}");
            Assert.Equal(0, deleteResult.ExitCode);
        }
    }

    private static string? ParseNetworkId(string output)
    {
        var match = Regex.Match(output, @"(?im)^[\s:]*nwid:\s*([0-9a-f]+)\s*$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseNetworkIdFromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("nwid", out var nwid))
        {
            return null;
        }

        var value = nwid.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static async Task<CommandResult> RunZtNetCommandAsync(string arguments, TimeSpan? timeout = null)
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
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        process.Start();

        using var cts = new CancellationTokenSource(timeout ?? CommandTimeout);
        var readOutTask = process.StandardOutput.ReadToEndAsync();
        var readErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        await Task.WhenAll(readOutTask, readErrTask).ConfigureAwait(false);

        var output = await readOutTask.ConfigureAwait(false);
        var error = await readErrTask.ConfigureAwait(false);
        return new CommandResult(process.ExitCode, output, error);
    }
}

internal readonly record struct CommandResult(int ExitCode, string StandardOutput, string StandardError);
