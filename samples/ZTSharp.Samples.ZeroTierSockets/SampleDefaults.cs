namespace ZTSharp.Samples.ZeroTierSockets;

internal static class SampleDefaults
{
    private const string TempRootDirectoryName = "libzt-dotnet-samples";

    public static string GetDefaultStatePath(string leafDirectoryName)
        => Path.Combine(Path.GetTempPath(), TempRootDirectoryName, leafDirectoryName);
}

