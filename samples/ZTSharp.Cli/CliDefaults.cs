namespace ZTSharp.Cli;

internal static class CliDefaults
{
    private const string TempRootDirectoryName = "libzt-dotnet-cli";

    public static string CreateTemporaryStatePath()
        => Path.Combine(Path.GetTempPath(), TempRootDirectoryName, "node-" + Guid.NewGuid().ToString("N"));
}

