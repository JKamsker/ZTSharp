using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace JKamsker.LibZt.Tests;

public class ExternalZtNetTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Ztnet_NetworkCreate_And_Get_E2E()
    {
        if (!ShouldRunE2e())
        {
            return;
        }

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

    [Fact]
    public async Task Ztnet_JoinActualNetwork_RequiresConfiguredEndpointE2E()
    {
        if (!ShouldRunE2e())
        {
            return;
        }

        var networkId = Environment.GetEnvironmentVariable("LIBZT_E2E_NETWORK_ID");
        Assert.False(string.IsNullOrWhiteSpace(networkId));

        var getResult = await RunZtNetCommandAsync($"network get {networkId}");
        Assert.Equal(0, getResult.ExitCode);
        Assert.Contains($"nwid: {networkId}", getResult.StandardOutput);
    }

    private static bool ShouldRunE2e()
    {
        var flag = Environment.GetEnvironmentVariable("LIBZT_RUN_E2E");
        return bool.TryParse(flag, out var enabled) && enabled;
    }

    private static string? ParseNetworkId(string output)
    {
        var match = Regex.Match(output, @"(?im)^[\s:]*nwid:\s*([0-9a-f]+)\s*$");
        return match.Success ? match.Groups[1].Value : null;
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
