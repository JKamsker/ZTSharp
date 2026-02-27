using System.Diagnostics;

namespace ZTSharp.Tests;

public sealed class FileStateStoreSecurityTests
{
    [Fact]
    public async Task ReadAsync_Throws_WhenPathTraversesJunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Junction traversal tests require Windows.");
        }

        var root = TestTempPaths.CreateGuidSuffixed("zt-state-root-");
        Directory.CreateDirectory(root);

        var outside = TestTempPaths.CreateGuidSuffixed("zt-state-outside-");
        Directory.CreateDirectory(outside);

        var secretPath = Path.Combine(outside, "secret.bin");
        await File.WriteAllBytesAsync(secretPath, new byte[] { 1, 2, 3 });

        var junction = Path.Combine(root, "escape");
        if (!TryCreateJunction(junction, outside))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Failed to create junction (insufficient privileges or mklink unavailable).");
        }

        var store = new FileStateStore(root);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReadAsync("escape/secret.bin"));
    }

    [Fact]
    public async Task DeleteAsync_PlanetAlias_DeletesBothPhysicalFiles()
    {
        var root = TestTempPaths.CreateGuidSuffixed("zt-state-alias-delete-");
        Directory.CreateDirectory(root);

        await File.WriteAllBytesAsync(Path.Combine(root, StateStorePlanetAliases.PlanetKey), new byte[] { 1 });
        await File.WriteAllBytesAsync(Path.Combine(root, StateStorePlanetAliases.RootsKey), new byte[] { 2 });

        var store = new FileStateStore(root);
        Assert.True(await store.DeleteAsync(StateStorePlanetAliases.PlanetKey));

        Assert.False(File.Exists(Path.Combine(root, StateStorePlanetAliases.PlanetKey)));
        Assert.False(File.Exists(Path.Combine(root, StateStorePlanetAliases.RootsKey)));

        Assert.Null(await store.ReadAsync(StateStorePlanetAliases.PlanetKey));
        Assert.Null(await store.ReadAsync(StateStorePlanetAliases.RootsKey));
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: true);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(milliseconds: 5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
    }
}

